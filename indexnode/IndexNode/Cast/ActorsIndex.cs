using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// The actor index, index/actors/vN/ (head.json moves with If-Match; unchanged since the head: "up to date", nothing written):
///   uploads.json  [{id, etag (of its units/&lt;id&gt;.bin), units:[{key, actor, prod, n, row, rows}]}]
///   codes.bin     one 64-byte sign code (vector - mean) per row
///   vec.f16       one float16 row per vector: per unit the mbf centre first, then 3-4 representatives
///   mean.f32      the mean the codes are relative to (kept from the first build)
///   r50.f16       0.13.32: one float16 row per unit that has an r50 vector (the w600k_r50 centre of up to 30 faces), apart from vec.f16; uploads.json maps unit key -> row ("r50"; -1 none), plus the r50/&lt;id&gt;.bin ETag it was read at ("r50etag") and its dim ("r50dim")
///   pending.json  the band pairs a job had to skip (a partner without r50) that the build has not settled yet: {upload: [[unit, other, cos]]}; the build settles them from the r50 rows (links.json gets the linked ones)
/// A search loads codes.bin only (46 MB at 720k vectors), routes by hamming distance, then re-checks the top units exactly by ranged reads of their centre rows in vec.f16.
/// </summary>
public sealed class ActorsIndex
{
    public int Version, Dim; public float[] Mean = []; public byte[] Codes = []; public byte[]? Vec; public List<(string Id, string ETag, List<IUnit> Units)> Uploads = [];
    public sealed record IUnit(string Key, string Actor, string Prod, int N, int Row, int Rows, int R50 = -1);
    public int R50Dim; public byte[]? R50Vec; public Dictionary<string, string> R50ETags = new(StringComparer.Ordinal); public Dictionary<string, List<(string Unit, string Other, double Cos)>> Pending = new(StringComparer.Ordinal);
    readonly Dictionary<int, byte[]> r50rows = new();
    /// <summary>The r50 vector of a unit from the index alone (memory, or rows prefetched by <see cref="PrefetchR50Async"/>); null when the unit has none (indexed before its upload was repaired) or its row was not fetched.</summary>
    public float[]? R50Of(int upload, int unit)
    {
        int row = Uploads[upload].Units[unit].R50; if (row < 0 || R50Dim <= 0) return null; var v = new float[R50Dim];
        if (R50Vec != null) { if ((row + 1L) * R50Dim * 2 > R50Vec.Length) return null; for (int d = 0; d < R50Dim; d++) v[d] = (float)BitConverter.ToHalf(R50Vec, (row * R50Dim + d) * 2); return v; }
        if (!r50rows.TryGetValue(row, out var rb)) return null; for (int d = 0; d < R50Dim; d++) v[d] = (float)BitConverter.ToHalf(rb, d * 2); return v;
    }
    public int[] RowUpload = [], RowUnit = []; public int Count => RowUpload.Length; public string Dir = "";
    public HashSet<string> Ids => Uploads.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
    public int RowReads;
    readonly Dictionary<int, byte[]> rows = new();
    /// <summary>0.13.28: files over this size (vec.f16 of a 60k-actor index) are not read whole: a shortlist from codes.bin (in memory), then coalesced range reads.</summary>
    public static long MaxWholeVec = 32L << 20; public static long MaxRangeBytes = 4L << 20; public const int MaxRangeReads = 8;
    /// <summary>The linked unit-key pairs of every upload's units file (links.json of the version): what the next build folds into actors without re-reading carried-forward files.</summary>
    public Dictionary<string, List<(string Unit, string Other)>> Links = new(StringComparer.Ordinal);
    /// <summary>The 64-byte sign code of v - mean (bit d set when v[d] &gt; mean[d]); vectors shorter than 512 dims leave the rest 0.</summary>
    public static void CodeOf(ReadOnlySpan<float> v, ReadOnlySpan<float> mean, Span<byte> code) { code.Clear(); for (int d = 0; d < Math.Min(Math.Min(v.Length, mean.Length), SearchData.CodeBytes * 8); d++) if (v[d] - mean[d] > 0) code[d >> 3] |= (byte)(1 << (d & 7)); }

    void Index() { RowUpload = new int[Uploads.Sum(u => u.Units.Sum(x => x.Rows))]; RowUnit = new int[RowUpload.Length]; for (int i = 0; i < Uploads.Count; i++) for (int k = 0; k < Uploads[i].Units.Count; k++) { var u = Uploads[i].Units[k]; for (int r = 0; r < u.Rows; r++) { RowUpload[u.Row + r] = i; RowUnit[u.Row + r] = k; } } }

    public static async Task<(ActorsIndex? Idx, string? HeadETag)> LoadAsync(ActorsIO io, CancellationToken ct, bool withVec = false, BlobContent? head = null, long vecCap = long.MaxValue, bool links = true, int? announced = null, int dim = 512)
    {
        var paths = io.Paths; BlobContent? hc = null; JsonObject h;
        if (announced is > 0) h = new JsonObject { ["version"] = announced, ["dim"] = dim };   // 0.13.28: the queue announced the version: its immutable files are read directly, head.json is not
        else { hc = head ?? await io.GetAsync(ActorsData.HeadFile(paths), ct); if (hc == null) return (null, null); h = JsonNode.Parse(Encoding.UTF8.GetString(hc.Data)) as JsonObject ?? throw new InvalidDataException("actors/head.json: not an object"); }
        int v = (int?)h["version"] ?? 0; var dir = ActorsData.VersionDir(paths, v);
        var up = JsonNode.Parse(Encoding.UTF8.GetString((await io.GetAsync(dir + "uploads.json", ct) ?? throw new FileNotFoundException(dir + "uploads.json")).Data)) as JsonArray ?? [];
        var x = new ActorsIndex { Version = v, Dim = (int?)h["dim"] ?? 512, Dir = dir };
        foreach (var o in up.OfType<JsonObject>()) x.Uploads.Add(((string)o["id"]!, (string?)o["etag"] ?? "", (o["units"] as JsonArray)!.OfType<JsonObject>().Select(u => new IUnit((string)u["key"]!, (string)u["actor"]!, (string?)u["prod"] ?? "", (int)u["n"]!, (int)u["row"]!, (int)u["rows"]!, (int?)u["r50"] ?? -1)).ToList()));
        foreach (var o in up.OfType<JsonObject>()) { if ((string?)o["r50etag"] is { Length: > 0 } re) x.R50ETags[(string)o["id"]!] = re; if ((int?)o["r50dim"] is > 0 and var rd) x.R50Dim = rd; }
        if (withVec && links && await io.GetAsync(dir + "links.json", ct) is { } lk && JsonNode.Parse(Encoding.UTF8.GetString(lk.Data)) is JsonObject lo) foreach (var kv in lo) x.Links[kv.Key] = (kv.Value as JsonArray ?? []).OfType<JsonArray>().Select(p => ((string)p[0]!, (string)p[1]!)).ToList();
        if (withVec && links && await io.GetAsync(dir + "pending.json", ct) is { } pj && JsonNode.Parse(Encoding.UTF8.GetString(pj.Data)) is JsonObject po) foreach (var kv in po) x.Pending[kv.Key] = (kv.Value as JsonArray ?? []).OfType<JsonArray>().Select(p => ((string)p[0]!, (string)p[1]!, (double)p[2]!)).ToList();
        var mean = (await io.GetAsync(dir + "mean.f32", ct) ?? throw new FileNotFoundException(dir + "mean.f32")).Data; x.Mean = new float[mean.Length / 4]; Buffer.BlockCopy(mean, 0, x.Mean, 0, mean.Length);
        x.Codes = (await io.GetAsync(dir + "codes.bin", ct) ?? throw new FileNotFoundException(dir + "codes.bin")).Data; x.Index();
        if (x.Codes.Length != (long)x.Count * SearchData.CodeBytes) throw new InvalidDataException("actors index: codes.bin does not match uploads.json");
        if (withVec && (long)x.Count * x.Dim * 2 <= vecCap) { x.Vec = (await io.GetAsync(dir + "vec.f16", ct) ?? throw new FileNotFoundException(dir + "vec.f16")).Data; if (x.Vec.Length != (long)x.Count * x.Dim * 2) throw new InvalidDataException("actors index: vec.f16 does not match uploads.json"); }
        long r50n = x.Uploads.SelectMany(u => u.Units).Select(u => (long)u.R50 + 1).DefaultIfEmpty(0).Max();
        if (withVec && r50n > 0 && x.R50Dim > 0 && r50n * x.R50Dim * 2 <= vecCap) { x.R50Vec = (await io.GetAsync(dir + "r50.f16", ct) ?? throw new FileNotFoundException(dir + "r50.f16")).Data; if (x.R50Vec.Length != r50n * x.R50Dim * 2) throw new InvalidDataException("actors index: r50.f16 does not match uploads.json"); }
        return (x, hc?.ETag);
    }

    /// <summary>Hamming routing: the best <paramref name="top"/> distinct units (by their closest row) for a centre, after the allow filters (run on the routed candidates only, never on the whole index).</summary>
    public List<(int Upload, int Unit, int Dist)> Route(float[] centre, int top, Func<string, bool> allow, Func<IUnit, bool>? unitOk = null)
    {
        if (Count == 0) return []; var q = new byte[SearchData.CodeBytes]; CodeOf(centre, Mean, q); var best = new Dictionary<(int, int), int>();
        var qs = new ulong[8]; for (int i = 0; i < 8; i++) qs[i] = BitConverter.ToUInt64(q, i * 8);
        for (int r = 0; r < Count; r++)
        {
            int d = 0; for (int i = 0; i < 8; i++) d += BitOperations.PopCount(BitConverter.ToUInt64(Codes, r * SearchData.CodeBytes + i * 8) ^ qs[i]);
            var k = (RowUpload[r], RowUnit[r]); if (!best.TryGetValue(k, out var o) || d < o) best[k] = d;
        }
        var okUp = new Dictionary<int, bool>(); bool Ok(int u) { if (!okUp.TryGetValue(u, out var ok)) okUp[u] = ok = allow(Uploads[u].Id); return ok; }
        return best.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).Where(kv => Ok(kv.Key.Item1) && (unitOk == null || unitOk(Uploads[kv.Key.Item1].Units[kv.Key.Item2]))).Take(top).Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
    }


    /// <summary>The vector rows of a shortlist when vec.f16 is not in memory: sorted rows are merged into ranges (gap grown until at most <see cref="MaxRangeReads"/> reads, each at most <see cref="MaxRangeBytes"/>), read once; rows left out by the cap are dropped from the candidates.</summary>
    public Task PrefetchAsync(ActorsIO io, IEnumerable<int> wanted, CancellationToken ct, int budget = MaxRangeReads) => Vec != null ? Task.CompletedTask : PrefetchFileAsync(io, "vec.f16", Dim * 2, Count, rows, wanted, ct, budget);
    /// <summary>The same for the r50 rows of a shortlist when r50.f16 is not in memory.</summary>
    public Task PrefetchR50Async(ActorsIO io, IEnumerable<int> wanted, CancellationToken ct, int budget = MaxRangeReads) => R50Vec != null || R50Dim <= 0 ? Task.CompletedTask : PrefetchFileAsync(io, "r50.f16", R50Dim * 2, int.MaxValue, r50rows, wanted, ct, budget);

    async Task PrefetchFileAsync(ActorsIO io, string file, int rowBytes, int total, Dictionary<int, byte[]> have, IEnumerable<int> wanted, CancellationToken ct, int budget)
    {
        if (budget <= 0) return;
        var want = wanted.Where(r => !have.ContainsKey(r)).Distinct().OrderBy(r => r).ToList(); if (want.Count == 0) return;
        int maxRows = (int)Math.Max(1, MaxRangeBytes / rowBytes); List<(int From, int To, int N)> chunks = [];
        for (int gap = 0; ; gap = gap == 0 ? 1 : gap * 2)
        {
            chunks = []; int s = want[0], e = want[0], n = 1;
            for (int i = 1; i < want.Count; i++) { int r = want[i]; if (r - e - 1 <= gap && r - s + 1 <= maxRows) { e = r; n++; } else { chunks.Add((s, e, n)); s = e = r; n = 1; } }
            chunks.Add((s, e, n));
            if (chunks.Count <= budget || gap > total) break;
        }
        foreach (var (from, to, _) in chunks.OrderByDescending(c => c.N).Take(budget))
        {
            var rr = await io.GetRangeAsync(Dir + file, (long)from * rowBytes, (to - from + 1) * rowBytes, null, ct); RowReads++;
            if (rr.Data is not { Length: > 0 } || rr.Data.Length < (to - from + 1) * rowBytes) continue;
            foreach (var r in want) if (r >= from && r <= to) have[r] = rr.Data.AsSpan((r - from) * rowBytes, rowBytes).ToArray();
        }
    }

    // ---- 0.13.28: one copy per process per store. The queue is the change feed: the actors-index done line announces the version, and the immutable files of that version are read whole, once. head.json is read only on a cold start (no announcement seen yet). No timer.
    sealed class Cached { public readonly SemaphoreSlim Gate = new(1, 1); public ActorsIndex? Idx; public int Ver = -1; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Cached> cache = new();
    public static void ForgetCache() { foreach (var kv in cache) { kv.Value.Ver = -1; kv.Value.Idx = null; } }
    /// <summary>The index for the announced version (0 = none announced since this node started: one cold read of head.json, kept until an announcement arrives).</summary>
    public static async Task<ActorsIndex?> ForAnnouncedAsync(ActorsIO io, int announced, CancellationToken ct)
    {
        var c = cache.GetValue(io.Writes ? io.Store : io, _ => new Cached()); await c.Gate.WaitAsync(ct);
        try
        {
            if (c.Ver >= 0 && (announced == 0 || c.Ver >= announced)) return c.Idx;   // already holding this version (or newer): nothing is read
            ActorsIndex? idx; try { idx = (await LoadAsync(io, ct, withVec: true, vecCap: MaxWholeVec, links: false, announced: announced > 0 ? announced : null)).Idx; } catch (FileNotFoundException) when (announced > 0) { idx = null; }   // 0.13.32: an announced version deleted by `actors repair`: no index until the rebuild
            c.Idx = idx; c.Ver = idx?.Version ?? Math.Max(announced, 0); return idx;
        }
        finally { c.Gate.Release(); }
    }

    /// <summary>The exact cosine of a centre against each routed unit's centre row (one ranged read of dim x 2 bytes each, or from memory after a build).</summary>
    public async Task<List<(int Upload, int Unit, double Cos)>> ExactAsync(ActorsIO io, float[] centre, List<(int Upload, int Unit, int Dist)> routed, CancellationToken ct)
    {
        var res = new List<(int, int, double)>();
        foreach (var (u, k, _) in routed)
        {
            int row = Uploads[u].Units[k].Row; var v = new float[Dim];
            if (Vec != null) for (int d = 0; d < Dim; d++) v[d] = (float)BitConverter.ToHalf(Vec, (row * Dim + d) * 2);
            else if (rows.TryGetValue(row, out var rb)) { for (int d = 0; d < Dim; d++) v[d] = (float)BitConverter.ToHalf(rb, d * 2); }
            else continue;   // 0.13.28: a row outside the prefetched ranges (the per-job read cap) is not a candidate
            res.Add((u, k, CastLogic.Cosine(centre, v)));
        }
        return res.OrderByDescending(x => x.Item3).ToList();
    }

    /// <summary>Connected components of the linked units (an edge to a unit that has no file is ignored). Each component takes the older (smallest) of the actor ids its units already had, a new id when none had one, and every other id it held becomes a redirect to it. New ids are handed out in the order of each component's smallest unit key, so the result does not depend on the order the uploads ran in. The state is written with If-Match; on a lost race the fold is redone on the state that won.</summary>
    public static async Task<Dictionary<string, string>> FoldActorsAsync(ActorsIO io, List<string> keys, List<(string Unit, string Other)> edges, Dictionary<string, string> prev, CancellationToken ct)
    {
        var at = new Dictionary<string, int>(StringComparer.Ordinal); for (int i = 0; i < keys.Count; i++) at[keys[i]] = i;
        var parent = Enumerable.Range(0, keys.Count).ToArray(); int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var (a, b) in edges) if (at.TryGetValue(a, out var i) && at.TryGetValue(b, out var j)) { int ri = Find(i), rj = Find(j); if (ri != rj) parent[ri] = rj; }
        var comps = Enumerable.Range(0, keys.Count).GroupBy(Find).Select(g => g.Select(i => keys[i]).OrderBy(k => k, StringComparer.Ordinal).ToList()).OrderBy(c => c[0], StringComparer.Ordinal).ToList();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var (st, etag) = await ActorsData.LoadStateAsync(io, ct); var before = st.ToBytes(); var res = new Dictionary<string, string>(StringComparer.Ordinal); var now = Protocol.NowUtc();
            foreach (var c in comps)
            {
                var roots = c.Select(k => prev.GetValueOrDefault(k, "")).Where(a => a.Length > 0).Select(st.Resolve).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var id = roots.Count == 0 ? st.Alloc() : roots[0];
                if (roots.Count > 1) { var by = c.FirstOrDefault(k => !prev.ContainsKey(k)) ?? c[0]; foreach (var o in roots.Skip(1)) st.Merge(o, id, by, now); }
                foreach (var k in c) res[k] = id;
            }
            var after = st.ToBytes(); if (after.AsSpan().SequenceEqual(before) && etag != null) return res;   // nothing changed: nothing written
            if (await ActorsData.SaveStateAsync(io, st, etag, ct)) return res;
        }
        throw new IOException("actors/state.json kept changing");
    }

    public sealed record BuildReport(bool UpToDate, int Version, int Uploads, int Units, int Vectors, int Reused, int Read, string Note) { public int R50Rows { get; init; } public int R50Read { get; init; } public int Resolved { get; init; } public int Pending { get; init; } }

    /// <summary>Builds the next version from the units/ and r50/ listings, carrying forward the head's rows of every upload whose unit-file AND r50-file ETags did not change (only the changed files are read); head.json moves with If-Match. Band pairs a job had to skip (no r50 for a partner) are settled here from the r50 rows. <paramref name="full"/>: nothing is carried forward. Dry run: nothing written.</summary>
    public static async Task<BuildReport> BuildAsync(ActorsIO store, CancellationToken ct, Func<string, Task<bool>>? isAi = null, int keep = 2, double tiebreakCut = 0.64, bool full = false)
    {
        var paths = store.Paths; var (head, headEtag) = await LoadAsync(store, ct, withVec: true);
        var list = (await store.ListAsync(ActorsData.UnitsDir(paths), ct)).Where(e => e.Name.EndsWith(".bin", StringComparison.Ordinal)).ToList();
        var r50et = (await store.ListAsync(ActorsData.R50Dir(paths), ct)).Where(e => e.Name.EndsWith(".bin", StringComparison.Ordinal)).ToDictionary(e => Path.GetFileNameWithoutExtension(e.Name), e => e.ETag, StringComparer.Ordinal);
        var etags = list.ToDictionary(e => Path.GetFileNameWithoutExtension(e.Name), e => e.ETag, StringComparer.Ordinal); var manual = list.Where(e => e.Manual).Select(e => Path.GetFileNameWithoutExtension(e.Name)).ToHashSet(StringComparer.Ordinal);
        var old = head?.Uploads.ToDictionary(u => u.Id, StringComparer.Ordinal) ?? [];
        bool R50Same(string id) => (r50et.GetValueOrDefault(id) ?? "") == (head!.R50ETags.GetValueOrDefault(id) ?? "");
        bool same = !full && head != null && old.Count == etags.Count && etags.All(kv => old.TryGetValue(kv.Key, out var o) && o.ETag == kv.Value && kv.Value.Length > 0 && R50Same(kv.Key));
        if (same) return new BuildReport(true, head!.Version, head.Uploads.Count, head.Uploads.Sum(u => u.Units.Count), head.Count, head.Uploads.Count, 0, $"up to date (v{head.Version})");
        int reused = 0, read = 0, r50read = 0, dim = head?.Dim ?? 0, r50dim = head?.R50Dim ?? 0; float[]? mean = head?.Mean; var docs = new List<(string Id, string ETag, UnitDoc? Doc, R50Doc? R50)>(); var r50tag = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, et) in etags.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!full && head != null && old.TryGetValue(id, out var o) && o.ETag == et && et.Length > 0 && R50Same(id)) { docs.Add((id, et, null, null)); reused++; if (head.R50ETags.TryGetValue(id, out var ot)) r50tag[id] = ot; continue; }
            var c = await store.GetAsync(ActorsData.UnitFile(paths, id), ct); if (c == null) continue; var d = UnitDoc.FromBytes(c.Data, ActorsData.UnitFile(paths, id)); read++;
            if (isAi != null && !manual.Contains(id) && !d.Manual && await isAi(id)) continue;   // flagged since its units were written: no entry
            R50Doc? rd = null; if (r50et.ContainsKey(id) && await store.GetAsync(ActorsData.R50File(paths, id), ct) is { } rc) { rd = R50Doc.FromBytes(rc.Data, ActorsData.R50File(paths, id)); r50read++; r50tag[id] = rc.ETag ?? r50et[id]; if (r50dim == 0 && rd.Units.Count > 0) r50dim = rd.Dim; }
            docs.Add((id, c.ETag ?? et, d, rd)); if (dim == 0) dim = d.Dim;
        }
        if (mean == null) { var cs = docs.Where(x => x.Doc != null).SelectMany(x => x.Doc!.Units.Where(u => u.Vec.Length > 0).Select(u => u.Vec[0])).ToList(); mean = cs.Count == 0 ? new float[Math.Max(dim, 1)] : CastLogic.Centroid(cs); }
        int total = docs.Sum(x => x.Doc == null ? old[x.Id].Units.Sum(u => u.Rows) : x.Doc.Units.Sum(u => u.Vec.Length));
        int total50 = docs.Sum(x => x.Doc == null ? old[x.Id].Units.Count(u => u.R50 >= 0) : x.R50 == null || x.R50.Dim != r50dim ? 0 : x.Doc.Units.Count(u => x.R50.Units.ContainsKey(u.Key)));
        var codes = new byte[(long)total * SearchData.CodeBytes]; var vec = new byte[(long)total * dim * 2]; var vec50 = new byte[(long)total50 * r50dim * 2]; var ups = new List<(string Id, string ETag, List<IUnit> Units)>(); int row = 0, row50 = 0;
        var r50ByKey = new Dictionary<string, float[]>(StringComparer.Ordinal); var r50HeadRow = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, et, doc, rd) in docs)
        {
            var units = new List<IUnit>();
            if (doc == null) foreach (var u in old[id].Units)
            {
                int r5 = -1; if (u.R50 >= 0) { r5 = row50++; Buffer.BlockCopy(head!.R50Vec!, u.R50 * r50dim * 2, vec50, r5 * r50dim * 2, r50dim * 2); r50HeadRow[u.Key] = u.R50; }
                units.Add(u with { Row = row, R50 = r5 }); Buffer.BlockCopy(head!.Codes, u.Row * SearchData.CodeBytes, codes, row * SearchData.CodeBytes, u.Rows * SearchData.CodeBytes); Buffer.BlockCopy(head.Vec!, u.Row * dim * 2, vec, row * dim * 2, u.Rows * dim * 2); row += u.Rows;
            }
            else foreach (var u in doc.Units)
            {
                int r5 = -1; if (rd != null && rd.Dim == r50dim && rd.Units.TryGetValue(u.Key, out var rv)) { r5 = row50++; for (int d = 0; d < r50dim; d++) BitConverter.TryWriteBytes(vec50.AsSpan((r5 * r50dim + d) * 2, 2), (Half)rv[d]); r50ByKey[u.Key] = rv; }
                units.Add(new IUnit(u.Key, u.Actor, doc.Prod, u.N, row, u.Vec.Length, r5)); foreach (var v in u.Vec) { CodeOf(v, mean, codes.AsSpan(row * SearchData.CodeBytes, SearchData.CodeBytes)); for (int d = 0; d < dim; d++) BitConverter.TryWriteBytes(vec.AsSpan((row * dim + d) * 2, 2), (Half)v[d]); row++; }
            }
            ups.Add((id, et, units));
        }
        // actors are formed HERE (one writer): the linked unit pairs of every units file fold into connected components; a component keeps the id its units had, a merge keeps the older id and records a redirect in state.json
        var links = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal); var pending = new Dictionary<string, List<(string Unit, string Other, double Cos)>>(StringComparer.Ordinal);
        foreach (var (id, _, doc, _) in docs)
        {
            links[id] = doc == null ? [.. head!.Links.GetValueOrDefault(id) ?? []] : doc.Links.OfType<JsonObject>().Where(l => (bool?)l["linked"] == true).Select(l => ((string)l["unit"]!, (string)l["other"]!)).ToList();
            var pl = doc == null ? head!.Pending.GetValueOrDefault(id) ?? [] : doc.Links.OfType<JsonObject>().Where(l => (bool?)l["pending"] == true).Select(l => ((string)l["unit"]!, (string)l["other"]!, (double?)l["cos"] ?? 0)).ToList();
            if (pl.Count > 0) pending[id] = pl;
        }
        // the band pairs a job skipped (a partner with no r50 yet): settled from the r50 rows now that the partner's file may be here; both vectors needed, else the pair stays pending (pairs whose units are gone are dropped)
        var keySet = ups.SelectMany(u => u.Units.Select(x => x.Key)).ToHashSet(StringComparer.Ordinal); int resolved = 0;
        float[]? R50Key(string k) { if (r50ByKey.TryGetValue(k, out var v)) return v; if (r50HeadRow.TryGetValue(k, out var hr) && head?.R50Vec != null) { v = new float[r50dim]; for (int d = 0; d < r50dim; d++) v[d] = (float)BitConverter.ToHalf(head.R50Vec, (hr * r50dim + d) * 2); return r50ByKey[k] = v; } return null; }
        foreach (var id in pending.Keys.ToList())
        {
            var still = new List<(string Unit, string Other, double Cos)>();
            foreach (var p in pending[id])
            {
                if (!keySet.Contains(p.Unit) || !keySet.Contains(p.Other)) continue;
                if (R50Key(p.Unit) is not { } va || R50Key(p.Other) is not { } vb) { still.Add(p); continue; }
                resolved++; if (CastLogic.Cosine(va, vb) >= tiebreakCut) links[id].Add((p.Unit, p.Other));
            }
            if (still.Count > 0) pending[id] = still; else pending.Remove(id);
        }
        var prevActor = new Dictionary<string, string>(StringComparer.Ordinal); if (head != null) foreach (var u in head.Uploads) foreach (var x in u.Units) prevActor[x.Key] = x.Actor;
        var actorOf = await FoldActorsAsync(store, ups.SelectMany(u => u.Units.Select(x => x.Key)).ToList(), links.Values.SelectMany(x => x).ToList(), prevActor, ct);
        ups = ups.Select(u => (u.Id, u.ETag, u.Units.Select(x => x with { Actor = actorOf[x.Key] }).ToList())).ToList();
        int ver = (head?.Version ?? 0) + 1; int nUnits = ups.Sum(u => u.Units.Count); int nPending = pending.Sum(kv => kv.Value.Count);
        var rep = new BuildReport(false, ver, ups.Count, nUnits, row, reused, read, $"v{ver}: {ups.Count} uploads, {nUnits} units, {row} vectors, {row50} r50 rows ({reused} uploads carried forward, {read} read, {r50read} r50 files read; {resolved} skipped band pairs settled, {nPending} still pending)") { R50Rows = row50, R50Read = r50read, Resolved = resolved, Pending = nPending };
        if (store.Dry) return rep with { Note = "would write " + rep.Note };
        var dir = ActorsData.VersionDir(paths, ver);
        var uj = new JsonArray(ups.Select(u => { var o = new JsonObject { ["id"] = u.Id, ["etag"] = u.ETag, ["units"] = new JsonArray(u.Units.Select(x => { var xo = new JsonObject { ["key"] = x.Key, ["actor"] = x.Actor, ["prod"] = x.Prod, ["n"] = x.N, ["row"] = x.Row, ["rows"] = x.Rows }; if (x.R50 >= 0) xo["r50"] = x.R50; return (JsonNode)xo; }).ToArray()) }; if (r50tag.TryGetValue(u.Id, out var rt)) o["r50etag"] = rt; if (r50dim > 0 && u.Units.Any(x => x.R50 >= 0)) o["r50dim"] = r50dim; return (JsonNode)o; }).ToArray());
        await store.PutAsync(dir + "links.json", Encoding.UTF8.GetBytes(new JsonObject(links.Where(kv => kv.Value.Count > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonArray(kv.Value.Select(p => (JsonNode)new JsonArray(p.Item1, p.Item2)).ToArray())))).ToJsonString()), null, false, false, ct);
        await store.PutAsync(dir + "pending.json", Encoding.UTF8.GetBytes(new JsonObject(pending.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonArray(kv.Value.Select(p => (JsonNode)new JsonArray(p.Unit, p.Other, p.Cos)).ToArray())))).ToJsonString()), null, false, false, ct);
        var mb = new byte[mean.Length * 4]; Buffer.BlockCopy(mean, 0, mb, 0, mb.Length);
        await store.PutAsync(dir + "uploads.json", Encoding.UTF8.GetBytes(uj.ToJsonString()), null, false, false, ct); await store.PutAsync(dir + "mean.f32", mb, null, false, false, ct);
        await store.PutAsync(dir + "codes.bin", codes, null, false, false, ct); await store.PutAsync(dir + "vec.f16", vec, null, false, false, ct);
        if (row50 > 0) await store.PutAsync(dir + "r50.f16", vec50, null, false, false, ct);
        var hb = Encoding.UTF8.GetBytes(new JsonObject { ["version"] = ver, ["dim"] = dim, ["r50dim"] = r50dim, ["uploads"] = ups.Count, ["units"] = nUnits, ["vectors"] = row, ["r50rows"] = row50, ["builtAt"] = Protocol.NowUtc() }.ToJsonString());
        if (!await store.PutAsync(ActorsData.HeadFile(paths), hb, headEtag, headEtag == null, false, ct)) throw new IOException("actors/head.json moved while this build ran (another build won); nothing is lost, run again");
        for (int o2 = ver - keep; o2 >= 1; o2--) { var names = (await store.ListAsync(ActorsData.VersionDir(paths, o2), ct)).Select(e => e.Name).ToList(); if (names.Count == 0) break; foreach (var n in names) await store.DeleteAsync(n, ct); }
        return rep;
    }
}
