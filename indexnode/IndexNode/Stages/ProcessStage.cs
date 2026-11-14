using System.Text.Json;

namespace IndexNode;

public sealed class ProcessStage(Tools tools, RestStore store, NodeConfig config, HttpClient http) : IStageHandler
{
    /// <summary>Set by `run` from flag > environment > config; null or unconfigured means AI steps are skipped.</summary>
    public static AiConfig? Ai { get; set; }

    /// <summary>Steps that need a model. None exist yet (merging subtitle text and transcript, naming the cast and checking doubtful OCR lines come later); each will be listed here.</summary>
    public static readonly string[] AiSteps = [];

    /// <summary>The AI steps this run cannot do for lack of an endpoint; they go under "skipped" in processed.json, they do not fail the job.</summary>
    public static string[] SkippedAiSteps(AiConfig? ai) => ai is { Configured: true } ? [] : AiSteps;

    public static readonly string[] KnownSteps = ["frames", "sscd", "clip", "transcribe", "ocrsubs", "compress"];

    /// <summary>The steps this node can run: the ones without models always, the model steps when their files are installed or may be downloaded.</summary>
    public static string[] SupportedSteps(NodeConfig cfg, string? modelsDir = null) =>
        KnownSteps.Where(s => !Models.ByStep.ContainsKey(s) || cfg.ModelsAutoDownload || Models.HasStep(s, modelsDir)).ToArray();

    /// <summary>A node never claims a job that needs a step it cannot run.</summary>
    public static bool CanRun(JobDescriptor job, IReadOnlyCollection<string> supported)
    {
        var steps = job.Steps is { Length: > 0 } ? job.Steps : Protocol.DefaultSteps;
        return steps.All(supported.Contains);
    }

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token;
        var job = ctx.Job; var id = job.JobId; var paths = ctx.Paths;
        var steps = job.Steps is { Length: > 0 } ? job.Steps : Protocol.DefaultSteps;
        var supported = SupportedSteps(config);
        var unsupported = steps.Where(s => !supported.Contains(s)).ToArray();
        if (unsupported.Length > 0)
        {
            Log.Info($"process {id}: this node cannot run step(s) {string.Join(',', unsupported)}: skipping the job, released without an attempt");
            throw new JobSkipped("unsupported step: " + string.Join(',', unsupported));
        }
        var compress = job.Compress ?? new CompressSettings();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var listing = (await store.ListAsync(paths.VideoDir(id), false, false, ct)).ToDictionary(b => b.Name, b => b.Size);
        bool Has(string file) => listing.ContainsKey(paths.Video(id, file));
        if (Has("processed.json")) return;
        var downloaded = await store.GetAsync(paths.Video(id, "downloaded.json"), ct)
            ?? throw new InvalidOperationException($"{id} has no downloaded.json yet");
        double duration = JsonDocument.Parse(downloaded.Data).RootElement.TryGetProperty("durationSeconds", out var dEl) ? dEl.GetDouble() : 0;

        var need = new Dictionary<string, bool>
        {
            ["frames"] = steps.Contains("frames") && !Has("frames.json"),
            ["sscd"] = steps.Contains("sscd") && !Has("sscd.f16"),
            ["clip"] = steps.Contains("clip") && !Has("clip.f16"),
            ["transcribe"] = steps.Contains("transcribe") && !Has("asr/" + Transcribe.ModelName + ".zh.json"),
            ["ocrsubs"] = steps.Contains("ocrsubs") && !Has("ocr/lines.json"),
            ["compress"] = steps.Contains("compress") && !Has("compact.mp4"),
        };
        // sscd and clip work on the extracted frames, so they are (re)extracted locally when only those are missing
        bool extractFrames = need["frames"] || need["sscd"] || need["clip"];
        bool needVideo = extractFrames || need["transcribe"] || need["ocrsubs"] || need["compress"];

        var work = Path.Combine(Path.GetTempPath(), "indexnode", "p-" + id);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            var videoBlob = paths.Video(id, "video.mp4");
            var video = Path.Combine(work, "video.mp4");
            long originalBytes = 0;
            if (needVideo)
            {
                ctx.Report("fetch", null);
                await NexisAccess.PrepareAsync(store, videoBlob, ct);
                using var keep = NexisAccess.KeepTouched(store, videoBlob);
                await store.DownloadToFileAsync(videoBlob, video, ct);
                originalBytes = new FileInfo(video).Length;
                if (duration <= 0) duration = await Frames.DurationAsync(tools, video, ct);
            }
            else originalBytes = listing.GetValueOrDefault(videoBlob);

            var framesDir = Path.Combine(work, "frames");
            List<FrameEntry>? frames = null;
            if (extractFrames)
            {
                ctx.Report("frames", null);
                int segs = ctx.ThreadShare();
                frames = await Frames.ExtractAsync(tools, video, work, framesDir, segs, duration, p => ctx.Report("frames", p), ct);
                if (need["frames"]) await ctx.Store.PutAsync(paths.Video(id, "frames.json"), Frames.ToJson(frames), null, null, false, ct);
                Log.Info($"process {id}: {frames.Count} frames in {segs} segment(s)");
            }
            if (need["sscd"])
            {
                ctx.Report("sscd", null);
                var model = await Sscd.EnsureModelAsync(config, http, ct);
                var bytes = await Sscd.DescribeAsync(tools, model, framesDir, frames!.Count, ctx.ThreadShare(), p => ctx.Report("sscd", p), ct);
                await ctx.Store.PutAsync(paths.Video(id, "sscd.f16"), bytes, null, null, false, ct, "application/octet-stream");
            }
            if (need["clip"])
            {
                ctx.Report("clip", null);
                var vision = await Models.EnsureAsync(Models.ClipVision, http, ct);
                var bytes = await Clip.EmbedFramesAsync(tools, vision, framesDir, frames!.Count, ctx.ThreadShare(), p => ctx.Report("clip", p), ct);
                await ctx.Store.PutAsync(paths.Video(id, "clip.f16"), bytes, null, null, false, ct, "application/octet-stream");
                await ctx.Store.PutAsync(paths.Video(id, "clip.json"), Clip.InfoJson(), null, null, false, ct);
            }
            if (need["transcribe"])
            {
                ctx.Report("transcribe", null);
                var model = await Models.EnsureAsync(Models.AsrModel, http, ct);
                var tokens = await Models.EnsureAsync(Models.AsrTokens, http, ct);
                var vad = await Models.EnsureAsync(Models.Vad, http, ct);
                var t0 = watch.Elapsed.TotalSeconds;
                var audio = await Transcribe.ReadAudioAsync(tools, video, 0, 0, ct);
                var segs = Transcribe.Run(model, tokens, vad, audio, ctx.ThreadShare(), p => ctx.Report("transcribe", p), ct);
                var (lang, isDubbed) = Transcribe.Dubbing(segs);
                await ctx.Store.PutAsync(paths.Video(id, "asr/" + Transcribe.ModelName + ".zh.vtt"), System.Text.Encoding.UTF8.GetBytes(Transcribe.Vtt(segs)), null, null, false, ct, "text/vtt");
                await ctx.Store.PutAsync(paths.Video(id, "asr/" + Transcribe.ModelName + ".zh.json"), Transcribe.ToJson(segs, lang, isDubbed, audio.Length / (double)Transcribe.SampleRate, watch.Elapsed.TotalSeconds - t0), null, null, false, ct);
            }
            if (need["ocrsubs"])
            {
                ctx.Report("ocrsubs", null);
                var det = await Models.EnsureAsync(Models.OcrDet, http, ct);
                var rec = await Models.EnsureAsync(Models.OcrRec, http, ct);
                var dict = await Models.EnsureAsync(Models.OcrDict, http, ct);
                var (w, h) = await OcrSubs.SizeAsync(tools, video, ct);
                int workers = Math.Max(1, ctx.ThreadShare());
                var band = await OcrSubs.FindBandAsync(tools, det, rec, dict, video, w, h, 0, duration, workers, p => ctx.Report("ocrsubs", p), ct);
                OcrResult res;
                if (band == null)
                {
                    Log.Info($"process {id}: no subtitle band found: no burned-in subtitles");
                    res = new OcrResult([], [], 0, 0, w, 0, 0);
                }
                else
                {
                    Log.Info($"process {id}: subtitle band y={band.Value.Y} h={band.Value.H}");
                    res = await OcrSubs.ReadAsync(tools, video, det, rec, dict, w, h, band.Value.Y, band.Value.H, 0, duration, workers, p => ctx.Report("ocrsubs", p), ct);
                }
                var enc = System.Text.Encoding.UTF8;
                await ctx.Store.PutAsync(paths.Video(id, "ocr/subs.zh.vtt"), enc.GetBytes(OcrCues.ToVtt(res.Zh, false)), null, null, false, ct, "text/vtt");
                await ctx.Store.PutAsync(paths.Video(id, "ocr/subs.zh-Hans.vtt"), enc.GetBytes(OcrCues.ToVtt(res.Zh, true)), null, null, false, ct, "text/vtt");
                await ctx.Store.PutAsync(paths.Video(id, "ocr/lines.json"), OcrCues.ToJson(res.Zh, res.En, res.BandY, res.BandH, res.Width), null, null, false, ct);
                if (res.En.Count > 0) await ctx.Store.PutAsync(paths.Video(id, "ocr/subs.en.vtt"), enc.GetBytes(OcrCues.ToVtt(res.En, false)), null, null, false, ct, "text/vtt");
            }
            long compactBytes = 0;
            if (need["compress"])
            {
                ctx.Report("compress", null);
                var compact = Path.Combine(work, "compact.mp4");
                await Compress.RunAsync(tools, video, compact, compress, ctx.ThreadShare(), duration, p => ctx.Report("compress", p), ct);
                compactBytes = new FileInfo(compact).Length;
                ctx.Report("compress", "uploading");
                await store.PutFileAsync(paths.Video(id, "compact.mp4"), compact, "video/mp4", null, ct);
            }
            else if (steps.Contains("compress")) compactBytes = listing.GetValueOrDefault(paths.Video(id, "compact.mp4"));

            int frameCount = frames?.Count ?? 0;
            if (frames == null && steps.Contains("frames"))
            {
                var fj = await store.GetAsync(paths.Video(id, "frames.json"), ct);
                if (fj != null) frameCount = JsonDocument.Parse(fj.Data).RootElement.GetArrayLength();
            }
            // speech language and OCR cue count come from the outputs, whether this run made them or an earlier one did
            string? speechLanguage = null; bool? dubbed = null; int? ocrCues = null;
            if (steps.Contains("transcribe") && await store.GetAsync(paths.Video(id, "asr/" + Transcribe.ModelName + ".zh.json"), ct) is { } asr)
            {
                var root = JsonDocument.Parse(asr.Data).RootElement;
                speechLanguage = root.TryGetProperty("language", out var l) ? l.GetString() : null;
                dubbed = root.TryGetProperty("dubbed", out var d) && d.ValueKind == JsonValueKind.True;
            }
            if (steps.Contains("ocrsubs") && await store.GetAsync(paths.Video(id, "ocr/lines.json"), ct) is { } ocr)
                ocrCues = JsonDocument.Parse(ocr.Data).RootElement.GetProperty("zh").GetArrayLength();
            // sync.json, once transcribe and ocrsubs have both run for the job
            double? avOffset = null; bool? avSuspect = null;
            if (steps.Contains("transcribe") && steps.Contains("ocrsubs"))
            {
                var lines = await store.GetAsync(paths.Video(id, "ocr/lines.json"), ct);
                var asrFile = await store.GetAsync(paths.Video(id, "asr/" + Transcribe.ModelName + ".zh.json"), ct);
                if (lines != null && asrFile != null && AvSync.FromFiles(lines.Data, asrFile.Data) is { } sync)
                {
                    await ctx.Store.PutAsync(paths.Video(id, "sync.json"), AvSync.ToJson(sync), null, null, false, ct);
                    avOffset = sync.Offset; avSuspect = sync.Suspect;
                    Log.Info($"process {id}: av sync offset {sync.Offset:+0.00;-0.00} s, drift {sync.Drift:0.00} s{(sync.Suspect ? " (SUSPECT)" : "")}");
                }
            }
            var marker = JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1, steps, frames = frameCount, model = Sscd.ModelName, asrModel = steps.Contains("transcribe") ? Transcribe.ModelName : null,
                speechLanguage, dubbed, ocrCues, avOffset, avSuspect, originalBytes, compactBytes, skipped = SkippedAiSteps(Ai),
                nodeId = ctx.NodeId, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1), at = Protocol.NowUtc(),
            });
            await ctx.Store.PutAsync(paths.Video(id, "processed.json"), marker, null, null, false, ct);

            if (!compress.KeepOriginal && steps.Contains("compress") && (need["compress"] || compactBytes > 0))
            {
                await store.DeleteAsync(videoBlob, CancellationToken.None);
                Log.Info($"process {id}: deleted video.mp4 (keepOriginal=false)");
            }
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("INDEXNODE_KEEP_WORK") == null)
            {
                try { Directory.Delete(work, true); } catch { }
            }
            else Log.Info($"process {id}: kept work folder {work}");
        }
    }
}
