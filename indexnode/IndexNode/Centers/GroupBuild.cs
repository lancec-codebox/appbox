using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>0.13.2: the search-index job is the ONLY writer of groups. The pre-check is only a short-circuit of the check this build runs in full: every upload the build adds (processed, has centers.zip) is checked here, all its frames against the new index
/// (membership, offset, both shares, mixed), whatever its precheck.json says (none, failed or a hint). An upload the pre-check short-circuited (duplicate skip, AI hold: no centers.zip) joins from its pre-check record; once processed it is checked in full and replaces that placement.
/// No center pruning: every processed upload adds all its centers.</summary>
public static class GroupBuild
{
    sealed record Rec(GroupProposal? Proposal, string? At, double? Duration, bool HasPrecheck = false);

    /// <summary>The group proposal, time and duration of an upload from its video.json (0.13.2: no precheck.json, no downloaded.json). A missing or unreadable video.json throws: the group build fails loudly and is retried.</summary>
    static async Task<Rec> ReadRecAsync(IStore store, Paths paths, string id, CancellationToken ct, Action<string>? say)
    {
        var root = (await VideoJson.RequireAsync(store, paths, id, ct)).Root;
        var pc = root["precheck"] as JsonObject;
        return new Rec(pc == null ? null : GroupProposal.FromJson(pc["group"] as JsonObject), (string?)pc?["at"], VideoJson.Duration(root), pc != null);
    }

    /// <summary>The full check of one processed upload: every frame vector of its centers.zip (time order) against <paramref name="next"/> without itself. Null when its centers cannot be read (the caller falls back to the pre-check's hint).</summary>
    public static async Task<GroupProposal?> FullCheckAsync(IStore store, Paths paths, SearchData next, SearchUpload u, GroupSet set, PreCheckSettings s, int threads, CancellationToken ct)
    {
        List<(float T, float[] V)> frames = [];
        try
        {
            var z = await CentersZip.OpenAsync(store, paths.Video(u.Id, "centers.zip"), ct); if (z == null) return null;
            var h = await z.ReadHeaderAsync(ct);
            var blocks = await z.ReadBlocksAsync(Enumerable.Range(0, h.K).ToList(), CenterSearch.MergeGap, CenterSearch.MaxMerged, ct);
            foreach (var (_, m) in blocks) { var rows = AiProbe.RowsFromF16(m.Vectors); for (int i = 0; i < rows.Length && i < m.Times.Length; i++) { Sscd.L2Normalize(rows[i]); frames.Add((m.Times[i], rows[i])); } }
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"groups: centers of {u.Id} not read ({e.Message})"); return null; }
        if (frames.Count == 0) return GroupProposal.None;
        frames.Sort((a, b) => a.T.CompareTo(b.T));
        int ex = next.Uploads.FindIndex(x => x.Id == u.Id);
        var (best, _) = await PreCheckSearch.BestPerFrameAsync(store, paths, next, frames.Select(f => f.V).ToList(), s.TopCenters, threads, ct, ex);
        var hits = new List<FrameHit>();
        for (int i = 0; i < best.Length; i++) if (best[i] is { } b && b.Cosine >= s.Tau && b.Upload >= 0 && b.Upload < next.Uploads.Count) hits.Add(new FrameHit(i, next.Uploads[b.Upload].Id, b.Time, b.Cosine));
        var of = set.GroupOf();
        var ug = hits.Select(x => x.Upload).Distinct(StringComparer.Ordinal).ToDictionary(x => x, x => of.GetValueOrDefault(x), StringComparer.Ordinal);
        var gm = set.Groups.Where(g => ug.Values.Contains(g.Id)).ToDictionary(g => g.Id, g => (IReadOnlyList<GroupCopy>)g.Members.Select(m => new GroupCopy(m.Upload, m.SpeechLanguage, m.Dubbed)).ToList(), StringComparer.Ordinal);
        var a = new PreCheckAnalysis(null, next.Version, frames.Count, best.Count(b => b == null), [], 0, new Dictionary<string, double>()) { Hits = hits, UploadGroups = ug, GroupMembers = gm };
        return DramaGroups.Propose(a, 0, 0, s, i => frames[i].T);
    }

    public static async Task<GroupSet> ApplyAsync(IStore store, Paths paths, SearchData? head, SearchData next, CancellationToken ct, Action<string>? say = null, int threads = 4, PreCheckSettings? settings = null)
    {
        var s = settings ?? new PreCheckSettings();
        var prior = head == null ? null : await GroupStore.LoadAsync(store, paths, head.Version, ct);
        var set = prior ?? new GroupSet();
        var byId = next.Uploads.ToDictionary(u => u.Id, StringComparer.Ordinal);
        // a short-circuited placement of an upload that is now processed is replaced by the full check
        foreach (var g in set.Groups) g.Members.RemoveAll(m => m.CheckedBy == "precheck" && byId.ContainsKey(m.Upload));
        set.Groups.RemoveAll(g => g.Members.Count == 0);
        var known = set.GroupOf();
        var news = new List<NewUpload>(); int full = 0, hinted = 0;
        foreach (var u in next.Uploads.Where(u => !known.ContainsKey(u.Id)))
        {
            var r = await ReadRecAsync(store, paths, u.Id, ct, say);
            var prop = await FullCheckAsync(store, paths, next, u, set, s, threads, ct);
            if (prop == null) { prop = r.Proposal; hinted++; } else full++;
            news.Add(new NewUpload(u.Id, u.SpeechLanguage, u.Dubbed, r.Duration, r.At, prop));
        }
        // the uploads the pre-check short-circuited (record, no centers.zip) with a proposal in it
        var shorts = new HashSet<string>(StringComparer.Ordinal); var pre = paths.Prefix + "videos/";
        foreach (var b in await store.ListAsync(pre, false, false, ct))
        {
            if (!b.Name.EndsWith("/video.json", StringComparison.Ordinal)) continue;
            var id = b.Name[pre.Length..^"/video.json".Length]; if (id.Contains('/') || byId.ContainsKey(id)) continue;
            var r = await ReadRecAsync(store, paths, id, ct, say);
            if (!r.HasPrecheck) continue;   // downloaded, not pre-checked: not short-circuited
            shorts.Add(id); if (known.ContainsKey(id)) continue;
            if (r.Proposal == null) continue;   // nothing to place it by
            var (lang, dub) = await SearchIndexBuild.ReadSpeechAsync(store, paths, id, ct);
            var pc = await CatalogBuild.ReadPrecheckAsync(store, paths, id, ct);
            news.Add(new NewUpload(id, pc.Language ?? lang, pc.Language != null ? pc.Dubbed : dub, r.Duration, r.At, r.Proposal, true));
        }
        var before = set.Groups.ToDictionary(g => g.Id, g => g.Canonical, StringComparer.Ordinal);
        set.Apply(news, id => byId.TryGetValue(id, out var u) ? (u.SpeechLanguage, u.Dubbed, null) : null);   // the language of a known member may have been measured since
        // an upload no longer in the index (and not short-circuited) leaves its group; an emptied group goes
        foreach (var g in set.Groups) g.Members.RemoveAll(m => !byId.ContainsKey(m.Upload) && !shorts.Contains(m.Upload));
        set.Groups.RemoveAll(g => g.Members.Count == 0); set.ElectAll();
        set.BuiltAt = next.BuiltAt == null ? null : Protocol.Utc(next.BuiltAt.Value);
        var of = set.GroupOf();
        next.Uploads = next.Uploads.Select(u => u with { Group = of.GetValueOrDefault(u.Id) }).ToList();
        await GroupStore.WriteAsync(store, paths, set, next.Version, ct);
        int moved = set.Groups.Count(g => before.TryGetValue(g.Id, out var c) && c != g.Canonical);
        say?.Invoke($"groups: {news.Count} new uploads placed ({full} full check, {hinted} from the pre-check hint, {news.Count(n => n.ShortCircuit)} short-circuited), {set.Groups.Count} groups, {moved} canonical changed");
        return set;
    }
}
