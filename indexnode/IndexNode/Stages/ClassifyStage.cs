using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// Which channel uploads are dramas worth downloading (owner, 2026-11-14). The downloader's watch job lists a channel and hands its new uploads
/// here; any node with an AI endpoint decides: a stored decision first, then the length rule (shorter than MinMinutes is never a drama, no AI),
/// then one AI call per batch. Each decision is stored as classify/&lt;id&gt;.json and never asked again; candidates get download jobs.
/// A node without an AI endpoint passes the job on without spending an attempt.
/// <para>0.9.1: every new download job starts <c>unclassified</c> and the singleton job <c>classify/all</c> decides them in batches
/// (<see cref="RunAllAsync"/>); the per-channel jobs above are the old form and still run once.</para>
/// </summary>
public sealed class ClassifyStage(IStore store, Func<DateTimeOffset>? clock = null) : IStageHandler
{
    public const int MinMinutes = 20, AiBatch = 40, MaxPerRun = 400;
    /// <summary>The singleton is due again this long after a run that left nothing unclassified.</summary>
    public static readonly TimeSpan IdleEvery = TimeSpan.FromMinutes(10);
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    /// <summary>The AI for classification (chat + model), or null when this node has none.</summary>
    public Func<(IChat Chat, string Model)?> Ai { get; init; } = () => null;

    public static string Blob(Paths paths, string id) => $"{paths.Prefix}classify/{id}.json";

    public static string Prompt(string? channel, IEnumerable<ClassifyEntry> batch) => $$"""
        You decide which uploads of a YouTube channel are worth downloading for a catalogue of Chinese short dramas (vertical micro-dramas, usually published as one long video of all episodes, 1-3 hours, or as long multi-episode parts).
        {{(channel == null ? "Each upload names its channel." : "Channel: " + channel)}}
        A download candidate is a whole drama, or a multi-episode part of one (at least about 20 minutes), in any language (a dubbed drama is a candidate; give its language).
        Not candidates: trailers, previews, teasers, single short clips or highlights, recaps, reviews, commentary, reactions, behind the scenes, interviews, music, vlogs, ads, live streams, and anything that is not a scripted drama.
        Uploads (id | {{(channel == null ? "channel | " : "")}}length | title):
        {{string.Join("\n", batch.Select(e => $"{e.Id} | {(channel == null ? (e.ChannelName ?? "unknown channel") + " | " : "")}{(e.Duration is { } d ? $"{d / 60:F0} min" : "length unknown")} | {e.Title ?? "(no title)"}"))}}
        Answer with ONE JSON object: {"videos":[{"id":"","candidate":true,"kind":"full|part|dub|trailer|clip|recap|other","language":"zh|en|...","reason":"a few words"}]}, one entry per upload, in the same order.
        """;

    public static async Task SaveDecisionAsync(IStore store, Paths paths, string id, string? channelId, string? channelName, string? title, double? duration, bool candidate, string kind, string reason, string by, string? model, string at, CancellationToken ct) =>
        await store.PutAsync(Blob(paths, id), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, id, channel = channelId, channelName, title, duration, candidate, kind, reason, by, model, at }), null, null, false, ct);

    public async Task RunAsync(JobContext ctx)
    {
        if (ctx.Job.JobId == ClassifyJobs.SingletonId) { await RunAllAsync(ctx); return; }
        var job = ctx.Job; var ct = ctx.Token; var paths = ctx.Paths;
        var entries = job.Entries ?? [];
        ctx.Report("classify", $"{entries.Count} uploads of {job.Name}");
        var at = Protocol.Utc(now());
        async Task Save(ClassifyEntry e, bool candidate, string kind, string reason, string by, string? model) =>
            await store.PutAsync(Blob(paths, e.Id), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, id = e.Id, channel = job.Id, channelName = job.Name, title = e.Title, duration = e.Duration, candidate, kind, reason, by, model, at }), null, null, false, ct);
        var yes = new List<string>(); var ask = new List<ClassifyEntry>(); int stored = 0, rule = 0;
        foreach (var e in entries)
        {
            if (await store.GetAsync(Blob(paths, e.Id), ct) is { } b) { stored++; if (JsonNode.Parse(b.Data)?["candidate"]?.GetValue<bool>() == true) yes.Add(e.Id); continue; }
            if (e.Duration is { } d && d < MinMinutes * 60) { await Save(e, false, "short", $"{d / 60:F0} min: shorter than {MinMinutes} min", "rule", null); rule++; continue; }
            ask.Add(e);
        }
        if (ask.Count > 0 && Ai() is not { } ai) throw new JobSkipped("no AI endpoint on this node: classify needs one");
        int aiYes = 0;
        for (int i = 0; i < ask.Count; i += AiBatch)
        {
            var (chat, model) = Ai()!.Value;
            var batch = ask.Skip(i).Take(AiBatch).ToList();
            var res = await chat.JsonAsync(model, JsonValue.Create(Prompt(job.Name ?? job.Id ?? "", batch))!, "submit_classification", ct); // a failure retries the job
            var byId = ((res["videos"] as JsonArray) ?? []).OfType<JsonObject>().Where(o => o["id"] != null).GroupBy(o => o["id"]!.ToString()).ToDictionary(g => g.Key, g => g.First());
            var missing = batch.Where(e => !byId.ContainsKey(e.Id)).ToList();
            if (missing.Count > 0) throw new InvalidOperationException($"the AI left out {missing.Count} of {batch.Count} uploads (e.g. {missing[0].Id})");
            foreach (var e in batch)
            {
                var o = byId[e.Id];
                bool c = o["candidate"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) && cb;
                await Save(e, c, o["kind"]?.ToString() ?? "", (o["reason"]?.ToString() ?? "") + (o["language"] is { } l ? $" [{l}]" : ""), "ai", model);
                if (c) { yes.Add(e.Id); aiYes++; }
            }
        }
        int made = 0;
        foreach (var id in yes)
        {
            if (await store.GetTagsAsync(paths.Job("download", id), ct) != null || (await store.ListAsync(paths.VideoDir(id), false, false, ct)).Count > 0) continue;
            var d = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={id}", job.MaxHeight ?? 480, null);
            d.Source = job.Source; d.Sources = job.Source == null ? null : [job.Source]; d.AddedAt = at; d.Priority = job.Priority;
            if (await store.PutAsync(paths.Job("download", id), d.ToBytes(), Tags.Job("download", "pending", at, null, d.Priority), null, true, ct)) made++;
        }
        Log.Info($"classify {job.JobId}: {entries.Count} uploads: {stored} already decided, {rule} too short, {ask.Count} by AI ({aiYes} candidates); {made} download job(s) created");
    }

    /// <summary>
    /// One run of the singleton: the oldest unclassified download jobs (up to <see cref="MaxPerRun"/>), each decided by a stored decision, the length rule,
    /// the unavailable rule, then the AI in batches of <see cref="AiBatch"/>; candidates become pending, the rest failed + permanent with reason "classify: kind reason".
    /// The job is due again at once when more remain, else after <see cref="IdleEvery"/>.
    /// </summary>
    public async Task RunAllAsync(JobContext ctx)
    {
        var job = ctx.Job; var ct = ctx.Token; var paths = ctx.Paths;
        var at = Protocol.Utc(now());
        var all = await ClassifyJobs.ListUnclassifiedAsync(store, paths, ct);
        var todo = all.Take(MaxPerRun).ToList();
        ctx.Report("classify", $"{todo.Count} of {all.Count} unclassified");
        if (job.CountDay != at[..10]) { job.CountDay = at[..10]; job.Classified = 0; job.Rejected = 0; }
        int stored = 0, rule = 0, dead = 0, aiCount = 0, yes = 0, no = 0;
        var ask = new List<(BlobEntry Blob, JobDescriptor Job)>();

        async Task Decide(BlobEntry b, JobDescriptor d, bool candidate, string kind, string reason, string by, string? model, bool save)
        {
            var id = Path.GetFileNameWithoutExtension(b.Name);
            if (save) await SaveDecisionAsync(store, paths, id, d.ChannelId, d.ChannelName, d.Title, d.Duration, candidate, kind, reason, by, model, at, ct);
            await ClassifyJobs.ApplyAsync(store, paths, b.Name, b.Tags, d, candidate, kind, reason, at, ct);
            if (candidate) yes++; else no++;
            job.Classified = (job.Classified ?? 0) + 1; if (!candidate) job.Rejected = (job.Rejected ?? 0) + 1;
        }

        foreach (var b in todo)
        {
            JobDescriptor d;
            try { d = await store.GetAsync(b.Name, ct) is { } body ? JobDescriptor.Parse(body.Data) : throw new InvalidDataException("gone"); }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException) { Log.Info($"classify: skipping unreadable {b.Name}: {e.Message}"); continue; }
            var id = Path.GetFileNameWithoutExtension(b.Name);
            if (await store.GetAsync(Blob(paths, id), ct) is { } sb)                                    // 1. a stored decision, never asked twice
            {
                var n = JsonNode.Parse(sb.Data);
                bool c = n?["candidate"]?.GetValue<bool>() == true;
                await Decide(b, d, c, n?["kind"]?.ToString() ?? "", n?["reason"]?.ToString() ?? "", "stored", null, false); stored++; continue;
            }
            if (d.Duration is { } dur && dur < MinMinutes * 60)                                          // 2. the length rule
            { await Decide(b, d, false, "short", $"{dur / 60:F0} min: shorter than {MinMinutes} min", "rule", null, true); rule++; continue; }
            if (ClassifyJobs.IsUnavailable(d))                                                          // 3. dead videos, before any AI call
            { await Decide(b, d, false, "unavailable", ClassifyJobs.UnavailableReason(d), "rule", null, true); dead++; continue; }
            ask.Add((b, d));                                                                            // 4. the AI
        }
        if (ask.Count > 0 && Ai() is not { } ai) throw new JobSkipped("no AI endpoint on this node: classify needs one");
        for (int i = 0; i < ask.Count; i += AiBatch)
        {
            var (chat, model) = Ai()!.Value;
            var batch = ask.Skip(i).Take(AiBatch).ToList();
            var entries = batch.Select(x => new ClassifyEntry { Id = Path.GetFileNameWithoutExtension(x.Blob.Name), Title = x.Job.Title, Duration = x.Job.Duration, ChannelName = x.Job.ChannelName }).ToList();
            var res = await chat.JsonAsync(model, JsonValue.Create(Prompt(null, entries))!, "submit_classification", ct);
            var byId = ((res["videos"] as JsonArray) ?? []).OfType<JsonObject>().Where(o => o["id"] != null).GroupBy(o => o["id"]!.ToString()).ToDictionary(g => g.Key, g => g.First());
            var missing = entries.Where(e => !byId.ContainsKey(e.Id)).ToList();
            if (missing.Count > 0) throw new InvalidOperationException($"the AI left out {missing.Count} of {entries.Count} uploads (e.g. {missing[0].Id})");
            foreach (var (x, e) in batch.Zip(entries))
            {
                var o = byId[e.Id];
                bool c = o["candidate"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) && cb;
                await Decide(x.Blob, x.Job, c, o["kind"]?.ToString() ?? "", (o["reason"]?.ToString() ?? "") + (o["language"] is { } l ? $" [{l}]" : ""), "ai", model, true); aiCount++;
            }
        }
        job.Attempts = 0; job.LastError = null; job.LastPolled = at;
        bool more = all.Count > todo.Count;
        ctx.NextDue = Protocol.Utc(more ? now() : now() + IdleEvery);
        Log.Info($"classify all: {todo.Count} of {all.Count} unclassified: {stored} already decided, {rule} too short, {dead} unavailable, {aiCount} by AI; {yes} candidate(s), {no} rejected{(more ? "; more remain" : "")}");
    }
}

/// <summary>Creating, finding and deciding unclassified download jobs, and the singleton classify job.</summary>
public static class ClassifyJobs
{
    public const string SingletonId = "all";
    public static string SingletonBlob(Paths paths) => paths.Job("classify", SingletonId);

    /// <summary>Creates the singleton index/jobs/classify/all.json when it is missing (create-only: one blob however many creators race); a copy that ended failed or done is made pending again.</summary>
    public static async Task EnsureSingletonAsync(IStore store, Paths paths, CancellationToken ct = default, string? now = null)
    {
        var at = now ?? Protocol.NowUtc();
        var job = new JobDescriptor { Stage = "classify", JobId = SingletonId, Name = "classify all", AddedAt = at };
        var name = SingletonBlob(paths);
        if (await store.PutAsync(name, job.ToBytes(), Tags.Job("classify", "pending", at), null, true, ct)) return;
        var tags = await store.GetTagsAsync(name, ct);
        if (tags == null || tags.GetValueOrDefault(Tags.State) is not ("failed" or "done")) return;
        if (await store.GetAsync(name, ct) is { } c && JobDescriptor.Parse(c.Data) is { } old)
        {
            old.Attempts = 0; old.LastError = null; old.Permanent = null;
            try { await store.PutAsync(name, old.ToBytes(), Tags.Job("classify", "pending", tags.GetValueOrDefault(Tags.Added) ?? at, at), null, false, ct); } catch (StorageException) { } // leased by someone else: it is running
        }
    }

    /// <summary>Writes the download job as unclassified (create-only) and makes sure the singleton exists. False when the job was already there.</summary>
    public static async Task<bool> CreateUnclassifiedAsync(IStore store, Paths paths, JobDescriptor d, CancellationToken ct = default)
    {
        var made = await store.PutAsync(paths.Job("download", d.JobId), d.ToBytes(), Tags.Job("download", Tags.Unclassified, d.AddedAt, null, d.Priority), null, true, ct);
        if (made) await EnsureSingletonAsync(store, paths, ct);
        return made;
    }

    /// <summary>The unclassified download jobs, oldest first (idx_added, then name).</summary>
    public static async Task<List<BlobEntry>> ListUnclassifiedAsync(IStore store, Paths paths, CancellationToken ct)
    {
        IEnumerable<BlobEntry> found;
        try { found = await store.FindByTagsAsync(Tags.UnclassifiedWhere, ct); }
        catch (ForbiddenException) { found = (await store.ListAsync(paths.JobsDir("download"), true, false, ct)).Where(b => b.Tags.GetValueOrDefault(Tags.State) == Tags.Unclassified); }
        return found.Where(b => b.Name.StartsWith(paths.JobsDir("download"), StringComparison.Ordinal) && b.Tags.GetValueOrDefault(Tags.State) == Tags.Unclassified)
            .OrderBy(b => b.Tags.GetValueOrDefault(Tags.Added) ?? "", StringComparer.Ordinal).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
    }

    private static readonly string[] DeadAvailability = ["private", "deleted", "unavailable"];
    private static readonly string[] DeadTitles = ["[Private video]", "[Deleted video]"];

    /// <summary>Private, deleted or unavailable according to the listing, or a flat listing entry with no duration and a placeholder title.</summary>
    public static bool IsUnavailable(JobDescriptor d) =>
        d.Availability != null && DeadAvailability.Contains(d.Availability.Trim().ToLowerInvariant())
        || d.Duration == null && d.Title != null && DeadTitles.Contains(d.Title.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string UnavailableReason(JobDescriptor d) => d.Availability != null && DeadAvailability.Contains(d.Availability.Trim().ToLowerInvariant()) ? $"listed as {d.Availability.Trim().ToLowerInvariant()}" : $"listed as {d.Title}";

    /// <summary>Records a decision on the download job: candidate -> pending (tags kept, idx_classified set); not a candidate -> failed + permanent, reason "classify: kind reason".</summary>
    public static async Task ApplyAsync(IStore store, Paths paths, string blob, IReadOnlyDictionary<string, string> tags, JobDescriptor d, bool candidate, string kind, string reason, string at, CancellationToken ct)
    {
        var t = new Dictionary<string, string>(tags) { [Tags.State] = candidate ? "pending" : "failed", [Tags.Classified] = at };
        if (candidate) { await store.SetTagsAsync(blob, t, null, ct); return; }
        var why = ($"classify: {kind} {reason}").Trim(); if (why.Length > 300) why = why[..300];
        d.Reason = why; d.LastError = why; d.Permanent = true; d.FinishedAt = at;
        await store.PutAsync(blob, d.ToBytes(), t, null, false, ct);
    }
}
