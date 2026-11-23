namespace IndexNode;

/// <summary>
/// 0.13.26: the one way an upload enters the processing queue. Whoever owns the download side calls <see cref="EnterAsync"/> when an upload's download is done (data "full") or when the check settles it without a download
/// (duplicate, AI: data "precheck", a catalog and group member only). The line carries the facts processing needs, so processing never reads the download queue. A subject that already exists in the processing queue is left alone
/// (a repeat, or a crash between "done download" in the download log and this append: the next look enters it again, once).
/// </summary>
public static class Handoff
{
    public sealed record Facts(string Id, string Data, double? Duration = null, string? Title = null, string? Channel = null, string? Pub = null, int Priority = 0, System.Text.Json.Nodes.JsonNode? Check = null, string? State = null, string? Hold = null, double? AiScore = null, string? DuplicateOf = null, double? DuplicateShare = null, string? Url = null);

    public static QLine Line(Facts f, string at)
    {
        var job = new JobDescriptor { Stage = f.Data == "full" ? "download" : "check", JobId = f.Id, Url = f.Url, Duration = f.Duration, Title = f.Title, ChannelName = f.Channel, Pub = f.Pub, Priority = f.Priority, DuplicateOf = f.DuplicateOf, DuplicateShare = f.DuplicateShare, AddedAt = at };
        return new QLine { T = "enter", S = f.Id, Data = f.Data, Job = job, Pri = f.Priority, PubTime = f.Pub, At = at, State = f.Data == "full" ? "done" : f.State ?? "duplicate", Pre = f.Check, Hold = f.Hold, AiScore = f.AiScore };
    }

    /// <summary>Appends "enter" lines for the uploads the processing queue does not have yet (create-only: the state decides inside the conditional append). Returns how many were entered.</summary>
    public static async Task<int> EnterAsync(JobQueue processing, IReadOnlyList<Facts> items, CancellationToken ct)
    {
        int n = 0;
        foreach (var chunk in JobQueue.Chunks(items, _ => 700))
            await processing.AppendLinesAsync(st =>
            {
                var at = processing.NowUtc(); var lines = chunk.Where(f => st.Get(f.Id) == null).Select(f => Line(f, at)).ToList(); n += lines.Count; return lines;
            }, ct);
        return n;
    }
    /// <summary>The crash repair: a download the download log shows done but the processing queue has no subject for (the node died between "done download" and "enter") is entered now, once. Reads only the two in-memory states.</summary>
    public static Task<int> RepairAsync(JobQueue download, JobQueue processing, CancellationToken ct)
    {
        var missing = download.Read(d => d.Subjects.Values.Where(x => x.Entries.GetValueOrDefault("download") is { State: "done" }).Select(x => (x.Id, E: x.Entries["download"], x.Pub)).ToList())
            .Where(x => processing.Read(p => p.Get(x.Id) == null))
            .Select(x => new Facts(x.Id, "full", x.E.Job?.Duration, x.E.Job?.Title, x.E.Job?.ChannelName, x.Pub ?? x.E.Job?.Pub, x.E.Priority, Url: x.E.Job?.Url)).ToList();
        return EnterAsync(processing, missing, ct);
    }
    public static Task<int> EnterAsync(JobQueue processing, Facts item, CancellationToken ct) => EnterAsync(processing, [item], ct);
}
