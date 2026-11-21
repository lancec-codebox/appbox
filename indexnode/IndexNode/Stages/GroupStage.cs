using System.Diagnostics;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.10: the per-drama group step, after process. Every frame of this drama (its frames file, from the node's local cache) is searched against the EARLIER dramas only (lower sequence number; the index restricted to them, plus the dramas finished since the index build, by brute force).
/// From the best match per frame it writes, into video.json "grouping": the proposal (join / mixed / new, offset, shares), the coverage and the stretches (contiguous covered runs with their source drama and offset).
/// With retain.enabled, a drama that joins a group with at least retain.f of its frames covered at cosine &gt;= retain.t keeps only its uncovered frames, in index/search/retained/&lt;id&gt;.bin (the index is built from that file instead of the frames file).
/// The search-index job folds these records into groups and the catalog; it never searches frames for a drama that has one.
/// </summary>
public sealed class GroupStage(IStore store, PreCheckSettings? settings = null) : IStageHandler
{
    readonly GroupDedup dedup = new(store);
    public const int Chunk = 60;

    /// <summary>Maps the best-match arrays to covered runs: [from, to] own times (seconds) with the source upload and the median offset.</summary>
    public static List<JsonObject> Stretches(IReadOnlyList<double> times, FrameBest?[] best, Func<int, string> idOf, double t, double tolerance)
    {
        var res = new List<JsonObject>(); int i = 0, n = times.Count;
        while (i < n)
        {
            if (best[i] is not { } b || b.Cosine < t) { i++; continue; }
            var up = idOf(b.Upload); int j = i; var offs = new List<double> { b.Time - times[i] };
            while (j + 1 < n && best[j + 1] is { } nb && nb.Cosine >= t && idOf(nb.Upload) == up && Math.Abs(nb.Time - times[j + 1] - offs[0]) <= tolerance) { j++; offs.Add(nb.Time - times[j]); }
            offs.Sort(); double med = offs.Count % 2 == 1 ? offs[offs.Count / 2] : (offs[offs.Count / 2 - 1] + offs[offs.Count / 2]) / 2;
            res.Add(new JsonObject { ["from"] = Math.Round(times[i], 2), ["to"] = Math.Round(times[j], 2), ["frames"] = j - i + 1, ["upload"] = up, ["offset"] = Math.Round(med, 2) });
            i = j + 1;
        }
        return res;
    }

    /// <summary>Test seam: called with the number of frames each search call handles (the frames actually searched, sample and full pass alike).</summary>
    public Action<int>? OnSearch { get; init; }

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token; var paths = ctx.Paths; var id = ctx.BlobName; var s = settings ?? new PreCheckSettings();
        long seq = ctx.Queue?.Read(st => st.Get(id)?.Seq) ?? throw new InvalidOperationException($"group {id}: no sequence number");
        ctx.Report("group", "index");
        var clock = Stopwatch.StartNew(); double tDedup = 0, tIdx = 0, tOwn = 0, tSearch = 0, tScan = 0, tRead = 0, tScore = 0, tFresh = 0, tWrite = 0; int searched = 0;
        var head = await SearchData.LoadAsync(store, paths, null, ct);
        var q = ctx.Queue!;
        bool Earlier(string other) => other != id && (q.Read(st => st.Get(other)?.Seq) is not { } o || o < seq);
        var idx = head?.Restrict(u => Earlier(u.Id));
        // every earlier drama whose frames the index does not hold (not built yet, or finished after the build): searched by brute force from its retained file if it has one, else its frames file
        var inIndex = head?.Uploads.Select(u => u.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        var fresh = q.Read(st => st.Subjects.Values.Where(x => x.Id != id && !inIndex.Contains(x.Id) && Earlier(x.Id) && (x.CentersAt != null || x.Entries.TryGetValue("process", out var pe) && pe.State == "done")).OrderBy(x => x.Seq ?? long.MaxValue).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Id).ToList());
        tIdx = clock.Elapsed.TotalMilliseconds; int earlier = (idx?.Uploads.Count ?? 0) + fresh.Count;
        string Split() => $"{earlier} earlier ({idx?.Uploads.Count ?? 0} indexed + {fresh.Count} fresh); index {tIdx:F0} ms, own frames {tOwn:F0} ms, search {tSearch:F0} ms (scan {tScan:F0}, read {tRead:F0}, score {tScore:F0}), fresh {tFresh:F0} ms, dedup {tDedup:F0} ms, write {tWrite:F0} ms; {searched} frames searched";

        if (idx == null && fresh.Count == 0) { await WriteAsync(ctx, id, seq, GroupProposal.None, [], null, 0, 0, 0, [], null, s, ct, null); return; }
        // own frames, from the node's local copy of the frames file
        var sw = Stopwatch.StartNew();
        var z = await FramesReader.OpenAsync(store, paths.FramesFile(id), ct) ?? throw new FileNotFoundException(paths.FramesFile(id));
        var h = await z.ReadHeaderAsync(ct);
        var blocks = await z.ReadBlocksAsync(Enumerable.Range(0, h.K).ToList(), CenterSearch.MergeGap, CenterSearch.MaxMerged, ct);
        var own = new List<(float T, float[] V, byte[] F16)>();
        foreach (var (_, m) in blocks) { var rows = AiProbe.RowsFromF16(m.Vectors); for (int i = 0; i < rows.Length && i < m.Times.Length; i++) { Sscd.L2Normalize(rows[i]); own.Add((m.Times[i], rows[i], m.Vectors.AsSpan(i * CentersFile.Dim * 2, CentersFile.Dim * 2).ToArray())); } }
        own.Sort((a, b) => a.T.CompareTo(b.T));
        tOwn = sw.Elapsed.TotalMilliseconds;
        if (own.Count == 0) { await WriteAsync(ctx, id, seq, GroupProposal.None, [], null, 0, 0, 0, [], null, s, ct, null); return; }
        ctx.Report("group", $"{own.Count} frames");
        idx ??= new SearchData { Version = head?.Version ?? 0, Mean = head?.Mean ?? new float[CentersFile.Dim], Bits = [], RefUpload = [], RefCenter = [], Uploads = [] };
        var best = new FrameBest?[own.Count]; int threads = Math.Max(1, ctx.ThreadShare());
        var freshUps = new List<string>(); bool freshDone = false;
        var cache = new BlockCache(s.GroupCacheMb * 1024L * 1024L);   // decoded member blocks, shared by every search call of this step (a block is read and decoded once, not once per chunk)

        async Task SearchAsync(IReadOnlyList<int> ixs, int chunk)
        {
            for (int at = 0; at < ixs.Count; at += chunk)
            {
                var sel = ixs.Skip(at).Take(chunk).ToList(); var part = sel.Select(i => own[i].V).ToList(); FrameBest?[] b;
                if (idx.Count > 0)
                {
                    var t0 = Stopwatch.StartNew(); PreCheckSearch.Stats st; (b, st) = await PreCheckSearch.BestPerFrameAsync(store, paths, idx, part, s.TopCenters, threads, ct, -1, cache);
                    tSearch += t0.Elapsed.TotalMilliseconds; tScan += st.ScanMs; tRead += st.ReadMs; tScore += st.ScoreMs;
                }
                else b = new FrameBest?[part.Count];
                if (fresh.Count > 0)
                {
                    var t1 = Stopwatch.StartNew(); var used = await PreCheckSearch.MergeFreshAsync(store, paths, idx, fresh, part, b, threads, ct); tFresh += t1.Elapsed.TotalMilliseconds;
                    if (!freshDone) { freshUps.AddRange(used.Select(u => u.Id)); freshDone = true; }
                }
                for (int k = 0; k < sel.Count; k++) best[sel[k]] = b[k];
                searched += sel.Count; OnSearch?.Invoke(sel.Count);
            }
        }
        string IdOf(int u) => u < idx.Uploads.Count ? idx.Uploads[u].Id : freshUps[u - idx.Uploads.Count];
        var times = own.Select(f => (double)f.T).ToList();
        // 0.13.12: SAMPLE FIRST. The proposal comes from an even sample of the frames (the same sampler as the index job's full check); only a join or mixed drama is then searched in full
        var pick = PreCheck.SubsampleIndices(own.Count, s.GroupCheckFrames);
        await SearchAsync(pick, Math.Max(pick.Length, 1));
        var hits = new List<FrameHit>();
        for (int k = 0; k < pick.Length; k++) if (best[pick[k]] is { } bb && bb.Cosine >= s.Tau) hits.Add(new FrameHit(k, IdOf(bb.Upload), bb.Time, bb.Cosine));
        var a = new PreCheckAnalysis(null, idx.Version, pick.Length, 0, [], 0, new Dictionary<string, double>()) { Hits = hits };
        var prop = DramaGroups.Propose(a, 0, 0, s, k => times[pick[k]]);
        if (prop.Kind != GroupProposal.JoinKind && prop.Kind != GroupProposal.MixedKind)
        {
            // new / none: nothing is retained and no stretch matters: the record is written from the sample
            int sampleCovered = pick.Count(i => best[i] is { } b && b.Cosine >= s.RetainT);
            var sampleStretches = Stretches(pick.Select(i => times[i]).ToList(), pick.Select(i => best[i]).ToArray(), IdOf, s.RetainT, s.OffsetTolerance);
            await WriteAsync(ctx, id, seq, prop, [], null, own.Count, pick.Length, sampleCovered, sampleStretches, null, s, ct, Split);
            return;
        }
        if (prop.Kind == GroupProposal.JoinKind && !prop.Mixed && prop.JoinUpload != null)
        {
            // 0.13.13: DEDUP against the group only: every earlier member of the target's group, by its FULL frames file, brute force (no global index search)
            var td = Stopwatch.StartNew();
            var earlierSeq = q.Read(st => st.Subjects.Values.Where(x => x.Id != id && (x.Seq is not { } o || o < seq)).ToDictionary(x => x.Id, x => x.Seq, StringComparer.Ordinal));
            GroupSet? known = null; try { if (head != null) known = await GroupStore.LoadAsync(store, paths, head.Version, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
            var members = await dedup.SetAsync(paths, prop.JoinUpload, earlierSeq, known, ct);
            if (!members.Contains(prop.JoinUpload)) members.Insert(0, prop.JoinUpload);
            var (M, mem, tm, used) = await GroupDedup.LoadAsync(store, paths, members, ct);
            var dbest = GroupDedup.Best(own.Select(f => f.V).ToList(), M, mem, tm, threads, ct);
            tDedup = td.Elapsed.TotalMilliseconds;
            string DIdOf(int u) => used[u];
            int dcov = dbest.Count(b => b != null && b.Cosine >= s.RetainT);
            await WriteAsync(ctx, id, seq, prop, own.Select(f => ((double)f.T, f.F16)).ToList(), dcov, own.Count, own.Count, dcov, Stretches(times, dbest, DIdOf, s.RetainT, s.OffsetTolerance), dbest, s, ct, Split, used, M.Length / CentersFile.Dim);
            return;
        }
        // mixed: the full pass over the frames the sample did not search (its results are reused); keeps all frames, writes no file
        var inSample = new HashSet<int>(pick); var rest = Enumerable.Range(0, own.Count).Where(i => !inSample.Contains(i)).ToList();
        await SearchAsync(rest, 600);
        int covered = best.Count(b => b != null && b.Cosine >= s.RetainT);
        var stretches = Stretches(times, best, IdOf, s.RetainT, s.OffsetTolerance);
        await WriteAsync(ctx, id, seq, prop, own.Select(f => ((double)f.T, f.F16)).ToList(), covered, own.Count, own.Count, covered, stretches, best, s, ct, Split);
    }


    async Task WriteAsync(JobContext ctx, string id, long seq, GroupProposal prop, IReadOnlyList<(double T, byte[] F16)> own, int? covered, int total, int sampled, int sampleCovered, List<JsonObject> stretches, FrameBest?[]? best, PreCheckSettings s, CancellationToken ct, Func<string>? split, IReadOnlyList<string>? dedupAgainst = null, int dedupFrames = 0)
    {
        var paths = ctx.Paths; int kept = total; string? retained = null; var wsw = Stopwatch.StartNew();
        bool retain = s.RetainEnabled && covered != null && total > 0 && best != null && prop.Kind == GroupProposal.JoinKind && !prop.Mixed;   // 0.13.13: no retain.f gate: every join keeps only its frames unique among the group's earlier members (at least one)
        if (retain)
        {
            // only the frames no earlier drama covers; at least one (the least covered) so the file is never empty
            var keep = Enumerable.Range(0, total).Where(i => best![i] is not { } b || b.Cosine < s.RetainT).ToList();
            if (keep.Count == 0) keep.Add(Enumerable.Range(0, total).OrderBy(i => best![i]?.Cosine ?? -1).First());
            var f16 = new byte[keep.Count * CentersFile.Dim * 2]; for (int k = 0; k < keep.Count; k++) own[keep[k]].F16.CopyTo(f16, k * CentersFile.Dim * 2);
            var built = CentersStep.Build(id, f16, keep.Select(i => own[i].T).ToList(), Math.Max(1, ctx.ThreadShare()));
            var bin = FramesFile.FromCenters(built.Bin, seq); var name = paths.RetainedFile(id);
            await store.PutAsync(name, bin, null, null, false, ct, "application/octet-stream");
            await store.SetMetadataAsync(name, FramesFile.Metadata(bin), null, ct);
            kept = keep.Count; retained = name;
        }
        else if (store != null) { try { await store.DeleteAsync(paths.RetainedFile(id), null, ct); } catch (Exception e) when (e is StorageException { Status: 404 } || e is FileNotFoundException) { } }   // a re-run that no longer retains removes the old file
        var rec = new JsonObject
        {
            ["v"] = 1, ["seq"] = seq, ["at"] = Protocol.NowUtc(), ["proposal"] = prop.ToJson(), ["frames"] = total,
            // 0.13.12: `covered` is null when only the even sample was searched (new / none): `sampled` frames were searched, `sampleCovered` of them at cosine >= t
            ["sampled"] = sampled, ["sampleCovered"] = sampleCovered, ["covered"] = covered, ["coveredShare"] = covered == null || total == 0 ? null : Math.Round((double)covered.Value / total, 4),
            ["dedupAgainst"] = dedupAgainst == null ? null : new JsonArray(dedupAgainst.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["dedupFrames"] = dedupAgainst == null ? null : dedupFrames,
            ["retained"] = retained == null ? null : kept, ["retainedFile"] = retained, ["t"] = s.RetainT, ["f"] = s.RetainF, ["stretches"] = new JsonArray(stretches.Select(x => (JsonNode?)x).ToArray()),
        };
        await VideoJson.UpdateAsync(store, paths, id, root => root["grouping"] = rec, ct);
        Log.Info($"group {id}: seq {seq}, {prop.Kind}{(prop.JoinUpload != null ? " " + prop.JoinUpload : "")}, {(covered == null ? $"{sampleCovered}/{sampled} sampled frames covered" : $"{covered}/{total} frames covered")}, {(retained == null ? "all kept" : kept + " retained")}" + (split == null ? "" : "; " + split().Replace("write 0 ms", $"write {wsw.Elapsed.TotalMilliseconds:F0} ms")));
    }
}

