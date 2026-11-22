using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.16: the `sample` stage (downloader nodes only: it needs YouTube). Two 5-minute segments at 0.30 and 0.70 of the duration at 360p with one yt-dlp call per section, run in parallel,,
/// the audio of the first segment (copied out of it, no second download), and the info, description and thumbnail; uploaded under videos/&lt;id&gt;/sample/. Then it adds the `check` entry.
/// The files are loose blobs (never download.zip members: download.zip still means "the full video is stored").
/// </summary>
public sealed class SampleStage(ITools tools, IStore store, DownloadGate gate, PreCheckSettings settings) : IStageHandler
{
    public const string Dir = "sample/";
    /// <summary>0.13.21: the inline check (config check.url); null = the check entry is added as before.</summary>
    public InlineCheckClient? Inline { get; init; }

    /// <summary>What one sample download produced; the workspace holds the files until it is disposed.</summary>
    public sealed class Result : IAsyncDisposable
    {
        public required IToolWorkspace Work { get; init; }
        public required double Duration { get; init; }
        public required List<(double Start, double End, double Position)> Windows { get; init; }
        public required List<string> Segments { get; init; }   // workspace-relative names: sample0.mp4, sample1.mp4
        public string? Audio { get; init; }
        public JsonObject? Info { get; init; }
        /// <summary>The yt-dlp format id(s) the sections came in (e.g. 18; split streams show as 396+233).</summary>
        public string? Format { get; init; }
        public required Dictionary<string, double> Seconds { get; init; }
        public ValueTask DisposeAsync() => Work.DisposeAsync();
    }

    /// <summary>The download itself (no store, no queue): info first (its duration places the windows), then the sections call, then the audio and the metadata files.</summary>
    public async Task<Result> FetchAsync(string id, string url, bool useGate, Action<string?, string?> report, CancellationToken ct)
    {
        var s = settings; var sec = new Dictionary<string, double>(); var sw = System.Diagnostics.Stopwatch.StartNew();
        if (useGate) { await gate.WaitTurnAsync(ct); sec["gate"] = sw.Elapsed.TotalSeconds; sw.Restart(); }
        var work = await tools.CreateWorkspaceAsync("sample-" + id, ct);
        try
        {
            var common = new List<string> { "--no-playlist", "--retries", "3", "--no-warnings", "--newline" };
            report("sample", "info");
            var meta = new List<string>(common) { "--skip-download", "--write-info-json", "--write-thumbnail", "--convert-thumbnails", "jpg", "-o", "video.%(ext)s", url };
            var mr = await tools.RunAsync(work, "yt-dlp", meta, ct);
            if (mr.ExitCode != 0) YtDlp.Throw(mr.Stderr);
            sec["info"] = sw.Elapsed.TotalSeconds; sw.Restart();
            JsonObject? info = null; double duration = 0;
            if (await work.ExistsAsync("video.info.json", ct))
            {
                info = JsonNode.Parse(await work.ReadTextAsync("video.info.json", ct))!.AsObject();
                await work.WriteTextAsync("description.txt", (string?)info["description"] ?? "", ct);
                info.Remove("comments");
                duration = (double?)info["duration"] ?? 0;
            }
            if (duration <= 0) throw new InvalidOperationException("yt-dlp gave no duration: the sample windows cannot be placed");
            var wins = PreCheck.Windows(duration, s.WindowSeconds, s.WindowPositions);
            report("sample", "download");
            // one yt-dlp call per section, run at the same time: the two-segment sample takes about one section's time. The file name carries the section's start, so the two never collide
            var formats = new System.Collections.Concurrent.ConcurrentBag<string>();
            async Task Section((double Start, double End, double Position) w)
            {
                var args = new List<string>(common) { "-f", PreCheck.SampleFormat(s), "--download-sections", PreCheck.SectionArg(w.Start, w.End), "--no-simulate", "--print", "after_move:FORMAT=%(format_id)s", "--merge-output-format", "mp4", "-o", "sec-%(section_start)s.%(ext)s", url };
                var r = await tools.RunAsync(work, "yt-dlp", args, ct, onStdout: line =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"\[download\]\s+(\d+(\.\d+)?)%"); if (m.Success) report("sample", m.Groups[1].Value + "%");
                    if (line.StartsWith("FORMAT=", StringComparison.Ordinal)) formats.Add(line[7..].Trim());
                });
                if (r.ExitCode != 0) YtDlp.Throw(r.Stderr);
            }
            await Task.WhenAll(wins.Select(Section));
            var format = string.Join(",", formats.Distinct().OrderBy(x => x, StringComparer.Ordinal));
            sec["sections"] = sw.Elapsed.TotalSeconds; sw.Restart();
            var files = (await work.ListAsync("", "sec-*.mp4", ct)).OrderBy(f => ParseStart(f)).ToList();
            if (files.Count != wins.Count) throw new InvalidOperationException($"yt-dlp wrote {files.Count} section file(s), {wins.Count} expected ({string.Join(", ", files)})");
            var names = new List<string>();
            for (int k = 0; k < files.Count; k++)
            {
                var to = $"sample{k}.mp4"; var from = await work.LocalPathAsync(files[k], ct);
                File.Move(from, Path.Combine(Path.GetDirectoryName(from)!, to), true); names.Add(to);
            }
            // the first segment's audio, copied out (no re-encode; the container is Matroska audio so any codec fits)
            string? audio = null;
            var ff = await tools.RunAsync(work, "ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-i", names[0], "-vn", "-c:a", "copy", "audio.mka"], ct);
            if (ff.ExitCode == 0 && await work.ExistsAsync("audio.mka", ct)) audio = "audio.mka"; else Log.Info($"sample {id}: no separate audio file ({ff.Stderr.Trim()}); the check uses the first segment");
            sec["audio"] = sw.Elapsed.TotalSeconds;
            return new Result { Work = work, Duration = duration, Windows = wins, Segments = names, Audio = audio, Info = info, Format = format.Length > 0 ? format : null, Seconds = sec };
        }
        catch { await work.DisposeAsync(); throw; }
    }

    static double ParseStart(string f)
    {
        var n = Path.GetFileNameWithoutExtension(f); var i = n.IndexOf('-');
        return i >= 0 && double.TryParse(n[(i + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>The sample.json marker: what the check needs to place the files (and a repeat run to know the upload finished).</summary>
    public static JsonObject Marker(Result r, string nodeId) => new()
    {
        ["v"] = 1, ["duration"] = Math.Round(r.Duration, 2), ["nodeId"] = nodeId, ["at"] = Protocol.NowUtc(),
        ["segments"] = new JsonArray(r.Windows.Select((w, k) => (JsonNode)new JsonObject { ["file"] = r.Segments[k], ["start"] = w.Start, ["end"] = w.End, ["position"] = w.Position }).ToArray()),
        ["audio"] = r.Audio, ["format"] = r.Format,
    };

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token; var job = ctx.Job; var id = job.JobId; var paths = ctx.Paths;
        if (await ctx.Store.HeadAsync(paths.Video(id, Dir + "sample.json"), ct) == null)
        {
            if (string.IsNullOrEmpty(job.Url)) throw new PermanentFailure("job has no url");
            await using var res = await FetchAsync(id, job.Url, true, ctx.Report, ct);
            Traffic.Current.Add(Traffic.YoutubeIn, await res.Work.TotalBytesAsync(ct));
            ctx.Report("upload", "0%");
            for (int k = 0; k < res.Segments.Count; k++) await store.PutFileAsync(paths.Video(id, Dir + res.Segments[k]), await res.Work.LocalPathAsync(res.Segments[k], ct), "video/mp4", null, ct);
            if (res.Audio != null) await store.PutFileAsync(paths.Video(id, Dir + res.Audio), await res.Work.LocalPathAsync(res.Audio, ct), "audio/x-matroska", null, ct);
            if (res.Info != null) await store.PutAsync(paths.Video(id, Dir + "info.json"), JsonSerializer.SerializeToUtf8Bytes(res.Info), null, null, false, ct, "application/json");
            if (await res.Work.ExistsAsync("description.txt", ct)) await store.PutFileAsync(paths.Video(id, Dir + "description.txt"), await res.Work.LocalPathAsync("description.txt", ct), "text/plain; charset=utf-8", null, ct);
            if (await res.Work.ExistsAsync("video.jpg", ct)) await store.PutFileAsync(paths.Video(id, Dir + "thumb.jpg"), await res.Work.LocalPathAsync("video.jpg", ct), "image/jpeg", null, ct);
            // video.json (the check writes the pre-check record into it): created here by the holder of the video's lease
            await VideoJson.UpdateAsync(ctx.Store, paths, id, root => { if (root["youtube"] == null) root["youtube"] = VideoJson.YoutubeFrom(res.Info, id, job.Url, res.Duration); root["sample"] = Marker(res, ctx.NodeId); }, ct);
            await store.PutAsync(paths.Video(id, Dir + "sample.json"), JsonSerializer.SerializeToUtf8Bytes(Marker(res, ctx.NodeId)), null, null, false, ct, "application/json");   // last: its presence says the sample is complete
            Log.Info($"sample {id}: {res.Segments.Count} segment(s) uploaded ({string.Join(", ", res.Seconds.Select(x => x.Key + " " + x.Value.ToString("0.0", CultureInfo.InvariantCulture) + "s"))})");
        }
        if (Inline != null && await TryInlineAsync(ctx)) return;
        await AddCheck(ctx);
    }

    /// <summary>The inline check: true when the serve instance answered and its record and the finished check entry are in (the engine appends them with the sample's done line, in ONE append); false: fall back to the check entry.</summary>
    async Task<bool> TryInlineAsync(JobContext ctx)
    {
        var id = ctx.Job.JobId;
        if (await Inline!.TryAsync(id, ctx.Token) is not { } ans) return false;
        try { await InlineCheckClient.RecordAsync(ctx.Store, ctx.Paths, id, ans.Record, ctx.Token); }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"sample {id}: inline check record not written ({e.Message}); adding the check entry"); return false; }
        InlineCheckClient.AddOutcome(ctx, ans.Record);
        return true;
    }

    /// <summary>The follow-up: a `check` entry at the sample's priority (create-only).</summary>
    public static async Task AddCheck(JobContext ctx)
    {
        var id = ctx.Job.JobId;
        var c = new JobDescriptor { Stage = "check", JobId = id, Url = ctx.Job.Url ?? "https://www.youtube.com/watch?v=" + id, MaxHeight = ctx.Job.MaxHeight, Format = ctx.Job.Format, Subs = ctx.Job.Subs, MaxComments = ctx.Job.MaxComments, Priority = ctx.Job.Priority, AddedAt = Protocol.NowUtc() };
        await PubTime.StampAsync(ctx.Store, ctx.Paths, id, ctx.Job, c, ctx.Token);
        if (!ctx.FollowUps.Any(l => l.T == "add" && l.Stage == "check")) ctx.FollowUps.Add(QLine.Add(id, "check", c, Protocol.NowUtc()));
    }
}
