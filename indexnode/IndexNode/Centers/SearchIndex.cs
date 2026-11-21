using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IndexNode;

/// <summary>One upload in the global search table: where its centers.zip is and which rows of bits.bin/refs.bin are its centers.</summary>
public sealed record SearchUpload(string Id, string Sha, string ETag, long ZipSize, long DataStart, long DataLength, int Frames, int K, int RowStart, string? SpeechLanguage = null, bool? Dubbed = null, string? Group = null);

/// <summary>The global search table, as published under index/search/vN/: mean.f32 (512 f32), bits.bin (64-byte sign codes of center - mean), refs.bin (per row: u32 upload index, u32 center index), uploads.yaml.</summary>
public sealed class SearchData
{
    public const int CodeBytes = 64;
    public int Version { get; set; }
    public int MeanVersion { get; set; }
    public long MeanFrames { get; set; }
    public required float[] Mean { get; set; }
    public required byte[] Bits { get; set; }
    public required int[] RefUpload { get; set; }
    public required int[] RefCenter { get; set; }
    public required List<SearchUpload> Uploads { get; set; }
    public int Count => RefUpload.Length;
    /// <summary>0.13.1: when this version's build started (uploads.yaml builtAt); the pre-check searches centers finished after it by brute force. Null on an older version (then the file's creation time, else unknown).</summary>
    public DateTimeOffset? BuiltAt { get; set; }
    /// <summary>0.13.1: catalog.json.gz of this version (duration, language, AI per upload); null on a version published before it.</summary>
    public Catalog? Catalog { get; set; }

    /// <summary>Bit d of a code (byte d/8, bit d%8, least significant first) is 1 when center[d] - mean[d] &gt; 0.</summary>
    public static void Code(ReadOnlySpan<float> center, ReadOnlySpan<float> mean, Span<byte> code)
    {
        code.Clear();
        for (int d = 0; d < CentersFile.Dim; d++) if (center[d] - mean[d] > 0) code[d >> 3] |= (byte)(1 << (d & 7));
    }

    // ---------------------------------------------------------------- scan
    /// <summary>The 64 x 256 lookup table of the asymmetric score: entry [b, v] = sum over the 8 bits j of v of (+q[8b+j] if the bit is set else -q[8b+j]). The score of a code is the sum of 64 lookups.</summary>
    public static float[] Lut(ReadOnlySpan<float> q)
    {
        var lut = new float[64 * 256];
        for (int b = 0; b < 64; b++) for (int v = 0; v < 256; v++)
        {
            float s = 0; for (int j = 0; j < 8; j++) s += (v >> j & 1) != 0 ? q[b * 8 + j] : -q[b * 8 + j];
            lut[b * 256 + v] = s;
        }
        return lut;
    }

    public static float ScoreCode(float[] lut, ReadOnlySpan<byte> code) { float s = 0; for (int b = 0; b < 64; b++) s += lut[b * 256 + code[b]]; return s; }

    /// <summary>The rows with the highest asymmetric score for <paramref name="q"/> (already minus the mean), best first. Parallel over row ranges, each keeping its own top C.</summary>
    public int[] Scan(float[] q, int topC, int threads, int excludeUpload = -1)
    {
        var lut = Lut(q); int n = Count; topC = Math.Min(topC, n);
        if (topC <= 0) return [];
        int parts = Math.Max(1, Math.Min(threads, n / 4096 + 1)); var local = new List<(float S, int I)>[parts];
        Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, p =>
        {
            int lo = (int)((long)n * p / parts), hi = (int)((long)n * (p + 1) / parts);
            var heap = new PriorityQueue<int, float>();   // min-heap on score: the worst kept row is on top
            for (int i = lo; i < hi; i++)
            {
                if (RefUpload[i] == excludeUpload) continue;   // --exclude-self: the queried upload's own centers
                float s = ScoreCode(lut, Bits.AsSpan(i * CodeBytes, CodeBytes));
                if (heap.Count < topC) heap.Enqueue(i, s);
                else if (heap.TryPeek(out _, out var worst) && s > worst) { heap.Dequeue(); heap.Enqueue(i, s); }
            }
            var l = new List<(float, int)>(heap.Count); while (heap.TryDequeue(out var i, out var sc)) l.Add((sc, i)); local[p] = l;
        });
        return local.SelectMany(l => l).OrderByDescending(x => x.S).ThenBy(x => x.I).Take(topC).Select(x => x.I).ToArray();
    }

    // ---------------------------------------------------------------- files
    public static string Dir(Paths p, int version) => $"{p.Prefix}search/v{version}/";
    public static string HeadName(Paths p) => $"{p.Prefix}search/head.yaml";

    public static byte[] MeanBytes(float[] mean) { var b = new byte[mean.Length * 4]; for (int i = 0; i < mean.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), mean[i]); return b; }
    public static float[] ParseMean(byte[] b) { var m = new float[b.Length / 4]; for (int i = 0; i < m.Length; i++) m[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(i * 4)); return m; }
    public byte[] RefsBytes() { var b = new byte[Count * 8]; for (int i = 0; i < Count; i++) { BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 8), RefUpload[i]); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 8 + 4), RefCenter[i]); } return b; }

    static string Q(string s) => JsonSerializer.Serialize(s);
    public string UploadsYaml()
    {
        var sb = new StringBuilder();
        sb.Append("version: ").Append(Version).Append('\n').Append("meanVersion: ").Append(MeanVersion).Append('\n').Append("meanFrames: ").Append(MeanFrames).Append('\n')
          .Append(BuiltAt == null ? "" : "builtAt: " + Protocol.Utc(BuiltAt.Value) + "\n").Append("dim: 512\ncodeBytes: 64\ncenters: ").Append(Count).Append("\nuploads:\n");
        foreach (var u in Uploads)
            sb.Append("  - id: ").Append(Q(u.Id)).Append("\n    sha: ").Append(Q(u.Sha)).Append("\n    etag: ").Append(Q(u.ETag)).Append("\n    zipSize: ").Append(u.ZipSize).Append("\n    dataStart: ").Append(u.DataStart)
              .Append("\n    dataLength: ").Append(u.DataLength).Append("\n    frames: ").Append(u.Frames).Append("\n    k: ").Append(u.K).Append("\n    rowStart: ").Append(u.RowStart).Append('\n')
              .Append(u.SpeechLanguage == null ? "" : "    speechLanguage: " + Q(u.SpeechLanguage) + "\n").Append(u.Dubbed == null ? "" : "    dubbed: " + (u.Dubbed.Value ? "true" : "false") + "\n").Append(u.Group == null ? "" : "    group: " + Q(u.Group) + "\n");
        return sb.ToString();
    }

    public static DateTimeOffset? ParseBuiltAt(string yaml)
    {
        foreach (var l in yaml.Split('\n')) { if (l.StartsWith("uploads:")) break; if (l.StartsWith("builtAt:") && DateTimeOffset.TryParse(l[8..].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) return t; }
        return null;
    }

    static string Val(string v) { v = v.Trim(); return v.StartsWith('"') ? JsonSerializer.Deserialize<string>(v)! : v; }
    public static (int Version, int MeanVersion, long MeanFrames, List<SearchUpload> Uploads) ParseUploadsYaml(string yaml)
    {
        int version = 0, meanVersion = 0; long meanFrames = 0; var ups = new List<SearchUpload>(); Dictionary<string, string>? cur = null;
        void Flush() { if (cur == null) return; ups.Add(new SearchUpload(cur["id"], cur["sha"], cur["etag"], long.Parse(cur["zipSize"]), long.Parse(cur["dataStart"]), long.Parse(cur["dataLength"]), int.Parse(cur["frames"]), int.Parse(cur["k"]), int.Parse(cur["rowStart"]), cur.GetValueOrDefault("speechLanguage") is { Length: > 0 } sl && sl != "null" ? sl : null, cur.TryGetValue("dubbed", out var db) && db is "true" or "false" ? db == "true" : null, cur.GetValueOrDefault("group") is { Length: > 0 } gr && gr != "null" ? gr : null)); cur = null; }
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r'); if (line.Length == 0 || line.StartsWith('#')) continue;
            var t = line.TrimStart(); bool item = t.StartsWith("- ");
            if (item) { Flush(); cur = []; t = t[2..]; }
            int c = t.IndexOf(':'); if (c < 0) continue; var key = t[..c].Trim(); var val = t[(c + 1)..];
            if (cur != null) { cur[key] = Val(val); continue; }
            if (key == "version") version = int.Parse(val); else if (key == "meanVersion") meanVersion = int.Parse(val); else if (key == "meanFrames") meanFrames = long.Parse(val);
        }
        Flush();
        return (version, meanVersion, meanFrames, ups);
    }

    public static byte[] HeadYaml(int version, string dir) => Encoding.UTF8.GetBytes($"version: {version}\npath: {dir}\n");
    public static int ParseHead(byte[] b) { foreach (var l in Encoding.UTF8.GetString(b).Split('\n')) if (l.StartsWith("version:")) return int.Parse(l[8..].Trim(), CultureInfo.InvariantCulture); throw new InvalidDataException("head.yaml has no version"); }

    /// <summary>0.13.1: after head.yaml moved to vN, deletes index/search/vK/ (every file) for K &lt; N - keep. Versions are derived data; nothing else is touched. Returns the versions removed.</summary>
    public static async Task<List<int>> PruneAsync(IStore store, Paths paths, int keep, CancellationToken ct)
    {
        var removed = new List<int>();
        if (keep < 0 || await store.GetAsync(HeadName(paths), ct) is not { } h) return removed;
        int n = ParseHead(h.Data); var root = paths.Prefix + "search/";
        var files = (await store.ListAsync(root + "v", false, false, ct)).Select(b => (b.Name, V: VersionOf(root, b.Name))).Where(x => x.V is { } v && v < n - keep).ToList();
        foreach (var g in files.GroupBy(x => x.V!.Value).OrderBy(g => g.Key))
        {
            foreach (var f in g) await store.DeleteAsync(f.Name, ct);
            removed.Add(g.Key);
        }
        return removed;
    }
    static int? VersionOf(string root, string name)
    {
        if (!name.StartsWith(root + "v", StringComparison.Ordinal)) return null;
        var rest = name[(root.Length + 1)..]; int slash = rest.IndexOf('/');
        return slash > 0 && int.TryParse(rest[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Loads the version <paramref name="version"/> (null: the head's). Null when there is no head yet. For the head's version a missing file (the version was pruned after head.yaml was read) re-reads head.yaml and tries once more.</summary>
    public static async Task<SearchData?> LoadAsync(IStore store, Paths paths, int? version, CancellationToken ct)
    {
        if (version != null) return await LoadVersionAsync(store, paths, version, ct);
        if (await store.GetAsync(HeadName(paths), ct) is not { } h) return null;
        int v0 = ParseHead(h.Data);
        try { return await LoadVersionAsync(store, paths, v0, ct); }
        catch (FileNotFoundException)
        {
            if (await store.GetAsync(HeadName(paths), ct) is { } h2 && ParseHead(h2.Data) != v0) return await LoadVersionAsync(store, paths, ParseHead(h2.Data), ct);
            throw;
        }
    }

    static async Task<SearchData?> LoadVersionAsync(IStore store, Paths paths, int? version, CancellationToken ct)
    {
        if (version == null) { if (await store.GetAsync(HeadName(paths), ct) is not { } h) return null; version = ParseHead(h.Data); }
        var dir = Dir(paths, version.Value);
        async Task<byte[]> Get(string f) => (await store.GetAsync(dir + f, ct))?.Data ?? throw new FileNotFoundException(dir + f);
        var yaml = Encoding.UTF8.GetString(await Get("uploads.yaml"));
        var (v, mv, mf, ups) = ParseUploadsYaml(yaml);
        var builtAt = ParseBuiltAt(yaml);
        if (builtAt == null) { try { builtAt = (await store.ListAsync(dir + "uploads.yaml", false, false, ct)).FirstOrDefault(b => b.Name == dir + "uploads.yaml")?.Created; } catch (Exception e) when (e is not OperationCanceledException) { } }
        var bits = await Get("bits.bin"); var refs = await Get("refs.bin"); var mean = ParseMean(await Get("mean.f32"));
        int n = refs.Length / 8; if (bits.Length != n * CodeBytes) throw new InvalidDataException($"bits.bin has {bits.Length / CodeBytes} codes, refs.bin {n}");
        var ru = new int[n]; var rc = new int[n]; for (int i = 0; i < n; i++) { ru[i] = BinaryPrimitives.ReadInt32LittleEndian(refs.AsSpan(i * 8)); rc[i] = BinaryPrimitives.ReadInt32LittleEndian(refs.AsSpan(i * 8 + 4)); }
        Catalog? cat = null; try { cat = await IndexNode.Catalog.ReadAsync(store, paths, version.Value, ct); } catch (InvalidDataException) { }   // an unreadable catalog counts as none
        return new SearchData { Version = version.Value, MeanVersion = mv, MeanFrames = mf, Mean = mean, Bits = bits, RefUpload = ru, RefCenter = rc, Uploads = ups, BuiltAt = builtAt, Catalog = cat };
    }

    /// <summary>Writes vN (files created only if new: a second builder of the same version loses), then moves head.yaml (If-Match on a rewrite, If-None-Match on the first).</summary>
    public async Task PublishAsync(IStore store, Paths paths, CancellationToken ct)
    {
        var dir = Dir(paths, Version);
        async Task Put(string f, byte[] d, string type) { if (!await store.PutAsync(dir + f, d, null, null, true, ct, type)) throw new InvalidOperationException($"{dir}{f} already exists: another node is publishing version {Version}"); }
        await Put("mean.f32", MeanBytes(Mean), "application/octet-stream"); await Put("bits.bin", Bits, "application/octet-stream"); await Put("refs.bin", RefsBytes(), "application/octet-stream");
        await Put("uploads.yaml", Encoding.UTF8.GetBytes(UploadsYaml()), "text/yaml");
        if (Catalog != null) { Catalog.Version = Version; await Put(IndexNode.Catalog.File, Catalog.ToGzip(), "application/gzip"); }   // both written before head.yaml moves
        var head = HeadName(paths); var body = HeadYaml(Version, dir);
        var cur = await store.GetAsync(head, ct);
        if (cur == null) { if (!await store.PutAsync(head, body, null, null, true, ct, "text/yaml")) throw new InvalidOperationException("head.yaml was created meanwhile: run again"); return; }
        if (ParseHead(cur.Data) >= Version) throw new InvalidOperationException($"head.yaml is at version {ParseHead(cur.Data)}, not below {Version}");
        var tmp = Path.Combine(Path.GetTempPath(), "indexnode-head-" + Guid.NewGuid().ToString("N")[..8] + ".yaml");
        try
        {
            File.WriteAllBytes(tmp, body);   // the only conditional whole-blob write the store offers is the zip one (one Put Block List with If-Match)
            if (cur.ETag == null || !await store.PutZipIfMatchAsync(head, tmp, new Dictionary<string, string>(), null, cur.ETag, ct)) throw new InvalidOperationException("head.yaml changed while publishing (If-Match failed): run again");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}

public static class SearchIndexBuild
{
    /// <summary>The centers.zip of every upload: id, ETag, size, idx_sha256.</summary>
    public static async Task<List<(string Id, BlobEntry Zip)>> ListCentersAsync(IStore raw, Paths paths, CancellationToken ct)
    {
        var l = new List<(string, BlobEntry)>(); var pre = paths.Prefix + "videos/";
        foreach (var b in await raw.ListAsync(pre, false, true, ct))
        {
            if (!b.Name.EndsWith("/centers.zip", StringComparison.Ordinal)) continue;
            var id = b.Name[pre.Length..^"/centers.zip".Length]; if (id.Contains('/')) continue;
            l.Add((id, b));
        }
        return l.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    public sealed record Loaded(SearchUpload Up, float[] Centers, float[] Sum);

    /// <summary>Reads one upload's header and center vectors (a few range requests).</summary>
    public static async Task<Loaded> LoadUploadAsync(IStore raw, Paths paths, string id, CancellationToken ct)
    {
        var z = await CentersZip.OpenAsync(raw, paths.Video(id, "centers.zip"), ct) ?? throw new FileNotFoundException(id + "/centers.zip");
        int first = (int)Math.Min(z.DataLength, CentersFile.HeaderSize + 256 * CentersFile.Dim * 2);
        var buf = await z.ReadAsync(0, first, ct); var h = CentersFile.ParseHeader(buf);
        int need = CentersFile.HeaderSize + h.K * CentersFile.Dim * 2;
        if (need > buf.Length) buf = await z.ReadAsync(0, need, ct);
        var centers = CentersFile.ParseCenters(buf.AsSpan(CentersFile.HeaderSize, h.K * CentersFile.Dim * 2), h.K);
        var sha = (await raw.HeadAsync(z.Blob, ct))?.Metadata.GetValueOrDefault("idx_sha256") ?? "";
        var (sl, db) = await ReadSpeechAsync(raw, paths, id, ct);
        return new Loaded(new SearchUpload(id, sha, z.ETag ?? "", z.ZipSize, z.DataStart, z.DataLength, h.Frames, h.K, 0, sl, db), centers, h.Sum);
    }

    /// <summary>The upload's speech language and dubbed from its video.json (0.13.2; the transcribe.zip range reads are gone). Null/null when the descriptor has no speech yet; a missing or unreadable video.json THROWS (the caller's entry fails and is retried).</summary>
    public static async Task<(string? Language, bool? Dubbed)> ReadSpeechAsync(IStore raw, Paths paths, string id, CancellationToken ct)
    {
        var sp = (await VideoJson.RequireAsync(raw, paths, id, ct)).Root["speech"] as System.Text.Json.Nodes.JsonObject;
        return ((string?)sp?["language"] is { Length: > 0 } l ? l : null, (bool?)sp?["dubbed"]);
    }

    /// <summary>Builds the table of <paramref name="uploads"/>. <paramref name="keep"/>: rows of the previous version to copy for unchanged uploads (update); everything else is read from the store. <paramref name="mean"/> null: computed from the uploads' sums (frozen afterwards).</summary>
    public static async Task<SearchData> BuildAsync(IStore raw, Paths paths, List<(string Id, BlobEntry Zip)> uploads, float[]? mean, SearchData? keep, int version, int meanVersion, int threads, Action<string> say, CancellationToken ct)
    {
        var loaded = new Loaded?[uploads.Count]; var reuse = new bool[uploads.Count]; int done = 0;
        var old = keep?.Uploads.ToDictionary(u => u.Id, StringComparer.Ordinal);
        for (int i = 0; i < uploads.Count; i++)
            if (old != null && mean != null && old.TryGetValue(uploads[i].Id, out var ou) && ou.Sha == uploads[i].Zip.Metadata.GetValueOrDefault("idx_sha256", "?") && ou.Sha != "") reuse[i] = true;
        await Parallel.ForEachAsync(Enumerable.Range(0, uploads.Count).Where(i => !reuse[i]), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, async (i, t) =>
        {
            loaded[i] = await LoadUploadAsync(raw, paths, uploads[i].Id, t);
            int d = Interlocked.Increment(ref done); if (d % 100 == 0) say($"  read {d} uploads");
        });
        double total = 0; long frames = 0;
        if (mean == null)
        {
            var sum = new double[CentersFile.Dim];
            foreach (var l in loaded) { if (l == null) continue; for (int d = 0; d < CentersFile.Dim; d++) sum[d] += l.Sum[d]; frames += l.Up.Frames; }
            if (frames == 0) throw new InvalidOperationException("no frames in any centers.zip");
            mean = sum.Select(s => (float)(s / frames)).ToArray(); total = frames;
        }
        var ups = new List<SearchUpload>(); var bits = new List<byte[]>(); var ru = new List<int>(); var rc = new List<int>(); int row = 0;
        var code = new byte[SearchData.CodeBytes];
        for (int i = 0; i < uploads.Count; i++)
        {
            int ui = ups.Count;
            if (reuse[i])
            {
                var ou = old![uploads[i].Id];
                var (sl, db) = ou.SpeechLanguage != null ? (ou.SpeechLanguage, ou.Dubbed) : await ReadSpeechAsync(raw, paths, ou.Id, ct);   // a reused upload is read again only while its language is unknown
                ups.Add(ou with { RowStart = row, SpeechLanguage = sl, Dubbed = db });
                var chunk = new byte[ou.K * SearchData.CodeBytes]; Array.Copy(keep!.Bits, (long)ou.RowStart * SearchData.CodeBytes, chunk, 0, chunk.Length); bits.Add(chunk);
                for (int c = 0; c < ou.K; c++) { ru.Add(ui); rc.Add(c); }
                row += ou.K;
            }
            else
            {
                var l = loaded[i]!; ups.Add(l.Up with { RowStart = row });
                var chunk = new byte[l.Up.K * SearchData.CodeBytes];
                for (int c = 0; c < l.Up.K; c++) { SearchData.Code(l.Centers.AsSpan(c * CentersFile.Dim, CentersFile.Dim), mean, chunk.AsSpan(c * SearchData.CodeBytes, SearchData.CodeBytes)); ru.Add(ui); rc.Add(c); }
                bits.Add(chunk); row += l.Up.K;
            }
        }
        var all = new byte[(long)row * SearchData.CodeBytes]; long o = 0; foreach (var b in bits) { b.CopyTo(all, o); o += b.Length; }
        return new SearchData { Version = version, MeanVersion = meanVersion, MeanFrames = keep != null && total == 0 ? keep.MeanFrames : (long)total, Mean = mean, Bits = all, RefUpload = [.. ru], RefCenter = [.. rc], Uploads = ups };
    }
}

public sealed record Hit(string Upload, double Time, int Row, float Score, int Center);
public sealed record QueryTimings(double ScanMs, double ReadMs, double ScoreMs, int Uploads, int Requests, long Bytes);

/// <summary>The query path (reusable by the download pre-check): bit scan of every center, range reads of the best centers' member blocks, exact cosine.</summary>
public static class CenterSearch
{
    public const int MergeGap = 32 * 1024, MaxMerged = 4 << 20;

    public static float[] MinusMean(float[] q, float[] mean) { var r = new float[q.Length]; for (int i = 0; i < r.Length; i++) r[i] = q[i] - mean[i]; return r; }

    public static async Task<(List<Hit> Hits, QueryTimings Timings)> QueryAsync(IStore raw, Paths paths, SearchData idx, float[] query, int topCenters, int top, int threads, CancellationToken ct)
    {
        var q = (float[])query.Clone(); Vec.Normalize(q);
        var sw = Stopwatch.StartNew();
        var rows = idx.Scan(MinusMean(q, idx.Mean), topCenters, threads);
        double scanMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
        var byUpload = rows.GroupBy(r => idx.RefUpload[r]).ToList();
        long bytes = 0; int requests = 0; var perUpload = new List<(SearchUpload U, int Center, CentersFile.Members M)>[byUpload.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, byUpload.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(32, threads * 4)), CancellationToken = ct }, async (g, t) =>
        {
            var up = idx.Uploads[byUpload[g].Key]; var centers = byUpload[g].Select(r => idx.RefCenter[r]).ToList();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var z = attempt == 0 ? CentersZip.FromIndex(raw, paths.Video(up.Id, "centers.zip"), up) : await CentersZip.OpenAsync(raw, paths.Video(up.Id, "centers.zip"), t) ?? throw new FileNotFoundException(up.Id);
                    var res = await z.ReadBlocksAsync(centers, MergeGap, MaxMerged, t);
                    Interlocked.Add(ref bytes, z.BytesRead); Interlocked.Add(ref requests, z.Requests);
                    perUpload[g] = res.Select(x => (up, x.Center, x.Members)).ToList(); return;
                }
                catch (StaleZipException) when (attempt == 0) { }   // the zip was rewritten since the index was built: reopen it once
            }
        });
        double readMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
        var hits = new List<Hit>(); var vec = new float[CentersFile.Dim];
        foreach (var list in perUpload) foreach (var (u, c, m) in list)
            for (int i = 0; i < m.Count; i++)
            {
                Vec.FromHalf(m.Vectors.AsSpan(i * CentersFile.Dim * 2, CentersFile.Dim * 2), vec);
                hits.Add(new Hit(u.Id, m.Times[i], m.Rows[i], Vec.Dot(q, vec), c));
            }
        var best = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Upload, StringComparer.Ordinal).ThenBy(h => h.Row).Take(top).ToList();
        return (best, new QueryTimings(scanMs, readMs, sw.Elapsed.TotalMilliseconds, byUpload.Count, requests, bytes));
    }
}
