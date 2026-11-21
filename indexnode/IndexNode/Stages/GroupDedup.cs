using System.Collections.Concurrent;
using System.Runtime.Intrinsics;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.13: the dedup of a JOIN drama. Its comparison set is every EARLIER member of the target group (lower seq; the whole predecessor chain), each by its FULL frames file (through FramesCache), not the global index.
/// Group membership: the earlier dramas' grouping records (an earlier member that joined X belongs to X's group: the join chain is followed to its root), plus the group the search index's groups file knows for the join target.
/// </summary>
public sealed class GroupDedup(IStore store, string? cacheDir = null)
{
    // 0.13.14: membership comes from the queue state (JoinOf, recorded on the group step's done line), then the search index's groups file (read once per index version, kept in memory and on disk),
    // and only for a drama in neither place from its video.json grouping record, which is then kept for good (memory + an append file in the cache directory). Nothing here expires.
    readonly ConcurrentDictionary<string, string?> parents = new(StringComparer.Ordinal);   // id -> the upload its grouping record joins (null: it is new / mixed / none)
    readonly ConcurrentDictionary<int, Lazy<Task<GroupSet?>>> groupFiles = new();   // index version -> the groups file (null: none)
    readonly ConcurrentDictionary<string, string> noRecord = new(StringComparer.Ordinal);   // id -> the queue LastAt when its video.json had no grouping record
    readonly object fileGate = new(); bool loaded;
    string? Dir => cacheDir ?? FramesCache.Current?.Dir;

    string? JoinsFile => Dir == null ? null : Path.Combine(Dir, "group-joins.jsonl");

    void LoadJoins()
    {
        lock (fileGate)
        {
            if (loaded) return; loaded = true;
            try
            {
                if (JoinsFile is { } f && File.Exists(f))
                    foreach (var line in File.ReadLines(f))
                        try { if (JsonNode.Parse(line) is JsonObject o && (string?)o["id"] is { } id) parents[id] = (string?)o["join"]; } catch (System.Text.Json.JsonException) { }
            }
            catch (IOException) { }
        }
    }

    void Remember(string id, string? join)
    {
        parents[id] = join;
        lock (fileGate)
            try { if (JoinsFile is { } f) { Directory.CreateDirectory(Dir!); File.AppendAllText(f, new JsonObject { ["id"] = id, ["join"] = join }.ToJsonString() + "\n"); } } catch (IOException) { }
    }

    /// <summary>The groups file for an index version: one store read per version and process, and none after a restart (the file is kept in the cache directory).</summary>
    public Task<GroupSet?> KnownAsync(Paths paths, int indexVersion, CancellationToken ct) =>
        groupFiles.GetOrAdd(indexVersion, v => new Lazy<Task<GroupSet?>>(async () =>
        {
            var f = Dir == null ? null : Path.Combine(Dir, $"groups-v{v}.json");
            try { if (f != null && File.Exists(f) && JsonNode.Parse(await File.ReadAllTextAsync(f, ct)) is JsonObject o) return GroupSet.FromJson(o); } catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
            GroupSet? g = null;
            try { g = await GroupStore.LoadAsync(store, paths, v, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
            try { if (g != null && f != null) { Directory.CreateDirectory(Dir!); var tmp = f + ".tmp"; await File.WriteAllTextAsync(tmp, g.ToJson().ToJsonString(), ct); File.Move(tmp, f, true); } } catch (IOException) { }
            return g;
        })).Value;

    /// <summary>Where each drama's join came from, for the group log line.</summary>
    public sealed record Membership(List<string> Members, int FromQueue, int FromGroups, int FromCache, int Reads);

    /// <summary>A grouping record read from video.json (the fallback): the join, or null when it joins none. Not cached when the drama has no record yet.</summary>
    async Task<(bool Found, string? Join)> ReadAsync(Paths paths, string id, CancellationToken ct)
    {
        try
        {
            var g = (await VideoJson.ReadAsync(store, paths, id, ct))?.Root["grouping"] as JsonObject; if (g == null) return (false, null);
            var pr = GroupProposal.FromJson(g["proposal"] as JsonObject);
            return (true, pr is { Kind: GroupProposal.JoinKind, JoinUpload: { } ju } ? ju : null);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return (false, null); }
    }

    /// <summary>The ids of the earlier members of the group the join target belongs to, lowest seq first (the target included). <paramref name="earlierSeq"/>: the seq of every earlier drama (null seq counts as earlier, as in the group step). <paramref name="queueJoin"/>: the queue state's record of a drama's join (null: not recorded).</summary>
    public async Task<Membership> SetAsync(Paths paths, string target, IReadOnlyDictionary<string, long?> earlierSeq, Func<string, (string? Join, string? Kind)?> queueJoin, GroupSet? known, CancellationToken ct, Func<string, string?>? queueStamp = null)
    {
        LoadJoins();
        int fq = 0, fg = 0, fc = 0, reads = 0;
        var inGroups = new Dictionary<string, string?>(StringComparer.Ordinal);   // member -> JoinedThrough, from the groups file
        if (known != null) foreach (var g in known.Groups) foreach (var m in g.Members) inGroups[m.Upload] = m.JoinedThrough;
        var memo = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        async Task<string?> ParentAsync(string x)
        {
            if (memo.TryGetValue(x, out var done)) return done;
            var r = await ParentCoreAsync(x); memo[x] = r; return r;
        }
        async Task<string?> ParentCoreAsync(string x)
        {
            if (queueJoin(x) is { Kind: not null } qj) { Interlocked.Increment(ref fq); return qj.Kind == GroupProposal.JoinKind ? qj.Join : null; }
            if (parents.TryGetValue(x, out var c)) { Interlocked.Increment(ref fc); return c; }
            if (inGroups.TryGetValue(x, out var jt)) { Interlocked.Increment(ref fg); return jt; }
            // a confirmed "no record" (its group step has not run) is remembered until a queue line for the drama arrives (its LastAt changes)
            var stamp = queueStamp?.Invoke(x) ?? "";
            if (noRecord.TryGetValue(x, out var ns) && ns == stamp) { Interlocked.Increment(ref fc); return null; }
            Interlocked.Increment(ref reads);
            var (found, join) = await ReadAsync(paths, x, ct);
            if (found) Remember(x, join); else noRecord[x] = stamp;
            return join;
        }
        async Task<string> RootAsync(string x) { var seen = new HashSet<string>(StringComparer.Ordinal) { x }; while (await ParentAsync(x) is { } up && seen.Add(up)) x = up; return x; }
        var root = await RootAsync(target);
        var members = new HashSet<string>(StringComparer.Ordinal);
        if (earlierSeq.ContainsKey(root)) members.Add(root);
        if (earlierSeq.ContainsKey(target)) members.Add(target);
        // every earlier drama whose chain ends at the root (its record joins into the group)
        var roots = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(earlierSeq.Keys, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (x, t) => { if (x != root && x != target) roots[x] = await RootAsync(x); });
        foreach (var (x, r) in roots) if (r == root || r == target) members.Add(x);
        // a group known only from the index's groups file
        if (known != null && known.GroupOf().TryGetValue(target, out var gid) && known.Groups.FirstOrDefault(g => g.Id == gid) is { } grp)
            foreach (var m in grp.Members) if (earlierSeq.ContainsKey(m.Upload)) members.Add(m.Upload);
        return new Membership(members.OrderBy(x => earlierSeq[x] ?? long.MinValue).ThenBy(x => x, StringComparer.Ordinal).ToList(), fq, fg, fc, reads);
    }

    /// <summary>The members' full frames, normalised, as one matrix (rows in member order): vectors, the member each row belongs to and its time. Members whose frames file is missing are left out (and not returned in <paramref name="used"/>).</summary>
    public static async Task<(float[] M, int[] Member, float[] Time, List<string> Used)> LoadAsync(IStore store, Paths paths, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var per = new (string Id, List<float[]> V, List<float> T)?[ids.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, ids.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (k, t) =>
        {
            try
            {
                var z = await FramesReader.OpenAsync(store, paths.FramesFile(ids[k]), t); if (z == null) return;
                var h = await z.ReadHeaderAsync(t); var blocks = await z.ReadBlocksAsync(Enumerable.Range(0, h.K).ToList(), CenterSearch.MergeGap, CenterSearch.MaxMerged, t);
                var vs = new List<float[]>(); var ts = new List<float>();
                foreach (var (_, m) in blocks) { var rows = AiProbe.RowsFromF16(m.Vectors); for (int i = 0; i < rows.Length && i < m.Times.Length; i++) { Normalize(rows[i]); vs.Add(rows[i]); ts.Add(m.Times[i]); } }
                per[k] = (ids[k], vs, ts);
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"group dedup: frames of {ids[k]} not read ({e.Message})"); }
        });
        var used = new List<string>(); int n = per.Sum(p => p?.V.Count ?? 0); var M = new float[(long)n * CentersFile.Dim]; var mem = new int[n]; var tm = new float[n]; int at = 0;
        foreach (var p in per) { if (p is not { } x) continue; int mi = used.Count; used.Add(x.Id); for (int i = 0; i < x.V.Count; i++, at++) { x.V[i].CopyTo(M, (long)at * CentersFile.Dim); mem[at] = mi; tm[at] = x.T[i]; } }
        return (M, mem, tm, used);
    }

    static void Normalize(float[] v) { double s = 0; foreach (var x in v) s += (double)x * x; if (s <= 0) return; float inv = (float)(1 / Math.Sqrt(s)); for (int i = 0; i < v.Length; i++) v[i] *= inv; }

    const int Tile = 64;   // own frames per task (64 x 2 KB = 128 KB: stays in L2 while every set row streams past once)

    /// <summary>
    /// Brute force: for every own frame (normalised) its best cosine against EVERY row of the set matrix. SIMD (AVX2 FMA), register-blocked 4 own frames x 2 set rows per step (8 accumulators, 6 loads for 8 FMAs), tiled so a set row is read once per tile of own frames, parallel over tiles.
    /// Returns (upload = member of the best row, time, cosine) per own frame (null for an empty set).
    /// </summary>
    public static FrameBest?[] Best(IReadOnlyList<float[]> own, float[] M, int[] member, float[] time, int threads, CancellationToken ct)
    {
        int n = own.Count, rows = member.Length, D = CentersFile.Dim; var best = new FrameBest?[n]; if (rows == 0 || n == 0) return best;
        var A = new float[(long)n * D]; for (int i = 0; i < n; i++) own[i].CopyTo(A, (long)i * D);
        int tiles = (n + Tile - 1) / Tile;
        Parallel.For(0, tiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, tl =>
        {
            int lo = tl * Tile, hi = Math.Min(n, lo + Tile); var bc = new float[hi - lo]; var bi = new int[hi - lo]; Array.Fill(bc, float.NegativeInfinity);
            Span<float> s = stackalloc float[8];
            for (int r = 0; r < rows; r += 2)
            {
                bool two = r + 1 < rows;
                ref float r0 = ref M[(long)r * D]; ref float r1 = ref M[(long)(two ? r + 1 : r) * D];
                int i = lo;
                for (; i + 4 <= hi; i += 4)
                {
                    Dots4x2(ref A[(long)i * D], ref r0, ref r1, D, s);
                    for (int k = 0; k < 4; k++)
                    {
                        if (s[k] > bc[i - lo + k]) { bc[i - lo + k] = s[k]; bi[i - lo + k] = r; }
                        if (two && s[4 + k] > bc[i - lo + k]) { bc[i - lo + k] = s[4 + k]; bi[i - lo + k] = r + 1; }
                    }
                }
                for (; i < hi; i++)
                {
                    float c0 = Vec.Dot(A.AsSpan((int)((long)i * D), D), M.AsSpan((int)((long)r * D), D)); if (c0 > bc[i - lo]) { bc[i - lo] = c0; bi[i - lo] = r; }
                    if (two) { float c1 = Vec.Dot(A.AsSpan((int)((long)i * D), D), M.AsSpan((int)((long)(r + 1) * D), D)); if (c1 > bc[i - lo]) { bc[i - lo] = c1; bi[i - lo] = r + 1; } }
                }
            }
            for (int i = lo; i < hi; i++) best[i] = new FrameBest(member[bi[i - lo]], time[bi[i - lo]], bc[i - lo]);
        });
        return best;
    }

    /// <summary>The dots of 4 consecutive own frames (a, a+D, ...) with two set rows: out[0..4) against r0, out[4..8) against r1.</summary>
    static void Dots4x2(ref float a, ref float r0, ref float r1, int D, Span<float> o)
    {
        Vector256<float> c00 = default, c01 = default, c02 = default, c03 = default, c10 = default, c11 = default, c12 = default, c13 = default;
        for (int k = 0; k < D; k += 8)
        {
            var x0 = Vector256.LoadUnsafe(ref r0, (nuint)k); var x1 = Vector256.LoadUnsafe(ref r1, (nuint)k);
            var a0 = Vector256.LoadUnsafe(ref a, (nuint)k); var a1 = Vector256.LoadUnsafe(ref a, (nuint)(D + k)); var a2 = Vector256.LoadUnsafe(ref a, (nuint)(2 * D + k)); var a3 = Vector256.LoadUnsafe(ref a, (nuint)(3 * D + k));
            c00 = Vector256.FusedMultiplyAdd(a0, x0, c00); c01 = Vector256.FusedMultiplyAdd(a1, x0, c01); c02 = Vector256.FusedMultiplyAdd(a2, x0, c02); c03 = Vector256.FusedMultiplyAdd(a3, x0, c03);
            c10 = Vector256.FusedMultiplyAdd(a0, x1, c10); c11 = Vector256.FusedMultiplyAdd(a1, x1, c11); c12 = Vector256.FusedMultiplyAdd(a2, x1, c12); c13 = Vector256.FusedMultiplyAdd(a3, x1, c13);
        }
        o[0] = Vector256.Sum(c00); o[1] = Vector256.Sum(c01); o[2] = Vector256.Sum(c02); o[3] = Vector256.Sum(c03);
        o[4] = Vector256.Sum(c10); o[5] = Vector256.Sum(c11); o[6] = Vector256.Sum(c12); o[7] = Vector256.Sum(c13);
    }
}

