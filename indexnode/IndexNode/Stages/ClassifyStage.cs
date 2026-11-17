using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// Which channel uploads are dramas worth downloading (owner, 2026-11-14). The downloader's watch job lists a channel and hands its new uploads
/// here; any node with an AI endpoint decides: a stored decision first, then the length rule (shorter than MinMinutes is never a drama, no AI),
/// then one AI call per batch. Each decision is stored as classify/&lt;id&gt;.json and never asked again; candidates get download jobs.
/// A node without an AI endpoint passes the job on without spending an attempt.
/// </summary>
public sealed class ClassifyStage(IStore store, Func<DateTimeOffset>? clock = null) : IStageHandler
{
    public const int MinMinutes = 20, AiBatch = 40;
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    /// <summary>The AI for classification (chat + model), or null when this node has none.</summary>
    public Func<(IChat Chat, string Model)?> Ai { get; init; } = () => null;

    public static string Blob(Paths paths, string id) => $"{paths.Prefix}classify/{id}.json";

    public static string Prompt(string channel, IEnumerable<ClassifyEntry> batch) => $$"""
        You decide which uploads of a YouTube channel are worth downloading for a catalogue of Chinese short dramas (vertical micro-dramas, usually published as one long video of all episodes, 1-3 hours, or as long multi-episode parts).
        Channel: {{channel}}
        A download candidate is a whole drama, or a multi-episode part of one (at least about 20 minutes), in any language (a dubbed drama is a candidate; give its language).
        Not candidates: trailers, previews, teasers, single short clips or highlights, recaps, reviews, commentary, reactions, behind the scenes, interviews, music, vlogs, ads, live streams, and anything that is not a scripted drama.
        Uploads (id | length | title):
        {{string.Join("\n", batch.Select(e => $"{e.Id} | {(e.Duration is { } d ? $"{d / 60:F0} min" : "length unknown")} | {e.Title ?? "(no title)"}"))}}
        Answer with ONE JSON object: {"videos":[{"id":"","candidate":true,"kind":"full|part|dub|trailer|clip|recap|other","language":"zh|en|...","reason":"a few words"}]}, one entry per upload, in the same order.
        """;

    public async Task RunAsync(JobContext ctx)
    {
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
}
