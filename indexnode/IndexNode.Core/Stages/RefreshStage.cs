using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>
/// A followed source (playlist or channel). Its job runs a <b>lookup</b> (full listing: on add, or by hand) or a <b>refresh</b> (incremental: scheduled, or by hand); after a run
/// the engine rewrites the job as pending, due at the next scheduled time, or as done when there is none (schedule once/off). Listings are yt-dlp's flat listing, newest first, without cookies.
/// </summary>
public sealed partial class RefreshStage(ITools tools, IStore store, Func<DateTimeOffset>? clock = null, Func<int, int, int>? random = null) : IStageHandler
{
    public const int ListSize = 30;
    public const int SeenCap = 500;
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public sealed record Entry(string Id, string? ChannelId, string? ChannelName, double? Duration = null, string? Title = null, string? Availability = null, string? UploadDate = null, long? Timestamp = null);

    /// <summary>The yt-dlp --print template of a listing line (see <see cref="ParseLine"/>).</summary>
    public const string ListPrint = "%(id)s\t%(channel_id)s\t%(channel)s\t%(duration)s\t%(availability)s\t%(upload_date)s\t%(timestamp)s\t%(title)s";
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
        var tabs = j.Tabs is { Length: > 0 } t ? t : ["videos"];   // 0.13.2: videos only (a channel's Shorts are never dramas; classify's length rule dropped them all anyway)
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
        return new Entry(id, Field(1), Field(2), dur, p.Length > 7 && string.Join(' ', p[7..]).Trim() is { Length: > 0 } title && title != "NA" ? title : null, Field(4), Field(5), Field(6) is { } ts && long.TryParse(ts, out var tv) ? tv : null);
    }

    public async Task<List<Entry>> ListAsync(JobDescriptor j, int end, CancellationToken ct)
    {
        var all = new List<Entry>();
        string? lastError = null; int failed = 0;
        var urls = ListingUrls(j);
        foreach (var url in urls)
        {
            var r = await tools.RunYtDlpAsync(end > 0 ? ["--flat-playlist", "--no-warnings", "--playlist-end", end.ToString(), "--print", ListPrint, url] : ["--flat-playlist", "--no-warnings", "--print", ListPrint, url], ct);
            if (r.ExitCode != 0 && (r.Stderr ?? "").Contains("does not have a", StringComparison.OrdinalIgnoreCase) && r.Stderr!.Contains(" tab", StringComparison.OrdinalIgnoreCase)) continue;   // 0.13.2: a missing tab (a channel with no Shorts tab) is an empty listing, not a failure
            if (r.ExitCode != 0) { failed++; lastError = r.Stderr; continue; }
            foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (ParseLine(line) is { } e && all.All(x => x.Id != e.Id)) all.Add(e);
        }
        if (failed > 0 && failed == urls.Length) YtDlp.Throw(lastError ?? "");
        return all;
    }

    public static string RequestsDir(Paths p) => $"{p.Prefix}requests/sources/";

    /// <summary>
    /// Add requests from the admin page (requests/sources/&lt;id&gt;.json, written without yt-dlp or the storage key in the browser): resolved here on the
    /// yt-dlp node into watch jobs. A playlist is curated (its videos so far are downloaded, up to 200, and its channels followed); a channel is ingested
    /// (its new uploads, after its first look-back, go to classification). The request is marked done (with the job id) or failed (with the error).
    /// </summary>
    public async Task ResolveRequestsAsync(Paths paths, JobQueue queue, CancellationToken ct)
    {
        foreach (var b in await store.ListAsync(RequestsDir(paths), false, false, ct))
        {
            if (await store.GetAsync(b.Name, ct) is not { } body || JsonNode.Parse(body.Data) is not JsonObject req || req["state"]?.ToString() != "pending") continue;
            var url = req["url"]?.ToString() ?? "";
            try
            {
                bool playlist = Sources.Classify(url)?.Kind == "playlist";
                var job = await Sources.ResolveAsync(tools, url, req["schedule"]?.ToString() ?? "once", ["videos"], req["followChannels"]?.GetValue<bool>() ?? playlist, playlist ? -1 : 0, 480, ct);
                bool made = await Sources.AddAsync(queue, job, ct);
                req["state"] = "done"; req["jobId"] = job.JobId; req["name"] = job.Name; req["note"] = made ? "watch job created" : "already watched";
                Log.Info($"watch: source request {url} -> {job.JobId} ({job.Name}){(made ? "" : " (already followed)")}");
            }
            catch (Exception e) when (e is not OperationCanceledException) { req["state"] = "failed"; req["error"] = Redact.Sas(e.Message.Length > 300 ? e.Message[..300] : e.Message); Log.Info($"watch: source request {url} failed: {req["error"]}"); }
            req["resolvedAt"] = Protocol.Utc(now());
            await store.PutAsync(b.Name, JsonSerializer.SerializeToUtf8Bytes(req), null, null, false, ct);
        }
    }

    /// <summary>0.13.16: every listed upload's publish time: its own (listing timestamp or upload_date), else an estimate that keeps the listing order (newest first).</summary>
    public static Dictionary<string, (string Pub, string Source)> PubsOf(IReadOnlyList<Entry> listed, DateTimeOffset anchor)
    {
        var filled = PubTime.Fill(listed.Select(e => PubTime.FromListing(e.Timestamp, e.UploadDate) is { } p ? ((string?)p, (string?)PubTime.Listing) : (null, null)).ToList(), anchor);
        var res = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        for (int i = 0; i < listed.Count; i++) res[listed[i].Id] = filled[i];
        return res;
    }

    private async Task<bool> KnownAsync(JobQueue queue, Paths paths, string id, CancellationToken ct) =>
        queue.Read(s => s.Entry(id, "download") != null) || (await store.ListAsync(paths.VideoDir(id), false, false, ct)).Count > 0;

    /// <summary>Answers the admin page's add requests; run by the refresh node about once a minute, whether or not any source is due.</summary>
    public Task HousekeepingAsync(Paths paths, JobQueue queue, CancellationToken ct) => ResolveRequestsAsync(paths, queue, ct);

    /// <summary>What the next claim does: the job's own record ("lookup" | "refresh"), else a job written before 0.10.3 that has never run is a lookup and any other is a refresh.</summary>
    public static string KindOf(JobDescriptor j) => j.Run is "lookup" or "refresh" ? j.Run : j.Seen == null ? "lookup" : "refresh";

    public async Task RunAsync(JobContext ctx)
    {
        var job = ctx.Job; var ct = ctx.Token; var paths = ctx.Paths;
        var queue = ctx.Queue ?? throw new InvalidOperationException("refresh runs on the queue");
        bool onWork = queue.Paths.IsWorkQueue;   // 0.13.26: a downloader works from the work queue: it writes nothing into any queue but hands the result back on its done line; the download service folds it into the full queue
        if (!onWork) await HousekeepingAsync(paths, queue, ct);
        if (string.IsNullOrEmpty(job.Id) || job.Kind is not ("playlist" or "channel")) throw new PermanentFailure("refresh job needs kind (playlist|channel) and id");
        var schedule = Schedule.Of(job);
        var run = KindOf(job); bool lookup = run == "lookup";
        int backfill = Math.Max(0, job.Backfill ?? 0);
        ctx.Report(run, job.Name);

        // lookup: the whole source (a playlist in full, a channel's newest ChannelFirstList or backfill); refresh: a channel's newest uploads until known ones are reached, a playlist in full
        int end = lookup ? (job.Kind == "playlist" ? 0 : Math.Max(ChannelFirstList, backfill)) : job.Kind == "playlist" ? 0 : ListSize;
        // 0.13.2: members.json is the source's one file; an old seen.json (and any ids the job body still carries) become members on first touch
        if (await SourceMembers.MigrateSeenAsync(store, paths, job.JobId, job.Seen, Protocol.Utc(now()), ct) is { } migrated && migrated > 0) Log.Info($"{job.JobId}: {migrated} id(s) of the old seen list became members");
        var seen = (await SourceMembers.ReadAsync(store, paths, job.JobId, ct)).Members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        bool firstPoll = job.LastPolled == null;
        var listed = await ListAsync(job, end, ct);
        while (!lookup && job.Kind == "channel" && end < 500 && listed.Count >= end && listed.All(e => !seen.Contains(e.Id))) { end *= 4; listed = await ListAsync(job, end, ct); } // every one new: go further back until a known one turns up
        var fresh = lookup ? listed : listed.Where(e => !seen.Contains(e.Id)).ToList();
        bool changed = listed.Any(e => !seen.Contains(e.Id));   // the set changed: a new upload, or a new item in a playlist
        int made = 0, skipped = 0;
        var addedAt = Protocol.Utc(now());

        // every unknown video becomes an unclassified download job (0.9.1); the singleton classify job decides it. A lookup of a playlist honours its backfill limit (none = every video)
        int limit = lookup && job.Kind == "playlist" && job.Backfill != null ? backfill : int.MaxValue;
        var pubs = PubsOf(listed, now());
        var batch = new List<JobDescriptor>();   // decided and appended together below: one append for the whole lookup, not one per video
        foreach (var e in fresh)
        {
            if (await KnownAsync(queue, paths, e.Id, ct)) { skipped++; continue; }
            if (batch.Count >= limit) break;
            var d = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={e.Id}", job.MaxHeight ?? 480, null);
            d.Source = job.JobId; d.Sources = [job.JobId]; d.AddedAt = addedAt; d.Priority = job.Priority;
            d.Title = e.Title; d.Duration = e.Duration; d.Availability = e.Availability;
            if (pubs.TryGetValue(e.Id, out var pb)) { d.Pub = pb.Pub; d.PubSource = pb.Source; }
            d.ChannelId = e.ChannelId ?? (job.Kind == "channel" ? job.Id : null); d.ChannelName = e.ChannelName ?? (job.Kind == "channel" ? job.Name : null);
            batch.Add(d);
        }
        var result = onWork ? new RunResult { Videos = batch } : null;
        if (onWork) made = batch.Count; else { made = (await ClassifyJobs.CreateUnclassifiedBatchAsync(queue, batch, ct)).Count; skipped += batch.Count - made; }

        if (job.FollowChannels == true)
        {
            var done = new HashSet<string>(); var follow = new List<JobDescriptor>();
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
                follow.Add(ch);
            }
            if (onWork) { foreach (var ch in follow) ch.Run = "lookup"; result!.Follow = follow; }
            else foreach (var ch in await Sources.AddBatchAsync(queue, follow, ct)) Log.Info($"{run} {job.JobId}: now following channel {ch.Name} ({ch.Id})");
        }

        await WriteMembersAsync(paths, job, listed, lookup, addedAt, ct);

        job.Seen = [];   // an empty list marks a source that has been looked at (KindOf); the ids live in members.json
        job.Found = (job.Found ?? 0) + made;
        job.LastPolled = addedAt; job.LastRun = run;
        if (lookup) job.LastLookup = addedAt; else job.LastRefresh = addedAt;
        job.Run = "refresh";
        job.Attempts = 0; job.LastError = null; job.Permanent = null;
        DateTimeOffset? next;
        string tierText = "";
        if (schedule.IsDynamic)
        {
            // 0.13.0 (owner): the tier follows the time since the source was last MODIFIED, recorded here: the last refresh that saw its set change. Only a channel's first poll
            // uses the newest upload's approximate date from the listing (a dead channel must not look freshly changed); a playlist's first poll counts as changed now.
            var nowT = now();
            if (firstPoll)
            {
                var newest = listed.Select(e => DynamicSchedule.ParseUploadDate(e.UploadDate)).Where(d => d != null).Select(d => d!.Value).DefaultIfEmpty().Max();
                DateTimeOffset? first = job.Kind == "playlist" ? nowT : newest == default ? null : newest;
                job.LastModified = first == null ? null : Protocol.Utc(first.Value);
            }
            else if (changed) job.LastModified = Protocol.Utc(nowT);
            DateTimeOffset? modified = DateTimeOffset.TryParse(job.LastModified, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var lm) ? lm : null;
            if (DynamicSchedule.IsInactive(nowT, modified)) { next = null; job.Inactive = true; tierText = ", inactive (last modified over a year ago)"; }
            else
            {
                job.Inactive = null;
                int tier = DynamicSchedule.Tier(nowT, modified);
                next = nowT + DynamicSchedule.Delay(tier, random);
                tierText = $", tier {DynamicSchedule.TierText(tier)}";
            }
        }
        else next = schedule.Next(now());
        job.NextRefresh = next == null ? null : Protocol.Utc(next.Value);
        if (result != null)
        {
            result.Modified = job.LastModified;
            job.Result = await RunResults.SpillAsync(store, paths, job.JobId, result, ct);
        }
        ctx.NextDue = job.NextRefresh; // once / off: the job is done until triggered by hand
        Log.Info($"{run} {job.JobId}: {listed.Count} listed, {fresh.Count} {(lookup ? "looked at" : "new")}, {made} download job(s) created, {skipped} already known; schedule {schedule.Describe()}{tierText}{(next != null ? ", next " + job.NextRefresh : "")}");
    }

    /// <summary>index/sources/&lt;id&gt;/members.json (for the catalog and for "is this new?"): written by the refresh claim, conditional on its ETag. A playlist is rewritten in full; a channel's listing is only its newest uploads, so it is merged into what is there (newest first).</summary>
    private async Task WriteMembersAsync(Paths paths, JobDescriptor job, List<Entry> listed, bool lookup, string at, CancellationToken ct)
    {
        var pubs = PubsOf(listed, now());
        job.Members = await SourceMembers.MergeAsync(store, paths, job, listed.Select(e => new SourceMember(e.Id, e.Title, e.Duration, e.ChannelId, e.ChannelName, e.Availability, null, pubs[e.Id].Pub, pubs[e.Id].Source)).ToList(), lookup, at, ct);
    }
}
