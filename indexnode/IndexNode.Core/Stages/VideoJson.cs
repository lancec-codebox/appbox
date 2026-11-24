using System.Text;
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
    /// <summary>Rev: the upload's revision, from the blob metadata x-ms-meta-rev (never the body; a file with none is 0, an older file with a body "rev" counts that).</summary>
    public sealed record Doc(string? ETag, JsonObject Root, long Rev = 0, bool HasRevMeta = false);
    public const string RevMeta = "rev";
    static long RevFrom(IReadOnlyDictionary<string, string>? meta, JsonObject? root) => meta != null && meta.TryGetValue(RevMeta, out var v) && long.TryParse(v, out var r) ? r : VideoAggregate.RevOf(root);

    public static string Name(Paths p, string id) => p.Video(id, "video.json");

    /// <summary>Null when there is no video.json; throws when it is there but not a JSON object (never a quiet null).</summary>
    public static async Task<Doc?> ReadAsync(IStore store, Paths paths, string id, CancellationToken ct) => await ReadAsync(store, paths, id, ct, fresh: false);

    // 0.13.18: video.json is cached on the node by ETag (a conditional GET: 304 costs one request and no bytes, and a read in the same step costs none). Docs are handed out as deep copies, so a caller's edits never leak.
    sealed record Cached(string? ETag, byte[] Data, DateTimeOffset At, IReadOnlyDictionary<string, string>? Meta = null);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStore, System.Collections.Concurrent.ConcurrentDictionary<string, Cached>> caches = new();
    static System.Collections.Concurrent.ConcurrentDictionary<string, Cached> CacheOf(IStore s) => caches.GetValue(StoreIdentity(s), _ => new(StringComparer.Ordinal));
    /// <summary>The store under the wrappers that do not change what a name means (metering, recording, local cache), so a node's lanes share one cache.</summary>
    static IStore StoreIdentity(IStore s) { for (int i = 0; i < 8; i++) s = s switch { MeteredStore m => m.Inner, RecordingStore r => r.Inner, LocalCacheStore l => l.Inner, StageZipStore z => z.Inner, _ => s }; return s; }
    /// <summary>How long a cached video.json is served without asking the store again (the same step reads it many times; a step is short). A write through <see cref="UpdateAsync"/> refreshes the entry.</summary>
    public static TimeSpan Fresh { get; set; } = TimeSpan.FromSeconds(20);
    public static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    public static void ClearCache(IStore store) => CacheOf(store).Clear();

    // 0.13.37: with a media table attached an upload is one point read of its row (cached like the blob: a step reads it many times); the blob answers for an upload with no row yet and for sections too big for a property.
    sealed record TableCached(Doc Doc, DateTimeOffset At);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStore, System.Collections.Concurrent.ConcurrentDictionary<string, TableCached>> tableCaches = new();
    static System.Collections.Concurrent.ConcurrentDictionary<string, TableCached> TableCacheOf(IStore s) => tableCaches.GetValue(StoreIdentity(s), _ => new(StringComparer.Ordinal));

    public static async Task<Doc?> ReadAsync(IStore store, Paths paths, string id, CancellationToken ct, bool fresh)
    {
        if (MediaTables.ForRead(store) is { } table)
        {
            var tc = TableCacheOf(store);
            if (!fresh && tc.TryGetValue(id, out var hit) && Now() - hit.At < Fresh) return Copy(hit.Doc);
            var row = await table.GetAsync(MediaTables.Upload, id, null, ct);
            if (row != null && MediaRecords.IsFull(row))
            {
                var d = new Doc(row.Timestamp, MediaRecords.DocOf(row)); tc[id] = new TableCached(d, Now()); return Copy(d);
            }
            tc.TryRemove(id, out _);
        }
        return await ReadBlobAsync(store, paths, id, ct, fresh);
    }
    static Doc Copy(Doc d) => d with { Root = (JsonObject)d.Root.DeepClone() };

    /// <summary>The per-upload video.json blob itself (cached by ETag). The read-modify-write of <see cref="UpdateAsync"/> and the readers of an upload with no table row use this.</summary>
    public static async Task<Doc?> ReadBlobAsync(IStore store, Paths paths, string id, CancellationToken ct, bool fresh)
    {
        var name = Name(paths, id); var key = name; var cache = CacheOf(store);
        if (!fresh && cache.TryGetValue(key, out var hit) && Now() - hit.At < Fresh) return Parse(name, hit.ETag, hit.Data, hit.Meta);
        if (cache.TryGetValue(key, out var old) && old.ETag != null)
        {
            var r = await store.GetIfChangedAsync(name, old.ETag, ct);
            if (!r.Exists) { cache.TryRemove(key, out _); return null; }
            if (!r.Changed) { cache[key] = old with { At = Now() }; return Parse(name, old.ETag, old.Data, old.Meta); }
            cache[key] = new Cached(r.ETag, r.Data!, Now(), r.Meta); return Parse(name, r.ETag, r.Data!, r.Meta);
        }
        var c = await store.GetAsync(name, ct);
        if (c == null) return null;
        cache[key] = new Cached(c.ETag, c.Data, Now(), c.Meta);
        return Parse(name, c.ETag, c.Data, c.Meta);
    }

    static Doc Parse(string name, string? etag, byte[] data, IReadOnlyDictionary<string, string>? meta = null)
    {
        try { return JsonNode.Parse(data) is JsonObject o ? new Doc(etag, o, RevFrom(meta, o), meta != null && meta.ContainsKey(RevMeta)) : throw new InvalidDataException($"{name} is not a JSON object"); }
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
            var cur = await ReadBlobAsync(store, paths, id, ct, fresh: attempt > 0);
            var root = cur?.Root ?? new JsonObject();
            var before = StretchesOf(root); var original = (JsonObject)root.DeepClone();
            root["v"] = Version; root["id"] = id;
            merge(root);
            long rev = (cur?.Rev ?? 0) + 1;   // monotonic per upload, kept in the blob METADATA (x-ms-meta-rev, set by the same put: no extra op): the If-Match below is what serialises the writers of one upload
            root.Remove("rev");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
            if (await store.PutIfMatchAsync(Name(paths, id), bytes, cur?.ETag, new Dictionary<string, string> { [RevMeta] = rev.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct))
            {
                cache.TryRemove(Name(paths, id), out _);   // the new ETag is not known: the next read asks (once)
                var st = StretchesOf(root);
                if (st != null && st != before && st != "[]") await store.PutAsync(StretchesName(paths, id), Encoding.UTF8.GetBytes(st), null, null, false, ct);
                root["rev"] = rev;   // the aggregate block carries it too (a copy; the per-upload body has none)
                var mine = rev;
                await VideoAggregate.WriteAsync(store, paths, id, rev, root, ct, async (nr, t) =>
                {   // a committed rev >= mine that is not my content: an old node wrote without the metadata. Still my put (its metadata rev is mine)? then raise the metadata to nr and re-put my block at nr
                    var h = await store.HeadAsync(Name(paths, id), t);
                    if (h == null || !h.Metadata.TryGetValue(RevMeta, out var cur) || cur != mine.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
                    await store.SetMetadataAsync(Name(paths, id), new Dictionary<string, string>(h.Metadata, StringComparer.OrdinalIgnoreCase) { [RevMeta] = nr.ToString(System.Globalization.CultureInfo.InvariantCulture) }, null, t);
                    cache.TryRemove(Name(paths, id), out _); mine = nr; root["rev"] = nr; return true;
                });   // dual write: the per-upload file above stays (always), the aggregate follows
                if (MediaTables.For(store) is { } table)
                {   // 0.13.37: the table follows with ONLY the sections this writer changed (and their flat fields): one Merge Entity, no read
                    // docRev: the blob rev this row mirrors IN FULL. Every writer here holds the whole document, so it writes every section (one request, no read) and docRev; a partial row (a stage mark alone, a non-table writer's gap) has no docRev and no reader uses it
                    var changed = MediaRecords.Changed(new JsonObject(), root);
                    var props = MediaRecords.Props(root, changed); props[MediaRecords.DocRev] = rev;
                    await table.InsertOrMergeAsync(MediaTables.Upload, id, props, ct);
                    TableCacheOf(store).TryRemove(id, out _);
                }
                return root;
            }
        }
        throw new IOException($"{id}: video.json kept changing (is another node writing it without the lease?)");
    }

    public static string StretchesName(Paths p, string id) => p.Video(id, "stretches.json");
    public static bool HasStretches(JsonNode? st) => st is JsonArray { Count: > 0 };
    static string? StretchesOf(JsonObject root) => (root["grouping"] as JsonObject)?["stretches"] is JsonNode s ? s.ToJsonString() : null;

    /// <summary>The stretches of one upload's grouping record: stretches.json, else (an upload written before the split) the array inside video.json. Null when it has none.</summary>
    public static async Task<JsonArray?> ReadStretchesAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        if (await store.GetAsync(StretchesName(paths, id), ct) is { } c && JsonNode.Parse(c.Data) is JsonArray a) return a;
        return ((await ReadAsync(store, paths, id, ct))?.Root["grouping"] as JsonObject)?["stretches"] as JsonArray;
    }

    /// <summary>Many uploads at once: the aggregate (one Get Block List + one GET for the whole blob), and only the uploads it does not hold are read one by one. Readers of every upload (the AI checks, the catalog, the search index) use this.</summary>
    public static async Task<Dictionary<string, Doc?>> ReadManyAsync(IStore store, Paths paths, IReadOnlyCollection<string> ids, CancellationToken ct, int threads = 8, bool all = false, IReadOnlyCollection<string>? select = null)
    {
        if (MediaTables.ForRead(store) is { } table)
        {   // 0.13.37: ONE query of the table ($select: just the fields the caller reads); the aggregate is not read. An upload with no row yet is read from its blob (a pre-import upload).
            var rows = await QueryUploadsAsync(table, all ? null : ids, null, select, ct);
            var got = new Dictionary<string, Doc?>(StringComparer.Ordinal);
            foreach (var row in rows.Where(MediaRecords.IsFull)) got[row.RowKey] = new Doc(row.Timestamp, MediaRecords.DocOf(row));
            if (!all) foreach (var id in ids.Distinct(StringComparer.Ordinal).Where(i => !got.ContainsKey(i)).ToList()) { try { got[id] = await ReadBlobAsync(store, paths, id, ct, false); } catch (InvalidDataException) { } }
            return got;
        }
        if (all) { var whole = await VideoAggregate.ReadManyAsync(store, paths, null, ct); return whole == null ? new() : whole.ToDictionary(kv => kv.Key, kv => (Doc?)kv.Value, StringComparer.Ordinal); }
        var res = new System.Collections.Concurrent.ConcurrentDictionary<string, Doc?>(StringComparer.Ordinal);
        var agg = await VideoAggregate.ReadManyAsync(store, paths, ids.Count < 50 ? ids : null, ct);
        foreach (var id in ids) if (agg != null && agg.TryGetValue(id, out var d)) res[id] = d;
        var rest = ids.Where(i => !res.ContainsKey(i)).Distinct(StringComparer.Ordinal).ToList();
        await Parallel.ForEachAsync(rest, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, async (id, t) => { try { res[id] = await ReadAsync(store, paths, id, t); } catch (InvalidDataException) { } });   // an unreadable file is left out: the caller's own read reports it
        return new Dictionary<string, Doc?>(res, StringComparer.Ordinal);
    }

    /// <summary>The upload rows of the table: every one (ids null), or the named ones (one request per 40 ids), optionally only those changed since a server Timestamp (the incremental readers: Timestamp ge).</summary>
    public const int PointReadMax = 25;
    public static async Task<List<TableRow>> QueryUploadsAsync(IMediaTable table, IReadOnlyCollection<string>? ids, DateTimeOffset? since, IReadOnlyCollection<string>? select, CancellationToken ct)
    {
        if (select != null) select = [.. select.Union([MediaRecords.DocRev, MediaRecords.InBlob])];   // IsFull needs both
        string baseF = $"PartitionKey eq '{MediaTables.Upload}'" + (since is { } s ? $" and Timestamp ge datetime'{s.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}'" : "");
        if (ids == null) return await table.QueryAsync(baseF, select, ct);
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        if (since == null && list.Count <= PointReadMax)
        {   // a few uploads: parallel point reads (a RowKey-or filter is a partition scan)
            var got = new System.Collections.Concurrent.ConcurrentBag<TableRow>();
            await Parallel.ForEachAsync(list, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (x, t) => { if (await table.GetAsync(MediaTables.Upload, x, select, t) is { } row) got.Add(row); });
            return got.ToList();
        }
        var want = list.ToHashSet(StringComparer.Ordinal);   // a larger set: the one full query (paged by 1000), kept client-side
        return (await table.QueryAsync(baseF, select, ct)).Where(r => want.Contains(r.RowKey)).ToList();
    }

    /// <summary>One upload for a bulk job: from the job's aggregate view when it holds the upload (one shared GET for the whole job), else the per-upload file (the aggregate lacks it yet).</summary>
    public static async Task<Doc?> ReadViaAsync(VideoAggregate.AggView? view, IStore store, Paths paths, string id, CancellationToken ct)
    {
        if (view != null && view.Has(id) && await view.DocAsync(id, ct) is { } d) return d;
        return await ReadAsync(store, paths, id, ct);
    }

    public static JsonObject Section(JsonObject root, string name) => root[name] as JsonObject ?? (JsonObject)(root[name] = new JsonObject());

    static readonly HashSet<string> YoutubeSkip = new(StringComparer.Ordinal) { "formats", "thumbnails", "http_headers", "heatmap", "subtitles", "automatic_captions", "requested_formats", "requested_downloads", "requested_subtitles", "_format_sort_fields", "description", "title", "channel", "channel_id", "uploader", "upload_date", "duration", "webpage_url", "language", "view_count", "like_count", "availability", "playable_in_embed" };
    public const int DescriptionChars = 20000;

    /// <summary>youtube: the curated yt-dlp metadata (0.13.37): the fields the section always had, the description (up to 20,000 characters), the counts, the availability, the language lists (subtitles, automatic captions, subtitle formats as codes; the audio tracks' languages from the audio-only formats), and every other scalar yt-dlp field under "info". Not kept: formats, thumbnails, http headers, heatmap, caption URL lists. Null fields are left out.</summary>
    public static JsonObject YoutubeFrom(JsonObject? info, string id, string? url, double? duration, string? downloadAudioLanguage = null)
    {
        var y = new JsonObject();
        void Put(string k, JsonNode? v) { if (v != null) y[k] = v; }
        Put("title", (string?)info?["title"]); Put("channel", (string?)info?["channel"] ?? (string?)info?["uploader"]); Put("channelId", (string?)info?["channel_id"]);
        Put("uploadDate", (string?)info?["upload_date"]);
        var d = (double?)info?["duration"] ?? duration; if (d is > 0) y["durationSeconds"] = Math.Round(d.Value, 2);
        y["url"] = (string?)info?["webpage_url"] ?? url ?? "https://www.youtube.com/watch?v=" + id;
        Put("language", (string?)info?["language"]);
        if (info == null) return y;
        Put("uploader", (string?)info["uploader"]);
        if ((string?)info["description"] is { Length: > 0 } desc) y["description"] = desc.Length > DescriptionChars ? desc[..DescriptionChars] : desc;
        if (info["view_count"] is JsonValue vc && vc.TryGetValue<double>(out var vcd)) y["viewCount"] = (long)vcd;
        if (info["like_count"] is JsonValue lc && lc.TryGetValue<double>(out var lcd)) y["likeCount"] = (long)lcd;
        Put("availability", (string?)info["availability"]);
        if (info["playable_in_embed"] is JsonValue pe && pe.TryGetValue<bool>(out var peb)) y["playableInEmbed"] = peb;
        JsonArray Codes(JsonNode? n) => new(((n as JsonObject)?.Select(kv => (JsonNode?)JsonValue.Create(kv.Key)) ?? []).ToArray());
        if (Codes(info["subtitles"]) is { Count: > 0 } sl) y["subtitleLanguages"] = sl;
        if (Codes(info["automatic_captions"]) is { Count: > 0 } al) y["autoCaptionLanguages"] = al;
        var fm = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var n in new[] { info["subtitles"], info["automatic_captions"] }) if (n is JsonObject so) foreach (var kv in so) if (kv.Value is JsonArray tracks) foreach (var tr in tracks) if ((string?)tr?["ext"] is { Length: > 0 } ext) fm.Add(ext);
        if (fm.Count > 0) y["subtitleFormats"] = new JsonArray(fm.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        var audio = (info["formats"] as JsonArray ?? []).OfType<JsonObject>().Where(f => (string?)f["vcodec"] == "none" && (string?)f["acodec"] is { } ac && ac != "none").ToList();
        var langs = audio.Select(f => (string?)f["language"]).Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.Ordinal).ToList();
        if (langs.Count > 0) y["audioLanguages"] = new JsonArray(langs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        var original = audio.FirstOrDefault(f => ((string?)f["format_note"] ?? "").Contains("original", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty((string?)f["language"]));
        var al0 = (string?)original?["language"] ?? (string.IsNullOrEmpty(downloadAudioLanguage) ? null : downloadAudioLanguage) ?? (string?)info["language"];
        if (!string.IsNullOrEmpty(al0)) y["audioLanguage"] = al0;
        var rest = new JsonObject();
        foreach (var kv in info) if (!YoutubeSkip.Contains(kv.Key) && kv.Value != null && !kv.Key.StartsWith('_') && (kv.Value is not JsonObject)) { var text = kv.Value.ToJsonString(); if (text.Length <= 2000) rest[kv.Key] = kv.Value.DeepClone(); }
        if (rest.Count > 0 && rest.ToJsonString().Length < 12000) y["info"] = rest;
        return y;
    }

    public static void SetSpeech(JsonObject root, string? language, bool? dubbed, string source) => root["speech"] = new JsonObject { ["language"] = language, ["dubbed"] = dubbed, ["source"] = source };
    public static void SetAi(JsonObject root, double? score, bool? flag, string source)
    {
        var old = root["ai"] as JsonObject;
        var ai = new JsonObject { ["score"] = score == null ? null : Math.Round(score.Value, 4), ["flag"] = flag, ["source"] = source };
        // 0.13.44: the owner's verdict (ai set real|ai) outlives every probe result
        if (old != null) foreach (var k in new[] { "verdict", "verdictBy", "verdictAt", "verdictReason" }) if (old[k] is { } v) ai[k] = v.DeepClone();
        root["ai"] = ai;
    }

    /// <summary>The duration of the upload: youtube.durationSeconds, else download.durationSeconds; null when the document says neither.</summary>
    public static double? Duration(JsonObject root) => (double?)(root["youtube"] as JsonObject)?["durationSeconds"] is > 0 and var y ? y : (double?)(root["download"] as JsonObject)?["durationSeconds"] is > 0 and var d ? d : null;
}
