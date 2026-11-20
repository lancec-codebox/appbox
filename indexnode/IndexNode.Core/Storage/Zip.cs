using System.Security.Cryptography;
using System.Text;

namespace IndexNode;

/// <summary>CRC-32 (the zip one, polynomial 0xEDB88320).</summary>
public static class Crc32
{
    private static readonly uint[] Table = Make();
    private static uint[] Make() { var t = new uint[256]; for (uint i = 0; i < 256; i++) { var c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; } return t; }
    public static uint Update(uint crc, ReadOnlySpan<byte> data) { var c = ~crc; foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8); return ~c; }
    public static uint Of(ReadOnlySpan<byte> data) => Update(0, data);
}

/// <summary>One member of a zip: its name, CRC, sizes and where its data starts.</summary>
public sealed record ZipEntry(string Name, uint Crc, long Size, long CompressedSize, long LocalOffset, int Method)
{
    public bool IsDirectory => Name.EndsWith('/');
}

/// <summary>Where a zip is read from: a file, or a blob read by ranges.</summary>
public interface IZipSource
{
    long Length { get; }
    /// <summary>Bytes off..off+len (clamped to the end).</summary>
    Task<byte[]> ReadAsync(long off, int len, CancellationToken ct);
}

public sealed class FileZipSource(string path) : IZipSource
{
    public long Length { get; } = new FileInfo(path).Length;
    public async Task<byte[]> ReadAsync(long off, int len, CancellationToken ct)
    {
        len = (int)Math.Min(len, Length - off);
        var buf = new byte[Math.Max(0, len)];
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, true);
        fs.Seek(off, SeekOrigin.Begin);
        int n = 0; while (n < buf.Length) { int r = await fs.ReadAsync(buf.AsMemory(n), ct); if (r == 0) break; n += r; }
        return buf;
    }
}

/// <summary>A standard zip, members stored (no compression), real CRC-32, zip64 when a size, an offset or the member count needs it. Opens in any zip tool.</summary>
public static class ZipFormat
{
    /// <summary>Sizes and offsets from here on need zip64 (tests lower it to cross the boundary with small files).</summary>
    public static long Zip64Limit { get; set; } = 0xFFFFFFFFL;
    public static int Zip64Count { get; set; } = 0xFFFF;

    /// <summary>A zip member to write: a file on disk or bytes.</summary>
    public readonly record struct Member(string Name, string? Path = null, byte[]? Bytes = null);

    public static string Sha256Of(string path) { using var fs = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant(); }

    /// <summary>Writes the zip to <paramref name="path"/>; returns each member's entry (name, CRC, size). Members are written in the order given; names use '/'.</summary>
    public static List<ZipEntry> Write(string path, IEnumerable<Member> members)
    {
        var entries = new List<ZipEntry>(); var meta = new List<(byte[] Name, bool Z64Local)>();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var buf = new byte[1 << 16];
        foreach (var m in members)
        {
            var name = Encoding.UTF8.GetBytes(m.Name);
            long size; uint crc = 0;
            if (m.Bytes != null) { size = m.Bytes.Length; crc = Crc32.Of(m.Bytes); }
            else { size = new FileInfo(m.Path!).Length; using var rs = File.OpenRead(m.Path!); int n; while ((n = rs.Read(buf, 0, buf.Length)) > 0) crc = Crc32.Update(crc, buf.AsSpan(0, n)); }
            long off = fs.Position; bool z64 = size >= Zip64Limit || off >= Zip64Limit;
            var h = new List<byte>();
            void U16(int v) { h.Add((byte)v); h.Add((byte)(v >> 8)); }
            void U32(uint v) { for (int i = 0; i < 4; i++) h.Add((byte)(v >> (8 * i))); }
            void U64(long v) { for (int i = 0; i < 8; i++) h.Add((byte)(v >> (8 * i))); }
            U32(0x04034b50); U16(z64 ? 45 : 20); U16(0x0800); U16(0); U16(0); U16(0x21); U32(crc);
            U32(z64 ? 0xFFFFFFFFu : (uint)size); U32(z64 ? 0xFFFFFFFFu : (uint)size); U16(name.Length); U16(z64 ? 20 : 0);
            h.AddRange(name);
            if (z64) { U16(1); U16(16); U64(size); U64(size); }
            fs.Write(h.ToArray());
            if (m.Bytes != null) fs.Write(m.Bytes);
            else { using var rs = File.OpenRead(m.Path!); int n; while ((n = rs.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n); }
            entries.Add(new ZipEntry(m.Name, crc, size, size, off, 0)); meta.Add((name, z64));
        }
        long cdStart = fs.Position;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i]; var name = meta[i].Name;
            bool zs = e.Size >= Zip64Limit, zo = e.LocalOffset >= Zip64Limit;
            var h = new List<byte>();
            void U16(int v) { h.Add((byte)v); h.Add((byte)(v >> 8)); }
            void U32(uint v) { for (int k = 0; k < 4; k++) h.Add((byte)(v >> (8 * k))); }
            void U64(long v) { for (int k = 0; k < 8; k++) h.Add((byte)(v >> (8 * k))); }
            int extra = (zs ? 16 : 0) + (zo ? 8 : 0); bool z = extra > 0;
            U32(0x02014b50); U16(z ? 45 : 20); U16(z ? 45 : 20); U16(0x0800); U16(0); U16(0); U16(0x21); U32(e.Crc);
            U32(zs ? 0xFFFFFFFFu : (uint)e.Size); U32(zs ? 0xFFFFFFFFu : (uint)e.Size); U16(name.Length); U16(z ? 4 + extra : 0); U16(0); U16(0); U16(0); U32(0);
            U32(zo ? 0xFFFFFFFFu : (uint)e.LocalOffset); h.AddRange(name);
            if (z) { U16(1); U16(extra); if (zs) { U64(e.Size); U64(e.Size); } if (zo) U64(e.LocalOffset); }
            fs.Write(h.ToArray());
        }
        long cdEnd = fs.Position, cdSize = cdEnd - cdStart;
        bool big = entries.Count >= Zip64Count || cdStart >= Zip64Limit || cdSize >= Zip64Limit;
        var t = new List<byte>();
        void T16(int v) { t.Add((byte)v); t.Add((byte)(v >> 8)); }
        void T32(uint v) { for (int k = 0; k < 4; k++) t.Add((byte)(v >> (8 * k))); }
        void T64(long v) { for (int k = 0; k < 8; k++) t.Add((byte)(v >> (8 * k))); }
        if (big)
        {
            T32(0x06064b50); T64(44); T16(45); T16(45); T32(0); T32(0); T64(entries.Count); T64(entries.Count); T64(cdSize); T64(cdStart);
            T32(0x07064b50); T32(0); T64(cdEnd); T32(1);
        }
        T32(0x06054b50); T16(0); T16(0); T16(big ? 0xFFFF : entries.Count); T16(big ? 0xFFFF : entries.Count);
        T32(big ? 0xFFFFFFFFu : (uint)cdSize); T32(big ? 0xFFFFFFFFu : (uint)cdStart); T16(0);
        fs.Write(t.ToArray());
        return entries;
    }

    /// <summary>Reads the member list: the end record, then the central directory.</summary>
    public static async Task<List<ZipEntry>> ReadDirectoryAsync(IZipSource src, CancellationToken ct, int tailHint = 65535 + 22 + 20 + 56)
    {
        // tailHint: how much of the end to read first (at least 98); a zip with few members needs about 1 KB, so a range-reading query does not pull 64 KB per zip
        int tailLen = (int)Math.Min(src.Length, Math.Max(tailHint, 22 + 20 + 56));
        long tailOff = src.Length - tailLen;
        var tail = await src.ReadAsync(tailOff, tailLen, ct);
        int eocd = -1;
        for (int i = tail.Length - 22; i >= 0; i--) if (BitConverter.ToUInt32(tail, i) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("not a zip: no end of central directory record");
        long count = BitConverter.ToUInt16(tail, eocd + 10), cdSize = BitConverter.ToUInt32(tail, eocd + 12), cdOff = BitConverter.ToUInt32(tail, eocd + 16);
        if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOff == 0xFFFFFFFF)
        {
            int loc = eocd - 20;
            if (loc < 0 || BitConverter.ToUInt32(tail, loc) != 0x07064b50) throw new InvalidDataException("zip64 locator missing");
            long e64 = BitConverter.ToInt64(tail, loc + 8);
            var r = await src.ReadAsync(e64, 56, ct);
            if (BitConverter.ToUInt32(r, 0) != 0x06064b50) throw new InvalidDataException("zip64 end record missing");
            count = BitConverter.ToInt64(r, 32); cdSize = BitConverter.ToInt64(r, 40); cdOff = BitConverter.ToInt64(r, 48);
        }
        var cd = cdOff >= tailOff ? tail.AsMemory((int)(cdOff - tailOff), (int)cdSize).ToArray() : await src.ReadAsync(cdOff, (int)cdSize, ct);
        var list = new List<ZipEntry>(); int p = 0;
        for (long i = 0; i < count; i++)
        {
            if (BitConverter.ToUInt32(cd, p) != 0x02014b50) throw new InvalidDataException("bad central directory entry");
            int method = BitConverter.ToUInt16(cd, p + 10); uint crc = BitConverter.ToUInt32(cd, p + 16);
            long csize = BitConverter.ToUInt32(cd, p + 20), usize = BitConverter.ToUInt32(cd, p + 24);
            int nl = BitConverter.ToUInt16(cd, p + 28), el = BitConverter.ToUInt16(cd, p + 30), cl = BitConverter.ToUInt16(cd, p + 32);
            long lo = BitConverter.ToUInt32(cd, p + 42);
            var name = Encoding.UTF8.GetString(cd, p + 46, nl);
            int x = p + 46 + nl, xend = x + el;
            while (x + 4 <= xend)
            {
                int id = BitConverter.ToUInt16(cd, x), len = BitConverter.ToUInt16(cd, x + 2), q = x + 4;
                if (id == 1)
                {
                    if (usize == 0xFFFFFFFF) { usize = BitConverter.ToInt64(cd, q); q += 8; }
                    if (csize == 0xFFFFFFFF) { csize = BitConverter.ToInt64(cd, q); q += 8; }
                    if (lo == 0xFFFFFFFF) { lo = BitConverter.ToInt64(cd, q); q += 8; }
                }
                x += 4 + len;
            }
            list.Add(new ZipEntry(name, crc, usize, csize, lo, method));
            p += 46 + nl + el + cl;
        }
        return list;
    }

    /// <summary>The bytes of one member (stored, or deflated by another tool), checked against its CRC.</summary>
    public static async Task<byte[]> ReadMemberAsync(IZipSource src, ZipEntry e, CancellationToken ct)
    {
        var lh = await src.ReadAsync(e.LocalOffset, 30, ct);
        if (lh.Length < 30 || BitConverter.ToUInt32(lh, 0) != 0x04034b50) throw new InvalidDataException($"bad local header for {e.Name}");
        long start = e.LocalOffset + 30 + BitConverter.ToUInt16(lh, 26) + BitConverter.ToUInt16(lh, 28);
        var raw = e.CompressedSize == 0 ? [] : await ReadAllAsync(src, start, e.CompressedSize, ct);
        if (e.Method == 8) { using var ms = new MemoryStream(raw); using var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress); var o = new MemoryStream(); ds.CopyTo(o); raw = o.ToArray(); }
        else if (e.Method != 0) throw new NotSupportedException($"zip method {e.Method}");
        if (Crc32.Of(raw) != e.Crc) throw new InvalidDataException($"CRC mismatch in {e.Name}");
        return raw;
    }

    private static async Task<byte[]> ReadAllAsync(IZipSource src, long off, long len, CancellationToken ct)
    {
        if (len <= int.MaxValue / 2) return await src.ReadAsync(off, (int)len, ct);
        var all = new byte[len]; long pos = 0;
        while (pos < len) { var part = await src.ReadAsync(off + pos, (int)Math.Min(1 << 26, len - pos), ct); part.CopyTo(all, pos); pos += part.Length; }
        return all;
    }
}
