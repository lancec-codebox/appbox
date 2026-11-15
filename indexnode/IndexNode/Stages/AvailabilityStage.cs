using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// The daily availability sweep (owner, 2026-11-14), on a yt-dlp node: no per-video polling. Every tracked video (a finished download) is mapped
/// once to its channel (from its info.json, remembered in channels/members.json); each channel is listed in full (an "ingest" channel also has a
/// watch job for its new uploads; the rest are "check" only) (flat, ~1 request per 100 videos); only a tracked video missing from its channel's
/// listing is checked on its own. Unavailable videos get videos/&lt;id&gt;/availability.json with yt-dlp's reason; a video that is back is restored.
/// The summary (availability.json) feeds the admin pages and the player's fallback. The job never ends: it is due again after <c>every</c> (1d).
/// </summary>
public sealed class AvailabilityStage(Tools tools, IStore store, Func<DateTimeOffset>? clock = null) : IStageHandler
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    public const string SweepId = "sweep";
    public static string MembersBlob(Paths p) => $"{p.Prefix}channels/members.json";
    public static string StateBlob(Paths p, string id) => $"{p.Prefix}videos/{id}/availability.json";
    public static string SummaryBlob(Paths p) => $"{p.Prefix}availability.json";

    public static JobDescriptor NewSweep(string every = "1d") => new() { Stage = "availability", JobId = SweepId, Kind = "sweep", Name = "availability sweep", Every = every, AddedAt = Protocol.NowUtc() };

    async Task<JsonObject?> J(string name, CancellationToken ct) => await store.GetAsync(name, ct) is { } b ? JsonNode.Parse(b.Data) as JsonObject : null;

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token; var paths = ctx.Paths; var job = ctx.Job; var at = Protocol.Utc(now());
        // 1. tracked videos: finished downloads
        var tracked = (await store.ListAsync(paths.JobsDir("download"), true, false, ct))
            .Where(b => b.Tags.GetValueOrDefault(Tags.State) == "done").Select(b => Path.GetFileNameWithoutExtension(b.Name)).ToList();
        ctx.Report("availability", $"{tracked.Count} tracked videos");
        // 2. each video's channel, read once and remembered
        var members = await J(MembersBlob(paths), ct) ?? new JsonObject(); int learned = 0;
        foreach (var id in tracked.Where(id => members[id] == null))
            if (await J(paths.Video(id, "info.json"), ct) is { } info && info["channel_id"]?.ToString() is { Length: > 0 } cid) { members[id] = cid; members[id + "|name"] = info["channel"]?.ToString(); learned++; }
        if (learned > 0) await store.PutAsync(MembersBlob(paths), Encoding(members), null, null, false, ct);
        var byChannel = tracked.Where(id => members[id] != null).GroupBy(id => members[id]!.ToString()).ToDictionary(g => g.Key, g => g.ToList());
        // 3. a channel is "ingest" when it has a watch job (its new uploads are classified and downloaded), else "check" only (owner, 2026-11-14):
        //    the sweep lists every channel of a tracked video either way, and never starts ingesting one by itself
        var channels = new JsonArray();
        // 4. one full listing per channel; only the missing are checked on their own
        var unavailable = new JsonArray(); int checkedAlone = 0, restored = 0, gone = 0, listingsFailed = 0;
        foreach (var (cid, ids) in byChannel)
        {
            var r = await tools.RunYtDlpAsync(["--flat-playlist", "--no-warnings", "--print", "%(id)s", $"https://www.youtube.com/channel/{cid}/videos"], ct);
            var listed = r.ExitCode == 0 ? r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet() : [];
            if (r.ExitCode != 0) listingsFailed++;
            bool ingest = await store.GetTagsAsync(paths.Job("watch", "ch-" + cid), ct) != null;
            channels.Add(new JsonObject { ["id"] = cid, ["name"] = members[ids[0] + "|name"]?.ToString(), ["mode"] = ingest ? "ingest" : "check", ["tracked"] = ids.Count, ["listed"] = r.ExitCode == 0 ? listed.Count : null, ["listingOk"] = r.ExitCode == 0 });
            foreach (var id in ids)
            {
                var state = await J(StateBlob(paths, id), ct);
                bool wasGone = state?["available"]?.GetValue<bool>() == false;
                if (listed.Contains(id))
                {
                    if (wasGone) { await Put(StateBlob(paths, id), new JsonObject { ["available"] = true, ["checkedAt"] = at, ["since"] = at, ["evidence"] = "listed by its channel again" }, ct); restored++; }
                    continue;
                }
                // missing from the listing (or the listing failed): ask about this one video
                checkedAlone++;
                var p = await tools.RunYtDlpAsync(["--no-warnings", "--skip-download", "--print", "%(availability)s", $"https://www.youtube.com/watch?v={id}"], ct);
                if (p.ExitCode == 0) { if (wasGone) { await Put(StateBlob(paths, id), new JsonObject { ["available"] = true, ["checkedAt"] = at, ["since"] = at, ["evidence"] = "plays again" }, ct); restored++; } continue; }
                var reason = p.Stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? p.Stderr.Trim();
                reason = reason.Length > 300 ? reason[..300] : reason;
                var since = wasGone ? state?["since"]?.ToString() ?? at : at;
                await Put(StateBlob(paths, id), new JsonObject { ["available"] = false, ["checkedAt"] = at, ["since"] = since, ["reason"] = reason, ["channel"] = cid }, ct);
                unavailable.Add(new JsonObject { ["id"] = id, ["channel"] = cid, ["since"] = since, ["reason"] = reason });
                if (!wasGone) gone++;
            }
        }
        await Put(SummaryBlob(paths), new JsonObject
        {
            ["v"] = 1, ["at"] = at, ["tracked"] = tracked.Count, ["mapped"] = byChannel.Sum(kv => kv.Value.Count), ["channels"] = byChannel.Count,
            ["listingsFailed"] = listingsFailed, ["checkedAlone"] = checkedAlone, ["unavailable"] = unavailable, ["channelList"] = channels,
        }, ct);
        job.LastPolled = at; job.Attempts = 0; job.LastError = null;
        ctx.NextDue = Protocol.Utc(now() + WatchStage.ParseEvery(job.Every ?? "1d"));
        Log.Info($"availability: {tracked.Count} tracked, {byChannel.Count} channels ({listingsFailed} listings failed), {checkedAlone} checked alone: {gone} newly unavailable, {restored} back, {unavailable.Count} unavailable in all");
    }

    static byte[] Encoding(JsonNode n) => JsonSerializer.SerializeToUtf8Bytes(n);
    Task Put(string name, JsonNode n, CancellationToken ct) => store.PutAsync(name, Encoding(n), null, null, false, ct);
}
