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
        var vj = await VideoJson.RequireAsync(store, paths, id, ct);   // 0.13.2: the record lives in video.json (precheck.json is not written any more); a missing descriptor fails the claim, loudly
        if (vj.Root["precheck"] is JsonObject pr)
        {   // a repeat (the claim was interrupted after the record): a duplicate is finished again, anything else just goes on
            if (PreCheckDecision.IsSkip((string?)pr["decision"])) { await FinishDuplicateAsync(ctx, pr, null, ct); }
            return;
        }

        var run = await AnalyzeAsync(ctx, null, ct, strict: true);
        if (run == null) return;
        await RecordAsync(store, paths, id, run.Record, ct);
        if (PreCheckDecision.IsSkip(run.Decision.Decision)) { await FinishDuplicateAsync(ctx, run.Record, run.Summary, ct); return; }
        // an AI-flagged upload is not skipped (0.13.1): the engine holds it until control aiDramas is on (the record says ai.flag)
        // anything else: the summary is kept in downloaded.json's cache copy only when the zip is re-committed by a later stage; download.zip itself is not touched
    }

    /// <summary>Writes the pre-check record, and the speech and AI facts it measured, into video.json (this claim holds the video's lease; conditional on the ETag read, a 412 reads and merges again).</summary>
    public static Task RecordAsync(IStore store, Paths paths, string id, JsonObject rec, CancellationToken ct) => VideoJson.UpdateAsync(store, paths, id, root =>
    {
        root["precheck"] = JsonNode.Parse(rec.ToJsonString());
        var sp = rec["speech"] as JsonObject; var ai = rec["ai"] as JsonObject;
        if ((string?)sp?["speechLanguage"] is { Length: > 0 } lang) VideoJson.SetSpeech(root, lang, (bool?)sp?["dubbed"], "precheck");
        if ((double?)ai?["score"] is { } score) VideoJson.SetAi(root, score, (bool?)ai?["flag"], "precheck");
    }, ct);

    /// <summary>What one pre-check found (no store write).</summary>
    public sealed record Run(PreCheckDecision Decision, JsonObject Record, JsonObject Summary, PreCheckAnalysis Analysis, bool Ai);

    /// <summary>The analysis on the stored video, with no write of any kind. Null when the pre-check is skipped (no video, no index, no model, a failed analysis: the drama is processed). keepSample: a folder the cut window is copied into.</summary>
    public async Task<Run?> AnalyzeAsync(JobContext ctx, string? keepSample, CancellationToken ct, bool strict = false)
    {
        // strict (the node's process claim): a pre-check that cannot run fails the claim loudly instead of silently going on to frames
        Run? Skip(string why) => strict ? throw new InvalidOperationException($"pre-check could not run for {ctx.Job.JobId}: {why}") : null;
        var store = ctx.Store; var paths = ctx.Paths; var id = ctx.Job.JobId; var s = settings;
        var videoBlob = paths.Video(id, "video.mp4");
        if (await store.HeadAsync(videoBlob, ct) == null) { Log.Info($"process {id}: pre-check skipped (no video.mp4 stored)"); return null; }
        if (await analyzer.UnavailableAsync(s, ct) is { } unavailable) { Log.Info($"process {id}: pre-check skipped ({unavailable})"); return Skip(unavailable); }
        var infoBlob = await store.GetAsync(paths.Video(id, "info.json"), ct);
        var info = infoBlob == null ? new JsonObject() : JsonNode.Parse(infoBlob.Data) as JsonObject ?? new JsonObject();
        var vroot = (await VideoJson.RequireAsync(store, paths, id, ct)).Root;   // 0.13.2: the download facts are video.json's
        var downloaded = vroot["download"] as JsonObject ?? new JsonObject();
        var work = Path.Combine(Path.GetTempPath(), "indexnode", "pc-" + id + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ctx.Report("precheck", "fetch");
            var video = Path.Combine(work, "video.mp4");
            await store.DownloadToFileAsync(videoBlob, video, ct);
            double duration = VideoJson.Duration(vroot) ?? (double?)info["duration"] ?? 0;
            if (duration <= 0) duration = await Frames.DurationAsync(tools, video, ct);
            if (duration <= 0) { Log.Info($"process {id}: pre-check skipped (no duration)"); return Skip("no duration"); }
            var wins = PreCheck.Windows(duration, s.WindowSeconds, s.WindowPositions);
            var segs = new List<SampleSegment>(); var tcut = System.Diagnostics.Stopwatch.StartNew();
            for (int k = 0; k < wins.Count; k++)
            {
                var (ws, we, wp) = wins[k]; string clip = video;
                if (!(wins.Count == 1 && ws <= 0 && we >= duration))
                {
                    ctx.Report("precheck", $"sample {ws:0}-{we:0}s");
                    clip = Path.Combine(work, $"sample{k}.mp4");
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var r = await Proc.RunAsync(await tools.FfmpegAsync(ct), ["-hide_banner", "-loglevel", "error", "-y", "-ss", ws.ToString(inv), "-i", video, "-t", (we - ws).ToString(inv), "-c", "copy", clip], ct);
                    if (r.ExitCode != 0 || !File.Exists(clip)) { Log.Info($"process {id}: pre-check skipped (could not cut the segment: {r.Stderr.Trim()})"); return Skip("could not cut the segment: " + r.Stderr.Trim()); }
                }
                segs.Add(new SampleSegment(clip, ws, we, wp));
            }
            double cutSeconds = tcut.Elapsed.TotalSeconds;
            var (start, end) = (segs[0].Start, segs[^1].End);
            if (keepSample != null) { Directory.CreateDirectory(keepSample); for (int k = 0; k < segs.Count; k++) File.Copy(segs[k].Path, Path.Combine(keepSample, segs.Count == 1 ? "sample.mp4" : $"sample{k}.mp4"), true); }
            PreCheckAnalysis analysis;
            try { analysis = await analyzer.AnalyzeSegmentsAsync(segs, s, Math.Max(1, ctx.ThreadShare()), p => ctx.Report("precheck", p), ct); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"process {id}: pre-check skipped (analysis failed: {e.Message})"); return Skip("analysis failed: " + e.Message); }
            if (analysis.SkippedReason != null) { Log.Info($"process {id}: pre-check skipped ({analysis.SkippedReason})"); return Skip(analysis.SkippedReason); }
            analysis = analysis with { Group = ProposeGroup(analysis, segs, s) };
            var decision = PreCheck.Decide(analysis, duration, _ => null, s);
            // the language ID runs only when it can change the decision: an authoritative skip whose group copies are all dubs (the canonical exception); the audio is the first segment's
            if (PreCheck.SpeechNeeded(analysis, decision))
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var speech = await analyzer.SpeechAsync(segs[0].Path, s, Math.Max(1, ctx.ThreadShare()), ct);
                var sec = new Dictionary<string, double>(analysis.Seconds) { ["speech"] = sw2.Elapsed.TotalSeconds };
                analysis = analysis with { Speech = speech, Seconds = sec };
                decision = PreCheck.Decide(analysis, duration, _ => null, s);
            }
            { var sd = new Dictionary<string, double>(analysis.Seconds) { ["cut"] = cutSeconds }; if (!sd.ContainsKey("speech")) sd["speechSkipped"] = 1; analysis = analysis with { Seconds = sd }; }
            bool ai = analysis.AiScore >= s.AiFlag;
            if (PreCheckSettings.AiThresholdWarning(s, analysis.Frames) is { } warn) Log.Info($"process {id}: WARNING {warn}");
            var rec = PreCheck.Record(id, decision, analysis, info, DownloadedFormats.FromInfo(info), (start, end), duration, s, ctx.NodeId, version, ai, 0, !PreCheckDecision.IsSkip(decision.Decision));
            rec["youtube"] = new JsonObject { ["id"] = id, ["url"] = ctx.Job.Url ?? (string?)info["webpage_url"] ?? "https://www.youtube.com/watch?v=" + id };
            rec["seconds"]!["total"] = Math.Round(sw.Elapsed.TotalSeconds, 2); rec["threads"] = Math.Max(1, ctx.ThreadShare()); rec["source"] = "stored-video";
            Log.Info($"process {id}: pre-check {decision.Decision} ({string.Join("; ", decision.Reasons)}), ai score {analysis.AiScore:0.000}{(ai ? " (flag)" : "")}, {sw.Elapsed.TotalSeconds:0.0}s");
            analysis = analysis with { Seconds = new Dictionary<string, double>(analysis.Seconds) { ["total"] = Math.Round(sw.Elapsed.TotalSeconds, 2) } };
            return new Run(decision, rec, PreCheck.Summary(decision, analysis, ai, s), analysis, ai);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>0.13.15: the group proposal over the two segments: the frame index runs through the segments in order; each frame's own time is its place in its segment.</summary>
    public static GroupProposal ProposeGroup(PreCheckAnalysis a, IReadOnlyList<SampleSegment> segs, PreCheckSettings s)
    {
        var sg = a.Segments; if (sg == null || sg.Count == 0) return DramaGroups.Propose(a, segs[0].Start, segs[0].End - segs[0].Start, s);
        var from = new List<int>(); int at = 0; foreach (var x in sg) { from.Add(at); at += x.Frames; }
        double Own(int frame) { int k = from.FindLastIndex(f => f <= frame); if (k < 0) k = 0; return sg[k].Start + (frame - from[k] + 0.5) / Math.Max(1, sg[k].Frames) * (sg[k].End - sg[k].Start); }
        return DramaGroups.Propose(a, segs[0].Start, segs[0].End - segs[0].Start, s, Own);
    }

    /// <summary>The skip: nothing is deleted or rewritten. The exception carries what the done line records.</summary>
    private Task FinishDuplicateAsync(JobContext ctx, JsonObject rec, JsonObject? summary, CancellationToken ct)
    {
        var top = (rec["top"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var upload = (string?)rec["matchUpload"] ?? (string?)top?["upload"] ?? "?"; var share = (double?)top?["share"] ?? 0;
        var sp = rec["speech"] as JsonObject;
        var why = string.Join("; ", (rec["reasons"] as JsonArray)?.Select(r => r?.ToString()) ?? []);
        Log.Info($"process {ctx.Job.JobId}: pre-check skip (duplicate of {upload}): download.zip and the video are left as they are, marked for 'indexnode trash empty'");
        throw new DuplicateFound(upload, share, (string?)sp?["speechLanguage"], (bool?)sp?["dubbed"], (string?)sp?["languageSource"], (string?)sp?["metadataLanguage"], ctx.Job.Url) { Why = why, Group = (string?)rec["groupId"], GroupOffset = (double?)rec["groupOffset"], MatchShare = (double?)rec["matchShare"], DupShare = (double?)rec["dupShare"], Mixed = (bool?)(rec["group"] as JsonObject)?["mixed"] is true ? true : null, AiScore = (double?)(rec["ai"] as JsonObject)?["score"] };
    }
}
