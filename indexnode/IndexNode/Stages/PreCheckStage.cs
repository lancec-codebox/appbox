using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.0: the pre-check is the first step of a `process` claim (a drama with no processing done yet). The 0.12.0 analysis exactly as built (<see cref="PreCheckAnalyzer"/>, <see cref="PreCheck.Decide"/>), but on the stored video in region:
/// the same window (precheck.window-seconds centred at precheck.window-position) is cut out of video.mp4 with ffmpeg (stream copy) instead of being downloaded from YouTube; frames, SSCD lookup, AI score and the SenseVoice language ID are unchanged.
/// A skip (a visual copy; an AI upload; a non-Chinese dub of a drama we hold in Chinese. A Chinese upload of a dub-only drama is not a skip: it is canonical and processed) changes NOTHING in the store: download.zip, the video and the metadata stay as they are.
/// It writes precheck.json (a new file) and throws <see cref="DuplicateFound"/>; the engine's metadata-only done line carries the decision, the reason and `trash: download.zip/video` (a mark for deletion that only `indexnode trash empty` ever acts on).
/// Anything else records precheck.json and the claim goes on to frames. A missing index or model, or an analysis that fails, skips the pre-check (the drama is processed).
/// </summary>
public sealed class PreCheckStage(Tools tools, IPreCheckAnalyzer analyzer, PreCheckSettings settings, string version) : IStageHandler
{
    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token; var store = ctx.Store; var paths = ctx.Paths; var id = ctx.Job.JobId; var s = settings;
        if (!s.Enabled) return;
        var videoBlob = paths.Video(id, "video.mp4");
        if (await store.GetAsync(paths.Video(id, "precheck.json"), ct) is { } prior)
        {   // a repeat (the claim was interrupted after the record): a duplicate is finished again, anything else just goes on
            var pr = JsonNode.Parse(prior.Data) as JsonObject;
            if ((string?)pr?["decision"] == PreCheckDecision.Duplicate) { await FinishDuplicateAsync(ctx, pr!, null, ct); }
            return;
        }

        var run = await AnalyzeAsync(ctx, null, ct);
        if (run == null) return;
        await store.PutAsync(paths.Video(id, "precheck.json"), Encoding.UTF8.GetBytes(run.Record.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), null, null, false, ct);
        if (run.Decision.Decision == PreCheckDecision.Duplicate) { await FinishDuplicateAsync(ctx, run.Record, run.Summary, ct); return; }
        // anything else: the summary is kept in downloaded.json's cache copy only when the zip is re-committed by a later stage; download.zip itself is not touched
    }

    /// <summary>What one pre-check found (no store write).</summary>
    public sealed record Run(PreCheckDecision Decision, JsonObject Record, JsonObject Summary, PreCheckAnalysis Analysis, bool Ai);

    /// <summary>The analysis on the stored video, with no write of any kind. Null when the pre-check is skipped (no video, no index, no model, a failed analysis: the drama is processed). keepSample: a folder the cut window is copied into.</summary>
    public async Task<Run?> AnalyzeAsync(JobContext ctx, string? keepSample, CancellationToken ct)
    {
        var store = ctx.Store; var paths = ctx.Paths; var id = ctx.Job.JobId; var s = settings;
        var videoBlob = paths.Video(id, "video.mp4");
        if (await store.HeadAsync(videoBlob, ct) == null) { Log.Info($"process {id}: pre-check skipped (no video.mp4 stored)"); return null; }
        if (await analyzer.UnavailableAsync(s, ct) is { } unavailable) { Log.Info($"process {id}: pre-check skipped ({unavailable})"); return null; }
        var infoBlob = await store.GetAsync(paths.Video(id, "info.json"), ct);
        var info = infoBlob == null ? new JsonObject() : JsonNode.Parse(infoBlob.Data) as JsonObject ?? new JsonObject();
        var downloaded = await store.GetAsync(paths.Video(id, "downloaded.json"), ct) is { } d0 ? JsonNode.Parse(d0.Data) as JsonObject ?? new JsonObject() : new JsonObject();
        var work = Path.Combine(Path.GetTempPath(), "indexnode", "pc-" + id + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ctx.Report("precheck", "fetch");
            var video = Path.Combine(work, "video.mp4");
            await store.DownloadToFileAsync(videoBlob, video, ct);
            double duration = (double?)downloaded["durationSeconds"] ?? (double?)info["duration"] ?? 0;
            if (duration <= 0) duration = await Frames.DurationAsync(tools, video, ct);
            if (duration <= 0) { Log.Info($"process {id}: pre-check skipped (no duration)"); return null; }
            var (start, end, whole) = PreCheck.Window(duration, s.WindowSeconds, s.WindowPosition);
            string sample = video;
            if (!whole)
            {
                ctx.Report("precheck", $"sample {start:0}-{end:0}s");
                sample = Path.Combine(work, "sample.mp4");
                var r = await Proc.RunAsync(await tools.FfmpegAsync(ct), ["-hide_banner", "-loglevel", "error", "-y", "-ss", start.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", video, "-t", (end - start).ToString(System.Globalization.CultureInfo.InvariantCulture), "-c", "copy", sample], ct);
                if (r.ExitCode != 0 || !File.Exists(sample)) { Log.Info($"process {id}: pre-check skipped (could not cut the window: {r.Stderr.Trim()})"); return null; }
            }
            if (keepSample != null) { Directory.CreateDirectory(keepSample); File.Copy(sample, Path.Combine(keepSample, "sample.mp4"), true); }
            PreCheckAnalysis analysis;
            try { analysis = await analyzer.AnalyzeAsync(sample, s, Math.Max(1, ctx.ThreadShare()), p => ctx.Report("precheck", p), ct); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"process {id}: pre-check skipped (analysis failed: {e.Message})"); return null; }
            if (analysis.SkippedReason != null) { Log.Info($"process {id}: pre-check skipped ({analysis.SkippedReason})"); return null; }
            var known = new Dictionary<string, double?>();
            foreach (var m in analysis.Top.Take(5))
            {
                double? d = null;
                try { if (await store.GetAsync(paths.Video(m.Upload, "downloaded.json"), ct) is { } c) d = (double?)JsonNode.Parse(c.Data)?["durationSeconds"]; } catch (Exception e) when (e is not OperationCanceledException) { }
                known[m.Upload] = d;
            }
            var decision = PreCheck.Decide(analysis, duration, u => known.GetValueOrDefault(u), s);
            bool ai = analysis.AiScore >= s.AiFlag;
            if (PreCheckSettings.AiThresholdWarning(s, analysis.Frames) is { } warn) Log.Info($"process {id}: WARNING {warn}");
            var rec = PreCheck.Record(id, decision, analysis, info, DownloadedFormats.FromInfo(info), (start, end), duration, s, ctx.NodeId, version, ai, 0, decision.Decision != PreCheckDecision.Duplicate);
            rec["youtube"] = new JsonObject { ["id"] = id, ["url"] = ctx.Job.Url ?? (string?)info["webpage_url"] ?? "https://www.youtube.com/watch?v=" + id };
            rec["seconds"]!["total"] = Math.Round(sw.Elapsed.TotalSeconds, 2); rec["threads"] = Math.Max(1, ctx.ThreadShare()); rec["source"] = "stored-video";
            Log.Info($"process {id}: pre-check {decision.Decision} ({string.Join("; ", decision.Reasons)}), ai score {analysis.AiScore:0.000}{(ai ? " (flag)" : "")}, {sw.Elapsed.TotalSeconds:0.0}s");
            return new Run(decision, rec, PreCheck.Summary(decision, analysis, ai, s), analysis, ai);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>The skip: nothing is deleted or rewritten. The exception carries what the done line records.</summary>
    private Task FinishDuplicateAsync(JobContext ctx, JsonObject rec, JsonObject? summary, CancellationToken ct)
    {
        var top = (rec["top"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var upload = (string?)rec["matchUpload"] ?? (string?)top?["upload"] ?? "?"; var share = (double?)top?["share"] ?? 0;
        var sp = rec["speech"] as JsonObject;
        var why = string.Join("; ", (rec["reasons"] as JsonArray)?.Select(r => r?.ToString()) ?? []);
        Log.Info($"process {ctx.Job.JobId}: pre-check skip (duplicate of {upload}): download.zip and the video are left as they are, marked for 'indexnode trash empty'");
        throw new DuplicateFound(upload, share, (string?)sp?["speechLanguage"], (bool?)sp?["dubbed"], (string?)sp?["languageSource"], (string?)sp?["metadataLanguage"], ctx.Job.Url) { Why = why, AiScore = (double?)(rec["ai"] as JsonObject)?["score"] };
    }
}
