using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>
/// A followed source (playlist or channel). Its job runs a <b>lookup</b> (full listing: on add, or by hand) or a <b>refresh</b> (incremental: scheduled, or by hand); after a run
/// the engine rewrites the job as pending, due at the next scheduled time, or as done when there is none (schedule once/off). Listings are yt-dlp's flat listing, newest first, without cookies.
/// </summary>
public sealed partial class RefreshStage(Tools tools, IStore store, Func<DateTimeOffset>? clock = null) : IStageHandler
{
    public const int ListSize = 30;
    public const int SeenCap = 500;
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public sealed record Entry(string Id, string? ChannelId, string? ChannelName, double? Duration = null, string? Title = null, string? Availability = null);

    /// <summary>The yt-dlp --print template of a listing line (see <see cref="ParseLine"/>).</summary>
    public const string ListPrint = "%(id)s\t%(channel_id)s\t%(channel)s\t%(duration)s\t%(availability)s\t%(title)s";
    /// <summary>A channel's first poll looks this far back: its back catalogue goes to classification too.</summary>
    public const int ChannelFirstList = 100;

    [GeneratedRegex(@"^(\d+)\s*([smhd])$", RegexOptions.IgnoreCase)]
    private static partial Regex EveryRegex();

    public static TimeSpan ParseEvery(string? every)
    {
        if (string.IsNullOrWhiteSpace(every)) return TimeSpan.FromHours(1);
        var m = EveryRegex().Match(every.Trim());
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n) || n <= 0) throw new ArgumentException($"bad interval '{every}' (use e.g. 30m, 1h, 1d)");
        return char.ToLowerInvariant(m.Groups[2].Value[0]) switch { 's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n) };
    }

    public static string[] ListingUrls(JobDescriptor j)
    {
        if (j.Kind == "playlist") return [$"https://www.youtube.com/playlist?list={j.Id}"];
        var tabs = j.Tabs is { Length: > 0 } t ? t : ["videos", "shorts"];
        return tabs.Select(tab => $"https://www.youtube.com/channel/{j.Id}/{tab}").ToArray();
    }

    /// <summary>One yt-dlp line "id TAB channel_id TAB channel TAB duration TAB availability TAB title"; "NA" and empty fields mean missing.</summary>
    public static Entry? ParseLine(string line)
    {
        var p = line.TrimEnd('\r').Split('\t');
        var id = p[0].Trim();
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$")) return null;
        string? Field(int i) => p.Length > i && p[i].Trim() is { Length: > 0 } v && v != "NA" ? v : null;
        double? dur = Field(3) is { } d && double.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv) ? dv : null;
        return new Entry(id, Field(1), Field(2), dur, p.Length > 5 && string.Join(' ', p[5..]).Trim() is { Length: > 0 } title && title != "NA" ? title : null, Field(4));
    }

    public async Task<List<Entry>> ListAsync(JobDescriptor j, int end, CancellationToken ct)
    {
        var all = new List<Entry>();
        string? lastError = null; int failed = 0;
        var urls = ListingUrls(j);
        foreach (var url in urls)
        {
            var r = await tools.RunYtDlpAsync(end > 0 ? ["--flat-playlist", "--no-warnings", "--playlist-end", end.ToString(), "--print", ListPrint, url] : ["--flat-playlist", "--no-warnings", "--print", ListPrint, url], ct);
            if (r.ExitCode != 0) { failed++; lastError = r.Stderr; continue; }
            foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (ParseLine(line) is { } e && all.All(x => x.Id != e.Id)) all.Add(e);
        }
        if (failed == urls.Length) YtDlp.Throw(lastError ?? "");
        return all;
    }

    public static string RequestsDir(Paths p) => $"{p.Prefix}requests/sources/";

    /// <summary>
    /// Add requests from the admin page (requests/sources/&lt;id&gt;.json, written without yt-dlp or the storage key in the browser): resolved here on the
    /// yt-dlp node into watch jobs. A playlist is curated (its videos so far are downloaded, up to 200, and its channels followed); a channel is ingested
    /// (its new uploads, after its first look-back, go to classification). The request is marked done (with the job id) or failed (with the error).
    /// </summary>
    public async Task ResolveRequestsAsync(Paths paths, CancellationToken ct)
    {
        foreach (var b in await store.ListAsync(RequestsDir(paths), false, false, ct))
        {
            if (await store.GetAsync(b.Name, ct) is not { } body || JsonNode.Parse(body.Data) is not JsonObject req || req["state"]?.ToString() != "pending") continue;
            var url = req["url"]?.ToString() ?? "";
            try
            {
                bool playlist = Sources.Classify(url)?.Kind == "playlist";
                var job = await Sources.ResolveAsync(tools, url, req["schedule"]?.ToString() ?? "once", ["videos"], req["followChannels"]?.GetValue<bool>() ?? playlist, playlist ? -1 : 0, 480, ct);
                bool made = await Sources.AddAsync(store, paths, job, ct);
                req["state"] = "done"; req["jobId"] = job.JobId; req["name"] = job.Name; req["note"] = made ? "watch job created" : "already watched";
                Log.Info($"watch: source request {url} -> {job.JobId} ({job.Name}){(made ? "" : " (already followed)")}");
            }
            catch (Exception e) when (e is not OperationCanceledException) { req["state"] = "failed"; req["error"] = Redact.Sas(e.Message.Length > 300 ? e.Message[..300] : e.Message); Log.Info($"watch: source request {url} failed: {req["error"]}"); }
            req["resolvedAt"] = Protocol.Utc(now());
            await store.PutAsync(b.Name, JsonSerializer.SerializeToUtf8Bytes(req), null, null, false, ct);
        }
    }

    private async Task<bool> KnownAsync(Paths paths, string id, CancellationToken ct) =>
        await store.GetTagsAsync(paths.Job("download", id), ct) != null || (await store.ListAsync(paths.VideoDir(id), false, false, ct)).Count > 0;

    /// <summary>Migrates pre-0.10.3 watch jobs and answers the admin page's add requests; run by the refresh node about once a minute, whether or not any source is due.</summary>
    public async Task HousekeepingAsync(Paths paths, CancellationToken ct)
    {
        await Sources.MigrateLegacyAsync(store, paths, ct);
        await ResolveRequestsAsync(paths, ct);
    }

    /// <summary>What the next claim does: the job's own record ("lookup" | "refresh"), else a job written before 0.10.3 that has never run is a lookup and any other is a refresh.</summary>
    public static string KindOf(JobDescriptor j) => j.Run is "lookup" or "refresh" ? j.Run : j.Seen == null ? "lookup" : "refresh";

    public async Task RunAsync(JobContext ctx)
    {
        var job = ctx.Job; var ct = ctx.Token; var paths = ctx.Paths;
        await HousekeepingAsync(paths, ct);
        if (string.IsNullOrEmpty(job.Id) || job.Kind is not ("playlist" or "channel")) throw new PermanentFailure("refresh job needs kind (playlist|channel) and id");
        var schedule = Schedule.Of(job);
        var run = KindOf(job); bool lookup = run == "lookup";
        int backfill = Math.Max(0, job.Backfill ?? 0);
        ctx.Report(run, job.Name);

        // lookup: the whole source (a playlist in full, a channel's newest ChannelFirstList or backfill); refresh: a channel's newest uploads until known ones are reached, a playlist in full
        int end = lookup ? (job.Kind == "playlist" ? 0 : Math.Max(ChannelFirstList, backfill)) : job.Kind == "playlist" ? 0 : ListSize;
        var seen = new HashSet<string>(job.Seen ?? []);
        var listed = await ListAsync(job, end, ct);
        while (!lookup && job.Kind == "channel" && end < 500 && listed.Count >= end && listed.All(e => !seen.Contains(e.Id))) { end *= 4; listed = await ListAsync(job, end, ct); } // every one new: go further back until a known one turns up
        var fresh = lookup ? listed : listed.Where(e => !seen.Contains(e.Id)).ToList();
        int made = 0, skipped = 0;
        var addedAt = Protocol.Utc(now());

        // every unknown video becomes an unclassified download job (0.9.1); the singleton classify job decides it. A lookup of a playlist honours its backfill limit (none = every video)
        int limit = lookup && job.Kind == "playlist" && job.Backfill != null ? backfill : int.MaxValue;
        foreach (var e in fresh)
        {
            if (await KnownAsync(paths, e.Id, ct)) { skipped++; continue; }
            if (made >= limit) break;
            var d = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={e.Id}", job.MaxHeight ?? 480, null);
            d.Source = job.JobId; d.Sources = [job.JobId]; d.AddedAt = addedAt; d.Priority = job.Priority;
            d.Title = e.Title; d.Duration = e.Duration; d.Availability = e.Availability;
            d.ChannelId = e.ChannelId ?? (job.Kind == "channel" ? job.Id : null); d.ChannelName = e.ChannelName ?? (job.Kind == "channel" ? job.Name : null);
            if (await ClassifyJobs.CreateUnclassifiedAsync(store, paths, d, ct)) made++; else skipped++;
        }

        if (job.FollowChannels == true)
        {
            var done = new HashSet<string>();
            foreach (var e in fresh)
            {
                var cid = e.ChannelId;
                string? cname = e.ChannelName;
                if (cid == null)
                {
                    var r = await tools.RunYtDlpAsync(["--no-warnings", "--skip-download", "--print", "%(channel_id)s\t%(channel)s", $"https://www.youtube.com/watch?v={e.Id}"], ct);
                    var line = r.ExitCode == 0 ? r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split('\t') : null;
                    if (line != null && line[0].Trim() is { Length: > 0 } c && c != "NA") { cid = c; cname ??= line.Length > 1 && line[1].Trim() != "NA" ? line[1].Trim() : null; }
                }
                if (cid == null || !done.Add(cid)) continue;
                var ch = JobDescriptor.NewSource("channel", cid, cname ?? cid, "once", ["videos"], false, 0, job.MaxHeight ?? 480); // Shorts are never dramas
                ch.Source = job.JobId; ch.AddedAt = addedAt; ch.Priority = job.Priority;
                if (await Sources.AddAsync(store, paths, ch, ct)) Log.Info($"{run} {job.JobId}: now following channel {ch.Name} ({cid})");
            }
        }

        await WriteMembersAsync(paths, job, listed, lookup, addedAt, ct);

        job.Seen = fresh.Select(e => e.Id).Concat(job.Seen ?? []).Distinct().Take(SeenCap).ToList();
        job.Found = (job.Found ?? 0) + made;
        job.LastPolled = addedAt; job.LastRun = run;
        if (lookup) job.LastLookup = addedAt; else job.LastRefresh = addedAt;
        job.Run = "refresh";
        job.Attempts = 0; job.LastError = null; job.Permanent = null;
        var next = schedule.Next(now());
        job.NextRefresh = next == null ? null : Protocol.Utc(next.Value);
        ctx.NextDue = job.NextRefresh; // once / off: the job is done until triggered by hand
        Log.Info($"{run} {job.JobId}: {listed.Count} listed, {fresh.Count} {(lookup ? "looked at" : "new")}, {made} download job(s) created, {skipped} already known; schedule {schedule.Describe()}{(next != null ? ", next " + job.NextRefresh : "")}");
    }

    public sealed record Member(string Id, string? Title, double? Duration, string? ChannelId, string? ChannelName, string? Availability);

    /// <summary>index/sources/&lt;id&gt;/members.json (for the catalog): the member list of the last look. A playlist is rewritten in full; a channel's listing is only its newest uploads, so it is merged into what is there (newest first).</summary>
    private async Task WriteMembersAsync(Paths paths, JobDescriptor job, List<Entry> listed, bool lookup, string at, CancellationToken ct)
    {
        var members = listed.Select(e => new Member(e.Id, e.Title, e.Duration, e.ChannelId, e.ChannelName, e.Availability)).ToList();
        string? looked = at;
        if (await store.GetAsync(paths.SourceMembers(job.JobId), ct) is { } old)
            try
            {
                var o = JsonNode.Parse(old.Data) as JsonObject;
                if (job.Kind == "channel") members = members.Concat((o?["members"]?.Deserialize<List<Member>>(Protocol.Json) ?? []).Where(h => members.All(m => m.Id != h.Id))).ToList();
                if (!lookup) looked = o?["lookedUpAt"]?.ToString() ?? looked;
            }
            catch (JsonException) { }
        job.Members = members.Count;
        var doc = new { v = 1, sourceId = job.JobId, kind = job.Kind, id = job.Id, name = job.Name, updatedAt = at, lookedUpAt = lookup ? at : looked, members };
        await store.PutAsync(paths.SourceMembers(job.JobId), JsonSerializer.SerializeToUtf8Bytes(doc, Protocol.Json), null, null, false, ct, "application/json");
    }
}
