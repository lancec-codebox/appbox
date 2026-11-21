using System.Collections.Concurrent;
using System.Runtime.Intrinsics;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.13: the dedup of a JOIN drama. Its comparison set is every EARLIER member of the target group (lower seq; the whole predecessor chain), each by its FULL frames file (through FramesCache), not the global index.
/// Group membership: the earlier dramas' grouping records (an earlier member that joined X belongs to X's group: the join chain is followed to its root), plus the group the search index's groups file knows for the join target.
/// </summary>
public sealed class GroupDedup(IStore store)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    readonly ConcurrentDictionary<string, (string? Parent, DateTimeOffset At)> parents = new(StringComparer.Ordinal);   // id -> the upload its grouping record joins (null: it is new / mixed / none)

    async Task<string?> ParentAsync(Paths paths, string id, CancellationToken ct)
    {
        if (parents.TryGetValue(id, out var c) && DateTimeOffset.UtcNow - c.At < CacheFor) return c.Parent;
        string? p = null;
        try
        {
            var g = (await VideoJson.ReadAsync(store, paths, id, ct))?.Root["grouping"] as JsonObject; var pr = g == null ? null : GroupProposal.FromJson(g["proposal"] as JsonObject);
            p = pr is { Kind: GroupProposal.JoinKind, JoinUpload: { } ju } ? ju : null;
            if (g == null) return null;   // no record yet: not cached (its step may finish any moment)
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
        parents[id] = (p, DateTimeOffset.UtcNow); return p;
    }

    /// <summary>The ids of the earlier members of the group the join target belongs to, lowest seq first (the target included). <paramref name="earlierSeq"/>: the seq of every earlier drama (null seq counts as earlier, as in the group step).</summary>
    public async Task<List<string>> SetAsync(Paths paths, string target, IReadOnlyDictionary<string, long?> earlierSeq, GroupSet? known, CancellationToken ct)
    {
        async Task<string> RootAsync(string x) { var seen = new HashSet<string>(StringComparer.Ordinal) { x }; while (await ParentAsync(paths, x, ct) is { } up && seen.Add(up)) x = up; return x; }
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
        return members.OrderBy(x => earlierSeq[x] ?? long.MinValue).ThenBy(x => x, StringComparer.Ordinal).ToList();
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

