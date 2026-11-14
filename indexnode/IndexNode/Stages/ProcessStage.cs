using System.Text.Json;

namespace IndexNode;

public sealed class ProcessStage(Tools tools, RestStore store, NodeConfig config, HttpClient http) : IStageHandler
{
    /// <summary>Set by `run` from flag > environment > config; null or unconfigured means AI steps are skipped.</summary>
    public static AiConfig? Ai { get; set; }

    /// <summary>`indexnode process --job`: redo the listed steps of an already processed job (their existing outputs are replaced; processed.json and the video are left alone).</summary>
    public bool Redo { get; init; }

    /// <summary>Steps that need a model. None exist yet (merging subtitle text and transcript, naming the cast and checking doubtful OCR lines come later); each will be listed here.</summary>
    public static readonly string[] AiSteps = ["describe"];

    /// <summary>The AI steps this run cannot do for lack of an endpoint; they go under "skipped" in processed.json, they do not fail the job.</summary>
    public static string[] SkippedAiSteps(AiConfig? ai) => ai is { Configured: true } ? [] : AiSteps;

    public static readonly string[] KnownSteps = ["frames", "sscd", "clip", "transcribe", "ocrsubs", "compress", "cast", "describe", "sync"];

    /// <summary>The outputs that make a video fully processed, per step: the node that writes the last one also writes processed.json.</summary>
    static readonly (string Step, string File)[] Outputs = [("frames", "frames/manifest.json"), ("sscd", "sscd/manifest.json"), ("clip", "clip/manifest.json"), ("transcribe", "asr/" + Transcribe.ModelName + ".zh.json"), ("ocrsubs", "ocr/lines.json"), ("sync", "sync.json"), ("cast", "cast/clusters.json"), ("describe", "describe.json")];

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
        bool stepJob = Protocol.IsStep(job.Stage);
        var steps = stepJob ? [job.Stage] : job.Steps is { Length: > 0 } ? job.Steps : Protocol.DefaultSteps;
        var supported = SupportedSteps(config);
        var unsupported = steps.Where(s => !supported.Contains(s)).ToArray();
        if (unsupported.Length > 0)
        {
            Log.Info($"process {id}: this node cannot run step(s) {string.Join(',', unsupported)}: skipping the job, released without an attempt");
            throw new JobSkipped("unsupported step: " + string.Join(',', unsupported));
        }
        if (!stepJob && job.Stage == "process" && !steps.Contains("compress") && !Redo)
        {
            // compatibility path: a legacy process job fans out into step jobs (only where they do not exist yet) and is done
            var made = new List<string>();
            foreach (var step in Protocol.StepStages.Where(x => steps.Contains(x) || x is "sync" or "describe" or "cast"))
            {
                var sj = JobDescriptor.NewStep(step, id);
                if (await ctx.Store.PutAsync(paths.Job(step, id), sj.ToBytes(), Tags.Job(step, "pending", sj.AddedAt), null, true, ct)) made.Add(step);
            }
            Log.Info($"process {id}: fanned out into step jobs ({(made.Count == 0 ? "all exist" : string.Join(',', made))})");
            return;
        }
        var compress = job.Compress ?? new CompressSettings();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var listing = (await store.ListAsync(paths.VideoDir(id), false, false, ct)).ToDictionary(b => b.Name, b => b.Size);
        bool Has(string file) => !Redo && listing.ContainsKey(paths.Video(id, file));
        bool HasAny(string file) => listing.ContainsKey(paths.Video(id, file));
        if (Has("processed.json") && !Redo && !stepJob) return;
        var downloaded = await store.GetAsync(paths.Video(id, "downloaded.json"), ct)
            ?? throw new InvalidOperationException($"{id} has no downloaded.json yet");
        double duration = JsonDocument.Parse(downloaded.Data).RootElement.TryGetProperty("durationSeconds", out var dEl) ? dEl.GetDouble() : 0;

        var need = new Dictionary<string, bool>
        {
            ["frames"] = steps.Contains("frames") && !Has("frames/manifest.json"),
            ["sscd"] = steps.Contains("sscd") && !Has("sscd/manifest.json"),
            ["clip"] = steps.Contains("clip") && !Has("clip/manifest.json"),
            ["transcribe"] = steps.Contains("transcribe") && !Has("asr/" + Transcribe.ModelName + ".zh.json"),
            ["ocrsubs"] = steps.Contains("ocrsubs") && !Has("ocr/lines.json"),
            ["compress"] = steps.Contains("compress") && !Has("compact.mp4"),
            ["cast"] = steps.Contains("cast") && !Has("cast/clusters.json"),
            ["describe"] = steps.Contains("describe") && !Has("describe.json") && Ai is { Configured: true },
            ["sync"] = stepJob && job.Stage == "sync",
        };
        // sscd, clip and cast read the stored frames/keyframes chunks; only the frames step (and transcribe, ocrsubs, compress, the cast credits) decode the video
        bool needVideo = need["frames"] || need["transcribe"] || need["ocrsubs"] || need["compress"] || need["cast"];

        var work = Path.Combine(Path.GetTempPath(), "indexnode", "p-" + id);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            var videoBlob = paths.Video(id, "video.mp4");
            if (needVideo && !HasAny("video.mp4") && HasAny("compact.mp4")) { videoBlob = paths.Video(id, "compact.mp4"); Log.Info($"process {id}: video.mp4 is gone, working from compact.mp4"); }
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

            int frameCount = 0;
            if (need["frames"])
            {
                ctx.Report("frames", null);
                int segs = ctx.ThreadShare();
                var r = await Frames.ProduceAsync(tools, video, work, FrameSettings.From(config), segs, duration, p => ctx.Report("frames", p), (name, bytes, type) => ctx.Store.PutAsync(paths.Video(id, name), bytes, null, null, false, ct, type), ct);
                frameCount = r.Kept;
                Log.Info($"process {id}: {r.Kept} frames kept of {r.Sampled} sampled ({r.FrameBytes / 1048576.0:F1} MB AV1) and {r.Keyframes} keyframes ({r.KeyframeBytes / 1048576.0:F1} MB all-intra AV1)");
            }
            // the stored frames, fetched (and hydrated) once for the steps that embed them
            ChunkDir? frameSet = null;
            async Task<ChunkDir> FrameSet() => frameSet ??= await FrameChunks.DownloadAsync(store, paths, id, "frames", Path.Combine(work, "framechunks"), ct) ?? throw new InvalidOperationException($"{id} has no frames/ chunks yet (run the frames step)");
            async Task<string> FramesDir() { var d = Path.Combine(work, "frames"); if (!Directory.Exists(d)) await (await FrameSet()).ExtractAsync(tools, d, ct); return d; }
            if (need["sscd"])
            {
                ctx.Report("sscd", null);
                var model = await Sscd.EnsureModelAsync(config, http, ct);
                var fs = await FrameSet();
                var bytes = await Sscd.DescribeAsync(tools, model, await FramesDir(), fs.Count, ctx.ThreadShare(), p => ctx.Report("sscd", p), ct);
                await FrameChunks.PutVectorsAsync(ctx.Store, paths, id, "sscd", fs, bytes, Sscd.Dim, new { model = Sscd.ModelId(model), inputSize = Sscd.ShortSide, prune = new { cosine = FrameSettings.From(config).SscdCosine, lookback = FrameSettings.From(config).LookbackSeconds } }, ct);
                // second pruning pass: SSCD cosine over the same look-back window; the pruned frames stay stored, sscd/keep.json and the spans say which stand for which
                var fset = FrameSettings.From(config);
                var (keep, rep) = FrameChunks.SscdPass(fs.Times, bytes, Sscd.Dim, fset.SscdCosine, fset.LookbackSeconds);
                await FrameChunks.ApplySscdPassAsync(ctx.Store, paths, id, fs, rep, keep, fset.SscdCosine, fset.LookbackSeconds, ct);
                Log.Info($"process {id}: sscd pass kept {keep.Count} of {rep.Length} frames");
            }
            if (need["clip"])
            {
                ctx.Report("clip", null);
                var vision = await Models.EnsureAsync(Models.ClipVision, http, ct);
                var fs = await FrameSet();
                var bytes = await Clip.EmbedFramesAsync(tools, vision, await FramesDir(), fs.Count, ctx.ThreadShare(), p => ctx.Report("clip", p), ct);
                await FrameChunks.PutVectorsAsync(ctx.Store, paths, id, "clip", fs, bytes, Clip.Dim, System.Text.Json.Nodes.JsonNode.Parse(Clip.InfoJson())!, ct);
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
            if (need["cast"])
            {
                ctx.Report("cast", null);
                var castDir = Path.Combine(work, "castout");
                var kfs = await FrameChunks.DownloadAsync(store, paths, id, "keyframes", Path.Combine(work, "kfchunks"), ct) ?? throw new InvalidOperationException($"{id} has no keyframes/ chunks yet (run the frames step)");
                var msg = await CastStage.RunAsync(tools, http, video, duration, castDir, new CastStage.Options(config.CastModel ?? "buffalo_l", ctx.ThreadShare()), s => ctx.Report("cast", s), ct, null, kfs);
                Log.Info($"process {id}: cast {msg}");
                foreach (var f in Directory.GetFiles(Path.Combine(castDir, "cast")))
                    await ctx.Store.PutAsync(paths.Video(id, "cast/" + Path.GetFileName(f)), await File.ReadAllBytesAsync(f, ct), null, null, false, ct, f.EndsWith(".jpg") ? "image/jpeg" : f.EndsWith(".f16") ? "application/octet-stream" : "application/json");
            }
            if (need["describe"])
            {
                ctx.Report("describe", null);
                var d = await DescribeJob.GatherAsync(new StoreJob(store, paths, id), Path.Combine(work, "describe"), ct);
                var bytes = await DescribeJob.RunAsync(new AiClient(http, Ai!), Ai, d, ct);
                if (bytes != null) await ctx.Store.PutAsync(paths.Video(id, "describe.json"), bytes, null, null, false, ct);
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

            if (stepJob)
            {
                // the node that finishes the last step writes processed.json
                var after = (await store.ListAsync(paths.VideoDir(id), false, false, ct)).Select(b => b.Name).ToHashSet();
                bool aiOff = !after.Contains(paths.Video(id, "describe.json")) && Ai is not { Configured: true };
                if (Outputs.Any(o => !(o.Step == "describe" && aiOff) && !after.Contains(paths.Video(id, o.File)))) { Log.Info($"process {id}: step {job.Stage} done"); return; }
                steps = Protocol.DefaultSteps.Concat(["cast", "sync", "describe"]).ToArray();
                Log.Info($"process {id}: step {job.Stage} was the last one");
            }
            if (frameCount == 0 && steps.Contains("frames") && await store.GetAsync(paths.Video(id, "frames/manifest.json"), ct) is { } fm)
                frameCount = JsonDocument.Parse(fm.Data).RootElement.GetProperty("count").GetInt32();
            // speech language and OCR cue count come from the outputs, whether this run made them or an earlier one did
            string? speechLanguage = null; bool? dubbed = null; int? ocrCues = null;
            if (!stepJob && steps.Contains("transcribe") && await store.GetAsync(paths.Video(id, "asr/" + Transcribe.ModelName + ".zh.json"), ct) is { } asr)
            {
                var root = JsonDocument.Parse(asr.Data).RootElement;
                speechLanguage = root.TryGetProperty("language", out var l) ? l.GetString() : null;
                dubbed = root.TryGetProperty("dubbed", out var d) && d.ValueKind == JsonValueKind.True;
            }
            if (!stepJob && steps.Contains("ocrsubs") && await store.GetAsync(paths.Video(id, "ocr/lines.json"), ct) is { } ocr)
                ocrCues = JsonDocument.Parse(ocr.Data).RootElement.GetProperty("zh").GetArrayLength();
            // sync.json, once transcribe and ocrsubs have both run for the job
            double? avOffset = null; bool? avSuspect = null;
            if ((steps.Contains("transcribe") && steps.Contains("ocrsubs")) || need["sync"])
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
                v = 1, steps, frames = frameCount, model = Sscd.ModelId(config.ModelPath ?? ""), asrModel = steps.Contains("transcribe") ? Transcribe.ModelName : null,
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
