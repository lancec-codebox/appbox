using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>The video.json of an upload is missing: the readers fail loudly (the catalog entry, the claim) and try again later; "indexnode videos backfill" creates it for the uploads that predate it.</summary>
public sealed class VideoJsonMissingException(string id) : InvalidOperationException($"{id}: index/videos/{id}/video.json is missing (run: indexnode videos backfill)");

/// <summary>
/// 0.13.2: index/videos/&lt;id&gt;/video.json, the per-video descriptor: every fact about the upload that is not job state. v, id, youtube (title, channel, channelId, uploadDate, durationSeconds, url, language),
/// download (what downloaded.json holds), precheck (the whole pre-check record), speech (language, dubbed, source precheck|transcribe) and ai (score, flag, source precheck|centers).
/// Only the holder of the video's lease writes it (the download claim creates it, the process claim updates it), every write conditional on the ETag it read (412: read, merge, again). Anyone may read it.
/// </summary>
public static class VideoJson
{
    public const int Version = 1;
    public sealed record Doc(string? ETag, JsonObject Root);

    public static string Name(Paths p, string id) => p.Video(id, "video.json");

    /// <summary>Null when there is no video.json; throws when it is there but not a JSON object (never a quiet null).</summary>
    public static async Task<Doc?> ReadAsync(IStore store, Paths paths, string id, CancellationToken ct) => await ReadAsync(store, paths, id, ct, fresh: false);

    // 0.13.18: video.json is cached on the node by ETag (a conditional GET: 304 costs one request and no bytes, and a read in the same step costs none). Docs are handed out as deep copies, so a caller's edits never leak.
    sealed record Cached(string? ETag, byte[] Data, DateTimeOffset At);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStore, System.Collections.Concurrent.ConcurrentDictionary<string, Cached>> caches = new();
    static System.Collections.Concurrent.ConcurrentDictionary<string, Cached> CacheOf(IStore s) => caches.GetValue(StoreIdentity(s), _ => new(StringComparer.Ordinal));
    /// <summary>The store under the wrappers that do not change what a name means (metering, recording, local cache), so a node's lanes share one cache.</summary>
    static IStore StoreIdentity(IStore s) { for (int i = 0; i < 8; i++) s = s switch { MeteredStore m => m.Inner, RecordingStore r => r.Inner, LocalCacheStore l => l.Inner, _ => s }; return s; }
    /// <summary>How long a cached video.json is served without asking the store again (the same step reads it many times; a step is short). A write through <see cref="UpdateAsync"/> refreshes the entry.</summary>
    public static TimeSpan Fresh { get; set; } = TimeSpan.FromSeconds(20);
    public static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    public static void ClearCache(IStore store) => CacheOf(store).Clear();

    public static async Task<Doc?> ReadAsync(IStore store, Paths paths, string id, CancellationToken ct, bool fresh)
    {
        var name = Name(paths, id); var key = name; var cache = CacheOf(store);
        if (!fresh && cache.TryGetValue(key, out var hit) && Now() - hit.At < Fresh) return Parse(name, hit.ETag, hit.Data);
        if (cache.TryGetValue(key, out var old) && old.ETag != null)
        {
            var r = await store.GetIfChangedAsync(name, old.ETag, ct);
            if (!r.Exists) { cache.TryRemove(key, out _); return null; }
            if (!r.Changed) { cache[key] = old with { At = Now() }; return Parse(name, old.ETag, old.Data); }
            cache[key] = new Cached(r.ETag, r.Data!, Now()); return Parse(name, r.ETag, r.Data!);
        }
        var c = await store.GetAsync(name, ct);
        if (c == null) return null;
        cache[key] = new Cached(c.ETag, c.Data, Now());
        return Parse(name, c.ETag, c.Data);
    }

    static Doc Parse(string name, string? etag, byte[] data)
    {
        try { return JsonNode.Parse(data) is JsonObject o ? new Doc(etag, o) : throw new InvalidDataException($"{name} is not a JSON object"); }
        catch (JsonException e) { throw new InvalidDataException($"{name} is unreadable: {e.Message}", e); }
    }

    public static async Task<Doc> RequireAsync(IStore store, Paths paths, string id, CancellationToken ct) => await ReadAsync(store, paths, id, ct) ?? throw new VideoJsonMissingException(id);

    /// <summary>
    /// Reads video.json (a missing one starts a new document: If-None-Match), lets <paramref name="merge"/> change it and writes it back conditional on the ETag read. A 412 reads again and merges again.
    /// Called only by the holder of the video's lease. Returns the document as written.
    /// </summary>
    public static async Task<JsonObject> UpdateAsync(IStore store, Paths paths, string id, Action<JsonObject> merge, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var cache = CacheOf(store);
            var cur = await ReadAsync(store, paths, id, ct, fresh: attempt > 0);
            var root = cur?.Root ?? new JsonObject();
            root["v"] = Version; root["id"] = id;
            merge(root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
            if (await store.PutIfMatchAsync(Name(paths, id), bytes, cur?.ETag, ct)) { cache.TryRemove(Name(paths, id), out _); return root; }   // the new ETag is not known: the next read asks (once)
        }
        throw new IOException($"{id}: video.json kept changing (is another node writing it without the lease?)");
    }

    public static JsonObject Section(JsonObject root, string name) => root[name] as JsonObject ?? (JsonObject)(root[name] = new JsonObject());

    /// <summary>youtube: what the info JSON says (null fields left out).</summary>
    public static JsonObject YoutubeFrom(JsonObject? info, string id, string? url, double? duration)
    {
        var y = new JsonObject();
        void Put(string k, JsonNode? v) { if (v != null) y[k] = v; }
        Put("title", (string?)info?["title"]); Put("channel", (string?)info?["channel"] ?? (string?)info?["uploader"]); Put("channelId", (string?)info?["channel_id"]);
        Put("uploadDate", (string?)info?["upload_date"]);
        var d = (double?)info?["duration"] ?? duration; if (d is > 0) y["durationSeconds"] = Math.Round(d.Value, 2);
        y["url"] = (string?)info?["webpage_url"] ?? url ?? "https://www.youtube.com/watch?v=" + id;
        Put("language", (string?)info?["language"]);
        return y;
    }

    public static void SetSpeech(JsonObject root, string? language, bool? dubbed, string source) => root["speech"] = new JsonObject { ["language"] = language, ["dubbed"] = dubbed, ["source"] = source };
    public static void SetAi(JsonObject root, double? score, bool? flag, string source) => root["ai"] = new JsonObject { ["score"] = score == null ? null : Math.Round(score.Value, 4), ["flag"] = flag, ["source"] = source };

    /// <summary>The duration of the upload: youtube.durationSeconds, else download.durationSeconds; null when the document says neither.</summary>
    public static double? Duration(JsonObject root) => (double?)(root["youtube"] as JsonObject)?["durationSeconds"] is > 0 and var y ? y : (double?)(root["download"] as JsonObject)?["durationSeconds"] is > 0 and var d ? d : null;
}
