using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.13: `indexnode precheck whatif [--out file.jsonl] [--limit N] [--ids file] [--threads N]`: a validation pass over the dramas we already have. No downloads, no YouTube calls, never a write to the store.
/// For every drama with a frames file: the sample is built from its OWN frames (the frames whose times fall in the pre-check window, evenly subsampled to precheck.frames; the stored SSCD vectors stand in for the live clip's),
/// looked up exactly as the pre-check does in the head index WITHOUT the drama itself (groups come from the groups file with the drama removed), then <see cref="PreCheck.Decide"/> runs under the old symmetric rule and the new asymmetric one.
/// </summary>
public static class PreCheckWhatIf
{
    /// <summary>0.13.15: Old = the 0.13.14 single-window rule on one window at 0.5; New = the two-segment rule (windows at 0.30/0.70, 30 frames each, the drama itself excluded). FalseSkips: every new duplicate/subset where the drama's own group step says new or mixed, or its covered share is under 0.5. AiChanges: dramas whose AI flag differs between the two samples.</summary>
    public sealed record Summary(int Dramas, Dictionary<string, int> Old, Dictionary<string, int> New, int OldSkipNewProcessed, int NewSkipOldProcessed, int NewButGroupJoin, int Failed, IReadOnlyList<string> FalseSkips, IReadOnlyList<string> AiChanges, double Seconds);

    public static async Task<int> RunAsync(Args a, IStore? remote = null)
    {
        var (cfg, store, paths, _) = CentersCommands.OpenStore(a, remote);
        int threads = Math.Max(1, a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads);
        int limit = a.Get("limit") is { } l ? int.Parse(l) : int.MaxValue;
        var ids = a.Get("ids") is { } f ? File.ReadAllLines(f).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).ToList() : null;
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        TextWriter output = a.Get("out") is { } o ? new StreamWriter(Path.GetFullPath(o), false, new UTF8Encoding(false)) : Console.Out;
        try
        {
            var sum = await RunAsync(store, paths, cfg.PreCheck ?? new(), threads, limit, ids, output, line => Console.Error.WriteLine(line), cts.Token);
            Console.Error.WriteLine(Format(sum));
            if (output != Console.Out) Console.WriteLine(Format(sum));
            return 0;
        }
        finally { if (output != Console.Out) output.Dispose(); }
    }

    public static string Format(Summary s)
    {
        string C(Dictionary<string, int> d) => string.Join(", ", d.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} {x.Value}"));
        var sb = new StringBuilder();
        sb.Append($"precheck whatif: {s.Dramas} dramas{(s.Failed > 0 ? $" ({s.Failed} failed)" : "")} in {s.Seconds:0.0} s ({(s.Dramas == 0 ? 0 : s.Seconds / s.Dramas):0.00} s per drama)\n  old (one window at 0.5): {C(s.Old)}\n  new (two segments 0.30/0.70): {C(s.New)}\n  skipped by the old rule, processed by the new: {s.OldSkipNewProcessed}; skipped by the new, processed by the old: {s.NewSkipOldProcessed}; new says new where the group step said join: {s.NewButGroupJoin}");
        sb.Append($"\n  possible false skips (new duplicate/subset where the group step said new/mixed or covered < 0.5): {s.FalseSkips.Count}");
        foreach (var x in s.FalseSkips) sb.Append("\n    " + x);
        sb.Append($"\n  AI flag would change: {s.AiChanges.Count}");
        foreach (var x in s.AiChanges) sb.Append("\n    " + x);
        return sb.ToString();
    }

    public static async Task<Summary> RunAsync(IStore store, Paths paths, PreCheckSettings s, int threads, int limit, IReadOnlyList<string>? only, TextWriter output, Action<string> progress, CancellationToken ct)
    {
        var head = await SearchData.LoadAsync(store, paths, null, ct) ?? throw new InvalidOperationException("no search index yet: run search-index build first");
        var gs = await GroupStore.LoadAsync(store, paths, head.Version, ct);
        var ofGroup = gs?.GroupOf() ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var groupMembers = (gs?.Groups ?? []).ToDictionary(g => g.Id, g => g.Members, StringComparer.Ordinal);
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal); for (int i = 0; i < head.Uploads.Count; i++) indexOf[head.Uploads[i].Id] = i;
        var all = new List<string>();
        foreach (var b in await store.ListAsync(paths.FramesDir, false, true, ct)) { if (!b.Name.EndsWith(".bin", StringComparison.Ordinal)) continue; var id = b.Name[paths.FramesDir.Length..^".bin".Length]; if (!id.Contains('/')) all.Add(id); }
        all.Sort(StringComparer.Ordinal);
        var work = (only == null ? all : only.Where(x => all.Contains(x)).ToList()).Take(limit).ToList();
        var cache = new BlockCache(s.GroupCacheMb * 1024L * 1024L);
        var oldC = new ConcurrentDictionary<string, int>(); var newC = new ConcurrentDictionary<string, int>();
        int done = 0, oldSkipNewNot = 0, newSkipOldNot = 0, newVsJoin = 0, failed = 0; var outLock = new object(); var falseSkips = new ConcurrentBag<string>(); var aiChanges = new ConcurrentBag<string>(); var clock = System.Diagnostics.Stopwatch.StartNew();
        int perDrama = Math.Max(1, threads / 2);
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(threads, 8)), CancellationToken = ct }, async (id, c) =>
        {
            try
            {
                var rec = await OneAsync(store, paths, head, ofGroup, groupMembers, indexOf, s, id, perDrama, cache, c);
                if (rec == null) { Interlocked.Increment(ref failed); return; }
                string od = (string)rec["old"]!["decision"]!, nd = (string)rec["new"]!["decision"]!;
                oldC.AddOrUpdate(od, 1, (_, v) => v + 1); newC.AddOrUpdate(nd, 1, (_, v) => v + 1);
                if (PreCheckDecision.IsSkip(od) && !PreCheckDecision.IsSkip(nd)) Interlocked.Increment(ref oldSkipNewNot);
                if (!PreCheckDecision.IsSkip(od) && PreCheckDecision.IsSkip(nd)) Interlocked.Increment(ref newSkipOldNot);
                var gk = (string?)rec["groupStep"]?["kind"];
                if (nd == PreCheckDecision.New && gk == "join") Interlocked.Increment(ref newVsJoin);
                if (rec["possibleFalseSkip"] is JsonObject fsk) falseSkips.Add(fsk.ToJsonString());
                if ((bool?)rec["ai"]?["changed"] == true) aiChanges.Add($"{id}: score {rec["ai"]!["old"]} -> {rec["ai"]!["new"]} (flag {rec["ai"]!["oldFlag"]} -> {rec["ai"]!["newFlag"]})");
                lock (outLock) output.WriteLine(rec.ToJsonString());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Interlocked.Increment(ref failed); Log.Info($"whatif {id}: {Redact.Sas(e.Message)}"); }
            int n = Interlocked.Increment(ref done); if (n % 50 == 0) progress($"precheck whatif: {n}/{work.Count} dramas");
        });
        lock (outLock) output.Flush();
        return new Summary(work.Count - failed, new Dictionary<string, int>(oldC), new Dictionary<string, int>(newC), oldSkipNewNot, newSkipOldNot, newVsJoin, failed, falseSkips.OrderBy(x => x, StringComparer.Ordinal).ToList(), aiChanges.OrderBy(x => x, StringComparer.Ordinal).ToList(), clock.Elapsed.TotalSeconds);
    }

    static async Task<JsonObject?> OneAsync(IStore store, Paths paths, SearchData head, Dictionary<string, string> ofGroup, Dictionary<string, List<GroupMember>> groupMembers, Dictionary<string, int> indexOf, PreCheckSettings s, string id, int threads, BlockCache cache, CancellationToken ct)
    {
        var vj = await VideoJson.ReadAsync(store, paths, id, ct);
        var z = await FramesReader.OpenAsync(store, paths.FramesFile(id), ct); if (z == null) return null;
        var h = await z.ReadHeaderAsync(ct);
        var blocks = await z.ReadBlocksAsync(Enumerable.Range(0, h.K).ToList(), CenterSearch.MergeGap, CenterSearch.MaxMerged, ct);
        var own = new List<(float T, float[] V)>();
        foreach (var (_, m) in blocks) { var rows = AiProbe.RowsFromF16(m.Vectors); for (int i = 0; i < rows.Length && i < m.Times.Length; i++) { Sscd.L2Normalize(rows[i]); own.Add((m.Times[i], rows[i])); } }
        own.Sort((x, y) => x.T.CompareTo(y.T)); if (own.Count == 0) return null;
        double duration = (vj != null ? VideoJson.Duration(vj.Root) : null) ?? own[^1].T;
        string? GroupOf(string up) => ofGroup.TryGetValue(up, out var g) ? g : null;
        double? DurationOf(string up) => head.Catalog?.Find(up)?.DurationSeconds;
        int ex = indexOf.TryGetValue(id, out var xi) ? xi : -1;
        List<float[]> Pick(double ws, double we, int frames)
        {
            var inWin = own.Where(f => f.T >= ws && f.T <= we).ToList(); if (inWin.Count == 0) inWin = own;   // a drama with no frame in the window: all of them
            return (frames <= 0 ? Enumerable.Range(0, inWin.Count).ToArray() : PreCheck.SubsampleIndices(inWin.Count, frames)).Select(i => inWin[i].V).ToList();
        }
        // ---- the new sample: one window per position, frames/2 each, one lookup; the old sample: the single window at 0.5, all frames
        var wins = PreCheck.Windows(duration, s.WindowSeconds, s.WindowPositions);
        int per = s.Frames <= 0 ? 0 : Math.Max(1, s.Frames / Math.Max(1, wins.Count));
        var rowsNew = new List<float[]>(); var info = new List<PreCheckAnalyzer.SegInfo>();
        foreach (var (ws, we, wp) in wins) { var r = Pick(ws, we, per); rowsNew.AddRange(r); info.Add(new PreCheckAnalyzer.SegInfo(wp, ws, we, r.Count)); }
        var (os, oe, _) = PreCheck.Window(duration, s.WindowSeconds, s.WindowPosition);
        var rowsOld = Pick(os, oe, s.Frames);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (bestN, _) = await PreCheckSearch.BestPerFrameAsync(store, paths, head, rowsNew, s.TopCenters, threads, ct, ex, cache);
        var (bestO, _) = await PreCheckSearch.BestPerFrameAsync(store, paths, head, rowsOld, s.TopCenters, threads, ct, ex, cache);
        var upIds = head.Uploads.Select(u => u.Id).ToList();
        IReadOnlyList<GroupCopy> Copies(string g) => groupMembers[g].Where(m => m.Upload != id).Select(m => new GroupCopy(m.Upload, m.SpeechLanguage, m.Dubbed, m.Duration ?? DurationOf(m.Upload))).ToList();
        PreCheckAnalysis Build(FrameBest?[] best, int n, List<float[]> rows)
        {
            var hits = new List<FrameHit>();
            for (int i = 0; i < best.Length; i++) if (best[i] is { } b && b.Cosine >= s.Tau && b.Upload >= 0 && b.Upload < upIds.Count && upIds[b.Upload] != id) hits.Add(new FrameHit(i, upIds[b.Upload], b.Time, b.Cosine));
            var ug = hits.Select(x => x.Upload).Distinct(StringComparer.Ordinal).ToDictionary(u => u, u => GroupOf(u), StringComparer.Ordinal);
            var gm = ug.Values.Where(g => g != null).Distinct().ToDictionary(g => g!, g => Copies(g!), StringComparer.Ordinal);
            var (matches, noHit) = PreCheckSearch.Shares(head, best, s.Tau); matches = matches.Where(m => m.Upload != id).ToList();
            for (int i = 0; i < matches.Count; i++) { var m = matches[i]; matches[i] = m with { Duration = DurationOf(m.Upload) ?? (GroupOf(m.Upload) is { } mg ? groupMembers[mg].FirstOrDefault(x => x.Upload == m.Upload)?.Duration : null), SpeechLanguage = head.Catalog?.Find(m.Upload)?.SpeechLanguage, Dubbed = head.Catalog?.Find(m.Upload)?.Dubbed }; }
            return new PreCheckAnalysis(null, head.Version, n, noHit, matches, AiProbe.Default.Score(rows.ToArray()), new Dictionary<string, double>()) { Hits = hits, UploadGroups = ug, GroupMembers = gm };
        }
        var anOld = Build(bestO, rowsOld.Count, rowsOld);
        anOld = anOld with { Group = DramaGroups.Propose(anOld, os, oe - os, s) };
        var anNew = Build(bestN, rowsNew.Count, rowsNew);
        var segs = await PreCheckAnalyzer.SegmentsAsync(store, paths, info, bestN, upIds, anNew.UploadGroups.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), anNew.GroupMembers.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), id, rowsNew.ToArray(), s, threads, new Dictionary<string, double>(), ct);
        // every upload a segment matched needs its group known (a member the lookup did not list but a direct check found)
        var ugAll = new Dictionary<string, string?>(anNew.UploadGroups, StringComparer.Ordinal); var gmAll = new Dictionary<string, IReadOnlyList<GroupCopy>>(anNew.GroupMembers, StringComparer.Ordinal);
        foreach (var u in segs.SelectMany(x => x.Shares.Keys).Distinct()) { var g = GroupOf(u); ugAll[u] = g; if (g != null && !gmAll.ContainsKey(g)) gmAll[g] = Copies(g); }
        anNew = anNew with { Segments = segs, UploadGroups = ugAll, GroupMembers = gmAll };
        var segInfoW = wins.Select(w => new SampleSegment("", w.Start, w.End, w.Position)).ToList();
        anNew = anNew with { Group = PreCheckStage.ProposeGroup(anNew, segInfoW, s) };
        var oldD = PreCheck.DecideSingle(anOld, duration, _ => null, s);
        if (oldD.Decision == PreCheckDecision.Partial) oldD = oldD with { Decision = PreCheckDecision.Overlap };   // the retired word, written as it would be today
        var newD = PreCheck.Decide(anNew, duration, _ => null, s);
        double lookupSecs = sw.Elapsed.TotalSeconds;
        var grp = (vj?.Root["grouping"] as JsonObject); var gp = GroupProposal.FromJson(grp?["proposal"] as JsonObject);
        JsonObject Dec(PreCheckDecision d) => new() { ["decision"] = d.Decision, ["reasons"] = new JsonArray(d.Reasons.Select(r => (JsonNode?)r).ToArray()), ["member"] = d.MatchedMember ?? d.Match?.Upload };
        double aiOld = anOld.AiScore, aiNew = anNew.AiScore; bool fOld = aiOld >= s.AiFlag, fNew = aiNew >= s.AiFlag;
        var o = new JsonObject
        {
            ["id"] = id, ["duration"] = Math.Round(duration, 2), ["sampleFrames"] = rowsNew.Count, ["oldFrames"] = rowsOld.Count,
            ["window"] = new JsonArray((JsonNode?)Math.Round(os, 1), Math.Round(oe, 1)),
            ["segments"] = new JsonArray(segs.Select(x => (JsonNode?)new JsonObject { ["position"] = x.Position, ["start"] = Math.Round(x.Start, 1), ["end"] = Math.Round(x.End, 1), ["frames"] = x.Frames, ["top"] = new JsonObject(x.Shares.OrderByDescending(kv => kv.Value.Share).Take(3).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject { ["share"] = Math.Round(kv.Value.Share, 3), ["time"] = Math.Round(kv.Value.MedianTime, 1) }))) }).ToArray()),
            ["old"] = Dec(oldD), ["new"] = Dec(newD),
            ["membersChecked"] = new JsonArray(newD.Checked.Select(c => (JsonNode?)new JsonObject { ["member"] = c.Member, ["group"] = c.Group, ["duration"] = c.Duration, ["shares"] = new JsonArray(c.Shares.Select(x => (JsonNode?)Math.Round(x, 3)).ToArray()), ["times"] = new JsonArray(c.Times.Select(x => (JsonNode?)(x == null ? null : Math.Round(x.Value, 1))).ToArray()), ["passed"] = c.Passed, ["reason"] = c.Reason }).ToArray()),
            ["ai"] = new JsonObject { ["old"] = Math.Round(aiOld, 4), ["new"] = Math.Round(aiNew, 4), ["oldFlag"] = fOld, ["newFlag"] = fNew, ["changed"] = fOld != fNew },
            ["groupStep"] = grp == null ? null : new JsonObject { ["kind"] = gp?.Kind, ["joinUpload"] = gp?.JoinUpload, ["sampled"] = grp["sampled"]?.DeepClone(), ["sampleCovered"] = grp["sampleCovered"]?.DeepClone(), ["covered"] = grp["covered"]?.DeepClone(), ["coveredShare"] = grp["coveredShare"]?.DeepClone() },
            ["lookupSeconds"] = Math.Round(lookupSecs, 2),
        };
        if (PreCheckDecision.IsSkip(newD.Decision))
        {
            var kind = gp?.Kind; double? cov = (double?)grp?["coveredShare"];
            if (kind is "new" or "mixed" || cov is < 0.5)
            {
                var chk = newD.Checked.FirstOrDefault(c => c.Passed);
                o["possibleFalseSkip"] = new JsonObject { ["id"] = id, ["decision"] = newD.Decision, ["member"] = newD.MatchedMember, ["duration"] = Math.Round(duration, 1), ["memberDuration"] = chk?.Duration, ["shares"] = chk == null ? null : new JsonArray(chk.Shares.Select(x => (JsonNode?)Math.Round(x, 3)).ToArray()), ["times"] = chk == null ? null : new JsonArray(chk.Times.Select(x => (JsonNode?)(x == null ? null : Math.Round(x.Value, 1))).ToArray()), ["groupStepKind"] = kind, ["coveredShare"] = cov };
            }
        }
        return o;
    }
}

