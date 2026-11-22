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
        if (sub == "aggregate") return await VideosAggregateCommand.RunAsync(a);
        if (sub != "backfill") throw new ArgumentException("videos backfill [--dry-run] | videos aggregate [--dry-run]");
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

    /// <summary>video.json of one upload from what is stored. With no duration anywhere (idx_summary, downloaded.json, info.json, precheck, the same files inside download.zip, ffprobe) the duration is recorded as unknown (youtube.durationSeconds null, durationSource "unknown"), not a failure.</summary>
    public static async Task<JsonObject> BuildAsync(IStore store, Paths paths, string id, Dictionary<string, BlobEntry> files, double aiFlag, CancellationToken ct)
    {
        async Task<JsonObject?> J(string f) => files.ContainsKey(f) && await store.GetAsync(paths.Video(id, f), ct) is { } c ? JsonNode.Parse(c.Data) as JsonObject ?? throw new InvalidDataException($"{paths.Video(id, f)} is not a JSON object") : null;
        var root = new JsonObject { ["v"] = VideoJson.Version, ["id"] = id };
        var info = await J("info.json");
        JsonObject? dl = null;
        if (files.GetValueOrDefault("download.zip")?.Metadata.GetValueOrDefault("idx_summary") is { Length: > 2 } sum && JsonNode.Parse(sum) is JsonObject sj && sj.Count > 0) dl = sj;
        dl ??= await J("downloaded.json");
        var pc = await J("precheck.json");
        string? durSource = null;
        double? dur = (double?)dl?["durationSeconds"] is > 0 and var dd ? dd : null;
        if (dur != null) durSource = files.ContainsKey("downloaded.json") ? "downloaded.json" : "idx_summary";
        if (dur == null && (double?)pc?["video"]?["duration"] is > 0 and var pd) { dur = pd; durSource = "precheck"; }
        if (dur == null && (double?)info?["duration"] is > 0 and var idur) { dur = idur; durSource = "info.json"; }
        // 0.13.3: idx_summary is "{}" when the metadata overflowed 8 KB: read downloaded.json / info.json inside download.zip (ranged reads), then ffprobe the stored video's header
        if (dur == null && files.ContainsKey("download.zip"))
        {
            var (zd, zsrc) = await ZipDurationAsync(store, paths, id, ct);
            if (zd != null) { dur = zd; durSource = zsrc; }
        }
        if (dur == null && files.ContainsKey("video.mp4") && await ProbeStoredVideoAsync(store, paths, id, ct) is { } fd) { dur = fd; durSource = "ffprobe"; }
        root["youtube"] = VideoJson.YoutubeFrom(info, id, (string?)pc?["youtube"]?["url"], dur);
        { var y0 = (JsonObject)root["youtube"]!; if ((double?)y0["durationSeconds"] is not > 0) { y0["durationSeconds"] = null; y0["durationSource"] = "unknown"; } else y0["durationSource"] = (double?)info?["duration"] is > 0 ? "info.json" : durSource; }   // no source knows it: recorded as unknown, never a failure
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
        return root;
    }

    /// <summary>0.13.3: the duration from downloaded.json or info.json INSIDE download.zip, by ranged reads (directory at the end, then only that member). Null when neither is there or says one.</summary>
    public static async Task<(double? Duration, string? Source)> ZipDurationAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        try
        {
            var blob = paths.Video(id, "download.zip"); var h = await ZipOpen.HeadReadableAsync(store, blob, ct, mustBeZip: false); if (h == null || h.Size < ZipOpen.MinZip) return (null, null);   // 0.13.5: a ghost is hydrated first
            var src = new BlobZipSource(store, blob, h.Size, h.ETag, h.Metadata.GetValueOrDefault("idx_sha256"));
            var es = await ZipFormat.ReadDirectoryAsync(src, ct, 4096);
            foreach (var (name, key) in new[] { ("downloaded.json", "durationSeconds"), ("info.json", "duration") })
            {
                var e = es.FirstOrDefault(x => x.Name == name); if (e == null || e.Size > 64 * 1024 * 1024) continue;
                var j = JsonNode.Parse(await ZipFormat.ReadMemberAsync(src, e, ct)) as JsonObject;
                if ((double?)j?[key] is > 0 and var d) return (d, "download.zip:" + name);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"backfill: {id}: download.zip not readable for a duration ({e.Message})"); }
        return (null, null);
    }

    /// <summary>0.13.3: last resort: ffprobe on the first bytes of the stored video.mp4 (a ranged read, never the whole blob). Null when ffprobe is missing or the header holds no duration.</summary>
    public static async Task<double?> ProbeStoredVideoAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        string? tmp = null;
        try
        {
            var blob = paths.Video(id, "video.mp4"); var h = await store.HeadAsync(blob, ct); if (h == null || h.Size == 0) return null;
            var r = await GhostSafeRead.RangeAsync(store, new BlobPin(blob, h.ETag, h.Size), 0, (int)Math.Min(h.Size, 32 * 1024 * 1024), ct); if (r.Data == null) return null;
            tmp = Path.Combine(Path.GetTempPath(), "indexnode-probe-" + Guid.NewGuid().ToString("N") + ".mp4"); await File.WriteAllBytesAsync(tmp, r.Data, ct);
            var psi = new System.Diagnostics.ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var x in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", tmp }) psi.ArgumentList.Add(x);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var o = await p.StandardOutput.ReadToEndAsync(ct); await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 && double.TryParse(o.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
        finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
    }

    /// <summary>0.13.3: builds and creates video.json for one upload (create-only), for the holder of its lease (a process claim that finds none). Returns the document.</summary>
    public static async Task<JsonObject> EnsureAsync(IStore store, Paths paths, string id, double aiFlag, CancellationToken ct)
    {
        var existing = await VideoJson.ReadAsync(store, paths, id, ct); if (existing != null) return existing.Root;
        var pre = paths.Video(id, ""); var files = new Dictionary<string, BlobEntry>(StringComparer.Ordinal);
        foreach (var b in await store.ListAsync(pre, false, true, ct)) { var f = b.Name[pre.Length..]; if (f.Length > 0 && !f.Contains('/')) files[f] = b; }
        var root = await BuildAsync(store, paths, id, files, aiFlag, ct);
        if (!await store.PutIfMatchAsync(VideoJson.Name(paths, id), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(root), null, ct)) return (await VideoJson.RequireAsync(store, paths, id, ct)).Root;   // written meanwhile
        return root;
    }

    /// <summary>The one remaining reader of transcribe.zip (backfill only): the upload's language and dubbed from its asr member, by a few range reads. Null/null when there is no transcript.</summary>
    public static async Task<(string? Language, bool? Dubbed)> LegacySpeechAsync(IStore raw, Paths paths, string id, CancellationToken ct)
    {
        var blob = paths.Video(id, "transcribe.zip"); var h = await ZipOpen.HeadReadableAsync(raw, blob, ct, mustBeZip: false); if (h == null || h.Size < ZipOpen.MinZip) return (null, null);
        var src = new BlobZipSource(raw, blob, h.Size, h.ETag, h.Metadata.GetValueOrDefault("idx_sha256"));
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
