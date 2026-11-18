using System.Text.Json;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>indexnode sources add | list | remove | import | schedule | lookup | refresh: followed playlists and channels, stored as refresh jobs (index/jobs/refresh/&lt;sourceId&gt;.json; before 0.10.3 jobs/watch/).</summary>
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

    public static async Task<JobDescriptor> ResolveAsync(Tools tools, string url, string every, string[] tabs, bool follow, int backfill, int maxHeight, CancellationToken ct)
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
    /// Adds a source: its lookup job is due NOW at <see cref="Protocol.RefreshNowPriority"/>, so the next free downloader lists it (whatever the schedule: "once" means no refresh after this lookup).
    /// False when the source exists already (as a refresh job or an old watch job).
    /// </summary>
    public static async Task<bool> AddAsync(IStore store, Paths paths, JobDescriptor job, CancellationToken ct)
    {
        await MigrateLegacyAsync(store, paths, ct);
        if (await store.GetTagsAsync(paths.Job("refresh", job.JobId), ct) != null) return false;
        job.Run = "lookup";
        return await store.PutAsync(paths.Job("refresh", job.JobId), job.ToBytes(), Tags.Job("refresh", "pending", job.AddedAt, job.AddedAt, Protocol.RefreshNowPriority), null, true, ct);
    }

    /// <summary>
    /// Moves jobs/watch/&lt;id&gt;.json (pre-0.10.3) to jobs/refresh/&lt;id&gt;.json, state, due time, priority and body kept: the job keeps its schedule (its "every"), so an old source goes on as before.
    /// A job another node holds is left for the next call. Returns how many moved.
    /// </summary>
    public static async Task<int> MigrateLegacyAsync(IStore store, Paths paths, CancellationToken ct)
    {
        int moved = 0;
        foreach (var b in await store.ListAsync(paths.LegacyWatchDir, true, false, ct))
        {
            var id = Path.GetFileNameWithoutExtension(b.Name); var target = paths.Job("refresh", id);
            var lease = await store.AcquireLeaseAsync(b.Name, Protocol.LeaseSeconds, ct);
            if (lease == null) continue;
            try
            {
                if (await store.GetAsync(b.Name, ct) is not { } c) continue;
                var job = JobDescriptor.Parse(c.Data); job.Stage = "refresh"; job.Run ??= RefreshStage.KindOf(job);
                var tags = new Dictionary<string, string>(b.Tags) { [Tags.Stage] = "refresh" };
                if (await store.PutAsync(target, job.ToBytes(), tags, null, true, ct)) moved++;
                await store.DeleteAsync(b.Name, lease, ct);
            }
            finally { try { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); } catch { } }
        }
        if (moved > 0) Log.Info($"sources: moved {moved} watch job(s) to refresh");
        return moved;
    }

    /// <summary>Changes a source's schedule (once | off | 6h | 1d | daily@03:00): its job body and, for a pending job, the due time. A lookup that is due now stays due.</summary>
    public static async Task<JobDescriptor> SetScheduleAsync(IStore store, Paths paths, string sourceId, string schedule, CancellationToken ct, Func<DateTimeOffset>? now = null)
    {
        var sched = Schedule.Parse(schedule);
        await MigrateLegacyAsync(store, paths, ct);
        var name = paths.Job("refresh", sourceId);
        var tags = await store.GetTagsAsync(name, ct) ?? throw new ArgumentException($"no source {sourceId}");
        var lease = await store.AcquireLeaseAsync(name, Protocol.LeaseSeconds, ct) ?? throw new InvalidOperationException($"{sourceId} is being refreshed right now; try again in a minute");
        try
        {
            tags = await store.GetTagsAsync(name, ct) ?? tags;
            var job = JobDescriptor.Parse((await store.GetAsync(name, ct))!.Data);
            job.Schedule = sched.Text; job.Every = sched.Kind == "every" ? sched.Text : null;
            var t = new Dictionary<string, string>(tags);
            var clock = now ?? (() => DateTimeOffset.UtcNow);
            bool dueNow = tags.GetValueOrDefault(Tags.State) == "pending" && Tags.DueOf(tags).CompareTo(Protocol.Utc(clock())) <= 0 && Tags.PriorityOf(tags) == Protocol.RefreshNowPriority;
            if (!dueNow)
            {
                // from the last run, so a new interval does not push a source that is already late, nor start one that just ran
                var last = DateTimeOffset.TryParse(job.LastPolled, out var lp) ? lp : clock();
                var next = sched.Kind == "every" ? last + sched.Interval : sched.Next(clock());
                if (next == null) { t[Tags.State] = "done"; t[Tags.Due] = Protocol.Utc(clock()); if (job.Priority != 0) t[Tags.Priority] = job.Priority.ToString(); else t.Remove(Tags.Priority); job.NextRefresh = null; }
                else { t[Tags.State] = "pending"; t[Tags.Due] = Protocol.Utc(next.Value < clock() ? clock() : next.Value); job.NextRefresh = t[Tags.Due]; if (job.Priority != 0) t[Tags.Priority] = job.Priority.ToString(); else t.Remove(Tags.Priority); }
            }
            await store.PutAsync(name, job.ToBytes(), t, lease, false, ct);
            return job;
        }
        finally { try { await store.ReleaseLeaseAsync(name, lease, CancellationToken.None); } catch { } }
    }

    /// <summary>Makes the source's job due now at <see cref="Protocol.RefreshNowPriority"/>: kind "lookup" (a full listing again) or "refresh" (incremental). A job a node is running right now is left alone (false).</summary>
    public static async Task<bool> TriggerAsync(IStore store, Paths paths, string sourceId, string kind, CancellationToken ct, Func<DateTimeOffset>? now = null)
    {
        if (kind is not ("lookup" or "refresh")) throw new ArgumentException("kind is lookup or refresh");
        await MigrateLegacyAsync(store, paths, ct);
        var name = paths.Job("refresh", sourceId);
        if (await store.GetTagsAsync(name, ct) == null) throw new ArgumentException($"no source {sourceId}");
        var lease = await store.AcquireLeaseAsync(name, Protocol.LeaseSeconds, ct);
        if (lease == null) return false;
        try
        {
            var job = JobDescriptor.Parse((await store.GetAsync(name, ct))!.Data);
            var at = Protocol.Utc((now ?? (() => DateTimeOffset.UtcNow))());
            job.Run = kind; job.Attempts = 0; job.LastError = null; job.Permanent = null;
            await store.PutAsync(name, job.ToBytes(), Tags.Job("refresh", "pending", at, at, Protocol.RefreshNowPriority), lease, false, ct);
            return true;
        }
        finally { try { await store.ReleaseLeaseAsync(name, lease, CancellationToken.None); } catch { } }
    }

    /// <summary>The sources a schedule has made due (pending, due, not running): what "sources refresh --all-due" triggers. Sources whose schedule is once or off are done and never are.</summary>
    public static async Task<List<string>> DueAsync(IStore store, Paths paths, CancellationToken ct, Func<DateTimeOffset>? now = null)
    {
        await MigrateLegacyAsync(store, paths, ct);
        var at = Protocol.Utc((now ?? (() => DateTimeOffset.UtcNow))());
        return (await store.ListAsync(paths.JobsDir("refresh"), true, false, ct))
            .Where(b => b.Tags.GetValueOrDefault(Tags.State) == "pending" && Tags.DueOf(b.Tags).CompareTo(at) <= 0 && b.LeaseState != "leased" && Tags.PriorityOf(b.Tags) != Protocol.RefreshNowPriority) // not a lookup or manual run that is already queued
            .Select(b => Path.GetFileNameWithoutExtension(b.Name)).ToList();
    }

    public sealed record ImportResult(int Added, int Existed, int Skipped);

    /// <summary>One watch job per channel of a channels.json ([{channelId, channelName, ...}]); existing ones are left alone.</summary>
    public static async Task<ImportResult> ImportAsync(IStore store, Paths paths, byte[] json, string every, string[] tabs, int maxHeight, CancellationToken ct, int priority = 0)
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
            if (await AddAsync(store, paths, job, ct)) added++; else existed++;
        }
        return new ImportResult(added, existed, skipped);
    }

    public static async Task<List<JobDescriptor>> ListAsync(IStore store, Paths paths, CancellationToken ct)
    {
        var list = new List<JobDescriptor>();
        await MigrateLegacyAsync(store, paths, ct);
        foreach (var b in await store.ListAsync(paths.JobsDir("refresh"), false, false, ct))
            if (await store.GetAsync(b.Name, ct) is { } c) list.Add(JobDescriptor.Parse(c.Data));
        return list.OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One line of "sources list": the schedule, what the job does next, the last and next run.</summary>
    public static string FormatRow(JobDescriptor j, string? state = null)
    {
        var next = state == "pending" ? (RefreshStage.KindOf(j) + (j.NextRefresh != null ? " " + j.NextRefresh : " now")) : state == "done" ? "-" : j.NextRefresh ?? "-";
        return $"{j.JobId,-40} {j.Kind,-8} schedule {Schedule.Of(j).Describe(),-14} priority {j.Priority,4}  last {(j.LastRun ?? "-")} {(j.LastPolled ?? "never"),-20} next {next,-30} new {j.Found ?? 0,4}  {j.Name}";
    }
}
