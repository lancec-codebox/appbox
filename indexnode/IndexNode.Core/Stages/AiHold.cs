using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.23: the AI hold in the sample flow. A sample check (inline or the check stage) whose record says ai.flag adds the full download held (Hold "ai", priority unchanged) while control aiDramas is off;
/// duplicate and subset stay metadata-only. `jobs hold-ai` holds the pending download/process entries whose pre-check or sample check flagged AI.
/// </summary>
public static class AiHold
{
    /// <summary>0.13.44: the owner's verdict on an upload ("real" or "ai"), kept in video.json as ai.verdict. It wins over the probe's flag everywhere, and no later probe result removes it. Null: no verdict.</summary>
    public static string? Verdict(JsonObject? rec) => (string?)(rec?["ai"] as JsonObject)?["verdict"] is "real" or "ai" ? (string?)(rec?["ai"] as JsonObject)?["verdict"] : null;

    /// <summary>Is the upload treated as an AI drama? The owner's verdict when there is one, else the probe's flag.</summary>
    public static bool Flagged(JsonObject? rec) => Verdict(rec) is { } v ? v == "ai" : (rec?["ai"] as JsonObject)?["flag"] is JsonNode f && (bool?)f == true;

    /// <summary>The same for a check record (which never carries a verdict) of an upload whose video.json may: the verdict in <paramref name="root"/> wins.</summary>
    public static bool Flagged(JsonObject? rec, JsonObject? root) => Verdict(root) is { } v ? v == "ai" : Flagged(rec);

    public sealed record VerdictReport(string Id, string Verdict, bool Known, List<string> Released, List<string> Held);

    /// <summary>
    /// 0.13.44 (`indexnode ai set &lt;id&gt;... real|ai`): the owner has looked at an upload the probe flagged (or missed) and says what it is.
    /// video.json gets ai.verdict (with who and why; the probe's score and flag stay as they are). "real": every AI hold on the upload's entries is released, in the processing queue and in the full download queue.
    /// "ai": its pending download, process and actors entries get the hold. A dry run writes nothing.
    /// </summary>
    public static async Task<VerdictReport> SetVerdictAsync(JobQueue processing, JobQueue? download, IStore store, Paths paths, string id, string verdict, string? reason, string by, bool dry, CancellationToken ct)
    {
        if (verdict is not ("real" or "ai")) throw new ArgumentException("the verdict is real or ai");
        var doc = await VideoJson.ReadAsync(store, paths, id, ct);
        var released = new List<string>(); var held = new List<string>();
        foreach (var (q, name) in new[] { (processing, "processing"), (download, "download") })
        {
            if (q == null) continue;
            await q.PollAsync(ct);
            var entries = q.Read(st => st.Get(id)?.Entries.Values.Where(e => !e.Final).Select(e => (e.Stage, e.State, e.Hold)).ToList() ?? []);
            var lines = new List<QLine>(); var at = q.NowUtc();
            foreach (var (stage, state, hold) in entries)
            {
                if (verdict == "real" && hold == "ai") { lines.Add(new QLine { T = "hold", S = id, Stage = stage, Force = true, At = at, By = by }); released.Add(name + "/" + stage); }
                if (verdict == "ai" && hold == null && state == "pending" && stage is "download" or "process" or "actors") { lines.Add(new QLine { T = "hold", S = id, Stage = stage, Hold = "ai", AiScore = (double?)(doc?.Root["ai"] as JsonObject)?["score"], At = at, By = by }); held.Add(name + "/" + stage); }
            }
            if (!dry && lines.Count > 0) await q.AppendLinesAsync(lines, ct);
        }
        if (!dry && doc != null)
            await VideoJson.UpdateAsync(store, paths, id, root =>
            {
                var ai = root["ai"] as JsonObject ?? new JsonObject(); if (root["ai"] is not JsonObject) root["ai"] = ai;
                ai["verdict"] = verdict; ai["verdictBy"] = by; ai["verdictAt"] = processing.NowUtc(); if (reason != null) ai["verdictReason"] = reason; else ai.Remove("verdictReason");
            }, ct);
        return new VerdictReport(id, verdict, doc != null, released, held);
    }

    public sealed record BelowReport(int Download, int Process, int KeptAtOrAbove, int KeptVerdictAi, int KeptNoScore, int Records, List<string> Ids);

    /// <summary>
    /// 0.13.52 (`indexnode ai release-below [T]`; owner, 2026-11-24: the flag went from 0.479 to 0.62 and the uploads held under the old one are released): every AI hold whose upload scores BELOW
    /// <paramref name="threshold"/> is released, in the processing queue and in the full download queue, and the upload's video.json says flag false (the score and its source stay). No verdict is recorded:
    /// this is the threshold's decision, not the owner's look at the upload, so a later probe result (the pre-check at processing) still decides. Kept: uploads the owner marked ai, uploads at or above the
    /// threshold, uploads with no score anywhere. One read of the records (the aggregate), batched queue lines, then one record write per upload with <paramref name="gap"/> between them (the store limit). Idempotent.
    /// </summary>
    public static async Task<BelowReport> ReleaseBelowAsync(JobQueue processing, JobQueue? download, IStore store, Paths paths, double threshold, string by, bool dry, TimeSpan gap, CancellationToken ct, Action<string>? say = null)
    {
        var held = new List<(JobQueue Q, bool Dl, string Id, string Stage, double? Score)>();
        foreach (var (q, dl) in new[] { (processing, false), (download, true) })
        {
            if (q == null) continue;
            await q.PollAsync(ct);
            held.AddRange(q.Read(st => st.Subjects.Values.SelectMany(s => s.Entries.Values.Where(e => !e.Final && e.Hold == "ai").Select(e => (q, dl, s.Id, e.Stage, e.HoldAiScore))).ToList()));
        }
        var ids = held.Select(h => h.Id).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var docs = ids.Count == 0 ? new Dictionary<string, VideoJson.Doc?>() : await VideoJson.ReadManyAsync(store, paths, ids, ct);
        int keptAbove = 0, keptAi = 0, noScore = 0; var release = new List<(JobQueue Q, bool Dl, string Id, string Stage)>();
        foreach (var h in held.OrderBy(h => h.Id, StringComparer.Ordinal).ThenBy(h => h.Stage, StringComparer.Ordinal))
        {
            var root = docs.GetValueOrDefault(h.Id)?.Root;
            if (Verdict(root) == "ai") { keptAi++; continue; }
            var score = (double?)(root?["ai"] as JsonObject)?["score"] ?? h.Score;
            if (score == null) { noScore++; continue; }
            if (score >= threshold) { keptAbove++; continue; }
            release.Add((h.Q, h.Dl, h.Id, h.Stage));
        }
        // 0.13.54: the records FIRST, the holds second. 0.13.52 released the holds and then set the records (30 a minute, 8 minutes): 15 uploads had their actors job run in between,
        // read "AI-flagged", wrote no units and finished for good; every later actors job then rebuilt their units and went over its store budget (2026-11-25).
        // the records: flag false, so nothing downstream (actors, grouping, the catalog) goes on treating a released upload as AI
        var flip = release.Select(r => r.Id).Distinct(StringComparer.Ordinal).Where(id => (docs.GetValueOrDefault(id)?.Root["ai"] as JsonObject)?["flag"] is JsonNode f && (bool?)f == true).ToList(); int n = 0;
        if (!dry)
            foreach (var id in flip)
            {
                await VideoJson.UpdateAsync(store, paths, id, root => { if (root["ai"] is JsonObject ai && Verdict(root) != "ai") ai["flag"] = false; }, ct);
                if (++n % 50 == 0) say?.Invoke($"  {n} of {flip.Count} record(s) updated");
                if (gap > TimeSpan.Zero && n < flip.Count) await Task.Delay(gap, ct);
            }
        if (!dry && flip.Count > 0) say?.Invoke($"  {flip.Count} record(s) set to flag false; now the holds");
        if (!dry)
            foreach (var g in release.GroupBy(r => r.Q))
            {
                var lines = g.Select(r => new QLine { T = "hold", S = r.Id, Stage = r.Stage, Force = true, At = g.Key.NowUtc(), By = by }).ToList();
                for (int i = 0; i < lines.Count; i += 500) { await g.Key.AppendLinesAsync(lines.Skip(i).Take(500).ToList(), ct); say?.Invoke($"  released {Math.Min(i + 500, lines.Count)} of {lines.Count} hold(s) in the {(g.First().Dl ? "download" : "processing")} queue"); }
            }
        return new BelowReport(release.Count(r => r.Dl), release.Count(r => !r.Dl), keptAbove, keptAi, noScore, flip.Count, release.Select(r => r.Id).Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>Marks the add line of the full download held when the record says AI and aiDramas is off; logs why.</summary>
    public static bool Apply(JobContext ctx, JsonObject rec, QLine add, JsonObject? root = null)
    {
        if (!Flagged(rec, root) || ctx.AiDramasOn()) return false;
        var score = (double?)(rec["ai"] as JsonObject)?["score"]; add.Hold = "ai"; add.AiScore = score;
        Log.Info($"sample {ctx.Job.JobId}: check {(string?)rec["decision"]}, AI (score {(score is { } s ? s.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "?")}): held, aiDramas is off");
        return true;
    }

    public sealed record Report(int Download, int Process, int Skipped, int Unreadable, int Batches);

    /// <summary>The migration: pending, unheld download and process entries whose video.json says ai.flag true from the pre-check or a sample check (source "precheck") get the hold. Reads only for those subjects; batched appends; idempotent.</summary>
    public static async Task<Report> HoldPendingAsync(JobQueue q, IStore store, Paths paths, bool dry, CancellationToken ct, Action<string>? say = null)
    {
        var cand = q.Read(st => st.Subjects.Values.SelectMany(s => s.Entries.Values.Where(e => e.State == "pending" && !e.Final && e.Hold == null && e.Stage is "download" or "process").Select(e => (s.Id, e.Stage))).OrderBy(x => x.Id, StringComparer.Ordinal).ToList());
        var found = new System.Collections.Concurrent.ConcurrentBag<(string Id, string Stage, double? Score)>(); int skipped = 0, unreadable = 0;
        var allDocs = await VideoJson.ReadManyAsync(store, paths, cand.Select(c => c.Id).Distinct(StringComparer.Ordinal).ToList(), ct);   // the aggregate, not one video.json per candidate
        await Parallel.ForEachAsync(cand.Select(c => c.Id).Distinct(StringComparer.Ordinal), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (id, t) =>
        {
            try
            {
                var doc = allDocs.GetValueOrDefault(id); if (doc == null) { Interlocked.Increment(ref unreadable); return; }
                var ai = doc.Root["ai"] as JsonObject;
                if (ai != null && (bool?)ai["flag"] == true && (string?)ai["source"] == "precheck") { foreach (var c in cand.Where(c => c.Id == id)) found.Add((id, c.Stage, (double?)ai["score"])); }
                else Interlocked.Increment(ref skipped);
            }
            catch (Exception e) when (e is VideoJsonMissingException or InvalidDataException or System.Text.Json.JsonException) { Interlocked.Increment(ref unreadable); }
        });
        var lines = found.OrderBy(f => f.Id, StringComparer.Ordinal).ThenBy(f => f.Stage, StringComparer.Ordinal).Select(f => new QLine { T = "hold", S = f.Id, Stage = f.Stage, Hold = "ai", AiScore = f.Score, At = Protocol.NowUtc() }).ToList();
        int batches = 0;
        if (!dry) for (int i = 0; i < lines.Count; i += 500) { await q.AppendLinesAsync(lines.Skip(i).Take(500).ToList(), ct); batches++; say?.Invoke($"  held {Math.Min(i + 500, lines.Count)} of {lines.Count}"); }
        return new Report(lines.Count(l => l.Stage == "download"), lines.Count(l => l.Stage == "process"), skipped, unreadable, batches);
    }
}
