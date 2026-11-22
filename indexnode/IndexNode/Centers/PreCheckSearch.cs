using System.Collections.Concurrent;
using System.Diagnostics;

namespace IndexNode;

public sealed record FrameBest(int Upload, double Time, float Cosine);

/// <summary>An LRU of decoded member blocks ((upload id, center) -> vectors, times) bounded by bytes; the pre-check service keeps one across requests and clears it when a new index version is swapped in.</summary>
public sealed class BlockCache(long maxBytes)
{
    readonly object gate = new(); readonly Dictionary<(string, int), LinkedListNode<((string, int) Key, (float[] Vec, float[] Times) Val)>> map = new(); readonly LinkedList<((string, int) Key, (float[] Vec, float[] Times) Val)> lru = new();
    long bytes, hits, misses;
    static long Size((float[] Vec, float[] Times) v) => (long)(v.Vec.Length + v.Times.Length) * 4 + 64;
    public bool TryGet(string upload, int center, out (float[] Vec, float[] Times) v)
    {
        lock (gate)
        {
            if (map.TryGetValue((upload, center), out var n)) { lru.Remove(n); lru.AddFirst(n); v = n.Value.Val; hits++; return true; }
            misses++; v = default; return false;
        }
    }
    public void Put(string upload, int center, (float[] Vec, float[] Times) v)
    {
        lock (gate)
        {
            if (map.ContainsKey((upload, center))) return;
            var n = lru.AddFirst(((upload, center), v)); map[(upload, center)] = n; bytes += Size(v);
            while (bytes > maxBytes && lru.Last is { } last) { lru.RemoveLast(); map.Remove(last.Value.Key); bytes -= Size(last.Value.Val); }
        }
    }
    public void Clear() { lock (gate) { map.Clear(); lru.Clear(); bytes = 0; } }
    public (int Entries, long Bytes, long Hits, long Misses, long MaxBytes) Stats() { lock (gate) return (map.Count, bytes, hits, misses, maxBytes); }
}

/// <summary>The pre-check lookup: many query frames against the global search table (0.11.0 SearchData/frames files), with member blocks read once per (upload, center) and cached for the whole pre-check.</summary>
public static class PreCheckSearch
{
    /// <summary>0.13.21 (vote-first): VotedUploads = uploads with at least one top row; ReadUploads = how many of them were read (all when voteUploads is 0); WinnerVoteRank = the vote rank (1 = most votes) of the upload that is best for the most frames (0 = no winner).</summary>
    public sealed record Stats(double ScanMs, double ReadMs, double ScoreMs, int Blocks, int Requests, long Bytes, int VotedUploads = 0, int ReadUploads = 0, int WinnerVoteRank = 0);

    /// <summary>
    /// The vote: per upload, how many frames have it among their top rows (a plain count of frames, not of rows: one frame voting twice for the same upload counts once); ties go to the rank-weighted sum
    /// (a row at rank r of its frame's list adds 1/(1+r)), then to the upload index. A drama that matches turns up in frame after frame, a scattered one in a few.
    /// </summary>
    public static List<(int Upload, int Frames, double Weight)> Vote(SearchData idx, int[][] rows)
    {
        var frames = new Dictionary<int, int>(); var weight = new Dictionary<int, double>(); var seen = new HashSet<int>();
        foreach (var rs in rows)
        {
            seen.Clear();
            for (int rank = 0; rank < rs.Length; rank++)
            {
                var u = idx.RefUpload[rs[rank]];
                if (seen.Add(u)) frames[u] = frames.GetValueOrDefault(u) + 1;
                weight[u] = weight.GetValueOrDefault(u) + 1.0 / (1 + rank);
            }
        }
        return frames.Select(kv => (kv.Key, kv.Value, weight[kv.Key])).OrderByDescending(x => x.Value).ThenByDescending(x => x.Item3).ThenBy(x => x.Key).ToList();
    }

    /// <summary>
    /// Per frame: the query minus the frozen mean, asymmetric bit scan of every center (top <paramref name="topCenters"/>), exact cosine against the members of those centers; the best
    /// (upload index, time, cosine) per frame (null: no candidate). <paramref name="queries"/> are normalised here.
    /// </summary>
    public static async Task<(FrameBest?[] Best, Stats Stats)> BestPerFrameAsync(IStore raw, Paths paths, SearchData idx, IReadOnlyList<float[]> queries, int topCenters, int threads, CancellationToken ct, int excludeUpload = -1, BlockCache? shared = null, int voteUploads = 0)
    {
        int n = queries.Count; var q = new float[n][]; var rows = new int[n][];
        for (int i = 0; i < n; i++) { q[i] = (float[])queries[i].Clone(); Vec.Normalize(q[i]); }
        var sw = Stopwatch.StartNew();
        var qm = new float[n][]; for (int i = 0; i < n; i++) qm[i] = CenterSearch.MinusMean(q[i], idx.Mean);
        var scanned = idx.ScanMany(qm, topCenters, threads, excludeUpload); for (int i = 0; i < n; i++) rows[i] = scanned[i];   // frames are the parallel axis
        double scanMs = sw.Elapsed.TotalMilliseconds; sw.Restart();

        // 0.13.21: vote per upload first (no reads), then read and score only the top voteUploads uploads (0 = every upload that has a top row: the old behaviour)
        var vote = Vote(idx, rows); var keep = voteUploads > 0 ? vote.Take(voteUploads).Select(x => x.Upload).ToHashSet() : null;
        var need = new Dictionary<int, HashSet<int>>();
        foreach (var rs in rows) foreach (var r in rs) { var u = idx.RefUpload[r]; if (keep != null && !keep.Contains(u)) continue; if (!need.TryGetValue(u, out var set)) need[u] = set = []; set.Add(idx.RefCenter[r]); }
        var cache = new ConcurrentDictionary<(int Up, int Center), (float[] Vec, float[] Times)>();
        long bytes = 0; int requests = 0;
        await Parallel.ForEachAsync(need, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(64, threads * 8)), CancellationToken = ct }, async (kv, t) =>   // 0.13.8: the real bound is CentersBlob.MaxInFlight requests process-wide
        {
            var up = idx.Uploads[kv.Key]; var centers = new List<int>();
            foreach (var c in kv.Value) { if (shared != null && shared.TryGet(up.Id, c, out var hit)) cache[(kv.Key, c)] = hit; else centers.Add(c); }
            if (centers.Count == 0) return;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var z = await FramesReader.ForAsync(raw, paths, up, attempt, t);
                    var res = await z.ReadBlocksAsync(centers, CenterSearch.MergeGap, CenterSearch.MaxMerged, t);
                    Interlocked.Add(ref bytes, z.BytesRead); Interlocked.Add(ref requests, z.Requests);
                    foreach (var (c, m) in res)
                    {
                        var v = new float[m.Count * CentersFile.Dim]; Vec.FromHalf(m.Vectors, v);   // decoded once, reused by every frame that scans this center
                        cache[(kv.Key, c)] = (v, m.Times); shared?.Put(up.Id, c, (v, m.Times));
                    }
                    return;
                }
                catch (StaleZipException) when (attempt == 0) { }
            }
        });
        double readMs = sw.Elapsed.TotalMilliseconds; sw.Restart();

        var best = new FrameBest?[n];
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, i =>
        {
            FrameBest? b = null;
            foreach (var r in rows[i])
            {
                if (!cache.TryGetValue((idx.RefUpload[r], idx.RefCenter[r]), out var blk)) continue;   // a row of an upload that was not read (outside the vote's top) is skipped
                for (int m = 0; m < blk.Times.Length; m++)
                {
                    float c = Vec.Dot(q[i], blk.Vec.AsSpan(m * CentersFile.Dim, CentersFile.Dim));
                    if (b == null || c > b.Cosine) b = new FrameBest(idx.RefUpload[r], blk.Times[m], c);
                }
            }
            best[i] = b;
        });
        int winnerRank = 0;
        var wins = best.Where(b => b != null).GroupBy(b => b!.Upload).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (wins != null) { winnerRank = vote.FindIndex(x => x.Upload == wins.Key) + 1; }
        return (best, new Stats(scanMs, readMs, sw.Elapsed.TotalMilliseconds, cache.Count, requests, bytes, vote.Count, keep?.Count ?? vote.Count, winnerRank));
    }

    /// <summary>
    /// 0.13.1: the uploads whose centers.zip is newer than the index, searched by brute force (every center's members, exact cosine): their upload index is <c>idx.Uploads.Count + position in <paramref name="fresh"/></c>.
    /// Merges into <paramref name="best"/> (the indexed result per frame) and returns the fresh uploads that could be read (a missing or unreadable one is skipped, not fatal: the index is a cache of these).
    /// </summary>
    public static async Task<List<SearchUpload>> MergeFreshAsync(IStore raw, Paths paths, SearchData idx, IReadOnlyList<string> fresh, IReadOnlyList<float[]> queries, FrameBest?[] best, int threads, CancellationToken ct)
    {
        var used = new List<SearchUpload>(); int n = queries.Count; var q = new float[n][];
        for (int i = 0; i < n; i++) { q[i] = (float[])queries[i].Clone(); Vec.Normalize(q[i]); }
        // 0.13.18: ONE LIST of the retained folder says which dramas have a retained file (0.13.15 GET the retained name of every fresh drama first: a 404 for nearly all of them, on every group step)
        var haveRetained = (await raw.ListAsync(paths.RetainedDir, false, false, ct)).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var id in fresh)
        {
            CentersBlob? z; List<(int Center, CentersFile.Members Members)> blocks;
            try
            {
                z = (haveRetained.Contains(paths.RetainedFile(id)) ? await FramesReader.OpenAsync(raw, paths.RetainedFile(id), ct) : null) ?? await FramesReader.OpenAsync(raw, paths.FramesFile(id), ct); if (z == null) continue;   // 0.13.10: a retained file is the drama's vectors once it has one (what the index holds)
                var h = await z.ReadHeaderAsync(ct);
                blocks = await z.ReadBlocksAsync(Enumerable.Range(0, h.K).ToList(), CenterSearch.MergeGap, CenterSearch.MaxMerged, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"pre-check: fresh centers of {id} not searched ({e.Message})"); continue; }
            var (sl, db) = await SearchIndexBuild.ReadSpeechAsync(raw, paths, id, ct);
            int up = idx.Uploads.Count + used.Count; used.Add(new SearchUpload(id, "", z.ETag ?? "", z.ZipSize, 0, z.DataLength, 0, blocks.Count, 0, sl, db, null, z.Blob, 0));
            var vecs = blocks.Select(b => { var v = new float[b.Members.Count * CentersFile.Dim]; Vec.FromHalf(b.Members.Vectors, v); return (v, b.Members.Times); }).ToList();
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, i =>
            {
                FrameBest? b = best[i];
                foreach (var (v, times) in vecs)
                    for (int m = 0; m < times.Length; m++)
                    {
                        float c = Vec.Dot(q[i], v.AsSpan(m * CentersFile.Dim, CentersFile.Dim));
                        if (b == null || c > b.Cosine) b = new FrameBest(up, times[m], c);
                    }
                best[i] = b;
            });
        }
        return used;
    }

    /// <summary>Shares per upload: the fraction of all frames whose best match is in the upload with cosine &gt;= tau. Best first. <paramref name="extra"/>: the fresh uploads of <see cref="MergeFreshAsync"/> (indices after the index's own).</summary>
    public static (List<PreCheckMatch> Matches, int NoHit) Shares(SearchData idx, IReadOnlyList<FrameBest?> best, double tau, IReadOnlyList<SearchUpload>? extra = null)
    {
        SearchUpload Up(int i) => i < idx.Uploads.Count ? idx.Uploads[i] : extra![i - idx.Uploads.Count];
        int n = best.Count; int noHit = 0; var by = new Dictionary<int, (int Frames, float Cos, double Time)>();
        foreach (var b in best)
        {
            if (b == null || b.Cosine < tau) { noHit++; continue; }
            by.TryGetValue(b.Upload, out var cur);
            by[b.Upload] = (cur.Frames + 1, Math.Max(cur.Cos, b.Cosine), b.Cosine >= cur.Cos ? b.Time : cur.Time);
        }
        var list = by.Select(kv => new PreCheckMatch(Up(kv.Key).Id, n == 0 ? 0 : (double)kv.Value.Frames / n, kv.Value.Cos, kv.Value.Time, kv.Value.Frames, Up(kv.Key).SpeechLanguage, Up(kv.Key).Dubbed))
            .OrderByDescending(m => m.Share).ThenByDescending(m => m.BestCosine).ThenBy(m => m.Upload, StringComparer.Ordinal).ToList();
        return (list, noHit);
    }
}

/// <summary>The search index on a download node: head.yaml re-checked at most every <c>recheck</c> (conditional GET), vN/ cached under a local folder so bits.bin (15 MB) is fetched once per version.</summary>
public sealed class SearchIndexCache(string root, TimeSpan recheck, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly SemaphoreSlim gate = new(1, 1);
    SearchData? mem; DateTimeOffset checkedAt = DateTimeOffset.MinValue; string? headEtag;
    public int HeadReads { get; private set; }
    public int Downloads { get; private set; }

    /// <summary>The current index; null when the store has none. A store error with an index already loaded keeps that one.</summary>
    public async Task<SearchData?> GetAsync(IStore store, Paths paths, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (mem != null && now() - checkedAt < recheck) return mem;
            ConditionalResult head;
            try { head = await store.GetIfChangedAsync(SearchData.HeadName(paths), headEtag, ct); HeadReads++; }
            catch (Exception e) when (e is not OperationCanceledException && mem != null) { Log.Info($"search index: head.yaml check failed ({e.Message}); keeping v{mem.Version}"); checkedAt = now(); return mem; }
            checkedAt = now();
            if (!head.Exists) return mem = null;
            if (!head.Changed && mem != null) return mem;
            int version;
            if (head.Data != null) version = SearchData.ParseHead(head.Data);
            else version = SearchData.ParseHead((await store.GetAsync(SearchData.HeadName(paths), ct))?.Data ?? throw new FileNotFoundException("head.yaml"));
            headEtag = head.ETag;
            if (mem?.Version == version) return mem;
            var local = new LocalStore(root);
            SearchData? d = null;
            try { d = await SearchData.LoadAsync(local, paths, version, ct); } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or FormatException) { }
            if (d == null)
            {
                try { d = await SearchData.LoadAsync(store, paths, version, ct) ?? throw new FileNotFoundException("search index v" + version); }
                catch (FileNotFoundException)
                {
                    // 404 on the loaded version's files: it was pruned after head.yaml was read. Re-read head.yaml and retry once on the current version.
                    var h2 = await store.GetAsync(SearchData.HeadName(paths), ct) ?? throw new FileNotFoundException("head.yaml");
                    var v2 = SearchData.ParseHead(h2.Data); if (v2 == version) throw;
                    Log.Info($"search index: v{version} is gone; retrying on v{v2}");
                    version = v2; headEtag = null; local = new LocalStore(root);
                    d = await SearchData.LoadAsync(store, paths, version, ct) ?? throw new FileNotFoundException("search index v" + version);
                }
                Downloads++;
                var dir = SearchData.Dir(paths, version);
                await local.PutAsync(dir + "mean.f32", SearchData.MeanBytes(d.Mean), null, null, false, ct, "application/octet-stream");
                await local.PutAsync(dir + "refs.bin", d.RefsBytes(), null, null, false, ct, "application/octet-stream");
                await local.PutAsync(dir + "uploads.yaml", System.Text.Encoding.UTF8.GetBytes(d.UploadsYaml()), null, null, false, ct, "text/yaml");
                if (d.Catalog != null) await local.PutAsync(dir + Catalog.File, d.Catalog.ToGzip(), null, null, false, ct, "application/gzip");
                await local.PutAsync(dir + "bits.bin", d.Bits, null, null, false, ct, "application/octet-stream");   // last: a half-written folder fails the load above and is fetched again
            }
            return mem = d;
        }
        finally { gate.Release(); }
    }
}
