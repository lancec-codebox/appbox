using System.Text.Json;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>indexnode sources add | list | remove | import | schedule | lookup | refresh: followed playlists and channels, kept as refresh entries of the queue (0.13.0; subject ch-&lt;id&gt; / pl-&lt;id&gt;).</summary>
public static partial class Sources
{
    [GeneratedRegex(@"[?&]list=([A-Za-z0-9_-]+)")] private static partial Regex ListParam();
    [GeneratedRegex(@"youtube\.com/channel/(UC[A-Za-z0-9_-]{22})")] private static partial Regex ChannelPath();

    public static string[] ParseTabs(string? s) =>
        string.IsNullOrWhiteSpace(s) ? ["videos", "shorts"] : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t =>
            t is "videos" or "shorts" ? t : throw new ArgumentException($"unknown tab '{t}' (videos, shorts)")).ToArray();

    /// <summary>(kind, id) when the URL names one outright; null when yt-dlp has to resolve it (a @handle).</summary>
    public static (string Kind, string Id)? Classify(string url)
    {
        var l = ListParam().Match(url);
        if (l.Success)
        {
            if (l.Groups[1].Value is "WL" or "LL") throw new ArgumentException("Watch Later is never a source; seed channels from it with: indexnode seeds watch-later");
            return ("playlist", l.Groups[1].Value);
        }
        var c = ChannelPath().Match(url);
        return c.Success ? ("channel", c.Groups[1].Value) : null;
    }

    public static async Task<JobDescriptor> ResolveAsync(ITools tools, string url, string every, string[] tabs, bool follow, int backfill, int maxHeight, CancellationToken ct)
    {
        Schedule.Parse(every);
        var known = Classify(url);
        var r = await tools.RunYtDlpAsync(["--flat-playlist", "--no-warnings", "--playlist-end", "1", "--print", "%(playlist_title)s\t%(playlist_channel_id)s\t%(channel_id)s\t%(channel)s", known?.Kind == "channel" ? $"https://www.youtube.com/channel/{known.Value.Id}/videos" : url], ct);
        string[] p = r.ExitCode == 0 ? (r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").Split('\t') : [];
        string? F(int i) => p.Length > i && p[i].Trim() is { Length: > 0 } v && v != "NA" ? v.Trim() : null;
        if (known is { } k)
        {
            var name = k.Kind == "playlist" ? F(0) : F(3) ?? F(0)?.Replace(" - Videos", "");
            return JobDescriptor.NewSource(k.Kind, k.Id, name ?? k.Id, every, tabs, follow, backfill, maxHeight);
        }
        // a @handle or /c/ URL: the channel id comes from the listing
        var cid = F(1) ?? F(2);
        if (cid == null) { if (r.ExitCode != 0) YtDlp.Throw(r.Stderr); throw new InvalidOperationException("could not tell which channel or playlist that URL is"); }
        return JobDescriptor.NewSource("channel", cid, F(3) ?? F(0)?.Replace(" - Videos", "") ?? cid, every, tabs, follow, backfill, maxHeight);
    }

    /// <summary>
    /// Adds a source: its lookup is due NOW at <see cref="Protocol.RefreshNowPriority"/>, so the next free downloader lists it (whatever the schedule: "once" means no refresh after this lookup).
    /// False when the source exists already.
    /// </summary>
    public static async Task<bool> AddAsync(JobQueue queue, JobDescriptor job, CancellationToken ct)
    {
        job.Run = "lookup"; job.Stage = "refresh";
        bool made = false;
        await queue.AppendLinesAsync(st =>
        {
            made = false;
            if (st.Entry(job.JobId, "refresh") != null) return null;
            made = true;
            return [QLine.Add(job.JobId, "refresh", job, job.AddedAt, Protocol.RefreshNowPriority, job.AddedAt)];
        }, ct);
        return made;
    }

    private static JobDescriptor WithSeen(JobDescriptor j) => j;

    /// <summary>Changes a source's schedule (once | off | 6h | 1d | daily@03:00): its job body and, for a pending job, the due time. A lookup that is due now stays due.</summary>
    public static async Task<JobDescriptor> SetScheduleAsync(JobQueue queue, string sourceId, string schedule, CancellationToken ct, Func<DateTimeOffset>? now = null)
    {
        var sched = Schedule.Parse(schedule);
        var clock = now ?? (() => DateTimeOffset.UtcNow);
        JobDescriptor? result = null;
        await queue.PollAsync(ct);
        await queue.AppendLinesAsync(st =>
        {
            var e = st.Entry(sourceId, "refresh") ?? throw new ArgumentException($"no source {sourceId}");
            if (st.Get(sourceId)?.ClaimBy != null && st.Get(sourceId)!.Entries.Values.Any(x => x.Stage == "refresh" && x.State == "pending") && false) throw new InvalidOperationException($"{sourceId} is being refreshed right now; try again in a minute");
            var job = Clone(e.Job ?? new JobDescriptor { JobId = sourceId, Stage = "refresh" });
            job.Schedule = sched.Text; job.Every = sched.Kind == "every" ? sched.Text : null; job.Inactive = null;   // a new schedule brings an inactive source back
            bool dueNow = e.State == "pending" && string.CompareOrdinal(e.Due, Protocol.Utc(clock())) <= 0 && e.Priority == Protocol.RefreshNowPriority;
            var line = new QLine { T = "upd", S = sourceId, Stage = "refresh", Job = job };
            var lines = new List<QLine> { line };
            if (!dueNow)
            {
                var last = DateTimeOffset.TryParse(job.LastPolled, out var lp) ? lp : clock();
                var next = sched.IsDynamic ? clock() : sched.Kind == "every" ? last + sched.Interval : sched.Next(clock());   // dynamic: first check now, which computes the tier
                if (next == null) { job.NextRefresh = null; lines.Add(new QLine { T = "done", S = sourceId, Stage = "refresh", State = "done", At = Protocol.Utc(clock()) }); }
                else { var due = Protocol.Utc(next.Value < clock() ? clock() : next.Value); job.NextRefresh = due; lines.Add(new QLine { T = "retry", S = sourceId, Stage = "refresh", Due = due }); lines.Add(new QLine { T = "pri", S = sourceId, Pri = job.Priority }); }
            }
            result = job;
            return lines;
        }, ct);
        return result!;
    }

    internal static JobDescriptor Clone(JobDescriptor j) => JobDescriptor.Parse(j.ToBytes());

    /// <summary>Makes the source due now at <see cref="Protocol.RefreshNowPriority"/>: kind "lookup" (a full listing again) or "refresh" (incremental). A source a node is running right now is left alone (false).</summary>
    public static async Task<bool> TriggerAsync(JobQueue queue, string sourceId, string kind, CancellationToken ct, Func<DateTimeOffset>? now = null)
    {
        if (kind is not ("lookup" or "refresh")) throw new ArgumentException("kind is lookup or refresh");
        bool ok = false;
        await queue.PollAsync(ct);
        await queue.AppendLinesAsync(st =>
        {
            ok = false;
            var e = st.Entry(sourceId, "refresh") ?? throw new ArgumentException($"no source {sourceId}");
            if (st.Get(sourceId)?.ClaimBy != null) return null;
            var job = Clone(e.Job ?? new JobDescriptor { JobId = sourceId, Stage = "refresh" });
            var at = Protocol.Utc((now ?? (() => DateTimeOffset.UtcNow))());
            job.Run = kind; job.Attempts = 0; job.LastError = null; job.Permanent = null;   // a manual refresh also revives an inactive source; the run decides again from the upload dates
            ok = true;
            return [new QLine { T = "retry", S = sourceId, Stage = "refresh", Due = at, Job = job, At = at }, new QLine { T = "pri", S = sourceId, Pri = Protocol.RefreshNowPriority }];
        }, ct);
        return ok;
    }

    /// <summary>The sources a schedule has made due (pending, due, not running): what "sources refresh --all-due" triggers. Sources whose schedule is once or off are done and never are.</summary>
    public static Task<List<string>> DueAsync(JobQueue queue, Func<DateTimeOffset>? now = null)
    {
        var at = Protocol.Utc((now ?? (() => DateTimeOffset.UtcNow))());
        return Task.FromResult(queue.Read(st => st.Subjects.Values.Where(s => s.ClaimBy == null && s.Entries.TryGetValue("refresh", out var e) && e.State == "pending" && string.CompareOrdinal(e.Due, at) <= 0 && e.Priority != Protocol.RefreshNowPriority).Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal).ToList()));
    }

    public sealed record ImportResult(int Added, int Existed, int Skipped);

    /// <summary>One watch job per channel of a channels.json ([{channelId, channelName, ...}]); existing ones are left alone.</summary>
    public static async Task<ImportResult> ImportAsync(JobQueue queue, byte[] json, string every, string[] tabs, int maxHeight, CancellationToken ct, int priority = 0)
    {
        Schedule.Parse(every); Tags.CheckPriority(priority);
        var items = JsonDocument.Parse(json).RootElement;
        if (items.ValueKind != JsonValueKind.Array) throw new ArgumentException("channels file must be a JSON array");
        int added = 0, existed = 0, skipped = 0;
        foreach (var it in items.EnumerateArray())
        {
            var id = it.TryGetProperty("channelId", out var i) ? i.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, @"^UC[A-Za-z0-9_-]{22}$")) { skipped++; continue; }
            var name = it.TryGetProperty("channelName", out var n) ? n.GetString() : null;
            var job = JobDescriptor.NewSource("channel", id, string.IsNullOrWhiteSpace(name) ? id : name, every, tabs, false, 0, maxHeight); job.Priority = priority;
            if (await AddAsync(queue, job, ct)) added++; else existed++;
        }
        return new ImportResult(added, existed, skipped);
    }

    public static List<JobDescriptor> List(JobQueue queue) =>
        queue.Read(st => st.Subjects.Values.Where(s => s.Entries.ContainsKey("refresh")).Select(s => { var j = Clone(s.Entries["refresh"].Job ?? new JobDescriptor()); j.JobId = s.Id; j.Priority = s.Entries["refresh"].Priority; return j; }).ToList())
            .OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>One line of "sources list": the schedule, what the job does next, the last and next run.</summary>
    public static string FormatRow(JobDescriptor j, string? state = null)
    {
        var sch = Schedule.Of(j);
        var next = j.Inactive == true ? "inactive" : state == "pending" ? (RefreshStage.KindOf(j) + (j.NextRefresh != null ? " " + j.NextRefresh : " now")) : state == "done" ? "-" : j.NextRefresh ?? "-";
        string tier = sch.IsDynamic ? (j.Inactive == true ? " (inactive)" : DateTimeOffset.TryParse(j.LastModified, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var nu) ? " (tier " + DynamicSchedule.TierText(DynamicSchedule.Tier(DateTimeOffset.UtcNow, nu)) + ")" : j.LastPolled != null ? " (tier 32d)" : "") : "";
        return $"{j.JobId,-40} {j.Kind,-8} schedule {(sch.IsDynamic ? "dynamic" : "explicit " + sch.Describe()) + tier,-30} priority {j.Priority,4}  last {(j.LastRun ?? "-")} {(j.LastPolled ?? "never"),-20} next {next,-30} new {j.Found ?? 0,4}  {j.Name}";
    }
}
