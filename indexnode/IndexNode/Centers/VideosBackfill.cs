using System.Text;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.2 "indexnode videos backfill [--dry-run]": builds index/videos/&lt;id&gt;/video.json for every upload that has none, from download.zip's idx_summary (or downloaded.json), info.json, precheck.json, transcribe.zip and the AI probe on centers.zip.
/// Create-only (If-None-Match: a video.json that exists, even one a claim wrote meanwhile, is never touched). A video under an active claim (a live lease in jobs/active/) is skipped and reported: its claim writes the descriptor.
/// </summary>
public static class VideosBackfill
{
    public sealed record Report(int Videos, int Have, int Created, int Claimed, int Raced, List<string> ClaimedIds, List<string> Failed);

    public static async Task<int> RunAsync(Args a)
    {
        var sub = a.Positional.FirstOrDefault();
        if (sub != "backfill") throw new ArgumentException("videos backfill [--dry-run]");
        var (_, store, paths, http) = Commands.Open(a); using var _h = http;
        bool dry = a.Has("dry-run");
        var rep = await BackfillAsync(store, paths, dry, new PreCheckSettings().AiFlag, a.Get("threads") is { } t ? int.Parse(t) : 4, Console.WriteLine, default);
        Console.WriteLine($"{rep.Videos} upload(s): {rep.Have} already have video.json, {(dry ? "would create" : "created")} {rep.Created}, {rep.Claimed} skipped (under an active claim: its claim writes it){(rep.Raced > 0 ? $", {rep.Raced} written meanwhile (left alone)" : "")}{(dry ? " (dry run: nothing written)" : "")}");
        foreach (var id in rep.ClaimedIds.Take(20)) Console.WriteLine($"  claimed: {id}");
        foreach (var f in rep.Failed.Take(20)) Console.WriteLine($"  FAILED: {f}");
        return rep.Failed.Count > 0 ? 1 : 0;
    }

    public static async Task<Report> BackfillAsync(IStore store, Paths paths, bool dryRun, double aiFlag, int threads, Action<string>? say, CancellationToken ct)
    {
        var pre = paths.Prefix + "videos/";
        var byId = new SortedDictionary<string, Dictionary<string, BlobEntry>>(StringComparer.Ordinal);
        foreach (var b in await store.ListAsync(pre, false, true, ct))
        {
            var rest = b.Name[pre.Length..]; int slash = rest.IndexOf('/'); if (slash <= 0) continue;
            var f = rest[(slash + 1)..]; if (f.Contains('/')) continue;
            if (!byId.TryGetValue(rest[..slash], out var d)) byId[rest[..slash]] = d = new(StringComparer.Ordinal);
            d[f] = b;
        }
        // subjects under a claim: a live lease (or a file too young to have one) in jobs/active/
        var claimed = new HashSet<string>(StringComparer.Ordinal); var now = DateTimeOffset.UtcNow;
        foreach (var b in await store.ListAsync(paths.ActiveDir, false, false, ct))
            if (b.LeaseState == "leased" || b.Created is { } c && now - c < JobQueue.NewActiveGrace) claimed.Add(Path.GetFileNameWithoutExtension(b.Name));
        int videos = 0, have = 0, created = 0, nClaimed = 0, raced = 0; var claimedIds = new List<string>(); var failed = new List<string>(); int done = 0;
        var work = new List<(string Id, Dictionary<string, BlobEntry> Files)>();
        foreach (var (id, files) in byId)
        {
            if (!files.ContainsKey("video.mp4") && !files.ContainsKey("download.zip") && !files.ContainsKey("downloaded.json") && !files.ContainsKey("precheck.json")) continue;   // not an upload (comments only, say)
            videos++;
            if (files.ContainsKey("video.json")) { have++; continue; }
            if (claimed.Contains(id)) { nClaimed++; claimedIds.Add(id); continue; }
            work.Add((id, files));
        }
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, async (w, t) =>
        {
            try
            {
                var root = await BuildAsync(store, paths, w.Id, w.Files, aiFlag, t);
                if (dryRun) { Interlocked.Increment(ref created); return; }
                if (await store.PutIfMatchAsync(VideoJson.Name(paths, w.Id), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(root), null, t)) Interlocked.Increment(ref created);   // create-only
                else Interlocked.Increment(ref raced);
            }
            catch (Exception e) when (e is not OperationCanceledException) { lock (failed) failed.Add($"{w.Id}: {e.Message}"); }
            int d = Interlocked.Increment(ref done); if (d % 100 == 0) say?.Invoke($"  backfill: {d} of {work.Count}");
        });
        failed.Sort(StringComparer.Ordinal);
        return new Report(videos, have, created, nClaimed, raced, claimedIds, failed);
    }

    /// <summary>video.json of one upload from what is stored. Throws when nothing says anything about it (no duration anywhere): the upload is reported, not given an empty descriptor.</summary>
    public static async Task<JsonObject> BuildAsync(IStore store, Paths paths, string id, Dictionary<string, BlobEntry> files, double aiFlag, CancellationToken ct)
    {
        async Task<JsonObject?> J(string f) => files.ContainsKey(f) && await store.GetAsync(paths.Video(id, f), ct) is { } c ? JsonNode.Parse(c.Data) as JsonObject ?? throw new InvalidDataException($"{paths.Video(id, f)} is not a JSON object") : null;
        var root = new JsonObject { ["v"] = VideoJson.Version, ["id"] = id };
        var info = await J("info.json");
        JsonObject? dl = null;
        if (files.GetValueOrDefault("download.zip")?.Metadata.GetValueOrDefault("idx_summary") is { Length: > 2 } sum && JsonNode.Parse(sum) is JsonObject sj && sj.Count > 0) dl = sj;
        dl ??= await J("downloaded.json");
        var pc = await J("precheck.json");
        double? dur = (double?)dl?["durationSeconds"] is > 0 and var dd ? dd : (double?)pc?["video"]?["duration"] is > 0 and var pd ? pd : null;
        root["youtube"] = VideoJson.YoutubeFrom(info, id, (string?)pc?["youtube"]?["url"], dur);
        if (pc?["video"] is JsonObject pv)   // precheck.json knew the title/channel when info.json is gone
        {
            var y = (JsonObject)root["youtube"]!;
            foreach (var (k, pk) in new[] { ("title", "title"), ("channel", "channel"), ("channelId", "channelId"), ("uploadDate", "uploadDate") }) if (y[k] == null && (string?)pv[pk] is { } s) y[k] = s;
        }
        if (dl != null) root["download"] = dl.DeepClone();
        if (pc != null) root["precheck"] = pc.DeepClone();
        var (lang, dubbed) = await LegacySpeechAsync(store, paths, id, ct);
        if (lang != null) VideoJson.SetSpeech(root, lang, dubbed, "transcribe");
        else if ((string?)pc?["speech"]?["speechLanguage"] is { Length: > 0 } pl) VideoJson.SetSpeech(root, pl, (bool?)pc?["speech"]?["dubbed"], "precheck");
        if ((double?)pc?["ai"]?["score"] is { } ps) VideoJson.SetAi(root, ps, (bool?)pc?["ai"]?["flag"] ?? ps >= aiFlag, "precheck");
        else if (files.ContainsKey("centers.zip") && await CatalogBuild.AiScoreAsync(store, paths, id, ct) is { } cs) VideoJson.SetAi(root, cs, cs >= aiFlag, "centers");
        if (VideoJson.Duration(root) == null) throw new InvalidOperationException("no duration in download.zip's idx_summary, downloaded.json, info.json or precheck.json");
        return root;
    }

    /// <summary>The one remaining reader of transcribe.zip (backfill only): the upload's language and dubbed from its asr member, by a few range reads. Null/null when there is no transcript.</summary>
    public static async Task<(string? Language, bool? Dubbed)> LegacySpeechAsync(IStore raw, Paths paths, string id, CancellationToken ct)
    {
        var blob = paths.Video(id, "transcribe.zip"); var h = await raw.HeadAsync(blob, ct); if (h == null) return (null, null);
        var src = new BlobZipSource(raw, blob, h.Size, h.ETag);
        var es = await ZipFormat.ReadDirectoryAsync(src, ct, 2048);
        var e = es.FirstOrDefault(x => x.Name == "asr/" + Transcribe.ModelName + ".zh.json"); if (e == null) return (null, null);
        var lh = await src.ReadAsync(e.LocalOffset, 30, ct); if (lh.Length < 30 || BitConverter.ToUInt32(lh, 0) != 0x04034b50) return (null, null);
        long start = e.LocalOffset + 30 + BitConverter.ToUInt16(lh, 26) + BitConverter.ToUInt16(lh, 28);
        var raw0 = await src.ReadAsync(start, (int)Math.Min(e.CompressedSize, 4096), ct); string head;
        if (e.Method == 0) head = Encoding.UTF8.GetString(raw0);
        else
        {
            using var ds = new System.IO.Compression.DeflateStream(new MemoryStream(raw0), System.IO.Compression.CompressionMode.Decompress); var o = new MemoryStream(); var b = new byte[1024];
            try { int n; while (o.Length < 2048 && (n = ds.Read(b, 0, b.Length)) > 0) o.Write(b, 0, n); } catch (InvalidDataException) { }   // the slice ends mid-stream
            head = Encoding.UTF8.GetString(o.ToArray());
        }
        int seg = head.IndexOf("\"segments\"", StringComparison.Ordinal); if (seg >= 0) head = head[..seg];
        var lm = System.Text.RegularExpressions.Regex.Match(head, "\"language\"\\s*:\\s*\"([^\"]*)\""); var dm = System.Text.RegularExpressions.Regex.Match(head, "\"dubbed\"\\s*:\\s*(true|false)");
        return (lm.Success && lm.Groups[1].Value.Length > 0 ? lm.Groups[1].Value : null, dm.Success ? dm.Groups[1].Value == "true" : null);
    }
}
