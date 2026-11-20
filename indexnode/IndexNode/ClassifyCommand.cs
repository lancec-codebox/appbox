using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>
/// indexnode classify set | list | requeue: the owner's handles on the classify job (0.9.1).
/// A stored decision (classify/&lt;id&gt;.json) always wins over the rules, so set is final until set again.
/// </summary>
public static class ClassifyCommand
{
    public static async Task<int> RunAsync(Args a)
    {
        var (_, store, paths, http) = Commands.Open(a); using var _h = http;
        var queue = new JobQueue(store, paths, "cli"); await queue.LoadAsync(default);
        var sub = a.Positional.FirstOrDefault();
        switch (sub)
        {
            case "set":
            {
                const string use = "classify set <videoId> drama|not [--reason R]   (an id starting with '-': --video=<id> drama, or: classify set drama -- <id>)";
                var rest = a.Positional.Skip(1).ToList(); string? id, verdict;
                if (a.Get("video") is { } vid) { id = vid; verdict = rest.FirstOrDefault(); }
                else if (rest.Count > 0 && rest[0].ToLowerInvariant() is "drama" or "not" && rest.Count > 1) { verdict = rest[0]; id = rest[1]; }   // after "--" the id comes last
                else { id = rest.ElementAtOrDefault(0); verdict = rest.ElementAtOrDefault(1); }
                if (id == null || verdict == null) throw new ArgumentException(use);
                Console.WriteLine(await SetAsync(store, queue, paths, id, verdict, a.Get("reason"), DateTimeOffset.UtcNow, default));
                return 0;
            }
            case "list":
            {
                if (!a.Has("rejected")) throw new ArgumentException("classify list --rejected [--since 1d]");
                var since = RefreshStage.ParseEvery(a.Get("since") ?? "1d");
                var rows = Rejected(queue, DateTimeOffset.UtcNow - since);
                foreach (var r in rows) Console.WriteLine(r);
                Console.WriteLine($"{rows.Count} rejected since {a.Get("since") ?? "1d"} ago (overrule one with: indexnode classify set <videoId> drama)");
                return 0;
            }
            case "requeue":
            {
                var source = a.Get("source") ?? throw new ArgumentException("classify requeue --source <id>");
                int n = await RequeueSourceAsync(queue, source, default);
                Console.WriteLine($"{n} pending download(s) of {source} set back to unclassified (stored decisions still apply)");
                return 0;
            }
            default: throw new ArgumentException("classify set <videoId> drama|not [--reason R] | list --rejected [--since 1d] | requeue --source <id>");
        }
    }

    /// <summary>The singleton's counters for the UTC day of <paramref name="now"/> (zero when it has not run today).</summary>
    public static (string Day, int Classified, int Rejected) Today(JobQueue queue, DateTimeOffset now)
    {
        var day = Protocol.Utc(now)[..10];
        var j = queue.Read(st => st.Entry(ClassifyJobs.Subject, "classify")?.Job);
        return j != null && j.CountDay == day ? (day, j.Classified ?? 0, j.Rejected ?? 0) : (day, 0, 0);
    }

    /// <summary>Writes the decision (by "owner") and sets the download entry: drama -> pending, not -> failed + permanent. An entry that is running or done is left alone and said so.</summary>
    public static async Task<string> SetAsync(IStore store, JobQueue queue, Paths paths, string id, string verdict, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        bool drama = verdict.ToLowerInvariant() switch { "drama" => true, "not" => false, _ => throw new ArgumentException("verdict must be drama or not") };
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$")) throw new ArgumentException($"'{id}' is not a video id");
        var at = Protocol.Utc(now);
        await queue.PollAsync(ct);
        var e = queue.Read(st => st.Entry(id, "download"));
        var d = e?.Job;
        var why = string.IsNullOrWhiteSpace(reason) ? "set by the owner" : reason.Trim();
        await ClassifyStage.SaveDecisionAsync(store, paths, id, d?.ChannelId, d?.ChannelName, d?.Title, d?.Duration, drama, drama ? "drama" : "not-drama", why, "owner", null, at, ct);
        if (e == null || d == null)
        {
            if (!drama) return $"{id}: decision stored (not a drama); no download entry exists";
            var nd = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={id}", 480, null); nd.JobId = id; nd.AddedAt = at;
            await queue.AppendLinesAsync([QLine.Add(id, "download", nd, at)], ct);
            return $"{id}: decision stored; download entry created (pending)";
        }
        var state = e.State;
        bool classifyFailed = state == "failed" && d.Reason?.StartsWith("classify:", StringComparison.Ordinal) == true;
        if (state is not (Tags.Unclassified or "pending") && !classifyFailed) return $"{id}: decision stored; the download is {state}, left alone";
        if (drama)
        {
            d.Reason = null; d.LastError = null; d.Permanent = null; d.FinishedAt = null; d.Attempts = 0;
            await queue.AppendLinesAsync([new QLine { T = "retry", S = id, Stage = "download", Job = d, Due = at, At = at }], ct);
            return $"{id}: drama; download pending";
        }
        await queue.AppendLinesAsync([ClassifyJobs.Decision(id, d, false, "not-drama", why, at)], ct);
        return $"{id}: not a drama; download failed (permanent)";
    }

    /// <summary>The rejections since <paramref name="since"/>, newest first, one line each with the reason (from the queue's memory).</summary>
    public static List<string> Rejected(JobQueue queue, DateTimeOffset since)
    {
        var from = Protocol.Utc(since);
        return queue.Read(st => st.Subjects.Values.Where(s => s.Entries.TryGetValue("download", out var e) && e.State == "failed" && e.Job?.Reason?.StartsWith("classify:", StringComparison.Ordinal) == true && string.CompareOrdinal(e.FinishedAt ?? "", from) >= 0)
            .Select(s => (At: s.Entries["download"].FinishedAt ?? "", Line: $"{s.Id}  {s.Entries["download"].FinishedAt}  {s.Entries["download"].Job!.ChannelName ?? "?"}  \"{s.Entries["download"].Job!.Title ?? "?"}\"  {s.Entries["download"].Job!.Reason}")).ToList())
            .OrderByDescending(r => r.At, StringComparer.Ordinal).Select(r => r.Line).ToList();
    }

    /// <summary>Sets the pending (not running) downloads of a source back to unclassified and makes sure the singleton exists. Returns how many.</summary>
    public static async Task<int> RequeueSourceAsync(JobQueue queue, string source, CancellationToken ct)
    {
        await queue.PollAsync(ct);
        var ids = queue.Read(st => st.Subjects.Values.Where(s => s.ClaimBy == null && s.Entries.TryGetValue("download", out var e) && e.State == "pending" && (e.Job?.Source == source || e.Job?.Sources?.Contains(source) == true)).Select(s => s.Id).ToList());
        if (ids.Count == 0) return 0;
        await queue.AppendLinesAsync(ids.Select(id => new QLine { T = "cls", S = id, Stage = "download", State = Tags.Unclassified }).ToList(), ct);
        await ClassifyJobs.EnsureSingletonAsync(queue, ct);
        return ids.Count;
    }
}
