using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IndexNode;

/// <summary>The columnar layout of centers/centers.bin inside centers.zip (all little-endian):
/// header (2144 bytes): 0 magic "IXCT" | 4 u32 version (1) | 8 u32 k | 12 u32 dim (512) | 16 u32 frames | 20 u32 build | 24 8 reserved | 32 sscd input hash (SHA-256 of the sscd f16 rows, 32 bytes) | 64 upload vector sum (512 f32) |
/// 2112 u64 centersOff | 2120 u64 tableOff | 2128 u64 membersOff | 2136 u64 totalLength.
/// Then the k center vectors (f16, unit length), the center table (k x { u64 offset, u32 length, u32 count }; offsets from the start of centers.bin), then one member block per center, back to back:
/// count x 512 f16 vectors, count x f32 times, count x i32 original row numbers. No bit codes (they depend on the corpus mean).</summary>
public static class CentersFile
{
    public const int Dim = 512, Version = 1, Build = 1, HeaderSize = 2144, TableEntry = 16, SumOff = 64, HashOff = 32;
    public const string Member = "centers/centers.bin", ManifestMember = "centers/manifest.json";
    public static readonly byte[] Magic = "IXCT"u8.ToArray();

    public sealed record Header(int Version, int K, int Dim, int Frames, int Build, byte[] InputHash, float[] Sum, long CentersOff, long TableOff, long MembersOff, long TotalLength);
    public readonly record struct Block(long Offset, int Length, int Count);
    public sealed record Members(byte[] Vectors, float[] Times, int[] Rows) { public int Count => Times.Length; }

    public static int BlockLength(int count) => count * (Dim * 2 + 4 + 4);

    public static byte[] HashOf(ReadOnlySpan<byte> sscdF16Rows) => SHA256.HashData(sscdF16Rows);

    /// <summary>Builds centers.bin. <paramref name="f16"/> holds the vectors of the frames fed to k-means (row i = <paramref name="rows"/>[i], time <paramref name="times"/>[i]).</summary>
    public static byte[] Write(KMeansResult r, ReadOnlySpan<byte> f16, IReadOnlyList<float> times, IReadOnlyList<int> rows, byte[] inputHash, float[] sum)
    {
        int n = times.Count, k = r.K; var counts = r.Sizes;
        long centersOff = HeaderSize, tableOff = centersOff + (long)k * Dim * 2, membersOff = tableOff + (long)k * TableEntry;
        long total = membersOff; for (int j = 0; j < k; j++) total += BlockLength(counts[j]);
        var buf = new byte[total]; var s = buf.AsSpan();
        Magic.CopyTo(s); BinaryPrimitives.WriteUInt32LittleEndian(s[4..], Version); BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)k); BinaryPrimitives.WriteUInt32LittleEndian(s[12..], Dim);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)n); BinaryPrimitives.WriteUInt32LittleEndian(s[20..], Build);
        inputHash.AsSpan(0, 32).CopyTo(s[HashOff..]);
        for (int i = 0; i < Dim; i++) BinaryPrimitives.WriteSingleLittleEndian(s[(SumOff + i * 4)..], sum[i]);
        BinaryPrimitives.WriteInt64LittleEndian(s[2112..], centersOff); BinaryPrimitives.WriteInt64LittleEndian(s[2120..], tableOff); BinaryPrimitives.WriteInt64LittleEndian(s[2128..], membersOff); BinaryPrimitives.WriteInt64LittleEndian(s[2136..], total);
        for (int i = 0; i < k * Dim; i++) BinaryPrimitives.WriteHalfLittleEndian(s[(int)(centersOff + i * 2L)..], (Half)r.Centers[i]);
        var start = new long[k]; var fill = new int[k]; long off = membersOff;
        for (int j = 0; j < k; j++)
        {
            start[j] = off; int len = BlockLength(counts[j]);
            var t = s[(int)(tableOff + (long)j * TableEntry)..];
            BinaryPrimitives.WriteInt64LittleEndian(t, off); BinaryPrimitives.WriteInt32LittleEndian(t[8..], len); BinaryPrimitives.WriteInt32LittleEndian(t[12..], counts[j]);
            off += len;
        }
        for (int i = 0; i < n; i++)   // rows in ascending order inside each block
        {
            int j = r.Assign[i], m = fill[j]++, cnt = counts[j];
            f16.Slice(i * Dim * 2, Dim * 2).CopyTo(s[(int)(start[j] + (long)m * Dim * 2)..]);
            BinaryPrimitives.WriteSingleLittleEndian(s[(int)(start[j] + (long)cnt * Dim * 2 + m * 4L)..], times[i]);
            BinaryPrimitives.WriteInt32LittleEndian(s[(int)(start[j] + (long)cnt * (Dim * 2 + 4) + m * 4L)..], rows[i]);
        }
        return buf;
    }

    public static Header ParseHeader(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderSize || !b[..4].SequenceEqual(Magic)) throw new InvalidDataException("not a centers file (bad magic)");
        int ver = BinaryPrimitives.ReadInt32LittleEndian(b[4..]);
        if (ver != Version) throw new InvalidDataException($"centers file version {ver} (this build reads {Version})");
        int dim = BinaryPrimitives.ReadInt32LittleEndian(b[12..]); if (dim != Dim) throw new InvalidDataException($"centers dim {dim}");
        var sum = new float[Dim]; for (int i = 0; i < Dim; i++) sum[i] = BinaryPrimitives.ReadSingleLittleEndian(b[(SumOff + i * 4)..]);
        return new Header(ver, BinaryPrimitives.ReadInt32LittleEndian(b[8..]), dim, BinaryPrimitives.ReadInt32LittleEndian(b[16..]), BinaryPrimitives.ReadInt32LittleEndian(b[20..]), b.Slice(HashOff, 32).ToArray(), sum,
            BinaryPrimitives.ReadInt64LittleEndian(b[2112..]), BinaryPrimitives.ReadInt64LittleEndian(b[2120..]), BinaryPrimitives.ReadInt64LittleEndian(b[2128..]), BinaryPrimitives.ReadInt64LittleEndian(b[2136..]));
    }

    public static Block[] ParseTable(ReadOnlySpan<byte> t, int k)
    {
        var r = new Block[k];
        for (int j = 0; j < k; j++) r[j] = new Block(BinaryPrimitives.ReadInt64LittleEndian(t[(j * TableEntry)..]), BinaryPrimitives.ReadInt32LittleEndian(t[(j * TableEntry + 8)..]), BinaryPrimitives.ReadInt32LittleEndian(t[(j * TableEntry + 12)..]));
        return r;
    }

    /// <summary>The center vectors as floats (k x 512).</summary>
    public static float[] ParseCenters(ReadOnlySpan<byte> f16, int k) { var c = new float[k * Dim]; Vec.FromHalf(f16, c); return c; }

    public static Members ParseBlock(ReadOnlySpan<byte> blk, int count)
    {
        if (blk.Length != BlockLength(count)) throw new InvalidDataException($"member block of {blk.Length} bytes for {count} members");
        var v = blk[..(count * Dim * 2)].ToArray(); var t = new float[count]; var rows = new int[count];
        for (int i = 0; i < count; i++) { t[i] = BinaryPrimitives.ReadSingleLittleEndian(blk[(count * Dim * 2 + i * 4)..]); rows[i] = BinaryPrimitives.ReadInt32LittleEndian(blk[(count * (Dim * 2 + 4) + i * 4)..]); }
        return new Members(v, t, rows);
    }

    public static byte[] ManifestJson(Header h, double seconds, string? sscdZipSha) => JsonSerializerBytes(new Dictionary<string, object?>
    {
        ["v"] = 1, ["step"] = "centers", ["format"] = "IXCT", ["build"] = h.Build, ["dim"] = h.Dim, ["k"] = h.K, ["frames"] = h.Frames, ["bytes"] = h.TotalLength, ["iterations"] = 10, ["init"] = "kmeans++", ["seed"] = "sha256(uploadId)[0..4]",
        ["inputs"] = new { sscdVectorsSha256 = Convert.ToHexString(h.InputHash).ToLowerInvariant(), sscdZipSha256 = sscdZipSha },
        ["filter"] = "none (TODO: drop featureless frames)", ["seconds"] = Math.Round(seconds, 2),
    });
    static byte[] JsonSerializerBytes(object o) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(o);
}

/// <summary>A centers.zip read by ranges: only what a query needs is fetched (the zip's central directory, then the header, centers, table or a member block of centers/centers.bin).</summary>
public sealed class CentersZip
{
    public required IStore Store { get; init; }
    public required string Blob { get; init; }
    public required string? ETag { get; set; }
    /// <summary>idx_sha256 of the content opened (empty when unknown): a changed ETag with the same length and sha is a ghost transition, not a rewrite.</summary>
    public string? Sha { get; init; }
    public required long ZipSize { get; init; }
    /// <summary>Where centers/centers.bin starts inside the zip (past its local header).</summary>
    public required long DataStart { get; init; }
    public required long DataLength { get; init; }

    public static async Task<CentersZip?> OpenAsync(IStore store, string blob, CancellationToken ct)
    {
        // a Nexis ghost HEADs as 0 bytes (the data sits in uncommitted blocks): ZipOpen hydrates it and HEADs again; a 0-byte blob that is no ghost is "not a zip"
        var h = await ZipOpen.HeadReadableAsync(store, blob, ct); if (h == null) return null;
        var src = new BlobZipSource(store, blob, h.Size, h.ETag, h.Metadata.GetValueOrDefault("idx_sha256"));
        var es = await ZipFormat.ReadDirectoryAsync(src, ct, 2048);
        var e = es.FirstOrDefault(x => x.Name == CentersFile.Member) ?? throw new InvalidDataException($"{blob} has no {CentersFile.Member}");
        if (e.Method != 0) throw new NotSupportedException("centers.bin must be stored, not deflated");
        var lh = await src.ReadAsync(e.LocalOffset, 30 + 256, ct);
        if (lh.Length < 30 || BitConverter.ToUInt32(lh, 0) != 0x04034b50) throw new InvalidDataException("bad local header");
        return new CentersZip { Store = store, Blob = blob, ETag = h.ETag, Sha = h.Metadata.GetValueOrDefault("idx_sha256"), ZipSize = h.Size, DataStart = e.LocalOffset + 30 + BitConverter.ToUInt16(lh, 26) + BitConverter.ToUInt16(lh, 28), DataLength = e.Size };
    }

    /// <summary>A zip whose place is already known (the search index records ETag, size and where centers.bin starts): no HEAD, no directory read.</summary>
    public static CentersZip FromIndex(IStore store, string blob, SearchUpload u) => new() { Store = store, Blob = blob, ETag = u.ETag.Length == 0 ? null : u.ETag, Sha = u.Sha, ZipSize = u.ZipSize, DataStart = u.DataStart, DataLength = u.DataLength };

    public int Requests; public long BytesRead;

    /// <summary>0.13.8: at most this many ranged reads of centers.zip members are in flight in the whole process (the bound is on requests, not threads: a read is a round trip, not CPU).</summary>
    public const int MaxInFlight = 64;
    static readonly SemaphoreSlim InFlightGate = new(MaxInFlight, MaxInFlight);
    static int inFlight, peakInFlight;
    public static int InFlight => Volatile.Read(ref inFlight);
    /// <summary>The highest number of reads in flight since the last call (resets the peak).</summary>
    public static int TakePeakInFlight() => Interlocked.Exchange(ref peakInFlight, Volatile.Read(ref inFlight));

    /// <summary>The member blocks of the given centers: one table read, then ranges merged when the blocks are close (gap at most <paramref name="gap"/> bytes, a merged range at most <paramref name="maxMerged"/>).</summary>
    public async Task<List<(int Center, CentersFile.Members Members)>> ReadBlocksAsync(IReadOnlyList<int> centers, int gap, int maxMerged, CancellationToken ct)
    {
        var h = await ReadHeaderAsync(ct);
        var table = await ReadTableAsync(h, ct); int k = h.K;
        var all = centers.Where(c => c >= 0 && c < k).Distinct().Select(c => (C: c, B: table[c])).ToList();
        var result = new List<(int, CentersFile.Members)>();
        // an empty cluster has a zero-length block: no request for it (a ranged read of 0 bytes is "bytes=N--1" on the wire and throws)
        foreach (var x in all.Where(x => x.B.Length == 0 || x.B.Count == 0)) result.Add((x.C, CentersFile.ParseBlock([], 0)));
        var want = all.Where(x => x.B.Length > 0 && x.B.Count > 0).OrderBy(x => x.B.Offset).ToList();

        // 0.13.8: the merged ranges of one zip go out together (bounded by the process-wide in-flight cap in ReadAsync), not one after the other: on a real store each is a round trip
        var spans = new List<(int I, int J, long Start, long End)>();
        for (int i = 0; i < want.Count;)
        {
            int j = i; long start = want[i].B.Offset, end = start + want[i].B.Length;
            while (j + 1 < want.Count && want[j + 1].B.Offset - end <= gap && want[j + 1].B.Offset + want[j + 1].B.Length - start <= maxMerged) { j++; end = want[j].B.Offset + want[j].B.Length; }
            spans.Add((i, j, start, end)); i = j + 1;
        }
        var bufs = await Task.WhenAll(spans.Select(sp => ReadAsync(sp.Start, (int)(sp.End - sp.Start), ct)));
        for (int si = 0; si < spans.Count; si++)
            for (int m = spans[si].I; m <= spans[si].J; m++) result.Add((want[m].C, CentersFile.ParseBlock(bufs[si].AsSpan((int)(want[m].B.Offset - spans[si].Start), want[m].B.Length), want[m].B.Count)));
        return result;
    }

    /// <summary>Bytes off..off+len of centers.bin (one HTTP range request), pinned to the zip's ETag.</summary>
    public async Task<byte[]> ReadAsync(long off, int len, CancellationToken ct)
    {
        Interlocked.Increment(ref Requests); Interlocked.Add(ref BytesRead, len);
        await InFlightGate.WaitAsync(ct); int now = Interlocked.Increment(ref inFlight); int pk; while (now > (pk = peakInFlight)) Interlocked.CompareExchange(ref peakInFlight, now, pk);
        RangeRead r; var pin = new BlobPin(Blob, ETag, ZipSize, Sha);
        try { r = await GhostSafeRead.RangeAsync(Store, pin, DataStart + off, len, ct); ETag = pin.ETag; }
        finally { Interlocked.Decrement(ref inFlight); InFlightGate.Release(); }
        if (r.Mismatch) throw new StaleZipException(Blob);
        var d = r.Data ?? throw new FileNotFoundException(Blob + " is gone");
        if (d.Length != len) throw new InvalidDataException($"{Blob}: asked for {len} bytes at {off}, got {d.Length}");
        return d;
    }

    public async Task<CentersFile.Header> ReadHeaderAsync(CancellationToken ct) => CentersFile.ParseHeader(await ReadAsync(0, CentersFile.HeaderSize, ct));
    public async Task<CentersFile.Block[]> ReadTableAsync(CentersFile.Header h, CancellationToken ct) => CentersFile.ParseTable(await ReadAsync(h.TableOff, h.K * CentersFile.TableEntry, ct), h.K);
    public async Task<CentersFile.Members> ReadBlockAsync(CentersFile.Block b, CancellationToken ct) => CentersFile.ParseBlock(await ReadAsync(b.Offset, b.Length, ct), b.Count);
}
