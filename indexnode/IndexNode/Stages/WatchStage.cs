using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>
/// A followed source (playlist or channel), polled on a schedule. The job never ends: after a poll the engine rewrites it as
/// pending, due <c>every</c> later. Listings are yt-dlp's flat listing, newest first, without cookies.
/// </summary>
public sealed partial class WatchStage(Tools tools, IStore store, Func<DateTimeOffset>? clock = null) : IStageHandler
{
    public const int ListSize = 30;
    public const int SeenCap = 500;
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public sealed record Entry(string Id, string? ChannelId, string? ChannelName, double? Duration = null, string? Title = null);
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

    /// <summary>One yt-dlp line "id TAB channel_id TAB channel"; "NA" and empty fields mean missing.</summary>
    public static Entry? ParseLine(string line)
    {
        var p = line.TrimEnd('\r').Split('\t');
        var id = p[0].Trim();
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$")) return null;
        string? Field(int i) => p.Length > i && p[i].Trim() is { Length: > 0 } v && v != "NA" ? v : null;
        double? dur = Field(3) is { } d && double.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv) ? dv : null;
        return new Entry(id, Field(1), Field(2), dur, p.Length > 4 && Field(4) != null ? string.Join(' ', p[4..]).Trim() : null);
    }

    public async Task<List<Entry>> ListAsync(JobDescriptor j, int end, CancellationToken ct)
    {
        var all = new List<Entry>();
        string? lastError = null; int failed = 0;
        var urls = ListingUrls(j);
        foreach (var url in urls)
        {
            var r = await tools.RunYtDlpAsync(["--flat-playlist", "--no-warnings", "--playlist-end", end.ToString(), "--print", "%(id)s\t%(channel_id)s\t%(channel)s\t%(duration)s\t%(title)s", url], ct);
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
                var job = await Sources.ResolveAsync(tools, url, "1h", ["videos"], req["followChannels"]?.GetValue<bool>() ?? playlist, playlist ? 200 : 0, 480, ct);
                bool made = await Sources.AddAsync(store, paths, job, ct);
                req["state"] = "done"; req["jobId"] = job.JobId; req["name"] = job.Name; req["note"] = made ? "watch job created" : "already watched";
                Log.Info($"watch: source request {url} -> {job.JobId} ({job.Name}){(made ? "" : " (already watched)")}");
            }
            catch (Exception e) when (e is not OperationCanceledException) { req["state"] = "failed"; req["error"] = Redact.Sas(e.Message.Length > 300 ? e.Message[..300] : e.Message); Log.Info($"watch: source request {url} failed: {req["error"]}"); }
            req["resolvedAt"] = Protocol.Utc(now());
            await store.PutAsync(b.Name, JsonSerializer.SerializeToUtf8Bytes(req), null, null, false, ct);
        }
    }

    private async Task<bool> KnownAsync(Paths paths, string id, CancellationToken ct) =>
        await store.GetTagsAsync(paths.Job("download", id), ct) != null || (await store.ListAsync(paths.VideoDir(id), false, false, ct)).Count > 0;

    public async Task RunAsync(JobContext ctx)
    {
        var job = ctx.Job; var ct = ctx.Token; var paths = ctx.Paths;
        await ResolveRequestsAsync(paths, ct);
        if (string.IsNullOrEmpty(job.Id) || job.Kind is not ("playlist" or "channel")) throw new PermanentFailure("watch job needs kind (playlist|channel) and id");
        var every = ParseEvery(job.Every);
        bool first = job.Seen == null;
        int backfill = Math.Max(0, job.Backfill ?? 0);
        ctx.Report("poll", job.Name);

        var listed = await ListAsync(job, first ? Math.Max(job.Kind == "channel" ? ChannelFirstList : ListSize, backfill) : ListSize, ct);
        var seen = new HashSet<string>(job.Seen ?? []);
        var fresh = listed.Where(e => !seen.Contains(e.Id)).ToList();
        int made = 0, skipped = 0;
        var addedAt = Protocol.Utc(now());

        // which of the fresh videos get a download job: on the first poll only the newest `backfill`, afterwards all of them
        // a playlist is the owner's curation: its videos are downloaded; a channel's uploads are classified first (classify stage, on a node with AI)
        var toDownload = job.Kind == "channel" ? [] : first ? fresh.Take(backfill).ToList() : fresh;
        if (job.Kind == "channel" && fresh.Count > 0)
        {
            var cj = JobDescriptor.NewClassify(job, fresh.Select(e => new ClassifyEntry { Id = e.Id, Title = e.Title, Duration = e.Duration }).ToList(), addedAt);
            await store.PutAsync(paths.Job("classify", cj.JobId), cj.ToBytes(), Tags.Job("classify", "pending", addedAt), null, true, ct);
            Log.Info($"watch {job.JobId}: {fresh.Count} new uploads handed to classify/{cj.JobId}");
        }
        foreach (var e in toDownload)
        {
            if (await KnownAsync(paths, e.Id, ct)) { skipped++; continue; }
            var d = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={e.Id}", job.MaxHeight ?? 480, null);
            d.Source = job.JobId; d.AddedAt = addedAt;
            if (await store.PutAsync(paths.Job("download", e.Id), d.ToBytes(), Tags.Job("download", "pending", addedAt), null, true, ct)) made++; else skipped++;
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
                var ch = JobDescriptor.NewWatch("channel", cid, cname ?? cid, job.Every ?? "1h", ["videos"], false, 0, job.MaxHeight ?? 480); // Shorts are never dramas
                ch.Source = job.JobId; ch.AddedAt = addedAt;
                if (await store.PutAsync(paths.Job("watch", ch.JobId), ch.ToBytes(), Tags.Job("watch", "pending", addedAt), null, true, ct))
                    Log.Info($"watch {job.JobId}: now following channel {ch.Name} ({cid})");
            }
        }

        job.Seen = fresh.Select(e => e.Id).Concat(job.Seen ?? []).Distinct().Take(SeenCap).ToList();
        job.Found = (job.Found ?? 0) + made;
        job.LastPolled = addedAt;
        job.Attempts = 0; job.LastError = null;
        ctx.NextDue = Protocol.Utc(now() + every);
        Log.Info($"watch {job.JobId}: {listed.Count} listed, {fresh.Count} new, {made} download job(s) created, {skipped} already known{(first ? " (first poll)" : "")}");
    }
}
