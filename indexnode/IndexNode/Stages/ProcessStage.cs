using System.Text.Json;

namespace IndexNode;

public sealed class ProcessStage(Tools tools, IStore store, NodeConfig config, HttpClient http) : IStageHandler
{
    /// <summary>This node's --gpu-fallback; it overrides control.json gpu.hold when deciding whether to write gpu-ready markers.</summary>
    public TimeSpan? GpuFlag { get; init; }
    /// <summary>Set by `run` from flag > environment > config; null or unconfigured means AI steps are skipped.</summary>
    public static AiConfig? Ai { get; set; }

    /// <summary>`indexnode process --job`: redo the listed steps of an already processed job (their existing outputs are replaced; processed.json and the video are left alone).</summary>
    public bool Redo { get; init; }

    /// <summary>The model client for describe and translate; tests put a fake here.</summary>
    public Func<HttpClient, AiConfig, IChat>? Chat { get; init; }

    /// <summary>Steps that need a model. None exist yet (merging subtitle text and transcript, naming the cast and checking doubtful OCR lines come later); each will be listed here.</summary>
    public static readonly string[] AiSteps = ["describe", "translate"];

    /// <summary>The AI steps this run cannot do for lack of an endpoint; they go under "skipped" in processed.json, they do not fail the job.</summary>
    public static string[] SkippedAiSteps(AiConfig? ai) => ai is { Configured: true } ? [] : AiSteps;

    /// <summary>Every step of a fully processed video (sync is computed after transcribe + ocrsubs; clip is dropped).</summary>
    public static readonly string[] FullSteps = ["frames", "sscd", "centers", "thumbs", "transcribe", "ocrsubs", "cast", "describe", "translate"];
    public static readonly string[] KnownSteps = ["frames", "sscd", "centers", "dino", "thumbs", "clip", "transcribe", "ocrsubs", "compress", "cast", "describe", "translate", "sync"];

    /// <summary>The outputs that make a video fully processed, per step: the node that writes the last one also writes processed.json.</summary>
    // owner 2026-11-16: DINOv2 is not part of ingest (duplicates use SSCD), and clip left the default pipeline on 2026-11-14: opt-in steps are NOT listed here, or an upload processed step by step without them would never be marked processed
    public static readonly (string Step, string File)[] Outputs = [("frames", "frames/manifest.json"), ("sscd", "sscd/manifest.json"), ("centers", "centers/manifest.json"), ("thumbs", "thumbs/manifest.json"), ("transcribe", "asr/" + Transcribe.ModelName + ".zh.json"), ("ocrsubs", "ocr/lines.json"), ("sync", "sync.json"), ("cast", "cast/clusters.json"), ("describe", "describe.json"), ("translate", Translate.OutJson)];

    /// <summary>What each step derives, as paths under the video's folder (a trailing / is a folder). Never a source file: video.mp4, info.json, description.txt, thumb.jpg, subs/ (except our own subs/ai/), comments/, downloaded.json. sync.json is not listed: a redo never reruns sync.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Owned = new Dictionary<string, string[]>
    {
        ["frames"] = ["frames/", "keyframes/"], ["sscd"] = ["sscd/"], ["centers"] = ["centers/"], ["dino"] = ["dino/"], ["thumbs"] = ["thumbs/"], ["clip"] = ["clip/"], ["transcribe"] = ["asr/"], ["ocrsubs"] = ["ocr/"],
        ["cast"] = ["cast/"], ["describe"] = ["describe.json"], ["translate"] = ["subs/ai/"], ["compress"] = ["compact.mp4"],
    };

    /// <summary>The old blobs a redone step leaves behind: its own derived files (before the run) that the run did not write again. Empty when the run wrote none of them (nothing was replaced, so nothing is removed).</summary>
    public static string[] Stale(string step, string videoDir, IEnumerable<string> before, IReadOnlyCollection<string> written)
    {
        if (!Owned.TryGetValue(step, out var own)) return [];
        bool Mine(string n) => n.StartsWith(videoDir, StringComparison.Ordinal) && own.Any(o => o.EndsWith('/') ? n.Substring(videoDir.Length).StartsWith(o, StringComparison.Ordinal) : n.Substring(videoDir.Length) == o);
        if (!written.Any(Mine)) return [];
        var w = written.ToHashSet(StringComparer.Ordinal);
        return before.Where(n => Mine(n) && !w.Contains(n)).ToArray();
    }

    /// <summary>The steps this node can run: the ones without models always, the model steps when their files are installed or may be downloaded.</summary>
    public static string[] SupportedSteps(NodeConfig cfg, string? modelsDir = null) =>
        KnownSteps.Where(s => !Models.ByStep.ContainsKey(s) || cfg.ModelsAutoDownload || Models.HasStep(s, modelsDir)).ToArray();

    /// <summary>A node never claims a job that needs a step it cannot run.</summary>
    public static bool CanRun(JobDescriptor job, IReadOnlyCollection<string> supported)
    {
        var steps = job.Steps is { Length: > 0 } ? job.Steps : Protocol.DefaultSteps;
        return steps.All(supported.Contains);
    }

    /// <summary>One work folder per step: the steps of one video can run at the same time on a node and must not delete each other's files.</summary>
    public static string WorkDir(string id, string step) => Path.Combine(Path.GetTempPath(), "indexnode", $"p-{id}-{step}");

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token;
        var store = ctx.Store; // a drama pass hands in its local-cache store, so a step reads the earlier steps' files from disk
        var wst = new RecordingStore(ctx.Store); // everything this run writes; a redo needs it to tell new outputs from old
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
            foreach (var step in Protocol.StepStages.Where(x => steps.Contains(x) || x is "sync" or "describe" or "cast" or "translate"))
            {
                var sj = JobDescriptor.NewStep(step, id); sj.Priority = job.Priority;
                if (await wst.PutAsync(paths.Job(step, id), sj.ToBytes(), Tags.Job(step, "pending", sj.AddedAt, null, sj.Priority), null, true, ct)) made.Add(step);
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
        double duration = StoredJson.Doc(downloaded.Data, $"{id}/downloaded.json").RootElement.TryGetProperty("durationSeconds", out var dEl) ? dEl.GetDouble() : 0;

        // centers: also due again when sscd.zip changed since centers.zip was built (its idx_inputs no longer matches)
        bool centersStale = steps.Contains("centers") && !Redo && Has("centers/manifest.json") && await CentersStep.StaleAsync(store, paths, id, ct);
        var need = new Dictionary<string, bool>
        {
            ["frames"] = steps.Contains("frames") && !Has("frames/manifest.json"),
            ["sscd"] = steps.Contains("sscd") && !Has("sscd/manifest.json"),
            ["centers"] = steps.Contains("centers") && (!Has("centers/manifest.json") || centersStale),
            ["dino"] = steps.Contains("dino") && !Has("dino/manifest.json"),
            ["thumbs"] = steps.Contains("thumbs") && !Has("thumbs/manifest.json"),
            ["clip"] = steps.Contains("clip") && !Has("clip/manifest.json"),
            ["transcribe"] = steps.Contains("transcribe") && !Has("asr/" + Transcribe.ModelName + ".zh.json"),
            ["ocrsubs"] = steps.Contains("ocrsubs") && !Has("ocr/lines.json"),
            ["compress"] = steps.Contains("compress") && !Has("compact.mp4"),
            ["cast"] = steps.Contains("cast") && !Has("cast/clusters.json"),
            ["describe"] = steps.Contains("describe") && !Has("describe.json") && Ai is { Configured: true },
            // the step itself skips when the source text and the glossary seed are unchanged; the step needs only what the source has when describe.json is missing
            ["translate"] = steps.Contains("translate") && Ai is { Configured: true },
            ["sync"] = stepJob && job.Stage == "sync",
        };
        // sscd, clip and cast read the stored frames/keyframes chunks; only the frames step (and transcribe, ocrsubs, compress, the cast credits) decode the video
        bool needVideo = need["frames"] || need["transcribe"] || need["ocrsubs"] || need["compress"] || need["cast"];

        var work = WorkDir(id, stepJob ? job.Stage : "process");
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
                if (LocalCacheStore.Unwrap(store) is RestStore rs && !(store is LocalCacheStore lc && lc.Has(videoBlob))) await NexisAccess.PrepareAsync(rs, videoBlob, ct);
                using var keep = NexisAccess.KeepTouched(store, videoBlob);
                await store.DownloadToFileAsync(videoBlob, video, ct);
                originalBytes = new FileInfo(video).Length;
                if (duration <= 0) duration = await Frames.DurationAsync(tools, video, ct);
            }
            else originalBytes = listing.GetValueOrDefault(videoBlob);

            Task Put(string name, byte[] data, string type) => wst.PutAsync(paths.Video(id, name), data, null, null, false, ct, type);
            Task Commit(string stage) => StageCommit.CommitAsync(store, id, stage, ctx.NodeId, false, ct);   // the stage's zip goes up whole; its existence is the step's completion
            int frameCount = 0;
            if (need["frames"])
            {
                ctx.Report("frames", null);
                int segs = ctx.ThreadShare();
                var r = await Frames.ProduceAsync(tools, video, work, FrameSettings.From(config), segs, duration, p => ctx.Report("frames", p), (name, bytes, type) => wst.PutAsync(paths.Video(id, name), bytes, null, null, false, ct, type), ct);
                frameCount = r.Kept;
                await Commit("frames");
                // frames.zip is up, so the three GPU steps are ready now: one marker each wakes the on-demand GPU node
                var marked = await GpuRouting.WriteReadyMarkersAsync(store, paths, id, ct, GpuFlag);
                Log.Info($"process {id}: gpu-ready markers written for {(marked.Length == 0 ? "(none: GPU routing is off or no pending GPU step)" : string.Join(',', marked))}");
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
                var fdir = await FramesDir(); var box = await FrameChunks.EnsureBoxAsync(wst, paths, id, fs, fdir, ct);
                var bytes = await Sscd.DescribeAsync(tools, model, fdir, fs.Count, ctx.ThreadShare(), p => ctx.Report("sscd", p), ct, box);
                await FrameChunks.PutVectorsAsync(wst, paths, id, "sscd", fs, bytes, Sscd.Dim, new { model = Sscd.ModelId(model), inputSize = Sscd.ShortSide, prune = new { cosine = FrameSettings.From(config).SscdCosine, lookback = FrameSettings.From(config).LookbackSeconds } }, ct);
                // second pruning pass: SSCD cosine over the same look-back window; the pruned frames stay stored, sscd/keep.json and the spans say which stand for which
                var fset = FrameSettings.From(config);
                var (keep, rep) = FrameChunks.SscdPass(fs.Times, bytes, Sscd.Dim, fset.SscdCosine, fset.LookbackSeconds);
                await FrameChunks.ApplySscdPassAsync(wst, paths, id, fs, rep, keep, fset.SscdCosine, fset.LookbackSeconds, ct);
                Log.Info($"process {id}: sscd pass kept {keep.Count} of {rep.Length} frames");
                await Commit("sscd");
            }
            if (need["centers"])
            {   // after sscd: the per-drama k-means centers from the stored vectors (0.11.0)
                ctx.Report("centers", null);
                Log.Info($"process {id}: centers {await CentersStep.RunAsync(wst, paths, id, Math.Max(1, ctx.ThreadShare()), m => ctx.Report("centers", m), ct)}");
                await Commit("centers");
            }
            if (need["dino"])
            {   // after sscd: DINOv2-small vectors from the stored frames (opt-in step)
                ctx.Report("dino", null);
                var dt = new Dino.Timing();
                int n = await Dino.RunAsync(tools, http, wst, paths, id, Math.Max(1, ctx.ThreadShare()), m => ctx.Report("dino", m), ct, dt);
                Log.Info($"process {id}: dino {n} vectors, {dt}");
            }
            if (need["thumbs"])
            {   // after sscd: the same stored frames (hydrated on read), the sscd pass has rewritten their index but not their rows
                ctx.Report("thumbs", null);
                var tm = new Thumbs.Timing();
                int n = await Thumbs.RunStoredAsync(tools, wst, paths, id, Math.Max(1, ctx.ThreadShare()), m => ctx.Report("thumbs", m), ct, tm);
                Log.Info($"process {id}: thumbs {tm}");
                Log.Info($"process {id}: thumbs {n} thumbnails");
                await Commit("thumbs");
            }
            if (need["clip"])
            {
                ctx.Report("clip", null);
                var vision = await Models.EnsureAsync(Models.ClipVisionFor, http, ct);
                var fs = await FrameSet();
                var bytes = await Clip.EmbedFramesAsync(tools, vision, await FramesDir(), fs.Count, ctx.ThreadShare(), p => ctx.Report("clip", p), ct);
                await FrameChunks.PutVectorsAsync(wst, paths, id, "clip", fs, bytes, Clip.Dim, System.Text.Json.Nodes.JsonNode.Parse(Clip.InfoJson())!, ct);
            }
            if (need["transcribe"])
            {
                ctx.Report("transcribe", null);
                Log.Info($"process {id}: transcribe {await StageSteps.TranscribeAsync(tools, http, video, duration, ctx.ThreadShare(), Put, p => ctx.Report("transcribe", p), ct)}");
                await Commit("transcribe");
            }
            if (need["ocrsubs"])
            {
                ctx.Report("ocrsubs", null);
                Log.Info($"process {id}: text {await StageSteps.TextAsync(tools, http, video, duration, ctx.ThreadShare(), Put, p => ctx.Report("ocrsubs", p), ct)}");
                await Commit("ocrsubs");
            }
            if (need["cast"])
            {
                ctx.Report("cast", null);
                var castDir = Path.Combine(work, "castout");
                var shotsB = (await store.GetAsync(paths.Video(id, "frames/shots.json"), ct))?.Data;
                if (StageSteps.FramesAreJpeg(await FrameSet(), shotsB))
                    Log.Info($"process {id}: cast (frames) {await StageSteps.CastAsync(tools, http, await FrameSet(), shotsB, duration, ctx.ThreadShare(), work, Put, p => ctx.Report("cast", p), ct, ocrLines: (await store.GetAsync(paths.Video(id, "ocr/lines.json"), ct))?.Data)}");
                else
                {
                var kfs = await FrameChunks.DownloadAsync(store, paths, id, "keyframes", Path.Combine(work, "kfchunks"), ct) ?? throw new InvalidOperationException($"{id} has no keyframes/ chunks yet (run the frames step)");
                var msg = await CastStage.RunAsync(tools, http, video, duration, castDir, new CastStage.Options(config.CastModel ?? "buffalo_l", ctx.ThreadShare()), s => ctx.Report("cast", s), ct, null, kfs);
                Log.Info($"process {id}: cast {msg}");
                foreach (var f in Directory.GetFiles(Path.Combine(castDir, "cast")))
                    await wst.PutAsync(paths.Video(id, "cast/" + Path.GetFileName(f)), await File.ReadAllBytesAsync(f, ct), null, null, false, ct, f.EndsWith(".jpg") ? "image/jpeg" : f.EndsWith(".f16") ? "application/octet-stream" : "application/json");
                }
                await Commit("cast");
            }
            if (need["describe"])
            {
                ctx.Report("describe", null);
                var d = await DescribeJob.GatherAsync(new StoreJob(store, paths, id), Path.Combine(work, "describe"), ct);
                var bytes = await DescribeJob.RunAsync((Chat?.Invoke(http, Ai!) ?? new AiClient(http, Ai!)), Ai, d, ct);
                if (bytes != null) { await wst.PutAsync(paths.Video(id, "describe.json"), bytes, null, null, false, ct); await Commit("describe"); }
            }
            if (need["translate"])
            {
                ctx.Report("translate", null);
                Log.Info($"process {id}: translate {await StageSteps.TranslateAsync((Chat?.Invoke(http, Ai!) ?? new AiClient(http, Ai!)), Ai!, new StoreJob(store, paths, id), (n, b, type) => wst.PutAsync(paths.Video(id, n), b, null, null, false, ct, type), Redo, m => Log.Info($"process {id}: translate {m}"), ct)}");
                await Commit("translate");
            }
            long compactBytes = 0;
            if (need["compress"])
            {
                ctx.Report("compress", null);
                var compact = Path.Combine(work, "compact.mp4");
                await Compress.RunAsync(tools, video, compact, compress, ctx.ThreadShare(), duration, p => ctx.Report("compress", p), ct);
                compactBytes = new FileInfo(compact).Length;
                ctx.Report("compress", "uploading");
                await wst.PutFileAsync(paths.Video(id, "compact.mp4"), compact, "video/mp4", null, ct);
            }
            else if (steps.Contains("compress")) compactBytes = listing.GetValueOrDefault(paths.Video(id, "compact.mp4"));

            if (Redo)
            {
                // the new outputs are all in: now (and only now) the old files of the redone steps that this run did not write again go
                var vdir = paths.VideoDir(id); var written = wst.Written;
                foreach (var step in KnownSteps.Where(x => need.GetValueOrDefault(x)))
                    foreach (var old in Stale(step, vdir, listing.Keys, written)) { await store.DeleteAsync(old, ct); Log.Info($"process {id}: redo {step}: removed stale {old[vdir.Length..]}"); }
            }

            if (stepJob)
            {
                // the node that finishes the last step writes processed.json
                var after = (await store.ListAsync(paths.VideoDir(id), false, false, ct)).Select(b => b.Name).ToHashSet();
                bool aiOff = Ai is not { Configured: true };
                if (Outputs.Any(o => !(aiOff && AiSteps.Contains(o.Step)) && !after.Contains(paths.Video(id, o.File)))) { Log.Info($"process {id}: step {job.Stage} done"); return; }
                steps = Protocol.DefaultSteps.Concat(["cast", "sync", "describe", "translate"]).ToArray();
                Log.Info($"process {id}: step {job.Stage} was the last one");
            }
            // a redo (`process --job --steps`) replaces the listed outputs only: processed.json and the video stay as they are
            if (Redo) { Log.Info($"process {id}: redone {string.Join(',', steps)} (processed.json left alone)"); return; }
            if (frameCount == 0 && steps.Contains("frames") && await store.GetAsync(paths.Video(id, "frames/manifest.json"), ct) is { } fm)
                frameCount = StoredJson.Doc(fm.Data, $"{id}/frames/manifest.json").RootElement.GetProperty("count").GetInt32();
            // speech language and OCR cue count come from the outputs, whether this run made them or an earlier one did
            string? speechLanguage = null; bool? dubbed = null; int? ocrCues = null;
            if (!stepJob && steps.Contains("transcribe") && await store.GetAsync(paths.Video(id, "asr/" + Transcribe.ModelName + ".zh.json"), ct) is { } asr)
            {
                var root = StoredJson.Doc(asr.Data, $"{id}/asr/" + Transcribe.ModelName + ".zh.json").RootElement;
                speechLanguage = root.TryGetProperty("language", out var l) ? l.GetString() : null;
                dubbed = root.TryGetProperty("dubbed", out var d) && d.ValueKind == JsonValueKind.True;
            }
            if (!stepJob && steps.Contains("ocrsubs") && await store.GetAsync(paths.Video(id, "ocr/lines.json"), ct) is { } ocr)
                ocrCues = StoredJson.Doc(ocr.Data, $"{id}/ocr/lines.json").RootElement.GetProperty("zh").GetArrayLength();
            // sync.json, once transcribe and ocrsubs have both run for the job
            double? avOffset = null; bool? avSuspect = null;
            if ((steps.Contains("transcribe") && steps.Contains("ocrsubs")) || need["sync"])
            {
                var lines = await store.GetAsync(paths.Video(id, "ocr/lines.json"), ct);
                var asrFile = await store.GetAsync(paths.Video(id, "asr/" + Transcribe.ModelName + ".zh.json"), ct);
                if (lines != null && asrFile != null && AvSync.FromFiles(lines.Data, asrFile.Data) is { } sync)
                {
                    await wst.PutAsync(paths.Video(id, "sync.json"), AvSync.ToJson(sync), null, null, false, ct);
                    await Commit("sync");
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
            await wst.PutAsync(paths.Video(id, "processed.json"), marker, null, null, false, ct);

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
