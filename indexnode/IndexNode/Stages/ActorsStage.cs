using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// The "actors" queue stage (one upload per run, after its group step and its cast step at v2; writes only index/actors/units/&lt;id&gt;.bin and r50/&lt;id&gt;.bin of THAT upload, plus the CAS-guarded state.json).
/// 0.13.32: the repaired cast state of an upload (units + one r50 vector per unit) is made by the cast-repair stage (<see cref="RepairAsync"/>), from the upload's own cast.zip and frames.zip, read once each. This stage reads that state (units file + r50 file: no zip) and, only when it is missing or old (a manual run), makes it inline.
/// A predecessor's data comes from the actor index (units and r50 rows), else from its repaired files; only a predecessor whose cast-repair is not done (it runs on another lane right now) is read and computed here from its cast and frames, in memory, never written.
/// Faces: a brand-new (or mixed) drama uses all its cast faces; a join with a retained file uses only the faces on the retained frames. Units as in ActorsBuild (recursive split, 14+ faces, child probe).
/// Candidates: the units of uploads not in the actor index yet (read directly, like the group step reads earlier dramas) and the actor index (hamming routing, then an exact cosine re-check of the top units).
/// An AI-flagged upload (video.json ai.flag, checked again here) is skipped completely unless it was added by hand (job reason "manual" / actors add): no units, no links, no entry; AI-flagged candidates are dropped BEFORE any of their data is read.
/// </summary>
public sealed class ActorsStage(IStore store, ActorsBuild.Probe? probe = null, double tiebreakCut = 0.64, bool dry = false, string? outLocal = null) : IStageHandler
{
    public const int Top = 20, R50Faces = 30; public const string ManualReason = "manual", RepairReason = "repair";
    public Func<IFaceEmbedder>? MakeR50 { get; init; } public Func<CancellationToken, Task<string>>? R50Path { get; init; } public Func<byte[], Rgb> Decode { get; init; } = CastReembed.DecodeYuv;
    public RunResult? Last { get; private set; }

    public sealed record RunResult(string Id, string? Skipped, int FacesUsed, int Units, int ChildUnits, int Links, int Band, int BandLinked, int R50Units, int Predecessors, int PredecessorsAi, int IndexRouted, string Kind, string Prod, List<string> Actors, List<string> Writes) { public string Timing { get; init; } = ""; public int PairsSkipped { get; init; } public int PartnerR50 { get; init; } public bool Reused { get; init; } }
    sealed record Cand(string Id, string Key, string Actor, string Prod, double Cos, UnitRec? Rec, int IUp = -1, int IUnit = -1);

    public static bool AiFlag(JsonObject? root) => AiHold.Flagged(root);   // 0.13.44: the owner's verdict wins over the probe's flag

    /// <summary>0.13.28: the one path the node and `actors run` share: predecessors, AI flags, groups and the announced index version all come from the processing queue's state (snapshot + tail, loaded once). No groups file, no head.yaml, no aggregate.</summary>
    public async Task<RunResult> RunWithQueueAsync(ActorsIO io, JobQueue q, string id, bool manual, int threads, CancellationToken ct)
    {
        long? mySeq = q.Read(st => st.Get(id)?.Seq); if (mySeq == null) mySeq = SeqOf((await VideoJson.ReadAsync(io.Store, io.Paths, id, ct))?.Root["grouping"] as JsonObject);   // the queue has no seq for it yet: its own video.json says
        List<string> qpreds = q.Read(st => mySeq is { } me ? st.Subjects.Values.Where(x => x.Seq is { } sq && sq < me && PredKindOf(st, x.Id) != PredKind.Skip).OrderBy(x => x.Seq).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Id).ToList() : new List<string>());
        return await RunAsync(io, id, manual, threads, ct, qpreds, null, up => q.Read(st => st.Entry(up, "actors")?.Hold == "ai" || st.Entry(up, "process")?.Hold == "ai"), q.Read(GroupOfQueue), q.Read(st => st.ActorsIndexVer), up => q.Read(st => PredKindOf(st, up)), repaired: up => q.Read(st => st.Get(up)?.VerOf("cast") >= StepVersions.CastRepaired));
    }

    public async Task RunAsync(JobContext ctx)
    {
        var io = new ActorsIO(ctx.Store, ctx.Paths, dry, outLocal); ctx.Report("actors", "units");
        var q = ctx.Queue; List<string>? qpreds = q?.Read(st => st.Get(ctx.BlobName)?.Seq is { } me ? st.Subjects.Values.Where(x => x.Seq is { } sq && sq < me && PredKindOf(st, x.Id) != PredKind.Skip).OrderBy(x => x.Seq).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Id).ToList() : new List<string>());
        // 0.13.27: predecessors (a) and which are AI (b) come from the queue this node holds; its own grouping record (c) from its own video.json. No read of the aggregate.
        var r = await RunAsync(io, ctx.BlobName, ctx.Job.Reason == ManualReason, Math.Max(1, ctx.ThreadShare()), ctx.Token, qpreds, null, q == null ? null : up => q.Read(st => st.Entry(up, "actors")?.Hold == "ai" || st.Entry(up, "process")?.Hold == "ai"), q?.Read(GroupOfQueue), q?.Read(st => st.ActorsIndexVer), q == null ? null : up => q.Read(st => PredKindOf(st, up)), repaired: q == null ? null : up => q.Read(st => st.Get(up)?.VerOf("cast") >= StepVersions.CastRepaired)); Last = r;
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

    // 0.13.27: the unit docs this process has read or written (key: store, name). Units never change for a given grouping record, so a later job reads no predecessor file it already has.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, UnitDoc> seenUnits = new(StringComparer.Ordinal);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStore, object> storeIds = new(); static long nextStoreId;
    static string SeenKey(ActorsIO io, string name) { var st = io.Store; while (st is MeteredStore ms) st = ms.Inner; return storeIds.GetValue(st, _ => (object)Interlocked.Increment(ref nextStoreId)) + "|" + name; }   // 0.13.29: keyed by the underlying store, so a metering wrapper never splits the cache
    static void Remember(ActorsIO io, string name, UnitDoc d) { if (!io.Writes) return; if (seenUnits.Count > 50000) seenUnits.Clear(); seenUnits[SeenKey(io, name)] = d; }
    public static void ForgetSeen() { seenUnits.Clear(); ActorsIndex.ForgetCache(); }

    /// <summary>0.13.29: what a predecessor can have, from the queue state alone (no store call): Skip = it can never have units (AI held, failed, a catalog-only upload with no cast, or its process step not finished: nothing to read yet); Compute = its process step is done but its actors step is not (in flight on another lane, or not claimed yet): built from its cast files, no units-file probe; UnitsFile = its actors step is done, so its units file exists (written by whichever node ran it).</summary>
    public enum PredKind { Skip, Compute, UnitsFile }
    public static PredKind PredKindOf(QueueState st, string up)
    {
        var s = st.Get(up); if (s == null) return PredKind.Skip;
        if (s.Data != "full" || s.Entries.GetValueOrDefault("process") is not { State: "done" } || s.Entries.Values.Any(e => e.Hold == "ai")) return PredKind.Skip;   // 0.13.29: only fully processed, non-AI uploads are predecessors
        if (s.VerOf("cast") >= StepVersions.CastRepaired || s.Entries.GetValueOrDefault("actors") is { State: "done" }) return PredKind.UnitsFile;   // 0.13.32: its repaired units file (or, before its repair, the one its old actors run wrote)
        if (s.Entries.Values.Any(e => e.State == "failed" || e.Hold == "ai")) return PredKind.Skip;
        if (s.Data != "full" || s.Entries.GetValueOrDefault("process") is not { State: "done" }) return PredKind.Skip;
        return PredKind.Compute;
    }

    /// <summary>0.13.28: an upload's group comes from the queue state (the join record the group step's done line put there): each upload maps to the root of its join chain. No groups file, no head.yaml.</summary>
    public static Dictionary<string, string> GroupOfQueue(QueueState st)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in st.Subjects.Values)
        {
            if (s.JoinKind == null) continue; string cur = s.Id; var seen = 0;
            while (seen++ < 64 && st.Get(cur) is { JoinKind: "join", JoinOf: { Length: > 0 } up } && up != cur) cur = up;
            map[s.Id] = cur;
        }
        return map;
    }
    /// <summary>Without a queue (the CLI, a dry run): the search head's group store, read once for the call.</summary>
    internal static async Task<Dictionary<string, string>> GroupOfAsync(ActorsIO io, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try { if (await io.Store.GetAsync(SearchData.HeadName(io.Paths), ct) is { } hc && await GroupStore.LoadAsync(io.Store, io.Paths, SearchData.ParseHead(hc.Data), ct) is { } gs) map = gs.GroupOf(); }
        catch (Exception e) when (e is FileNotFoundException or InvalidDataException) { }
        return map;
    }

    sealed class Built { public string Prod = "", Kind = "new"; public ActorsBuild.Drama D = null!; public List<UnitRec> Units = []; public int Dim, FacesUsed, ChildUnits; }

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
        var b2 = new Built { Prod = prod, Kind = kind, D = d, ChildUnits = d.ChildUnits, Dim = d.Faces.FirstOrDefault(f => f.Emb.Length > 0)?.Emb.Length ?? 0, FacesUsed = d.Faces.Count(f => f.Emb.Length > 0 && (use == null || use(f))) };
        foreach (var u in d.Units)
        {
            var pick = CastReembed.PickPerGroup(u.Faces.Select(f => (I: f, G: 0, T: d.Faces[f].T, Q: d.Faces[f].Quality)), R50Faces);
            b2.Units.Add(new UnitRec { Key = u.Key, N = u.N, First = u.First, Last = u.Last, Child = u.Child, Faces = u.Faces.ToArray(), Rep = u.RepFaces.ToArray(), Pick = pick, Vec = new[] { u.Centre }.Concat(u.RepFaces.Select(i => d.Faces[i].Emb)).ToArray() });
        }
        return b2;
    }

    public async Task<RunResult> RunAsync(ActorsIO io, string id, bool manual, int threads, CancellationToken ct, IReadOnlyList<string>? preds = null, IReadOnlyList<string>? extraPreds = null, Func<string, bool>? queueAi = null, Dictionary<string, string>? queueGroups = null, int? announcedIndex = null, Func<string, PredKind>? queuePred = null, bool createOnly = false, Func<string, bool>? repaired = null)
    {
        var paths = io.Paths; var now = Protocol.NowUtc(); var sw = System.Diagnostics.Stopwatch.StartNew(); long msOwn = 0, msPred = 0, msRoute = 0, msR50 = 0; int predComputed = 0, predReused = 0, predCached = 0, predWritten = 0;
        var vj = await VideoJson.ReadAsync(io.Store, paths, id, ct); var uf = ActorsData.UnitFile(paths, id);
        var ex = await io.GetFreshAsync(uf, ct); var exDoc = ex == null ? null : UnitDoc.FromBytes(ex.Data, uf); manual |= exDoc?.Manual == true;   // 0.13.27: no listing: the unit file's own record says whether it was added by hand. 0.13.32: always read (the cast-repair stage wrote the units there)
        if (!manual && AiFlag(vj?.Root))
        {
            var w = new List<string>(); if (ex != null) { await io.DeleteAsync(uf, ct); w.Add("delete " + uf); }
            return Last = new RunResult(id, "AI-flagged (ai.flag): no units, no links, no index entry", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", "", [], w);
        }
        var rec = vj?.Root["grouping"] as JsonObject ?? throw new InvalidOperationException($"{id}: no grouping record in video.json (the group step has not run)");
        var groupOf = new Dictionary<string, string>(queueGroups ?? await GroupOfAsync(io, ct), StringComparer.Ordinal);   // 0.13.27: the search head and the group store are read once a minute per process, not per job
        preds ??= await PredecessorsAsync(io.Store, paths, id, SeqOf(rec), extraPreds, ct);
        var grp = UnitDoc.GrpOf(rec); var own = await EnsureOwnAsync(io, id, rec, groupOf, exDoc, threads, ct); var me = own.Me; var own50 = own.R50; int r50New = own.R50New; bool reused = own.Reused;
        msOwn = sw.ElapsedMilliseconds; var mine = me.Units; var kind = me.Kind; var prod = me.Prod; int dim = me.Dim;
        // candidates: the actor index for everything in it, and the predecessors that are not in it yet, computed right here from their cast files (their own stage run is never waited for or read)
        var idx = await ActorsIndex.ForAnnouncedAsync(io, announcedIndex ?? 0, ct); var indexed = idx?.Ids ?? [];   // 0.13.28: whole files once per version (under 32 MB), whether announced or a cold start
        var manualIds = new HashSet<string>(StringComparer.Ordinal);   // 0.13.27: no units/ listing per job; a predecessor is checked for "added by hand" only when its video.json is AI-flagged (below), from its own unit doc
        var aiCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var qmode = queueAi != null; var predDocs = qmode ? new Dictionary<string, VideoJson.Doc?>(StringComparer.Ordinal) : await VideoJson.ReadManyAsync(io.Store, paths, (preds ?? []).Where(p => p != id).Distinct(StringComparer.Ordinal).ToList(), ct);   // the aggregate: one block-list + one GET for every predecessor's video.json, no per-upload reads
        async Task<bool> Ai(string up) { if (manualIds.Contains(up)) return false; if (!aiCache.TryGetValue(up, out var f)) { aiCache[up] = f = qmode ? queueAi!(up) : AiFlag((predDocs.TryGetValue(up, out var pd) ? pd : await VideoJson.ReadAsync(io.Store, paths, up, ct))?.Root); if (f) { var puf0 = ActorsData.UnitFile(paths, up); var pc0 = seenUnits.TryGetValue(SeenKey(io, puf0), out var sd0) ? sd0 : await io.GetAsync(puf0, ct) is { } c0 ? UnitDoc.FromBytes(c0.Data, puf0) : null; if (pc0?.Manual == true) { manualIds.Add(up); aiCache[up] = f = false; } } } return f; }
        var pend = new List<(string Id, string Prod, List<UnitRec> Units)>(); int aiSkipped = 0, missing = 0;
        foreach (var up in (preds ?? []).Where(p => p != id && !indexed.Contains(p)).Distinct(StringComparer.Ordinal))
        {
            var pk = queuePred?.Invoke(up) ?? PredKind.UnitsFile;
            if (pk == PredKind.Skip) { missing++; continue; }   // 0.13.29: it can never have units (or none yet): no store call at all
            if (await Ai(up)) { aiSkipped++; continue; }   // an AI predecessor: its cast data is never read
            JsonObject? prec = null;
            if (!qmode && (predDocs.TryGetValue(up, out var pdoc) ? pdoc : await VideoJson.ReadAsync(io.Store, paths, up, ct))?.Root["grouping"] is not JsonObject prec0) { missing++; continue; } else if (!qmode) prec = (predDocs.TryGetValue(up, out var pdoc1) ? pdoc1 : await VideoJson.ReadAsync(io.Store, paths, up, ct))!.Root["grouping"] as JsonObject;
            try
            {
                var puf = ActorsData.UnitFile(paths, up);   // 1) its units file, when its own run (or an earlier run of ours) wrote it: a cache with the same keys, never a wait
                if (seenUnits.TryGetValue(SeenKey(io, puf), out var sd)) { pend.Add((up, sd.Prod, sd.Units)); predCached++; continue; }   // 0.13.27: a units file this process has read or written is not read again
                if (pk != PredKind.Compute && await io.GetAsync(puf, ct) is { } pc) { var pdoc2 = UnitDoc.FromBytes(pc.Data, puf); if (pdoc2.Gen >= UnitDoc.CurrentGen) Remember(io, puf, pdoc2); pend.Add((up, pdoc2.Prod, pdoc2.Units)); predReused++; continue; }   // only a repaired file is final: an older one is replaced by its cast-repair
                if (prec == null && (prec = (await VideoJson.ReadAsync(io.Store, paths, up, ct))?.Root["grouping"] as JsonObject) == null) { missing++; continue; }   // queue mode: the predecessor's own video.json, only when its units are not already at hand
                var ckey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(io.Store) + "|" + up + "|" + prec.ToJsonString().GetHashCode();
                if (unitCache.TryGetValue(ckey, out var hit)) { pend.Add((up, hit.Prod, hit.Units)); predCached++; continue; }   // 2) computed earlier in this process
                var b = await BuildUnitsAsync(io, up, prec, groupOf, ct); pend.Add((up, b.Prod, b.Units)); predComputed++;   // 3) from its cast files, then kept
                if (unitCache.Count > 400) unitCache.Clear(); unitCache[ckey] = (b.Prod, b.Units);
                predWritten = 0;   // 0.13.32: never written: a predecessor still in its cast-repair step is only read here (its units file is written by its own cast-repair job)
            }
            catch (Exception e) when (e is InvalidOperationException or FileNotFoundException or InvalidDataException) { missing++; }   // no cast files yet: nothing to compare
        }
        msPred = sw.ElapsedMilliseconds - msOwn; var pendUnits = pend.ToDictionary(p => p.Id, p => p.Units, StringComparer.Ordinal);
                int reads0 = idx?.RowReads ?? 0; var aiIdx = new HashSet<string>(StringComparer.Ordinal); var cands = new Dictionary<string, List<Cand>>(StringComparer.Ordinal); int routed = 0;
        foreach (var u in mine)
        {
            var list = new List<Cand>();
            foreach (var (up, pprod, punits) in pend) foreach (var pu in punits) { if (pprod == prod || pu.Vec.Length == 0) continue; double c = CastLogic.Cosine(u.Vec[0], pu.Vec[0]); if (c >= ActorsBuild.BandLow) list.Add(new Cand(up, pu.Key, "", pprod, c, pu)); }
            // 0.13.65: with the index's vectors in memory (read whole once per version) every actor is compared exactly; the AI rule is checked only for the uploads that are close enough
            if (idx?.Exact(u.Vec[0], ActorsBuild.BandLow, up => up != id && !aiIdx.Contains(up), iu => iu.Prod != prod) is { } exact)
            {
                foreach (var (iu, ik, cos) in exact) { var upId = idx.Uploads[iu].Id; if (aiIdx.Contains(upId)) continue; if (await Ai(upId)) { aiIdx.Add(upId); continue; } var x = idx.Uploads[iu].Units[ik]; list.Add(new Cand(upId, x.Key, "", x.Prod, cos, null, iu, ik)); routed++; }
            }
            else if (idx != null && idx.Count > 0)
            {
                var rt2 = idx.Route(u.Vec[0], Top, up => up != id && !aiIdx.Contains(up), iu => iu.Prod != prod);
                var survive = new List<(int Upload, int Unit, int Dist)>(); foreach (var rr in rt2) { var upId = idx.Uploads[rr.Upload].Id; if (aiIdx.Contains(upId)) continue; if (await Ai(upId)) { aiIdx.Add(upId); continue; } survive.Add(rr); }   // only the routed survivors are checked, never the whole index
                routed += survive.Count;
                if (idx.Vec == null) await idx.PrefetchAsync(io, survive.Select(x => idx.Uploads[x.Upload].Units[x.Unit].Row), ct, ActorsIndex.MaxRangeReads - (idx.RowReads - reads0));   // 0.13.28: over 32 MB, vec.f16 is not read whole: coalesced ranges, at most 8 reads
                foreach (var (iu, ik, cos) in await idx.ExactAsync(io, u.Vec[0], survive, ct)) { var x = idx.Uploads[iu].Units[ik]; if (cos >= ActorsBuild.BandLow) list.Add(new Cand(idx.Uploads[iu].Id, x.Key, "", x.Prod, cos, null, iu, ik)); }
            }
            cands[u.Key] = list;
        }
        msRoute = sw.ElapsedMilliseconds - msOwn - msPred;
        // the r50 tiebreak (units in a 0.70-0.75 pair): this upload's own vectors are at hand; a partner's comes from the actor index when it is indexed, else from its r50 file; a partner with none is a predecessor not repaired yet: the pair is skipped and recorded
        // (pending in the links), and the index build or a later run settles it. No upload's r50 is ever computed here but its own.
        var bandCands = mine.SelectMany(u => cands[u.Key].Where(c => c.Cos < ActorsBuild.DirectCut)).ToList();
        if (idx != null && idx.R50Vec == null) await idx.PrefetchR50Async(io, bandCands.Where(c => c.IUp >= 0).Select(c => idx.Uploads[c.IUp].Units[c.IUnit].R50).Where(r => r >= 0), ct, ActorsIndex.MaxRangeReads - (idx.RowReads - reads0));
        var partnerFiles = new Dictionary<string, R50Doc?>(StringComparer.Ordinal); var partner = new Dictionary<(string, string), float[]?>(); var toCompute = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal); int partnerComputed = 0;
        foreach (var c in bandCands)
        {
            if (partner.ContainsKey((c.Id, c.Key))) continue;
            float[]? pv = c.IUp >= 0 ? idx!.R50Of(c.IUp, c.IUnit) : null;   // 1) the actor index
            if (pv == null)
            {   // 2) the partner's repaired r50 file
                if (!partnerFiles.TryGetValue(c.Id, out var pd)) { var rfp = ActorsData.R50File(paths, c.Id); partnerFiles[c.Id] = pd = await io.GetAsync(rfp, ct) is { } pcn ? R50Doc.FromBytes(pcn.Data, rfp) : null; }
                if (pd != null && pd.Units.TryGetValue(c.Key, out var pv2)) pv = pv2;
            }
            partner[(c.Id, c.Key)] = pv;
            if (pv == null && !(repaired?.Invoke(c.Id) ?? false)) (toCompute.TryGetValue(c.Id, out var hs) ? hs : toCompute[c.Id] = []).Add(c.Key);   // 3) its cast-repair is not done (it runs on another lane): computed here from its cast and frames, in memory only
        }
        foreach (var (up, keys) in toCompute.OrderBy(k => k.Key, StringComparer.Ordinal)) foreach (var kv in await ComputePartnerR50Async(io, up, keys, pendUnits.GetValueOrDefault(up), threads, ct)) { partner[(up, kv.Key)] = kv.Value; partnerComputed++; }
        msR50 = sw.ElapsedMilliseconds - msOwn - msPred - msRoute; int skipped = 0;
        // the links of this upload as unit-key pairs (cosine, method, r50 score); actors are formed by the index build, never here
        var links = new JsonArray(); int nLinks = 0, band = 0, bandOk = 0;
        foreach (var u in mine)
            foreach (var c in cands[u.Key].OrderByDescending(c => c.Cos))
            {
                bool direct = c.Cos >= ActorsBuild.DirectCut; double? rv = null; bool ok = direct, pending = false;
                if (!direct)
                {
                    band++;
                    if (own50.TryGetValue(u.Key, out var a) && partner.GetValueOrDefault((c.Id, c.Key)) is { } b) { rv = CastLogic.Cosine(a, b); ok = rv >= tiebreakCut; if (ok) bandOk++; } else { pending = true; skipped++; }
                }
                var lk = new JsonObject { ["unit"] = u.Key, ["other"] = c.Key, ["cos"] = Math.Round(c.Cos, 4), ["method"] = direct ? "direct" : "tiebreak", ["r50"] = rv == null ? null : Math.Round(rv.Value, 4), ["linked"] = ok }; if (pending) lk["pending"] = true;
                links.Add(lk);
                if (ok) nLinks++;
            }
        var doc = new UnitDoc { Id = id, Prod = prod, Kind = kind, At = now, Manual = manual, Dim = dim, FacesUsed = me.FacesUsed, ChildUnits = me.ChildUnits, Gen = UnitDoc.CurrentGen, Grp = grp, Units = mine, Links = links };
        if (!await io.PutAsync(uf, doc.ToBytes(), ex?.ETag, ex == null, manual, ct)) throw new IOException($"{uf} changed while this run wrote it (two writers for one upload?)");
        Remember(io, uf, doc);
        return Last = new RunResult(id, null, doc.FacesUsed, mine.Count, me.ChildUnits, nLinks, band, bandOk, r50New, pend.Count, aiSkipped + aiIdx.Count, routed, kind, prod, [], io.Log.ToList()) { PairsSkipped = skipped, PartnerR50 = partnerComputed, Reused = reused, Timing = $"time own units {msOwn / 1000.0:F1}s, predecessors {msPred / 1000.0:F1}s (computed {predComputed}, reused from units files {predReused}, from process cache {predCached}, units files written {predWritten}), routing {msRoute / 1000.0:F1}s, r50 {msR50 / 1000.0:F1}s" };
    }

    static (string Prod, string Kind) ProdKindOf(JsonObject rec, string id, Dictionary<string, string> groupOf)
    {
        var prop = rec["proposal"] as JsonObject; var kind = (string?)prop?["kind"] ?? "new"; var joinUp = (string?)prop?["joinUpload"];
        var prod = groupOf.GetValueOrDefault(id) ?? (joinUp != null ? groupOf.GetValueOrDefault(joinUp) ?? joinUp : id); groupOf[id] = prod; return (prod, kind);
    }

    sealed record Own(Built Me, Dictionary<string, float[]> R50, bool Reused, int R50New);

    /// <summary>0.13.32: this upload's repaired cast state. A units file of the current generation for the same grouping record, with an r50 vector for every unit in r50/&lt;id&gt;.bin, is used as it is: no cast.zip, no frames.zip.
    /// Otherwise the units are built from the stored faces (no mbf vector recomputed) and the r50 vector of EVERY unit (non-child: child units are left out of the units already) is computed from this upload's own zips, each read once, and written to r50/&lt;id&gt;.bin.</summary>
    async Task<Own> EnsureOwnAsync(ActorsIO io, string id, JsonObject rec, Dictionary<string, string> groupOf, UnitDoc? exDoc, int threads, CancellationToken ct)
    {
        var grp = UnitDoc.GrpOf(rec); var rf = ActorsData.R50File(io.Paths, id);
        if (exDoc is { Gen: UnitDoc.CurrentGen } && exDoc.Grp == grp && await io.GetFreshAsync(rf, ct) is { } r50c && R50Doc.FromBytes(r50c.Data, rf) is { } r50d && exDoc.Units.All(u => r50d.Units.ContainsKey(u.Key)))
        {
            var (pr, kd) = ProdKindOf(rec, id, groupOf); var v = new Dictionary<string, float[]>(StringComparer.Ordinal); foreach (var u in exDoc.Units) v[u.Key] = r50d.Units[u.Key];
            return new Own(new Built { Prod = pr, Kind = kd, Units = exDoc.Units, Dim = exDoc.Dim, FacesUsed = exDoc.FacesUsed, ChildUnits = exDoc.ChildUnits }, v, true, 0);
        }
        Built me; Dictionary<string, float[]> own50; int dim;
        using (StageZipStore.WholeZips())   // cast.zip and frames.zip come down whole, once, for the units and the r50 pass together
        {
            me = await BuildUnitsAsync(io, id, rec, groupOf, ct);
            (own50, dim) = await OwnR50Async(io, id, me.Units, threads, ct);
        }
        await WriteOwnR50Async(io, id, own50, dim, ct);
        return new Own(me, own50, false, own50.Count);
    }

    public sealed record RepairResult(string Id, string? Skipped, int Units, int ChildUnits, int FacesUsed, int R50Units, bool Reused);

    /// <summary>The repair mode of the cast step for one upload: makes the repaired cast state under index/actors/ (units file without links, r50 file) from the upload's own cast and frames; an upload already at that state costs two reads and writes nothing.</summary>
    public async Task<RepairResult> RepairAsync(ActorsIO io, string id, bool manual, int threads, CancellationToken ct, Func<string, bool>? queueAi = null, Dictionary<string, string>? queueGroups = null)
    {
        var paths = io.Paths; var vj = await VideoJson.ReadAsync(io.Store, paths, id, ct); var uf = ActorsData.UnitFile(paths, id);
        var ex = await io.GetFreshAsync(uf, ct); var exDoc = ex == null ? null : UnitDoc.FromBytes(ex.Data, uf); manual |= exDoc?.Manual == true;
        if (!manual && (AiFlag(vj?.Root) || queueAi?.Invoke(id) == true)) return new RepairResult(id, "AI-flagged: no cast repair", 0, 0, 0, 0, false);
        var rec = vj?.Root["grouping"] as JsonObject ?? throw new InvalidOperationException($"{id}: no grouping record in video.json (the group step has not run)");
        var groupOf = new Dictionary<string, string>(queueGroups ?? await GroupOfAsync(io, ct), StringComparer.Ordinal);
        var own = await EnsureOwnAsync(io, id, rec, groupOf, exDoc, threads, ct);
        if (!own.Reused)
        {   // units without links (the actors job adds them); an old file's links name old units and are not carried
            var doc = new UnitDoc { Id = id, Prod = own.Me.Prod, Kind = own.Me.Kind, At = Protocol.NowUtc(), Manual = manual, Dim = own.Me.Dim, FacesUsed = own.Me.FacesUsed, ChildUnits = own.Me.ChildUnits, Gen = UnitDoc.CurrentGen, Grp = UnitDoc.GrpOf(rec), Units = own.Me.Units, Links = [] };
            if (!await io.PutAsync(uf, doc.ToBytes(), ex?.ETag, ex == null, manual, ct)) throw new IOException($"{uf} changed while this run wrote it (two writers for one upload?)");
        }
        return new RepairResult(id, null, own.Me.Units.Count, own.Me.ChildUnits, own.Me.FacesUsed, own.R50New, own.Reused);
    }

    /// <summary>The r50 vector of every unit of THIS upload: the centre of the w600k_r50 vectors of its picked faces (up to 30, one per character-unit), from its own cast.zip crops and frames.zip landmarks. One model pass over all the picked faces.</summary>
    async Task<(Dictionary<string, float[]> Vecs, int Dim)> OwnR50Async(ActorsIO io, string id, List<UnitRec> units, int threads, CancellationToken ct)
    {
        var res = new Dictionary<string, float[]>(StringComparer.Ordinal); var all = units.SelectMany(u => u.Pick).Distinct().OrderBy(x => x).ToArray(); if (all.Length == 0) return (res, 0);
        if (MakeR50 == null) throw new InvalidOperationException("the r50 vectors of the units cannot be computed (no model)");
        var r = (await CastReembed.RunManyAsync(io.Store, io.Paths, id, [new("w600k_r50", MakeR50)], Decode, threads, ct, onlyFaces: all))[0];
        var row = new Dictionary<int, int>(); for (int k = 0; k < r.Idx!.Length; k++) row[r.Idx[k]] = k;
        foreach (var u in units)
        {
            var vs = u.Pick.Where(row.ContainsKey).Select(f => { var v = new float[r.Dim]; for (int d = 0; d < r.Dim; d++) v[d] = BitConverter.ToSingle(r.F32, (row[f] * r.Dim + d) * 4); return v; }).ToList();
            if (vs.Count > 0) res[u.Key] = CastLogic.Centroid(vs);   // a unit whose picked faces all lack landmarks gets no r50 row (the tiebreak reads it as no r50)
        }
        if (r.NoLandmarks > 0) Log.Info($"cast repair: {r.NoLandmarks} of {all.Length} faces have no landmarks (no r50 for {units.Count(u => !res.ContainsKey(u.Key))} units)");   // {id}
        return (res, r.Dim);
    }

    /// <summary>A predecessor whose cast-repair is not done (it runs on another lane): the r50 vectors of the <paramref name="keys"/> units computed here from its cast and frames (one whole read of each zip), in memory only: nothing is written for another upload.</summary>
    async Task<Dictionary<string, float[]>> ComputePartnerR50Async(ActorsIO io, string up, HashSet<string> keys, List<UnitRec>? units, int threads, CancellationToken ct)
    {
        var uf = ActorsData.UnitFile(io.Paths, up);
        units ??= UnitDoc.FromBytes((await io.GetAsync(uf, ct) ?? throw new FileNotFoundException(uf)).Data, uf).Units;
        var sel = units.Where(u => keys.Contains(u.Key)).ToList(); using var whole = StageZipStore.WholeZips();
        return (await OwnR50Async(io, up, sel, threads, ct)).Vecs;
    }

    /// <summary>r50/&lt;id&gt;.bin of this upload, replaced whole (nobody else writes it; a lost CAS only re-reads the ETag).</summary>
    static async Task WriteOwnR50Async(ActorsIO io, string id, Dictionary<string, float[]> vecs, int dim, CancellationToken ct)
    {
        var rf = ActorsData.R50File(io.Paths, id); var doc = new R50Doc { Dim = dim }; foreach (var kv in vecs) doc.Units[kv.Key] = kv.Value; var bytes = doc.ToBytes();
        if (vecs.Count > 0 && await io.PutAsync(rf, bytes, null, true, false, ct)) return;   // a new upload: create-only, no probe
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var cur = await io.GetFreshAsync(rf, ct); if (cur == null && vecs.Count == 0) return;
            if (await io.PutAsync(rf, bytes, cur?.ETag, cur == null, false, ct)) return;
        }
        throw new IOException($"{rf} kept changing");
    }
}
