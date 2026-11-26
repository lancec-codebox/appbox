using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace IndexNode;

/// <summary>
/// 0.13.69 (PROTOCOL "The search index carries a code of every frame, not of the centers"; owner 2026-11-26: "Append log"): a 64-byte sign code of EVERY indexed frame, kept in an append log
/// `index/search/codes/m&lt;meanVersion&gt;/log-NNNN.bin` (under index/search/, which is never ghosted). The search-index stage appends one entry per upload it indexes (from the same frames or retained file
/// the version lists, with the version's frozen mean) and records where the entry is in the version (uploads.yaml `codes: "seg:offset[,offset]"`); readers keep a local copy of the log and fetch only
/// what is new. An entry is rebuilt from its frames file whenever it is missing, so a lost or dead entry never matters.
/// A part (one append block, at most <see cref="MaxFramesPerPart"/> frames): magic "IXFC" | u32 length (all of it, the closing newline included) | u32 format (1) | u32 mean version | u32 first frame |
/// u32 frames in this part | u32 frames of the upload | u16 id length | u16 blob length | u16 sha length | u16 0 | u32 0 | id, blob, sha (UTF-8) | zero padding to 8 | count x 64-byte codes |
/// count x u32 vector offsets in the frames file | count x f32 times | '\n' (the store's append reader checks the byte before an offset).
/// Frames are in frames-file order. Code bit d (byte d/8, bit d%8, least significant first) is set when v[d] &gt; mean[d], exactly as the centers' bits.bin.
/// </summary>
public static class FrameCodes
{
    public const int CodeBytes = 64, Words = 8, Fixed = 40, Format = 1;
    public const int MaxFramesPerPart = 50_000;      // 3.6 MB: one append block stays under 4 MiB
    public const int SegmentMaxBlocks = 40_000;      // an append blob holds 50,000 blocks: the next segment starts well before
    public const int VectorBytes = CentersFile.Dim * 2;
    static readonly byte[] Magic = "IXFC"u8.ToArray();

    public static string Dir(Paths p, int meanVersion) => $"{p.Prefix}search/codes/m{meanVersion}/";
    public static string Segment(Paths p, int meanVersion, int seg) => $"{Dir(p, meanVersion)}log-{seg:D4}.bin";

    public sealed record Part(string Id, string Blob, string Sha, int MeanVersion, int First, int Total, ulong[] Codes, uint[] Offsets, float[] Times)
    {
        public int Count => Times.Length;
    }

    // ------------------------------------------------------------------ pointer ("seg:off[,off]")
    public static string Pointer(int seg, IEnumerable<long> offsets) => seg + ":" + string.Join(",", offsets);
    public static (int Segment, long[] Offsets)? ParsePointer(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null; int c = s.IndexOf(':'); if (c <= 0) return null;
        if (!int.TryParse(s[..c], out var seg)) return null;
        var offs = s[(c + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();
        return offs.Length == 0 ? null : (seg, offs);
    }

    // ------------------------------------------------------------------ codes
    /// <summary>The code of a vector: bit d set when v[d] - mean[d] &gt; 0 (the same as <see cref="SearchData.Code"/>), as 8 words.</summary>
    public static ulong[] Code(ReadOnlySpan<float> v, ReadOnlySpan<float> mean)
    {
        var w = new ulong[Words];
        for (int d = 0; d < CentersFile.Dim; d++) if (v[d] - mean[d] > 0) w[d >> 6] |= 1UL << (d & 63);
        return w;
    }

    /// <summary>The parts of one upload from its frames (or retained) file's bytes: every member of every block, in file order.</summary>
    public static List<Part> FromFile(string id, string blob, string sha, int meanVersion, float[] mean, ReadOnlySpan<byte> file)
    {
        var h = CentersFile.ParseHeader(file[..CentersFile.HeaderSize]);
        var table = CentersFile.ParseTable(file.Slice((int)h.TableOff, h.K * CentersFile.TableEntry), h.K);
        int n = 0; foreach (var b in table) n += b.Count;
        var codes = new ulong[(long)n * Words]; var offs = new uint[n]; var times = new float[n]; int i = 0;
        foreach (var b in table)
        {
            if (b.Count == 0) continue;
            for (int r = 0; r < b.Count; r++, i++)
            {
                long vo = b.Offset + (long)r * VectorBytes; var vs = file.Slice((int)vo, VectorBytes);
                for (int d = 0; d < CentersFile.Dim; d++) if ((float)BinaryPrimitives.ReadHalfLittleEndian(vs[(d * 2)..]) - mean[d] > 0) codes[(long)i * Words + (d >> 6)] |= 1UL << (d & 63);
                offs[i] = checked((uint)vo); times[i] = BinaryPrimitives.ReadSingleLittleEndian(file[(int)(b.Offset + (long)b.Count * VectorBytes + r * 4L)..]);
            }
        }
        var parts = new List<Part>();
        for (int first = 0; first < n || (n == 0 && parts.Count == 0); first += MaxFramesPerPart)
        {
            int c = Math.Min(MaxFramesPerPart, n - first);
            parts.Add(new Part(id, blob, sha, meanVersion, first, n, codes.AsSpan(first * Words, c * Words).ToArray(), offs.AsSpan(first, c).ToArray(), times.AsSpan(first, c).ToArray()));
            if (n == 0) break;
        }
        return parts;
    }

    public static byte[] Encode(Part p)
    {
        var id = Encoding.UTF8.GetBytes(p.Id); var bl = Encoding.UTF8.GetBytes(p.Blob); var sh = Encoding.UTF8.GetBytes(p.Sha);
        int head = Fixed + id.Length + bl.Length + sh.Length; head = (head + 7) & ~7;
        int len = head + p.Count * (CodeBytes + 8) + 1;
        var b = new byte[len]; var s = b.AsSpan();
        Magic.CopyTo(s); BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)len); BinaryPrimitives.WriteUInt32LittleEndian(s[8..], Format); BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)p.MeanVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)p.First); BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)p.Count); BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)p.Total);
        BinaryPrimitives.WriteUInt16LittleEndian(s[28..], (ushort)id.Length); BinaryPrimitives.WriteUInt16LittleEndian(s[30..], (ushort)bl.Length); BinaryPrimitives.WriteUInt16LittleEndian(s[32..], (ushort)sh.Length);
        id.CopyTo(s[Fixed..]); bl.CopyTo(s[(Fixed + id.Length)..]); sh.CopyTo(s[(Fixed + id.Length + bl.Length)..]);
        int o = head;
        for (int i = 0; i < p.Codes.Length; i++, o += 8) BinaryPrimitives.WriteUInt64LittleEndian(s[o..], p.Codes[i]);
        for (int i = 0; i < p.Count; i++, o += 4) BinaryPrimitives.WriteUInt32LittleEndian(s[o..], p.Offsets[i]);
        for (int i = 0; i < p.Count; i++, o += 4) BinaryPrimitives.WriteSingleLittleEndian(s[o..], p.Times[i]);
        b[^1] = (byte)'\n';
        return b;
    }

    /// <summary>The length of the part that starts at <paramref name="b"/> (0 when fewer than <see cref="Fixed"/> bytes are there).</summary>
    public static int LengthAt(ReadOnlySpan<byte> b)
    {
        if (b.Length < Fixed) return 0;
        if (!b[..4].SequenceEqual(Magic)) throw new InvalidDataException("not a frame codes part (bad magic)");
        return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(b[4..]));
    }

    public static Part Decode(ReadOnlySpan<byte> b)
    {
        int len = LengthAt(b); if (len == 0 || b.Length < len) throw new InvalidDataException("a frame codes part is cut short");
        if (BinaryPrimitives.ReadUInt32LittleEndian(b[8..]) != Format) throw new InvalidDataException("unknown frame codes format");
        int mv = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[12..]), first = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[16..]), n = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[20..]), total = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[24..]);
        int il = BinaryPrimitives.ReadUInt16LittleEndian(b[28..]), bl = BinaryPrimitives.ReadUInt16LittleEndian(b[30..]), sl = BinaryPrimitives.ReadUInt16LittleEndian(b[32..]);
        var id = Encoding.UTF8.GetString(b.Slice(Fixed, il)); var blob = Encoding.UTF8.GetString(b.Slice(Fixed + il, bl)); var sha = Encoding.UTF8.GetString(b.Slice(Fixed + il + bl, sl));
        int o = (Fixed + il + bl + sl + 7) & ~7;
        if (o + n * (CodeBytes + 8) + 1 != len || b[len - 1] != (byte)'\n') throw new InvalidDataException($"frame codes part of {id}: length {len} does not match {n} frames");
        ulong[] codes; uint[] offs; float[] times;
        if (BitConverter.IsLittleEndian)
        {   // 0.13.71: one block copy per column (the file is little-endian, as this machine)
            codes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(b.Slice(o, n * CodeBytes)).ToArray(); o += n * CodeBytes;
            offs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(b.Slice(o, n * 4)).ToArray(); o += n * 4;
            times = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(b.Slice(o, n * 4)).ToArray(); o += n * 4;
        }
        else
        {
            codes = new ulong[(long)n * Words]; for (int i = 0; i < codes.Length; i++, o += 8) codes[i] = BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
            offs = new uint[n]; for (int i = 0; i < n; i++, o += 4) offs[i] = BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
            times = new float[n]; for (int i = 0; i < n; i++, o += 4) times[i] = BinaryPrimitives.ReadSingleLittleEndian(b[o..]);
        }
        return new Part(id, blob, sha, mv, first, total, codes, offs, times);
    }

    // ------------------------------------------------------------------ writer
    /// <summary>The appender of one process: it remembers the segment and its length, so an entry costs one Append Block (plus one HEAD on the first, or after another writer moved the end).</summary>
    public sealed class Writer(IStore store, Paths paths, int meanVersion)
    {
        int seg = -1; long pos = -1; int blocks;
        public int Appends { get; private set; }

        async Task FindEndAsync(CancellationToken ct)
        {
            if (seg < 0) seg = 0;
            while (true)
            {
                var name = Segment(paths, meanVersion, seg);
                var info = await store.GetAppendInfoAsync(name, ct);
                if (info == null) { await store.CreateAppendBlobAsync(name, true, ct); info = await store.GetAppendInfoAsync(name, ct) ?? throw new InvalidOperationException(name + " could not be created"); }
                if (info.Sealed) { seg++; continue; }
                if (info.Blocks >= SegmentMaxBlocks) { await store.SealAppendBlobAsync(name, info.Length, ct); seg++; continue; }
                pos = info.Length; blocks = info.Blocks; return;
            }
        }

        /// <summary>Appends the parts of one upload, all in one segment, one after the other; returns the pointer the version records.</summary>
        public async Task<string> AppendAsync(IReadOnlyList<Part> parts, CancellationToken ct)
        {
            var data = parts.Select(Encode).ToList();
            for (int attempt = 0; ; attempt++)
            {
                if (attempt > 20) throw new InvalidOperationException("frame codes: could not append after 20 tries (other writers?)");
                if (pos < 0) await FindEndAsync(ct);
                if (blocks + data.Count > SegmentMaxBlocks) { pos = -1; blocks = SegmentMaxBlocks; await store.SealAppendBlobAsync(Segment(paths, meanVersion, seg), await EndAsync(ct), ct); seg++; continue; }
                var name = Segment(paths, meanVersion, seg); var offs = new List<long>(); bool ok = true;
                foreach (var d in data)
                {
                    var r = await store.AppendBlockAsync(name, d, pos, ct); Appends++;
                    if (r.Outcome == AppendOutcome.Ok) { offs.Add(pos); pos = r.Length; blocks = r.Blocks; continue; }
                    // another writer moved the end, or the segment was sealed: start the whole entry again at the new end (parts already written stay as dead bytes)
                    if (r.Outcome == AppendOutcome.Sealed) seg++;
                    pos = -1; ok = false; break;
                }
                if (ok) return Pointer(seg, offs);
            }
        }

        async Task<long> EndAsync(CancellationToken ct) => (await store.GetAppendInfoAsync(Segment(paths, meanVersion, seg), ct))?.Length ?? 0;
    }

    /// <summary>
    /// The search-index stage's part: every upload of <paramref name="next"/> keeps the entry the head recorded for it (same id, same frames-file sha, same mean version), and up to <paramref name="max"/>
    /// of those without one get it now: one read of the frames (or retained) file the version lists, one append. <paramref name="pace"/> &gt; 0: at most that many uploads a minute.
    /// Returns how many uploads were filled; next.CodesMissing is set to how many still have none.
    /// </summary>
    public static async Task<int> FillAsync(IStore raw, Paths paths, SearchData next, int max, double pace, Action<string> say, CancellationToken ct, Writer? writer = null)
    {
        writer ??= new Writer(raw, paths, next.MeanVersion);
        int filled = 0, failed = 0; var gap = pace > 0 ? TimeSpan.FromSeconds(60.0 / pace) : TimeSpan.Zero; var sw = Stopwatch.StartNew();
        for (int i = 0; i < next.Uploads.Count && filled < max; i++)
        {
            var u = next.Uploads[i]; if (u.Codes != null) continue;
            var t0 = Stopwatch.GetTimestamp();
            try
            {
                var blob = u.File ?? paths.FramesFile(u.Id);
                var z = await FramesReader.OpenAsync(raw, blob, ct, whole: false) ?? throw new FileNotFoundException(blob);
                var bytes = await z.ReadAsync(0, checked((int)z.DataLength), ct);
                var sha = z.Sha ?? u.Sha;
                if (u.Sha.Length > 0 && sha != u.Sha) { say($"codes: {u.Id}: the frames file changed since the build (sha), left for the next build"); failed++; continue; }
                var parts = FromFile(u.Id, blob, u.Sha, next.MeanVersion, next.Mean, bytes);
                next.Uploads[i] = u with { Codes = await writer.AppendAsync(parts, ct) }; filled++;
                if (filled % 100 == 0) say($"  codes: {filled} uploads added, {sw.Elapsed.TotalMinutes:F1} min");
            }
            catch (Exception e) when (e is FileNotFoundException or InvalidDataException or StaleZipException) { say($"codes: {u.Id}: {e.Message} (left for the next build)"); failed++; }
            catch (NotSupportedException) { say("codes: this store has no append blobs (a local folder?): no frame codes added"); break; }
            if (gap > TimeSpan.Zero) { var left = gap - Stopwatch.GetElapsedTime(t0); if (left > TimeSpan.Zero) await Task.Delay(left, ct); }
        }
        next.CodesMissing = next.Uploads.Count(u => u.Codes == null);
        if (filled > 0 || next.CodesMissing > 0) say($"codes: {filled} uploads added to the log, {next.CodesMissing} without codes{(failed > 0 ? $" ({failed} failed)" : "")}");
        return filled;
    }
}

/// <summary>0.13.69: the codes of one index version, in memory: one entry per upload that has them (parts joined), each with its codes, vector offsets and times.</summary>
public sealed class FrameCodesView
{
    public required SearchData Index { get; init; }
    /// <summary>Per upload of the version (same index as Index.Uploads): its frames, or null when it has no codes.</summary>
    public required FrameCodesUpload?[] Uploads { get; init; }
    public long Frames { get; init; }
    public int Missing => Uploads.Count(u => u == null);

    public sealed record Near(int Upload, int Row, int Dist);

    /// <summary>0.13.71: the <paramref name="k"/> nearest frames of EACH query (the duplicate check's and the group step's many frames): the uploads are split among the threads, and each upload's codes
    /// are scanned for every query while they are in cache (read from memory once per call, not once per query). <paramref name="exclude"/>: an upload index left out (the subject itself).</summary>
    public List<Near>[] NearestMany(IReadOnlyList<ulong[]> qs, int k, int threads, int exclude = -1)
    {
        int Q = qs.Count; threads = Math.Max(1, threads); var live = Enumerable.Range(0, Uploads.Length).Where(i => Uploads[i] != null && i != exclude).ToArray();
        var result = new List<Near>[Q]; for (int i = 0; i < Q; i++) result[i] = [];
        if (k <= 0 || Q == 0 || live.Length == 0) return result;
        var parts = new List<Near>[threads][];
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, p =>
        {
            var mine = new List<Near>[Q]; var thr = new int[Q]; for (int i = 0; i < Q; i++) { mine[i] = new List<Near>(4 * k + 64); thr[i] = int.MaxValue; }
            var hist = new int[FrameCodes.CodeBytes * 8 + 1];
            for (int li = p; li < live.Length; li += threads)
            {
                int ui = live[li]; var c = Uploads[ui]!.Codes; int n = c.Length / FrameCodes.Words;
                for (int qi = 0; qi < Q; qi++)
                {
                    var q = qs[qi]; ulong q0 = q[0], q1 = q[1], q2 = q[2], q3 = q[3], q4 = q[4], q5 = q[5], q6 = q[6], q7 = q[7]; int t = thr[qi]; var m = mine[qi];
                    for (int r = 0, o = 0; r < n; r++, o += 8)
                    {
                        int d = BitOperations.PopCount(c[o] ^ q0) + BitOperations.PopCount(c[o + 1] ^ q1) + BitOperations.PopCount(c[o + 2] ^ q2) + BitOperations.PopCount(c[o + 3] ^ q3)
                              + BitOperations.PopCount(c[o + 4] ^ q4) + BitOperations.PopCount(c[o + 5] ^ q5) + BitOperations.PopCount(c[o + 6] ^ q6) + BitOperations.PopCount(c[o + 7] ^ q7);
                        if (d > t) continue;
                        m.Add(new Near(ui, r, d));
                        if (m.Count > 4 * k)
                        {
                            Array.Clear(hist); foreach (var h in m) hist[h.Dist]++;
                            int tt = 0, cum = 0; while (tt < hist.Length - 1 && cum + hist[tt] < k) { cum += hist[tt]; tt++; }
                            m.RemoveAll(h => h.Dist > tt); t = thr[qi] = tt;
                        }
                    }
                }
            }
            parts[p] = mine;
        });
        for (int qi = 0; qi < Q; qi++) result[qi] = parts.SelectMany(x => x[qi]).OrderBy(h => h.Dist).ThenBy(h => h.Upload).ThenBy(h => h.Row).Take(k).ToList();
        return result;
    }

    /// <summary>The <paramref name="k"/> frames nearest the query code (popcount distance), best first (ties: upload order, then row). Each of <paramref name="threads"/> parts keeps its candidates under a threshold that tightens as it goes.</summary>
    public List<Near> Nearest(ulong[] q, int k, int threads)
    {
        threads = Math.Max(1, threads); var live = Enumerable.Range(0, Uploads.Length).Where(i => Uploads[i] != null).ToArray();
        if (k <= 0 || live.Length == 0) return [];
        var parts = new List<Near>[threads];
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, p =>
        {
            var mine = new List<Near>(4 * k + 64); int thr = int.MaxValue; var hist = new int[FrameCodes.CodeBytes * 8 + 1];
            ulong q0 = q[0], q1 = q[1], q2 = q[2], q3 = q[3], q4 = q[4], q5 = q[5], q6 = q[6], q7 = q[7];
            for (int li = p; li < live.Length; li += threads)
            {
                int ui = live[li]; var c = Uploads[ui]!.Codes; int n = c.Length / FrameCodes.Words;
                for (int r = 0, o = 0; r < n; r++, o += 8)
                {
                    int d = BitOperations.PopCount(c[o] ^ q0) + BitOperations.PopCount(c[o + 1] ^ q1) + BitOperations.PopCount(c[o + 2] ^ q2) + BitOperations.PopCount(c[o + 3] ^ q3)
                          + BitOperations.PopCount(c[o + 4] ^ q4) + BitOperations.PopCount(c[o + 5] ^ q5) + BitOperations.PopCount(c[o + 6] ^ q6) + BitOperations.PopCount(c[o + 7] ^ q7);
                    if (d > thr) continue;
                    mine.Add(new Near(ui, r, d));
                    if (mine.Count > 4 * k)
                    {
                        Array.Clear(hist); foreach (var h in mine) hist[h.Dist]++;
                        int t = 0, cum = 0; while (t < hist.Length - 1 && cum + hist[t] < k) { cum += hist[t]; t++; }
                        mine.RemoveAll(h => h.Dist > t); thr = t;
                    }
                }
            }
            parts[p] = mine;
        });
        return parts.SelectMany(x => x).OrderBy(h => h.Dist).ThenBy(h => h.Upload).ThenBy(h => h.Row).Take(k).ToList();
    }
}

public sealed record FrameCodesUpload(ulong[] Codes, uint[] Offsets, float[] Times) { public int Count => Times.Length; }

/// <summary>
/// 0.13.69: a reader's local copy of the codes log (`&lt;root&gt;/m&lt;meanVersion&gt;/log-NNNN.bin`, complete parts only) and the view of the loaded version. A new version costs one ranged read per
/// segment with new bytes (whole new parts, up to 64 MB a read); nothing already here is read again.
/// </summary>
public sealed class FrameCodesCache(string root)
{
    static FrameCodesCache? shared;
    /// <summary>0.13.72: the process's one cache under the config dir (the check, the group step and the search share it).</summary>
    public static FrameCodesCache Shared => shared ??= new FrameCodesCache(Path.Combine(NodeConfig.ConfigDir(), "frame-codes"));
    readonly SemaphoreSlim gate = new(1, 1);
    FrameCodesView? view;
    readonly Dictionary<(int Mv, int Seg, long Off), FrameCodes.Part> parts = new();
    public int Reads { get; private set; }
    public long BytesRead { get; private set; }

    string LocalFile(int mv, int seg) => Path.Combine(root, $"m{mv}", $"log-{seg:D4}.bin");

    /// <summary>The view of <paramref name="idx"/>: from memory when it is the one already built, else the log is brought up to date and the view built (parts already decoded are kept).</summary>
    public async Task<FrameCodesView> ViewAsync(IStore store, Paths paths, SearchData idx, CancellationToken ct)
    {
        var v = Volatile.Read(ref view); if (v != null && ReferenceEquals(v.Index, idx)) return v;
        await gate.WaitAsync(ct);
        try
        {
            if (view != null && ReferenceEquals(view.Index, idx)) return view;
            int mv = idx.MeanVersion;
            var ptrs = idx.Uploads.Select(u => FrameCodes.ParsePointer(u.Codes)).ToArray();
            // how far each segment must be here: past the last part a pointer names (its length is read from the local copy as we go)
            var need = new Dictionary<int, long>();
            foreach (var p in ptrs) if (p is { } pp) need[pp.Segment] = Math.Max(need.GetValueOrDefault(pp.Segment, -1), pp.Offsets.Max());
            foreach (var (seg, last) in need) await SyncAsync(store, paths, mv, seg, last, ct);
            var ups = new FrameCodesUpload?[idx.Uploads.Count]; long frames = 0; bool dropped = false; var keep = new HashSet<(int, int, long)>();
            for (int i = 0; i < ups.Length; i++)
            {
                if (ptrs[i] is not { } p) continue;
                var got = new List<FrameCodes.Part>();
                var u = idx.Uploads[i]; bool bad = false; if (dropped) continue;
                foreach (var off in p.Offsets)
                {
                    var key = (mv, p.Segment, off);
                    if (!parts.TryGetValue(key, out var part))
                    {
                        try { part = ReadPart(mv, p.Segment, off); }
                        catch (InvalidDataException e)
                        {   // the local copy is not this store's log (another store, a log written again): it goes, the next view fetches the log anew; this upload counts as missing now
                            Log.Info($"frame codes: {u.Id}: {e.Message}; the local copy of log {p.Segment} is dropped"); try { File.Delete(LocalFile(mv, p.Segment)); } catch (IOException) { }
                            parts.Clear(); bad = dropped = true; break;
                        }
                        parts[key] = part;
                    }
                    got.Add(part); keep.Add(key);
                }
                if (bad) continue;
                if (got.Any(x => x.Id != u.Id || x.Sha != u.Sha)) { Log.Info($"frame codes: {u.Id}: the entry at {u.Codes} is for {got[0].Id} ({got[0].Sha[..Math.Min(8, got[0].Sha.Length)]}), not this upload: treated as missing"); continue; }
                got.Sort((a, b) => a.First.CompareTo(b.First));
                ups[i] = got.Count == 1 ? new FrameCodesUpload(got[0].Codes, got[0].Offsets, got[0].Times) : new FrameCodesUpload([.. got.SelectMany(x => x.Codes)], [.. got.SelectMany(x => x.Offsets)], [.. got.SelectMany(x => x.Times)]);
                frames += ups[i]!.Count;
            }
            foreach (var k in parts.Keys.Where(k => !keep.Contains(k)).ToList()) parts.Remove(k);
            var nv = new FrameCodesView { Index = idx, Uploads = ups, Frames = frames };
            if (!dropped) Volatile.Write(ref view, nv);   // a view made while the local copy was dropped is not kept: the next call fetches the log again
            return nv;
        }
        finally { gate.Release(); }
    }

    FrameCodes.Part ReadPart(int mv, int seg, long off)
    {
        using var fh = File.OpenHandle(LocalFile(mv, seg));
        var head = new byte[FrameCodes.Fixed]; if (RandomAccess.Read(fh, head, off) != head.Length) throw new InvalidDataException($"frame codes log {seg}: nothing at {off}");
        var len = FrameCodes.LengthAt(head); var b = new byte[len];
        if (RandomAccess.Read(fh, b, off) != len) throw new InvalidDataException($"frame codes log {seg}: the part at {off} is cut short");
        return FrameCodes.Decode(b);
    }

    /// <summary>0.13.71: ranges of this size are read <see cref="Streams"/> at a time. Measured 2026-11-26 on the hexad host (West US 2): one stream ~21 MB/s (the 961 MB log in 54 s), 16 parallel 16 MB ranges ~250 MB/s (curl); with 32 streams the cold load of the log is 6.4 s, from the local copy 1.3 s.</summary>
    public int RangeBytes { get; init; } = 16 << 20;
    public int Streams { get; init; } = 32;

    /// <summary>Reads segment <paramref name="seg"/> from the end of the local copy until the part at <paramref name="lastOffset"/> is whole here: one HEAD for the length, then windows of
    /// <see cref="Streams"/> parallel ranged reads. Keeps complete parts only (a part cut at the end of a window waits for the next one).</summary>
    async Task SyncAsync(IStore store, Paths paths, int mv, int seg, long lastOffset, CancellationToken ct)
    {
        var name = FrameCodes.Segment(paths, mv, seg); var path = LocalFile(mv, seg); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long have = File.Exists(path) ? new FileInfo(path).Length : 0;
        bool Whole() { if (have <= lastOffset) return false; using var fh = File.OpenHandle(path); var h = new byte[FrameCodes.Fixed]; return RandomAccess.Read(fh, h, lastOffset) == h.Length && lastOffset + FrameCodes.LengthAt(h) <= have; }
        if (Whole()) return;
        var info = await store.GetAppendInfoAsync(name, ct) ?? throw new FileNotFoundException(name); Reads++;
        if (info.Length <= lastOffset) throw new InvalidDataException($"frame codes log {seg}: the part at {lastOffset} is past the end ({info.Length})");
        var carry = Array.Empty<byte>(); long at = have;
        while (at < info.Length && !Whole())
        {
            var ranges = new List<(long Off, int Len)>();
            for (long o = at; o < info.Length && ranges.Count < Streams; o += RangeBytes) ranges.Add((o, (int)Math.Min(RangeBytes, info.Length - o)));
            var bufs = new byte[ranges.Count][];
            await Parallel.ForEachAsync(Enumerable.Range(0, ranges.Count), new ParallelOptions { MaxDegreeOfParallelism = Streams, CancellationToken = ct }, async (i, t) =>
            {
                var r = await store.GetRangeAsync(name, ranges[i].Off, ranges[i].Len, null, t);
                bufs[i] = r.Data is { } d0 && d0.Length == ranges[i].Len ? d0 : throw new InvalidDataException($"{name}: asked for {ranges[i].Len} bytes at {ranges[i].Off}, got {r.Data?.Length.ToString() ?? "none"}");
            });
            long got = ranges.Sum(x => (long)x.Len); Reads += ranges.Count; BytesRead += got; at += got;
            // whole parts go to the local copy; a part cut at the end of the window is carried into the next
            var d = new byte[carry.Length + got]; carry.CopyTo(d, 0); long w = carry.Length; foreach (var b in bufs) { b.CopyTo(d, w); w += b.Length; }
            int used = 0; while (true) { int len = FrameCodes.LengthAt(d.AsSpan(used)); if (len == 0 || used + len > d.Length) break; used += len; }
            if (used > 0) { using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read); fs.Write(d, 0, used); have += used; }
            carry = d[used..];
        }
        if (!Whole()) throw new InvalidDataException($"frame codes log {seg}: the part at {lastOffset} is not whole at the end ({info.Length})");
    }
}

/// <summary>0.13.69: the screenshot query on frame codes: scan every frame's code, the best <paramref name="k"/> read exactly (merged ranges of the frames files), cosine.</summary>
public static class FrameCodeSearch
{
    public static async Task<(List<Hit> Hits, QueryTimings Timings)> QueryAsync(IStore raw, Paths paths, FrameCodesView view, float[] query, int k, int top, int threads, CancellationToken ct, bool ranged = true, int scanThreads = 1)
    {
        var idx = view.Index; var q = (float[])query.Clone(); Vec.Normalize(q);
        var sw = Stopwatch.StartNew();
        var near = view.Nearest(FrameCodes.Code(q, idx.Mean), k, scanThreads);
        double scanMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
        // an upload stands where its nearest frame stands
        var byUpload = near.GroupBy(h => h.Upload).Select((g, i) => (Upload: g.Key, Rows: g.Select(h => h.Row).ToList(), Rank: i + 1)).ToList();
        long bytes = 0; int requests = 0; var scored = new List<Hit>[byUpload.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, byUpload.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(32, threads * 4)), CancellationToken = ct }, async (g, t) =>
        {
            var (ui, rows, rank) = byUpload[g]; var up = idx.Uploads[ui]; var fu = view.Uploads[ui]!;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var z = await FramesReader.ForAsync(raw, paths, up, attempt, t, whole: !ranged);
                    var want = rows.Select(r => (Row: r, Off: (long)fu.Offsets[r])).OrderBy(x => x.Off).ToList();
                    var res = new List<Hit>(); var vec = new float[CentersFile.Dim];
                    for (int i = 0; i < want.Count;)
                    {
                        int j = i; long start = want[i].Off, end = start + FrameCodes.VectorBytes;
                        while (j + 1 < want.Count && want[j + 1].Off - end <= CenterSearch.MergeGap && want[j + 1].Off + FrameCodes.VectorBytes - start <= CenterSearch.MaxMerged) { j++; end = want[j].Off + FrameCodes.VectorBytes; }
                        var buf = await z.ReadAsync(start, (int)(end - start), t);
                        for (int m = i; m <= j; m++)
                        {
                            Vec.FromHalf(buf.AsSpan((int)(want[m].Off - start), FrameCodes.VectorBytes), vec);
                            res.Add(new Hit(up.Id, fu.Times[want[m].Row], want[m].Row, Vec.Dot(q, vec), -1, rank, rows.Count));
                        }
                        i = j + 1;
                    }
                    Interlocked.Add(ref bytes, z.BytesRead); Interlocked.Add(ref requests, z.Requests);
                    scored[g] = res; return;
                }
                catch (StaleZipException) when (attempt == 0) { }
            }
        });
        double readMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
        var best = scored.Where(x => x != null).SelectMany(x => x).OrderByDescending(h => h.Score).ThenBy(h => h.Upload, StringComparer.Ordinal).ThenBy(h => h.Row).Take(top).ToList();
        return (best, new QueryTimings(scanMs, readMs, sw.Elapsed.TotalMilliseconds, byUpload.Count, requests, bytes, byUpload.Count));
    }
}
