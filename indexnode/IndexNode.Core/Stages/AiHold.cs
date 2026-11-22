using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.23: the AI hold in the sample flow. A sample check (inline or the check stage) whose record says ai.flag adds the full download held (Hold "ai", priority unchanged) while control aiDramas is off;
/// duplicate and subset stay metadata-only. `jobs hold-ai` holds the pending download/process entries whose pre-check or sample check flagged AI.
/// </summary>
public static class AiHold
{
    public static bool Flagged(JsonObject? rec) => (rec?["ai"] as JsonObject)?["flag"] is JsonNode f && (bool?)f == true;

    /// <summary>Marks the add line of the full download held when the record says AI and aiDramas is off; logs why.</summary>
    public static bool Apply(JobContext ctx, JsonObject rec, QLine add)
    {
        if (!Flagged(rec) || ctx.AiDramasOn()) return false;
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
        await Parallel.ForEachAsync(cand.Select(c => c.Id).Distinct(StringComparer.Ordinal), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (id, t) =>
        {
            try
            {
                var doc = await VideoJson.ReadAsync(store, paths, id, t); if (doc == null) { Interlocked.Increment(ref unreadable); return; }
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
