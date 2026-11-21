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
    public sealed record Summary(int Dramas, Dictionary<string, int> Old, Dictionary<string, int> New, int OldDupNewPartial, int OldDupNewPartialLonger, int DupButGroupNewOrMixed, int NewButGroupJoin, int Failed);

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
        return $"precheck whatif: {s.Dramas} dramas{(s.Failed > 0 ? $" ({s.Failed} failed)" : "")}\n  old rule: {C(s.Old)}\n  new rule: {C(s.New)}\n  old duplicate but new partial: {s.OldDupNewPartial} (longer than L: {s.OldDupNewPartialLonger})\n  disagreements with the group step: pre-check duplicate where the group step said new or mixed: {s.DupButGroupNewOrMixed}; pre-check new where the group step said join: {s.NewButGroupJoin}";
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
        int done = 0, oldDupNewPartial = 0, longer = 0, dupVsGroup = 0, newVsJoin = 0, failed = 0; var outLock = new object();
        int perDrama = Math.Max(1, threads / 2);
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(threads, 8)), CancellationToken = ct }, async (id, c) =>
        {
            try
            {
                var rec = await OneAsync(store, paths, head, ofGroup, groupMembers, indexOf, s, id, perDrama, cache, c);
                if (rec == null) { Interlocked.Increment(ref failed); return; }
                string od = (string)rec["old"]!["decision"]!, nd = (string)rec["new"]!["decision"]!;
                oldC.AddOrUpdate(od, 1, (_, v) => v + 1); newC.AddOrUpdate(nd, 1, (_, v) => v + 1);
                if (od == PreCheckDecision.Duplicate && nd == PreCheckDecision.Partial) { Interlocked.Increment(ref oldDupNewPartial); if ((bool?)rec["new"]!["longerThanL"] == true) Interlocked.Increment(ref longer); }
                var gk = (string?)rec["groupStep"]?["kind"];
                if (nd == PreCheckDecision.Duplicate && gk is "new" or "mixed") Interlocked.Increment(ref dupVsGroup);
                if (nd == PreCheckDecision.New && gk == "join") Interlocked.Increment(ref newVsJoin);
                lock (outLock) output.WriteLine(rec.ToJsonString());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Interlocked.Increment(ref failed); Log.Info($"whatif {id}: {Redact.Sas(e.Message)}"); }
            int n = Interlocked.Increment(ref done); if (n % 50 == 0) progress($"precheck whatif: {n}/{work.Count} dramas");
        });
        lock (outLock) output.Flush();
        return new Summary(work.Count - failed, new Dictionary<string, int>(oldC), new Dictionary<string, int>(newC), oldDupNewPartial, longer, dupVsGroup, newVsJoin, failed);
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
        var (start, end, _) = PreCheck.Window(duration, s.WindowSeconds, s.WindowPosition);
        var inWin = own.Where(f => f.T >= start && f.T <= end).ToList(); if (inWin.Count == 0) inWin = own;   // a drama with no frame in the window: all of them
        var pick = s.Frames <= 0 ? Enumerable.Range(0, inWin.Count).ToArray() : PreCheck.SubsampleIndices(inWin.Count, s.Frames);
        var rowsQ = pick.Select(i => inWin[i].V).ToList();
        int ex = indexOf.TryGetValue(id, out var xi) ? xi : -1;
        var (best, _) = await PreCheckSearch.BestPerFrameAsync(store, paths, head, rowsQ, s.TopCenters, threads, ct, ex, cache);
        var (matches, noHit) = PreCheckSearch.Shares(head, best, s.Tau);
        matches = matches.Where(m => m.Upload != id).ToList();
        // the groups WITHOUT this drama
        string? GroupOf(string up) => ofGroup.TryGetValue(up, out var g) ? g : null;
        double? DurationOf(string up) => head.Catalog?.Find(up)?.DurationSeconds;
        var hits = new List<FrameHit>();
        for (int i = 0; i < best.Length; i++) if (best[i] is { } b && b.Cosine >= s.Tau && b.Upload >= 0 && b.Upload < head.Uploads.Count && head.Uploads[b.Upload].Id != id) hits.Add(new FrameHit(i, head.Uploads[b.Upload].Id, b.Time, b.Cosine));
        var ug = hits.Select(x => x.Upload).Distinct(StringComparer.Ordinal).ToDictionary(u => u, u => GroupOf(u), StringComparer.Ordinal);
        var gm = ug.Values.Where(g => g != null).Distinct().ToDictionary(g => g!, g => (IReadOnlyList<GroupCopy>)groupMembers[g!].Where(m => m.Upload != id).Select(m => new GroupCopy(m.Upload, m.SpeechLanguage, m.Dubbed, m.Duration ?? DurationOf(m.Upload))).ToList(), StringComparer.Ordinal);
        // the top matches' groups too (the decision may look at the group of a match that came with fewer hits)
        foreach (var m in matches.Take(5)) { var g = GroupOf(m.Upload); if (g != null) { ug.TryAdd(m.Upload, g); if (!gm.ContainsKey(g)) gm[g] = groupMembers[g].Where(x => x.Upload != id).Select(x => new GroupCopy(x.Upload, x.SpeechLanguage, x.Dubbed, x.Duration ?? DurationOf(x.Upload))).ToList(); } }
        for (int i = 0; i < matches.Count && i < 5; i++)
        {
            var m = matches[i]; double? d = DurationOf(m.Upload);
            if (d == null) { try { d = (await CatalogBuild.ReadPrecheckAsync(store, paths, m.Upload, ct)).Duration; } catch (Exception e) when (e is not OperationCanceledException) { } }
            matches[i] = m with { Duration = d, SpeechLanguage = head.Catalog?.Find(m.Upload)?.SpeechLanguage, Dubbed = head.Catalog?.Find(m.Upload)?.Dubbed };
        }
        var an = new PreCheckAnalysis(null, head.Version, rowsQ.Count, noHit, matches, 0, new Dictionary<string, double>()) { Hits = hits, UploadGroups = ug, GroupMembers = gm };
        an = an with { Group = DramaGroups.Propose(an, start, end - start, s) };
        var oldD = PreCheck.Decide(an, duration, _ => null, s, legacy: true); var newD = PreCheck.Decide(an, duration, _ => null, s);
        var top = newD.Match ?? oldD.Match ?? matches.FirstOrDefault();
        var longest = top == null ? null : PreCheck.Longest(an, top, top.Duration);
        bool longerThanL = longest != null && duration > longest.Value.Duration + Math.Max(s.DurationSlackPercent * longest.Value.Duration, s.DurationSlackSeconds);
        var grp = (vj?.Root["grouping"] as JsonObject); var gp = GroupProposal.FromJson(grp?["proposal"] as JsonObject);
        JsonObject Dec(PreCheckDecision d) => new() { ["decision"] = d.Decision, ["reasons"] = new JsonArray(d.Reasons.Select(r => (JsonNode?)r).ToArray()) };
        var o = new JsonObject
        {
            ["id"] = id, ["duration"] = Math.Round(duration, 2), ["sampleFrames"] = rowsQ.Count, ["window"] = new JsonArray((JsonNode?)Math.Round(start, 1), Math.Round(end, 1)),
            ["top"] = top?.Upload, ["group"] = top == null ? null : GroupOf(top.Upload), ["share"] = top == null ? null : Math.Round(top.Share, 4), ["bestCosine"] = top == null ? null : Math.Round(top.BestCosine, 4), ["matchedDuration"] = top?.Duration,
            ["longest"] = longest == null ? null : new JsonObject { ["id"] = longest.Value.Id, ["duration"] = Math.Round(longest.Value.Duration, 2) },
            ["old"] = Dec(oldD), ["new"] = Dec(newD),
            ["groupStep"] = grp == null ? null : new JsonObject { ["kind"] = gp?.Kind, ["joinUpload"] = gp?.JoinUpload, ["sampled"] = grp["sampled"]?.DeepClone(), ["sampleCovered"] = grp["sampleCovered"]?.DeepClone(), ["covered"] = grp["covered"]?.DeepClone(), ["coveredShare"] = grp["coveredShare"]?.DeepClone() },
        };
        o["new"]!["longerThanL"] = longerThanL;
        return o;
    }
}

