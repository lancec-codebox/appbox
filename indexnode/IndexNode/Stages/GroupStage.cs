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

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token; var paths = ctx.Paths; var id = ctx.BlobName; var s = settings ?? new PreCheckSettings();
        long seq = ctx.Queue?.Read(st => st.Get(id)?.Seq) ?? throw new InvalidOperationException($"group {id}: no sequence number");
        ctx.Report("group", "index");
        var head = await SearchData.LoadAsync(store, paths, null, ct);
        var q = ctx.Queue!;
        bool Earlier(string other) => other != id && (q.Read(st => st.Get(other)?.Seq) is not { } o || o < seq);
        var idx = head?.Restrict(u => Earlier(u.Id));
        // every earlier drama whose frames the index does not hold (not built yet, or finished after the build): searched by brute force from its retained file if it has one, else its frames file
        var inIndex = head?.Uploads.Select(u => u.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        var fresh = q.Read(st => st.Subjects.Values.Where(x => x.Id != id && !inIndex.Contains(x.Id) && Earlier(x.Id) && (x.CentersAt != null || x.Entries.TryGetValue("process", out var pe) && pe.State == "done")).OrderBy(x => x.Seq ?? long.MaxValue).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Id).ToList());

        if (idx == null && fresh.Count == 0) { await WriteAsync(ctx, id, seq, GroupProposal.None, [], 0, 0, 0, [], null, s, ct); return; }
        // own frames, from the node's local copy of the frames file
        var z = await FramesReader.OpenAsync(store, paths.FramesFile(id), ct) ?? throw new FileNotFoundException(paths.FramesFile(id));
        var h = await z.ReadHeaderAsync(ct);
        var blocks = await z.ReadBlocksAsync(Enumerable.Range(0, h.K).ToList(), CenterSearch.MergeGap, CenterSearch.MaxMerged, ct);
        var own = new List<(float T, float[] V, byte[] F16)>();
        foreach (var (_, m) in blocks) { var rows = AiProbe.RowsFromF16(m.Vectors); for (int i = 0; i < rows.Length && i < m.Times.Length; i++) { Sscd.L2Normalize(rows[i]); own.Add((m.Times[i], rows[i], m.Vectors.AsSpan(i * CentersFile.Dim * 2, CentersFile.Dim * 2).ToArray())); } }
        own.Sort((a, b) => a.T.CompareTo(b.T));
        if (own.Count == 0) { await WriteAsync(ctx, id, seq, GroupProposal.None, [], 0, 0, 0, [], null, s, ct); return; }
        ctx.Report("group", $"{own.Count} frames");
        idx ??= new SearchData { Version = head?.Version ?? 0, Mean = head?.Mean ?? new float[CentersFile.Dim], Bits = [], RefUpload = [], RefCenter = [], Uploads = [] };
        var best = new FrameBest?[own.Count]; int threads = Math.Max(1, ctx.ThreadShare());
        var freshUps = new List<string>();
        for (int at = 0; at < own.Count; at += Chunk)
        {
            var part = own.Skip(at).Take(Chunk).Select(f => f.V).ToList();
            FrameBest?[] b;
            if (idx.Count > 0) (b, _) = await PreCheckSearch.BestPerFrameAsync(store, paths, idx, part, s.TopCenters, threads, ct);
            else b = new FrameBest?[part.Count];
            if (fresh.Count > 0) { var used = await PreCheckSearch.MergeFreshAsync(store, paths, idx, fresh, part, b, threads, ct); if (at == 0) freshUps.AddRange(used.Select(u => u.Id)); }
            Array.Copy(b, 0, best, at, b.Length);
        }
        string IdOf(int u) => u < idx.Uploads.Count ? idx.Uploads[u].Id : freshUps[u - idx.Uploads.Count];
        var times = own.Select(f => (double)f.T).ToList();
        // the proposal from an even sample of the best matches (the same sampler as the index job's full check)
        var pick = PreCheck.SubsampleIndices(own.Count, s.GroupCheckFrames);
        var hits = new List<FrameHit>();
        for (int k = 0; k < pick.Length; k++) if (best[pick[k]] is { } bb && bb.Cosine >= s.Tau) hits.Add(new FrameHit(k, IdOf(bb.Upload), bb.Time, bb.Cosine));
        var a = new PreCheckAnalysis(null, idx.Version, pick.Length, 0, [], 0, new Dictionary<string, double>()) { Hits = hits };
        var prop = DramaGroups.Propose(a, 0, 0, s, k => times[pick[k]]);
        int covered = best.Count(b => b != null && b.Cosine >= s.RetainT);
        var stretches = Stretches(times, best, IdOf, s.RetainT, s.OffsetTolerance);
        await WriteAsync(ctx, id, seq, prop, own.Select(f => ((double)f.T, f.F16)).ToList(), covered, own.Count, 0, stretches, best, s, ct);
    }

    async Task WriteAsync(JobContext ctx, string id, long seq, GroupProposal prop, IReadOnlyList<(double T, byte[] F16)> own, int covered, int total, int unused, List<JsonObject> stretches, FrameBest?[]? best, PreCheckSettings s, CancellationToken ct)
    {
        var paths = ctx.Paths; int kept = total; string? retained = null;
        bool retain = s.RetainEnabled && total > 0 && best != null && prop.Kind == GroupProposal.JoinKind && !prop.Mixed && (double)covered / total >= s.RetainF;
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
            ["v"] = 1, ["seq"] = seq, ["at"] = Protocol.NowUtc(), ["proposal"] = prop.ToJson(), ["frames"] = total, ["covered"] = covered, ["coveredShare"] = total == 0 ? 0 : Math.Round((double)covered / total, 4),
            ["retained"] = retained == null ? null : kept, ["retainedFile"] = retained, ["t"] = s.RetainT, ["f"] = s.RetainF, ["stretches"] = new JsonArray(stretches.Select(x => (JsonNode?)x).ToArray()),
        };
        await VideoJson.UpdateAsync(store, paths, id, root => root["grouping"] = rec, ct);
        Log.Info($"group {id}: seq {seq}, {prop.Kind}{(prop.JoinUpload != null ? " " + prop.JoinUpload : "")}, {covered}/{total} frames covered, {(retained == null ? "all kept" : kept + " retained")}");
    }
}
