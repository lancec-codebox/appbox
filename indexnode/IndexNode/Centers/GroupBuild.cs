using System.Diagnostics;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>0.13.2: the search-index job is the ONLY writer of groups. The pre-check is only a short-circuit of the check this build runs in full: every upload the build adds (processed, has centers.zip) is checked here, all its frames against the new index
/// (membership, offset, both shares, mixed), whatever its precheck.json says (none, failed or a hint). An upload the pre-check short-circuited (duplicate skip, AI hold: no centers.zip) joins from its pre-check record; once processed it is checked in full and replaces that placement.
/// No center pruning: every processed upload adds all its centers.</summary>
public static class GroupBuild
{
    sealed record Rec(GroupProposal? Proposal, string? At, double? Duration, bool HasPrecheck = false, GroupProposal? Grouped = null, double? CoveredShare = null, IReadOnlyList<RelStretch>? Stretches = null);

    /// <summary>The group proposal, time and duration of an upload from its video.json (0.13.2: no precheck.json, no downloaded.json). A missing or unreadable video.json throws: the group build fails loudly and is retried.</summary>
    static async Task<Rec> ReadRecAsync(IStore store, Paths paths, string id, CancellationToken ct, Action<string>? say, VideoAggregate.AggView? view = null) => (await ReadRecJsonAsync(store, paths, id, ct, say, view)).Rec;

    static async Task<(Rec Rec, string Json)> ReadRecJsonAsync(IStore store, Paths paths, string id, CancellationToken ct, Action<string>? say, VideoAggregate.AggView? view = null)
    {
        JsonObject root;
        try
        {
            root = (await VideoJson.ReadViaAsync(view, store, paths, id, ct))?.Root ?? new JsonObject();
            // the aggregate record has no grouping.stretches: only a join or mixed upload has them, and only for those is stretches.json read (per-upload fallback inside)
            if (view != null && view.Has(id) && root["grouping"] is JsonObject gj && gj["stretches"] == null && GroupProposal.FromJson(gj["proposal"] as JsonObject) is { Kind: GroupProposal.JoinKind or GroupProposal.MixedKind } && await VideoJson.ReadStretchesAsync(store, paths, id, ct) is { } st) gj["stretches"] = st;
        }   // 0.13.3: a missing or bad video.json places the upload with no hint (the catalog marks it missing), it does not fail the build
        catch (InvalidDataException) { root = new JsonObject(); }
        return (FromRoot(root), RecJson(root));
    }

    static Rec FromRoot(JsonObject root)
    {
        var pc = root["precheck"] as JsonObject;
        var gr = root["grouping"] as JsonObject;   // 0.13.10: the per-drama group step's record
        return new Rec(pc == null ? null : GroupProposal.FromJson(pc["group"] as JsonObject), (string?)pc?["at"], VideoJson.Duration(root), pc != null, gr == null ? null : GroupProposal.FromJson(gr["proposal"] as JsonObject),
            (double?)gr?["coveredShare"], gr?["stretches"] is JsonArray sa ? sa.OfType<JsonObject>().Select(x => new RelStretch((double?)x["from"] ?? 0, (double?)x["to"] ?? 0, (string?)x["upload"] ?? "", (double?)x["offset"] ?? 0)).ToList() : null);
    }

    /// <summary>0.13.23: the grouping record of a video.json as a compact JSON string, kept in uploads.yaml (next to videoEtag) so the next build reads video.json only when its ETag changed.</summary>
    public static string RecJson(JsonObject root)
    {
        var r = FromRoot(root);
        var o = new JsonObject { ["hp"] = r.HasPrecheck };
        if (r.Proposal != null) o["pc"] = r.Proposal.ToJson();
        if (r.At != null) o["at"] = r.At;
        if (r.Duration != null) o["dur"] = r.Duration;
        if (r.Grouped != null) o["gr"] = r.Grouped.ToJson();
        if (r.CoveredShare != null) o["cs"] = r.CoveredShare;
        if (r.Stretches != null) o["st"] = new JsonArray(r.Stretches.Select(x => (JsonNode?)new JsonObject { ["from"] = x.From, ["to"] = x.To, ["upload"] = x.Source, ["offset"] = x.Offset }).ToArray());
        return o.ToJsonString();
    }

    static Rec? RecFromJson(string? json)
    {
        if (json == null) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            return new Rec(GroupProposal.FromJson(o["pc"] as JsonObject), (string?)o["at"], (double?)o["dur"], (bool?)o["hp"] ?? false, GroupProposal.FromJson(o["gr"] as JsonObject), (double?)o["cs"],
                o["st"] is JsonArray sa ? sa.OfType<JsonObject>().Select(x => new RelStretch((double?)x["from"] ?? 0, (double?)x["to"] ?? 0, (string?)x["upload"] ?? "", (double?)x["offset"] ?? 0)).ToList() : null);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException) { return null; }
    }

    public const int ChunkFrames = 30;

    /// <summary>0.13.8: where the time of a full check goes, summed over the uploads checked (thread-safe): scan, member reads, scoring, and the requests and bytes of the reads.</summary>
    public sealed class CheckStats
    {
        long scan, read, score, own, uploads, requests, bytes; int peak;
        public void Add(double scanMs, double readMs, double scoreMs, int req, long by) { Interlocked.Add(ref scan, (long)(scanMs * 1000)); Interlocked.Add(ref read, (long)(readMs * 1000)); Interlocked.Add(ref score, (long)(scoreMs * 1000)); Interlocked.Add(ref requests, req); Interlocked.Add(ref bytes, by); }
        public void AddOwn(double ms, int req, long by) { Interlocked.Add(ref own, (long)(ms * 1000)); Interlocked.Add(ref requests, req); Interlocked.Add(ref bytes, by); }
        public void Done() => Interlocked.Increment(ref uploads);
        public void Peak(int p) { int c; while (p > (c = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, p, c); }
        /// <summary>"avg per upload: scan 12 ms, read 3400 ms (own centers 800 ms), score 40 ms; 190 requests, 38.0 MB; reads in flight 7 now, peak 61".</summary>
        public string Line()
        {
            long n = Math.Max(1, Interlocked.Read(ref uploads)); string Ms(long us) => (us / 1000.0 / n).ToString("F0");
            return $"avg per upload: scan {Ms(Interlocked.Read(ref scan))} ms, read {Ms(Interlocked.Read(ref read))} ms (own centers {Ms(Interlocked.Read(ref own))} ms), score {Ms(Interlocked.Read(ref score))} ms; {Interlocked.Read(ref requests) / n} requests, {Interlocked.Read(ref bytes) / n / 1048576.0:F1} MB; reads in flight {CentersBlob.InFlight} now, peak {Math.Max(Volatile.Read(ref peak), CentersBlob.TakePeakInFlight())}";
        }
    }

    /// <summary>The frames to search: the pre-check's own selection (<see cref="PreCheck.SubsampleIndices"/>, one sampler for both callers), here spread over the whole upload (the pre-check's window is 5 minutes around 50% of the video), so mixed cuts and the offset see its full length.</summary>
    public static List<(float T, float[] V)> Sample(List<(float T, float[] V)> frames, int max) =>
        max <= 0 || frames.Count <= max ? frames : PreCheck.SubsampleIndices(frames.Count, max).Select(i => frames[i]).ToList();

    /// <summary>The full check of one processed upload: every frame vector of its centers.zip (time order) against <paramref name="next"/> without itself. Null when its centers cannot be read (the caller falls back to the pre-check's hint).</summary>
    public static async Task<GroupProposal?> FullCheckAsync(IStore store, Paths paths, SearchData next, SearchUpload u, GroupSet set, PreCheckSettings s, int threads, CancellationToken ct, BlockCache? cache = null, CheckStats? stats = null)
    {
        List<(float T, float[] V)> frames = [];
        try
        {
            var own = Stopwatch.StartNew();
            var z = await FramesReader.OpenAsync(store, paths.FramesFile(u.Id), ct); if (z == null) return null;
            var h = await z.ReadHeaderAsync(ct);
            var blocks = await z.ReadBlocksAsync(Enumerable.Range(0, h.K).ToList(), CenterSearch.MergeGap, CenterSearch.MaxMerged, ct);
            stats?.AddOwn(own.Elapsed.TotalMilliseconds, z.Requests, z.BytesRead);
            foreach (var (_, m) in blocks) { var rows = AiProbe.RowsFromF16(m.Vectors); for (int i = 0; i < rows.Length && i < m.Times.Length; i++) { Sscd.L2Normalize(rows[i]); frames.Add((m.Times[i], rows[i])); } }
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"groups: centers of {u.Id} not read ({e.Message})"); return null; }
        if (frames.Count == 0) return GroupProposal.None;
        frames.Sort((a, b) => a.T.CompareTo(b.T));
        // 0.13.6: at most GroupCheckFrames frames, evenly spaced over the upload (the pre-check lookup on each); join, mixed and offset come from these samples
        frames = Sample(frames, s.GroupCheckFrames);
        int ex = next.Uploads.FindIndex(x => x.Id == u.Id);
        // in chunks, so one call never holds every block its frames touch; the shared LRU cache keeps what the next chunk needs
        var best = new FrameBest?[frames.Count];
        for (int at = 0; at < frames.Count; at += ChunkFrames)
        {
            var part = frames.Skip(at).Take(ChunkFrames).Select(f => f.V).ToList();
            var (b, st) = await PreCheckSearch.LookupAsync(store, paths, next, part, s, threads, ct, ex, cache);   // 0.13.72: frame codes once every upload has them
            stats?.Add(st.ScanMs, st.ReadMs, st.ScoreMs, st.Requests, st.Bytes); stats?.Peak(CentersBlob.InFlight);
            Array.Copy(b, 0, best, at, b.Length);
        }
        var hits = new List<FrameHit>();
        for (int i = 0; i < best.Length; i++) if (best[i] is { } b && b.Cosine >= s.Tau && b.Upload >= 0 && b.Upload < next.Uploads.Count) hits.Add(new FrameHit(i, next.Uploads[b.Upload].Id, b.Time, b.Cosine));
        var of = set.GroupOf();
        var ug = hits.Select(x => x.Upload).Distinct(StringComparer.Ordinal).ToDictionary(x => x, x => of.GetValueOrDefault(x), StringComparer.Ordinal);
        var gm = set.Groups.Where(g => ug.Values.Contains(g.Id)).ToDictionary(g => g.Id, g => (IReadOnlyList<GroupCopy>)g.Members.Select(m => new GroupCopy(m.Upload, m.SpeechLanguage, m.Dubbed)).ToList(), StringComparer.Ordinal);
        var a = new PreCheckAnalysis(null, next.Version, frames.Count, best.Count(b => b == null), [], 0, new Dictionary<string, double>()) { Hits = hits, UploadGroups = ug, GroupMembers = gm };
        return DramaGroups.Propose(a, 0, 0, s, i => frames[i].T, frames[0].T, frames[^1].T);   // 0.13.57: sorted by time: the upload's middle lies between the first and the last frame time
    }

    /// <summary>"groups: checked N of M (r/min, ETA hh:mm)".</summary>
    public static string Progress(int done, int total, TimeSpan elapsed)
    {
        double rate = elapsed.TotalMinutes > 0 ? done / elapsed.TotalMinutes : 0;
        var eta = rate > 0 ? TimeSpan.FromMinutes((total - done) / rate) : TimeSpan.Zero;
        return $"groups: checked {done} of {total} ({rate:F1}/min, ETA {(int)eta.TotalHours:D2}:{eta.Minutes:D2})";
    }

    public static async Task<GroupSet> ApplyAsync(IStore store, Paths paths, SearchData? head, SearchData next, CancellationToken ct, Action<string>? say = null, int threads = 4, PreCheckSettings? settings = null, bool bootstrap = false, bool write = true, bool requireGroups = false, bool foldOnly = false, bool speechFresh = false)
    {
        var view = await VideoAggregate.OpenViewAsync(store, paths, ct);   // bulk job: one Get Block List, the records from one GET of videos.agg; uploads it lacks are read one by one
        var s = settings ?? new PreCheckSettings();
        var prior = head == null ? null : await GroupStore.LoadAsync(store, paths, head.Version, ct);
        // 0.13.6: with no groups file the index job does not group (a bootstrap of every upload is hours of work: `indexnode groups build` does it once); the version is published with no groups
        if (requireGroups && !foldOnly && prior == null && !bootstrap)
        {
            say?.Invoke("groups: NO groups file yet: not grouping (catalog groupId stays null); run `indexnode groups build` first");
            return new GroupSet();
        }
        var set = prior ?? new GroupSet();
        var byId = next.Uploads.ToDictionary(u => u.Id, StringComparer.Ordinal);
        // a short-circuited placement of an upload that is now processed is replaced by the full check
        foreach (var g in set.Groups) g.Members.RemoveAll(m => m.CheckedBy == "precheck" && byId.ContainsKey(m.Upload));
        set.Groups.RemoveAll(g => g.Members.Count == 0);
        var known = set.GroupOf();
        var news = new List<NewUpload>(); int full = 0, hinted = 0, foldedTotal = 0, ungrouped = 0;
        // 0.13.3: the speech language of every upload is video.json's as it is NOW (not the index table's copy from when the centers were read): a group's canonical election and its reason use it
        var speech = new System.Collections.Concurrent.ConcurrentDictionary<string, (string? Lang, bool? Dub)>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(next.Uploads, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (u, t) =>
        {
            if (speechFresh && u.VideoEtag != null) { speech[u.Id] = (u.SpeechLanguage, u.Dubbed); return; }   // 0.13.22: the index build just carried these forward or read them (video.json's ETag unchanged): no second read
            try { speech[u.Id] = await SearchIndexBuild.ReadSpeechAsync(store, paths, u.Id, t, view); } catch (Exception e) when (e is not OperationCanceledException) { }
        });
        (string? Lang, bool? Dub) Sp(SearchUpload u) => speech.TryGetValue(u.Id, out var x) && x.Lang != null ? x : (u.SpeechLanguage, u.Dubbed);
        var unknown = next.Uploads.Where(u => !known.ContainsKey(u.Id)).ToList();
        // 0.13.6: a build full-checks at most GroupMaxNewPerBuild new uploads; the rest stay unplaced and are the next build's (a bootstrap checks them all)
        // 0.13.10: the index job (foldOnly) never runs the full check: a drama with a group record is folded from it, a drama without one is left out of the set (its own group in the catalog) until its group step has run
        var allRecs = new System.Collections.Concurrent.ConcurrentDictionary<string, Rec>(StringComparer.Ordinal);
        // 0.13.23: the record the index build kept for an upload whose video.json ETag it just checked (speechFresh); else one read, once per build
        async Task<Rec> RecOf(SearchUpload u, CancellationToken t)
        {
            if (allRecs.TryGetValue(u.Id, out var c)) return c;
            var r = (speechFresh && u.VideoEtag != null ? RecFromJson(u.GroupRec) : null) ?? await ReadRecAsync(store, paths, u.Id, t, say, view);
            allRecs[u.Id] = r; return r;
        }
        if (foldOnly) { var recs = new Dictionary<string, Rec>(StringComparer.Ordinal); foreach (var u in unknown) { var rr = await RecOf(u, ct); if (rr.Grouped != null) recs[u.Id] = rr; } ungrouped = unknown.Count(u => !recs.ContainsKey(u.Id)); unknown = unknown.Where(u => recs.ContainsKey(u.Id)).ToList(); }
        else if (!bootstrap && unknown.Count > s.GroupMaxNewPerBuild) { say?.Invoke($"groups: {unknown.Count} new uploads, checking {s.GroupMaxNewPerBuild} now (groups.max-new-per-build); the rest wait for the next build"); unknown = unknown.Take(s.GroupMaxNewPerBuild).ToList(); }
        int done = 0, todo = unknown.Count; var clock = Stopwatch.StartNew();
        var cache = new BlockCache(s.GroupCacheMb * 1024L * 1024L);
        // 0.13.7: several uploads are checked at once (bounded by threads), each with its share of the threads for its own frames; the check only reads the index and the (still unchanged) set, the placements are applied after, in index order
        int par = Math.Max(1, Math.Min(threads, unknown.Count)), inner = Math.Max(1, threads / par);
        var placed = new NewUpload?[unknown.Count]; int fullN = 0, hintN = 0, folded = 0; var cstats = new CheckStats();
        using var tick = new Timer(_ => { try { cstats.Peak(CentersBlob.InFlight); } catch { } }, null, 0, 200);
        await Parallel.ForEachAsync(Enumerable.Range(0, unknown.Count), new ParallelOptions { MaxDegreeOfParallelism = par, CancellationToken = ct }, async (k, t) =>
        {
            var u = unknown[k];
            var r = await RecOf(u, t);
            // 0.13.10: a drama the group step has done is folded from its record: no frames are read or searched here
            var prop = r.Grouped ?? (foldOnly ? null : await FullCheckAsync(store, paths, next, u, set, s, inner, t, cache, cstats)); cstats.Done();
            if (r.Grouped != null) Interlocked.Increment(ref folded);
            else if (prop == null) { prop = r.Proposal; Interlocked.Increment(ref hintN); } else Interlocked.Increment(ref fullN);
            var (l0, d0) = Sp(u);
            placed[k] = new NewUpload(u.Id, l0, d0, r.Duration, r.At, prop) { CoveredShare = r.Grouped != null ? r.CoveredShare : null, Stretches = r.Grouped != null ? r.Stretches : null };
            int n = Interlocked.Increment(ref done); if (n % 10 == 0 || n == todo) say?.Invoke(Progress(n, todo, clock.Elapsed) + "; " + cstats.Line() + $"; {par} uploads at once, {inner} threads each");
        });
        news.AddRange(placed.Select(x => x!)); full += fullN; foldedTotal = folded; hinted += hintN;
        // the uploads the pre-check short-circuited (record, no centers.zip) with a proposal in it
        var shorts = new HashSet<string>(StringComparer.Ordinal); var pre = paths.Prefix + "videos/"; var others = new Dictionary<string, OtherRec>(StringComparer.Ordinal);
        foreach (var b in await store.ListAsync(pre, false, false, ct))
        {
            if (!b.Name.EndsWith("/video.json", StringComparison.Ordinal)) continue;
            var id = b.Name[pre.Length..^"/video.json".Length]; if (id.Contains('/') || byId.ContainsKey(id)) continue;
            // 0.13.23: the record of a video.json that is no indexed upload is kept in uploads.yaml (others:) with its ETag, read again only when the ETag changed
            Rec r;
            if (b.ETag is { Length: > 0 } et)
            {
                if (head != null && head.Others.TryGetValue(id, out var o) && o.Etag == et && RecFromJson(o.Rec) is { } cr) { r = cr; others[id] = o; }
                else { var (rr, json) = await ReadRecJsonAsync(store, paths, id, ct, say, view); r = rr; others[id] = new OtherRec(et, json); }
            }
            else r = await ReadRecAsync(store, paths, id, ct, say, view);
            if (!r.HasPrecheck) continue;   // downloaded, not pre-checked: not short-circuited
            shorts.Add(id); if (known.ContainsKey(id)) continue;
            if (r.Proposal == null) continue;   // nothing to place it by
            var (lang, dub) = await SearchIndexBuild.ReadSpeechAsync(store, paths, id, ct, view);
            CatalogBuild.Pre pc;
            try { pc = await CatalogBuild.ReadPrecheckAsync(store, paths, id, ct); } catch (Exception e) when (e is VideoJsonMissingException or InvalidDataException) { continue; }
            news.Add(new NewUpload(id, pc.Language ?? lang, pc.Language != null ? pc.Dubbed : dub, r.Duration, r.At, r.Proposal, true));
        }
        next.Others = others;
        var before = set.Groups.ToDictionary(g => g.Id, g => g.Canonical, StringComparer.Ordinal);
        set.Apply(news, id => byId.TryGetValue(id, out var u) ? (Sp(u).Lang, Sp(u).Dub, null) : null, bootstrap);   // the language of a known member may have been measured since
        // an upload no longer in the index (and not short-circuited) leaves its group; an emptied group goes
        foreach (var g in set.Groups) g.Members.RemoveAll(m => !byId.ContainsKey(m.Upload) && !shorts.Contains(m.Upload));
        set.Groups.RemoveAll(g => g.Members.Count == 0); set.ElectAll(); set.Relate(s);   // 0.13.15: each member's relation to the canonical
        set.BuiltAt = next.BuiltAt == null ? null : Protocol.Utc(next.BuiltAt.Value);
        var of = set.GroupOf();
        next.Uploads = next.Uploads.Select(u => u with { Group = of.GetValueOrDefault(u.Id) }).ToList();
        if (write) await GroupStore.WriteAsync(store, paths, set, next.Version, ct);
        int moved = set.Groups.Count(g => before.TryGetValue(g.Id, out var c) && c != g.Canonical);
        say?.Invoke($"groups: {news.Count} new uploads placed ({foldedTotal} folded from the group step, {full} full check, {ungrouped} without a group record (ungrouped until their group step runs), {hinted} from the pre-check hint, {news.Count(n => n.ShortCircuit)} short-circuited), {set.Groups.Count} groups, {moved} canonical changed");
        return set;
    }
}
