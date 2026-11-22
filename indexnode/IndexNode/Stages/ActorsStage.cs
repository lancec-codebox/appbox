using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// The "actors" queue stage (one upload per run, after its group step; writes only index/actors/units/&lt;id&gt;.bin and r50/&lt;id&gt;.bin of THAT upload, plus the CAS-guarded state.json).
/// Faces: a brand-new (or mixed) drama uses all its cast faces; a join with a retained file uses only the faces on the retained frames. Units as in ActorsBuild (recursive split, 14+ faces, child probe).
/// Candidates: the units of uploads not in the actor index yet (read directly, like the group step reads earlier dramas) and the actor index (hamming routing, then an exact cosine re-check of the top units).
/// An AI-flagged upload (video.json ai.flag, checked again here) is skipped completely unless it was added by hand (job reason "manual" / actors add): no units, no links, no entry; AI-flagged candidates are dropped BEFORE any of their data is read.
/// </summary>
public sealed class ActorsStage(IStore store, ActorsBuild.Probe? probe = null, double tiebreakCut = 0.64, bool dry = false, string? outLocal = null) : IStageHandler
{
    public const int Top = 20, R50Faces = 30; public const string ManualReason = "manual";
    public Func<IFaceEmbedder>? MakeR50 { get; init; } public Func<CancellationToken, Task<string>>? R50Path { get; init; } public Func<byte[], Rgb> Decode { get; init; } = CastReembed.DecodeYuv;
    public RunResult? Last { get; private set; }

    public sealed record RunResult(string Id, string? Skipped, int FacesUsed, int Units, int ChildUnits, int Links, int Band, int BandLinked, int R50Units, int Predecessors, int PredecessorsAi, int IndexRouted, string Kind, string Prod, List<string> Actors, List<string> Writes) { public string Timing { get; init; } = ""; }
    sealed record Cand(string Id, string Key, string Actor, string Prod, double Cos, UnitRec? Rec);

    public static bool AiFlag(JsonObject? root) => (root?["ai"] as JsonObject)?["flag"] is JsonNode f && (bool?)f == true;

    public async Task RunAsync(JobContext ctx)
    {
        var io = new ActorsIO(ctx.Store, ctx.Paths, dry, outLocal); ctx.Report("actors", "units");
        var r = await RunAsync(io, ctx.BlobName, ctx.Job.Reason == ManualReason, Math.Max(1, ctx.ThreadShare()), ctx.Token, null, ctx.Queue?.Read(st => st.Get(ctx.BlobName)?.Seq is { } me ? st.Subjects.Values.Where(x => x.Seq is { } q && q < me).Select(x => x.Id).ToList() : new List<string>())); Last = r;
        if (ctx.Queue != null && ctx.Queue.Read(st => ActorsIndexJobs.AddLine(st, DateTimeOffset.UtcNow, ActorsIndexJobs.Delay)) is { } il) ctx.FollowUps.Add(il);   // the index follows (debounced): the single writer of actors
        Log.Info($"actors {r.Id}: " + (r.Skipped ?? $"{r.Units} units ({r.FacesUsed} faces, {r.ChildUnits} child), {r.Links} links ({r.BandLinked}/{r.Band} band), predecessors {r.Predecessors}, index {r.IndexRouted}; {r.Timing}"));
    }

    static bool HasTime(float[] sorted, double t) { int i = Array.BinarySearch(sorted, (float)t); if (i >= 0) return true; i = ~i; return (i < sorted.Length && Math.Abs(sorted[i] - t) < 0.02) || (i > 0 && Math.Abs(sorted[i - 1] - t) < 0.02); }

    public static long? SeqOf(JsonObject? g) => g?["seq"] is JsonNode n && long.TryParse(n.ToString(), out var q) ? q : null;

    /// <summary>The predecessors of an upload: every upload with a lower grouping.seq and a grouping record in its video.json, in seq order. Read from the aggregate (one list + one GET for all of them); <paramref name="known"/> are ids the queue knows (with a seq) that the aggregate may lack: those are read one by one. The queue's group entry state is NOT consulted (uploads grouped before the queue tracked it have none).</summary>
    public static async Task<List<string>> PredecessorsAsync(IStore store, Paths paths, string id, long? mySeq, IEnumerable<string>? known, CancellationToken ct)
    {
        if (mySeq is not { } me) return [];
        var all = await VideoJson.ReadManyAsync(store, paths, new List<string>(), ct, all: true);
        var seqs = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var kv in all) if (kv.Key != id && SeqOf(kv.Value?.Root["grouping"] as JsonObject) is { } q && q < me) seqs[kv.Key] = q;
        var extra = (known ?? []).Where(k => k != id && !all.ContainsKey(k)).Distinct(StringComparer.Ordinal).ToList();
        if (extra.Count > 0) foreach (var kv in await VideoJson.ReadManyAsync(store, paths, extra, ct)) if (SeqOf(kv.Value?.Root["grouping"] as JsonObject) is { } q && q < me) seqs[kv.Key] = q;   // not in the aggregate yet: the per-upload files
        return seqs.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).ToList();
    }

    // predecessor units computed from cast files, kept for later uploads of this process (key: store, id, the grouping record they were computed from)
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Prod, List<UnitRec> Units)> unitCache = new(StringComparer.Ordinal);

    sealed class Built { public string Prod = "", Kind = "new"; public ActorsBuild.Drama D = null!; public List<UnitRec> Units = []; public int Dim, FacesUsed; }

    /// <summary>The units of ONE upload from its cast files (cast/faces.json, faces.f16, clusters.json) and its grouping record: a join uses only the faces on its retained frames, anything else all faces. The same code makes an upload's own units and a predecessor's, so the keys are identical.</summary>
    async Task<Built> BuildUnitsAsync(ActorsIO io, string id, JsonObject rec, Dictionary<string, string> groupOf, CancellationToken ct)
    {
        var paths = io.Paths; var prop = rec["proposal"] as JsonObject; var kind = (string?)prop?["kind"] ?? "new"; var joinUp = (string?)prop?["joinUpload"]; var retained = (string?)rec["retainedFile"];
        Func<FaceRec, bool>? use = null;
        if (kind == GroupProposal.JoinKind && prop?["mixed"]?.GetValue<bool>() != true && retained != null)
        {
            var z = await FramesReader.OpenAsync(io.Store, retained, ct) ?? throw new FileNotFoundException(retained); var h = await z.ReadHeaderAsync(ct);
            var sorted = (await z.ReadBlocksAsync(Enumerable.Range(0, h.K).ToList(), CenterSearch.MergeGap, CenterSearch.MaxMerged, ct)).SelectMany(b => b.Members.Times).OrderBy(x => x).ToArray(); use = f => HasTime(sorted, f.T);
        }
        var prod = groupOf.GetValueOrDefault(id) ?? (joinUp != null ? groupOf.GetValueOrDefault(joinUp) ?? joinUp : id); groupOf[id] = prod;
        var d = await ActorsBuild.LoadDramaAsync(io.Store, paths, id, groupOf, probe, ct, use, manual: true);   // the AI rule is applied by the caller
        var b2 = new Built { Prod = prod, Kind = kind, D = d, Dim = d.Faces.FirstOrDefault(f => f.Emb.Length > 0)?.Emb.Length ?? 0, FacesUsed = d.Faces.Count(f => f.Emb.Length > 0 && (use == null || use(f))) };
        foreach (var u in d.Units)
        {
            var pick = CastReembed.PickPerGroup(u.Faces.Select(f => (I: f, G: 0, T: d.Faces[f].T, Q: d.Faces[f].Quality)), R50Faces);
            b2.Units.Add(new UnitRec { Key = u.Key, N = u.N, First = u.First, Last = u.Last, Child = u.Child, Faces = u.Faces.ToArray(), Rep = u.RepFaces.ToArray(), Pick = pick, Vec = new[] { u.Centre }.Concat(u.RepFaces.Select(i => d.Faces[i].Emb)).ToArray() });
        }
        return b2;
    }

    public async Task<RunResult> RunAsync(ActorsIO io, string id, bool manual, int threads, CancellationToken ct, IReadOnlyList<string>? preds = null, IReadOnlyList<string>? extraPreds = null)
    {
        var paths = io.Paths; var now = Protocol.NowUtc(); var sw = System.Diagnostics.Stopwatch.StartNew(); long msOwn = 0, msPred = 0, msRoute = 0, msR50 = 0; int predComputed = 0, predReused = 0, predCached = 0, predWritten = 0;
        var vj = await VideoJson.ReadAsync(io.Store, paths, id, ct); var uf = ActorsData.UnitFile(paths, id);
        var ex = await io.GetAsync(uf, ct); var exDoc = ex == null ? null : UnitDoc.FromBytes(ex.Data, uf); manual |= exDoc?.Manual == true || (await io.ListAsync(uf, ct)).Any(e => e.Name == uf && e.Manual);
        if (!manual && AiFlag(vj?.Root))
        {
            var w = new List<string>(); if (ex != null) { await io.DeleteAsync(uf, ct); w.Add("delete " + uf); }
            return Last = new RunResult(id, "AI-flagged (ai.flag): no units, no links, no index entry", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", "", [], w);
        }
        var rec = vj?.Root["grouping"] as JsonObject ?? throw new InvalidOperationException($"{id}: no grouping record in video.json (the group step has not run)");
        var groupOf = new Dictionary<string, string>(StringComparer.Ordinal);
        try { if (await io.Store.GetAsync(SearchData.HeadName(paths), ct) is { } hc && await GroupStore.LoadAsync(io.Store, paths, SearchData.ParseHead(hc.Data), ct) is { } gs) groupOf = gs.GroupOf(); } catch (Exception e) when (e is FileNotFoundException or InvalidDataException) { }
        preds ??= await PredecessorsAsync(io.Store, paths, id, SeqOf(rec), extraPreds, ct);
        var me = await BuildUnitsAsync(io, id, rec, groupOf, ct); msOwn = sw.ElapsedMilliseconds; var d = me.D; var mine = me.Units; var kind = me.Kind; var prod = me.Prod; int dim = me.Dim;
        // candidates: the actor index for everything in it, and the predecessors that are not in it yet, computed right here from their cast files (their own stage run is never waited for or read)
        var (idx, _) = await ActorsIndex.LoadAsync(io, ct); var indexed = idx?.Ids ?? [];
        var listed = await io.ListAsync(ActorsData.UnitsDir(paths), ct); var manualIds = listed.Where(e => e.Manual).Select(e => Path.GetFileNameWithoutExtension(e.Name)).ToHashSet(StringComparer.Ordinal);
        var aiCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var predDocs = await VideoJson.ReadManyAsync(io.Store, paths, (preds ?? []).Where(p => p != id).Distinct(StringComparer.Ordinal).ToList(), ct);   // the aggregate: one block-list + one GET for every predecessor's video.json, no per-upload reads
        async Task<bool> Ai(string up) { if (manualIds.Contains(up)) return false; if (!aiCache.TryGetValue(up, out var f)) aiCache[up] = f = AiFlag((predDocs.TryGetValue(up, out var pd) ? pd : await VideoJson.ReadAsync(io.Store, paths, up, ct))?.Root); return f; }
        var pend = new List<(string Id, string Prod, List<UnitRec> Units)>(); int aiSkipped = 0, missing = 0;
        foreach (var up in (preds ?? []).Where(p => p != id && !indexed.Contains(p)).Distinct(StringComparer.Ordinal))
        {
            if (await Ai(up)) { aiSkipped++; continue; }   // an AI predecessor: its cast data is never read
            if ((predDocs.TryGetValue(up, out var pdoc) ? pdoc : await VideoJson.ReadAsync(io.Store, paths, up, ct))?.Root["grouping"] is not JsonObject prec) { missing++; continue; }
            try
            {
                var puf = ActorsData.UnitFile(paths, up);   // 1) its units file, when its own run (or an earlier run of ours) wrote it: a cache with the same keys, never a wait
                if (await io.GetAsync(puf, ct) is { } pc) { var pdoc2 = UnitDoc.FromBytes(pc.Data, puf); pend.Add((up, pdoc2.Prod, pdoc2.Units)); predReused++; continue; }
                var ckey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(io.Store) + "|" + up + "|" + prec.ToJsonString().GetHashCode();
                if (unitCache.TryGetValue(ckey, out var hit)) { pend.Add((up, hit.Prod, hit.Units)); predCached++; continue; }   // 2) computed earlier in this process
                var b = await BuildUnitsAsync(io, up, prec, groupOf, ct); pend.Add((up, b.Prod, b.Units)); predComputed++;   // 3) from its cast files, then kept
                if (unitCache.Count > 400) unitCache.Clear(); unitCache[ckey] = (b.Prod, b.Units);
                var wdoc = new UnitDoc { Id = up, Prod = b.Prod, Kind = b.Kind, At = now, Manual = false, Dim = b.Dim, FacesUsed = b.FacesUsed, Units = b.Units, Links = [] };
                if (await io.PutAsync(puf, wdoc.ToBytes(), null, true, false, ct)) predWritten++;   // create-only: the next run reuses it; its own stage run replaces it (with the ETag) when it adds the links
            }
            catch (Exception e) when (e is InvalidOperationException or FileNotFoundException or InvalidDataException) { missing++; }   // no cast files yet: nothing to compare
        }
        msPred = sw.ElapsedMilliseconds - msOwn;
        var pendUnits = pend.ToDictionary(p => p.Id, p => p.Units, StringComparer.Ordinal);
        var aiIdx = new HashSet<string>(StringComparer.Ordinal); var cands = new Dictionary<string, List<Cand>>(StringComparer.Ordinal); int routed = 0;
        foreach (var u in mine)
        {
            var list = new List<Cand>();
            foreach (var (up, pprod, punits) in pend) foreach (var pu in punits) { if (pprod == prod || pu.Vec.Length == 0) continue; double c = CastLogic.Cosine(u.Vec[0], pu.Vec[0]); if (c >= ActorsBuild.BandLow) list.Add(new Cand(up, pu.Key, "", pprod, c, pu)); }
            if (idx != null && idx.Count > 0)
            {
                var rt2 = idx.Route(u.Vec[0], Top, up => up != id && !aiIdx.Contains(up), iu => iu.Prod != prod);
                var survive = new List<(int Upload, int Unit, int Dist)>(); foreach (var rr in rt2) { var upId = idx.Uploads[rr.Upload].Id; if (aiIdx.Contains(upId)) continue; if (await Ai(upId)) { aiIdx.Add(upId); continue; } survive.Add(rr); }   // only the routed survivors are checked, never the whole index
                routed += survive.Count;
                foreach (var (iu, ik, cos) in await idx.ExactAsync(io, u.Vec[0], survive, ct)) { var x = idx.Uploads[iu].Units[ik]; if (cos >= ActorsBuild.BandLow) list.Add(new Cand(idx.Uploads[iu].Id, x.Key, "", x.Prod, cos, null)); }
            }
            cands[u.Key] = list;
        }
        msRoute = sw.ElapsedMilliseconds - msOwn - msPred;
        // the r50 tiebreak: only for units in a 0.70-0.75 pair, computed once and stored
        var need = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var u in mine) foreach (var c in cands[u.Key].Where(c => c.Cos < ActorsBuild.DirectCut)) { (need.TryGetValue(id, out var a) ? a : need[id] = []).Add(u.Key); (need.TryGetValue(c.Id, out var b) ? b : need[c.Id] = []).Add(c.Key); }
        var r50 = new Dictionary<(string, string), float[]>(); int r50New = 0;
        foreach (var (up, keys) in need.OrderBy(k => k.Key, StringComparer.Ordinal)) r50New += await EnsureR50Async(io, up, keys, up == id ? mine : pendUnits.GetValueOrDefault(up), r50, threads, ct);
        msR50 = sw.ElapsedMilliseconds - msOwn - msPred - msRoute;
        // the links of this upload as unit-key pairs (cosine, method, r50 score); actors are formed by the index build, never here
        var links = new JsonArray(); int nLinks = 0, band = 0, bandOk = 0;
        foreach (var u in mine)
            foreach (var c in cands[u.Key].OrderByDescending(c => c.Cos))
            {
                bool direct = c.Cos >= ActorsBuild.DirectCut; double? rv = null; bool ok = direct;
                if (!direct) { band++; if (r50.TryGetValue((id, u.Key), out var a) && r50.TryGetValue((c.Id, c.Key), out var b)) rv = CastLogic.Cosine(a, b); ok = rv >= tiebreakCut; if (ok) bandOk++; }
                links.Add(new JsonObject { ["unit"] = u.Key, ["other"] = c.Key, ["cos"] = Math.Round(c.Cos, 4), ["method"] = direct ? "direct" : "tiebreak", ["r50"] = rv == null ? null : Math.Round(rv.Value, 4), ["linked"] = ok });
                if (ok) nLinks++;
            }
        var doc = new UnitDoc { Id = id, Prod = prod, Kind = kind, At = now, Manual = manual, Dim = dim, FacesUsed = me.FacesUsed, Units = mine, Links = links };
        if (!await io.PutAsync(uf, doc.ToBytes(), ex?.ETag, ex == null, manual, ct)) throw new IOException($"{uf} changed while this run wrote it (two writers for one upload?)");
        return Last = new RunResult(id, null, doc.FacesUsed, mine.Count, d.ChildUnits, nLinks, band, bandOk, r50New, pend.Count, aiSkipped + aiIdx.Count, routed, kind, prod, [], io.Log.ToList()) { Timing = $"time own units {msOwn / 1000.0:F1}s, predecessors {msPred / 1000.0:F1}s (computed {predComputed}, reused from units files {predReused}, from process cache {predCached}, units files written {predWritten}), routing {msRoute / 1000.0:F1}s, r50 {msR50 / 1000.0:F1}s" };
    }

    /// <summary>The r50 centres of <paramref name="keys"/> of upload <paramref name="up"/>: from r50/&lt;up&gt;.bin when stored (never recomputed), else computed now (up to 30 faces per unit, w600k_r50 fp32) and merged into that file with a CAS. Returns how many units were computed.</summary>
    async Task<int> EnsureR50Async(ActorsIO io, string up, HashSet<string> keys, List<UnitRec>? own, Dictionary<(string, string), float[]> into, int threads, CancellationToken ct)
    {
        var paths = io.Paths; var rf = ActorsData.R50File(paths, up);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var cur = await io.GetAsync(rf, ct); var doc = cur == null ? new R50Doc() : R50Doc.FromBytes(cur.Data, rf);
            foreach (var k in keys) if (doc.Units.TryGetValue(k, out var v)) into[(up, k)] = v;
            var todo = keys.Where(k => !doc.Units.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList(); if (todo.Count == 0) return 0;
            var units = own ?? UnitDoc.FromBytes((await io.GetAsync(ActorsData.UnitFile(paths, up), ct) ?? throw new FileNotFoundException(ActorsData.UnitFile(paths, up))).Data, ActorsData.UnitFile(paths, up)).Units;
            var pick = todo.ToDictionary(k => k, k => units.First(u => u.Key == k).Pick); var all = pick.Values.SelectMany(x => x).Distinct().OrderBy(x => x).ToArray();
            if (MakeR50 == null) throw new InvalidOperationException("the r50 tiebreaker is not available (no model)");
            var res = (await CastReembed.RunManyAsync(io.Store, paths, up, [new("w600k_r50", MakeR50)], Decode, threads, ct, onlyFaces: all))[0];
            var row = new Dictionary<int, int>(); for (int k = 0; k < res.Idx!.Length; k++) row[res.Idx[k]] = k; doc.Dim = res.Dim;
            foreach (var (k, fs) in pick) { var vs = fs.Select(f => { var v = new float[res.Dim]; for (int d = 0; d < res.Dim; d++) v[d] = BitConverter.ToSingle(res.F32, (row[f] * res.Dim + d) * 4); return v; }).ToList(); if (vs.Count > 0) { doc.Units[k] = CastLogic.Centroid(vs); into[(up, k)] = doc.Units[k]; } }
            if (await io.PutAsync(rf, doc.ToBytes(), cur?.ETag, cur == null, false, ct)) return todo.Count;   // lost a race: read again (the winner may have computed the same units)
        }
        throw new IOException($"{rf} kept changing");
    }
}
