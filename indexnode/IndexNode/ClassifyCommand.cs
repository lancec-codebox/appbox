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
                Console.WriteLine(await SetAsync(store, paths, id, verdict, a.Get("reason"), DateTimeOffset.UtcNow, default));
                return 0;
            }
            case "list":
            {
                if (!a.Has("rejected")) throw new ArgumentException("classify list --rejected [--since 1d]");
                var since = RefreshStage.ParseEvery(a.Get("since") ?? "1d");
                var rows = await RejectedAsync(store, paths, DateTimeOffset.UtcNow - since, default);
                foreach (var r in rows) Console.WriteLine(r);
                Console.WriteLine($"{rows.Count} rejected since {a.Get("since") ?? "1d"} ago (overrule one with: indexnode classify set <videoId> drama)");
                return 0;
            }
            case "requeue":
            {
                var source = a.Get("source") ?? throw new ArgumentException("classify requeue --source <id>");
                int n = await RequeueSourceAsync(store, paths, source, default);
                Console.WriteLine($"{n} pending download(s) of {source} set back to unclassified (stored decisions still apply)");
                return 0;
            }
            default: throw new ArgumentException("classify set <videoId> drama|not [--reason R] | list --rejected [--since 1d] | requeue --source <id>");
        }
    }

    /// <summary>The singleton's counters for the UTC day of <paramref name="now"/> (zero when it has not run today).</summary>
    public static async Task<(string Day, int Classified, int Rejected)> TodayAsync(IStore store, Paths paths, DateTimeOffset now, CancellationToken ct)
    {
        var day = Protocol.Utc(now)[..10];
        if (await store.GetAsync(ClassifyJobs.SingletonBlob(paths), ct) is not { } c) return (day, 0, 0);
        var j = JobDescriptor.Parse(c.Data);
        return j.CountDay == day ? (day, j.Classified ?? 0, j.Rejected ?? 0) : (day, 0, 0);
    }

    /// <summary>Writes the decision (by "owner") and sets the download job: drama -> pending, not -> failed + permanent. A job already leased or done is left alone and said so.</summary>
    public static async Task<string> SetAsync(IStore store, Paths paths, string id, string verdict, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        bool drama = verdict.ToLowerInvariant() switch { "drama" => true, "not" => false, _ => throw new ArgumentException("verdict must be drama or not") };
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$")) throw new ArgumentException($"'{id}' is not a video id");
        var at = Protocol.Utc(now);
        var name = paths.Job("download", id);
        JobDescriptor? d = await store.GetAsync(name, ct) is { } body ? JobDescriptor.Parse(body.Data) : null;
        var why = string.IsNullOrWhiteSpace(reason) ? "set by the owner" : reason.Trim();
        await ClassifyStage.SaveDecisionAsync(store, paths, id, d?.ChannelId, d?.ChannelName, d?.Title, d?.Duration, drama, drama ? "drama" : "not-drama", why, "owner", null, at, ct);
        var tags = await store.GetTagsAsync(name, ct);
        if (d == null || tags == null)
        {
            if (!drama) return $"{id}: decision stored (not a drama); no download job exists";
            var nd = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={id}", 480, null); nd.JobId = id; nd.AddedAt = at;
            await store.PutAsync(name, nd.ToBytes(), new Dictionary<string, string>(Tags.Job("download", "pending", at)) { [Tags.Classified] = at }, null, true, ct);
            return $"{id}: decision stored; download job created (pending)";
        }
        var state = tags.GetValueOrDefault(Tags.State);
        bool classifyFailed = state == "failed" && d.Reason?.StartsWith("classify:", StringComparison.Ordinal) == true;
        if (state is not (Tags.Unclassified or "pending") && !classifyFailed) return $"{id}: decision stored; the download job is {state}, left alone";
        if (drama)
        {
            d.Reason = null; d.LastError = null; d.Permanent = null; d.FinishedAt = null; d.Attempts = 0;
            var t = new Dictionary<string, string>(tags) { [Tags.State] = "pending", [Tags.Classified] = at };
            await store.PutAsync(name, d.ToBytes(), t, null, false, ct);
            return $"{id}: drama; download job pending";
        }
        await ClassifyJobs.ApplyAsync(store, paths, name, tags, d, false, "not-drama", why, at, ct);
        return $"{id}: not a drama; download job failed (permanent)";
    }

    /// <summary>The rejections since <paramref name="since"/>, newest first, one line each with the reason.</summary>
    public static async Task<List<string>> RejectedAsync(IStore store, Paths paths, DateTimeOffset since, CancellationToken ct)
    {
        var from = Protocol.Utc(since);
        IEnumerable<BlobEntry> found;
        try { found = await Tags.FindFullAsync(store, Tags.StateWhere("download", "failed") + $" AND \"{Tags.Classified}\" >= '{from}'", ct); }
        catch (ForbiddenException) { found = (await store.ListAsync(paths.JobsDir("download"), true, false, ct)).Where(b => b.Tags.GetValueOrDefault(Tags.State) == "failed"); }
        var rows = new List<(string At, string Line)>();
        foreach (var b in found.Where(b => b.Name.StartsWith(paths.JobsDir("download"), StringComparison.Ordinal)))
        {
            if (string.CompareOrdinal(b.Tags.GetValueOrDefault(Tags.Classified) ?? "", from) < 0) continue;
            if (await store.GetAsync(b.Name, ct) is not { } c) continue;
            var d = JobDescriptor.Parse(c.Data);
            if (d.Reason?.StartsWith("classify:", StringComparison.Ordinal) != true) continue;
            rows.Add((b.Tags[Tags.Classified], $"{Path.GetFileNameWithoutExtension(b.Name)}  {b.Tags[Tags.Classified]}  {d.ChannelName ?? "?"}  \"{d.Title ?? "?"}\"  {d.Reason}"));
        }
        return rows.OrderByDescending(r => r.At, StringComparer.Ordinal).Select(r => r.Line).ToList();
    }

    /// <summary>Sets the pending (not leased) downloads of a source back to unclassified and makes sure the singleton exists. Returns how many.</summary>
    public static async Task<int> RequeueSourceAsync(IStore store, Paths paths, string source, CancellationToken ct)
    {
        IEnumerable<BlobEntry> found;
        try { found = await Tags.FindFullAsync(store, Tags.StateWhere("download", "pending"), ct); }
        catch (ForbiddenException) { found = (await store.ListAsync(paths.JobsDir("download"), true, false, ct)).Where(b => b.Tags.GetValueOrDefault(Tags.State) == "pending"); }
        int n = 0;
        foreach (var b in found.Where(b => b.Name.StartsWith(paths.JobsDir("download"), StringComparison.Ordinal) && b.LeaseState != "leased"))
        {
            if (await store.GetAsync(b.Name, ct) is not { } c) continue;
            var d = JobDescriptor.Parse(c.Data);
            if (d.Source != source && d.Sources?.Contains(source) != true) continue;
            await store.SetTagsAsync(b.Name, new Dictionary<string, string>(b.Tags) { [Tags.State] = Tags.Unclassified }, null, ct);
            n++;
        }
        if (n > 0) await ClassifyJobs.EnsureSingletonAsync(store, paths, ct);
        return n;
    }
}
