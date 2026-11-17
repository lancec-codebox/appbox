using System.Diagnostics;

namespace IndexNode;

/// <summary>
/// `indexnode stages run`: pull exactly the inputs the named stages need, run them in order in this process, write their outputs, done.
/// No queue, no job records. The same command runs locally (bench fixtures) and on any machine (the GPU script runner).
///
///   indexnode stages run --video <id> --stages frames,sscd [--prefix index/] [--out-prefix index-test/run1/] [--threads N] [--timeout 120] [--model path]
///   indexnode stages run --local <dir> --video <id> --stages sscd     # <dir>/videos/<id>/... instead of blob storage
///   cast: [--face-det path] [--face-rec path] [--face-alt path] [--threshold x] [--merge x] [--model-id name] [--out dir]  (--out: write cast/ there, not to the store)
///
/// Progress goes to stderr as "<elapsed>s [stage] ...", one summary line per stage to stdout. Past --timeout seconds everything stops (exit 124).
/// </summary>
public static class StagesCommand
{
    static readonly string[] Known = ["frames", "sscd", "dino", "thumbs", "transcribe", "text", "cast", "sync", "describe"];

    /// <summary>
    /// `stages translate --job &lt;id&gt; --out &lt;dir&gt;`: the translate step as a trial. Reads the inputs from the store, writes en.vtt and en.json to &lt;dir&gt; only (never to the store) and always runs.
    ///   [--prefix index/] [--local &lt;dir&gt;] [--ai-base-url U --ai-key K --ai-text-model M --ai-api responses --ai-session-id S]
    /// </summary>
    public static async Task<int> TranslateAsync(Args a)
    {
        var id = a.Get("job") ?? throw new ArgumentException("stages translate --job <id> --out <dir> [--local <dir>] [--prefix index/] [--ai-...]");
        var outDir = a.Get("out") ?? throw new ArgumentException("--out <dir> is required (the trial writes en.vtt and en.json there, never to the store)");
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp();
        var ai = AiConfig.Resolve(cfg.Ai, k => a.Get(k), Environment.GetEnvironmentVariable);
        if (!ai.Configured) throw new InvalidOperationException("translate needs an AI endpoint: ai.* in the config, INDEXNODE_AI_* or --ai-base-url/--ai-key/--ai-text-model");
        IJobSource src;
        if (a.Get("local") is { } local) src = new LocalJob(Path.Combine(Path.GetFullPath(local), "videos", id), id);
        else
        {
            if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or set INDEXNODE_SAS_URL");
            src = new StoreJob(new RestStore(http, cfg.Sas), new Paths(a.Get("prefix") ?? "index/"), id);
        }
        using var cts = a.Get("timeout") is { } to ? new CancellationTokenSource(TimeSpan.FromSeconds(double.Parse(to, System.Globalization.CultureInfo.InvariantCulture))) : new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var sw = Stopwatch.StartNew(); Directory.CreateDirectory(outDir);
        Task Put(string name, byte[] data, string type) { File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(name)), data); return Task.CompletedTask; }
        try
        {
            var msg = await StageSteps.TranslateAsync(new AiClient(http, ai), ai, src, Put, true, m => Console.Error.WriteLine($"{sw.Elapsed.TotalSeconds,6:F1}s [translate] {m}"), cts.Token);
            Console.WriteLine($"translate: {msg} -> {outDir}");
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { Console.Error.WriteLine($"{sw.Elapsed.TotalSeconds,6:F1}s [translate] TIMED OUT"); return 124; }
    }

    public static async Task<int> RunAsync(Args a)
    {
        if (a.Positional.FirstOrDefault() == "translate") return await TranslateAsync(a);
        if (a.Positional.FirstOrDefault() != "run") throw new ArgumentException("stages run --video <id> --stages frames,sscd [--local <dir>] [--out-prefix P] [--threads N] [--timeout S]");
        var id = a.Get("video") ?? throw new ArgumentException("--video <id> is required");
        var stages = (a.Get("stages") ?? throw new ArgumentException("--stages is required")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var s in stages) if (!Known.Contains(s)) throw new ArgumentException($"unknown stage '{s}' (this version knows: {string.Join(", ", Known)})");
        int threads = a.Get("threads") is { } t ? int.Parse(t) : Environment.ProcessorCount;
        double timeout = a.Get("timeout") is { } to ? double.Parse(to, System.Globalization.CultureInfo.InvariantCulture) : 0;

        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var tools = new Tools(http, cfg);
        // same device rule as `run`: --device, else INDEXNODE_DEVICE (set in the GPU image), else the config; on cuda the fp32 models are used and CPU fallback fails hard
        OrtOptions.Device = OrtOptions.ParseDevice(a.Get("device") ?? Environment.GetEnvironmentVariable("INDEXNODE_DEVICE") ?? cfg.Device);
        IStore store; Paths paths;
        if (a.Get("local") is { } local) { store = new LocalStore(Path.GetFullPath(local)); paths = new Paths(""); }
        else { store = new RestStore(http, cfg.Sas); paths = new Paths(a.Get("prefix") ?? "index/"); }
        if (a.Get("out-prefix") is { } op) store = new RedirectStore(store, paths.Prefix, op.EndsWith('/') ? op : op + "/");

        using var cts = timeout > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeout)) : new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ct = cts.Token; var all = Stopwatch.StartNew(); string current = "";
        void P(string stage, string msg) => Console.Error.WriteLine($"{all.Elapsed.TotalSeconds,6:F1}s [{stage}] {msg}");

        var work = Path.Combine(Path.GetTempPath(), "indexnode-stages", $"{id}-{Environment.ProcessId}");
        Directory.CreateDirectory(work);
        try
        {
            Task Put(string name, byte[] data, string type) => store.PutAsync(paths.Video(id, name), data, null, null, false, ct, type);
            string? videoPath = null;
            async Task<string> Video()
            {   // the job's video, fetched once for every stage that reads it (--video-blob: a video stored elsewhere)
                if (videoPath != null) return videoPath;
                var blob = a.Get("video-blob") ?? paths.Video(id, "video.mp4"); var v = Path.Combine(work, "video.mp4");
                if (!File.Exists(v))
                {
                    P(current, "fetching video.mp4");
                    var rest = store as RestStore ?? (store as RedirectStore)?.Inner as RestStore;
                    if (rest != null) await NexisAccess.PrepareAsync(rest, blob, ct);
                    await store.DownloadToFileAsync(blob, v, ct);
                }
                return videoPath = v;
            }
            ChunkDir? frameSet = null;
            async Task<ChunkDir> FrameSet() => frameSet ??= await FrameChunks.DownloadAsync(store, paths, id, "frames", Path.Combine(work, "framechunks"), ct)
                ?? throw new InvalidOperationException($"{id} has no frames/ chunks (run the frames stage first)");
            foreach (var stage in stages)
            {
                current = stage; var sw = Stopwatch.StartNew();
                switch (stage)
                {
                    case "frames":
                    {
                        var videoBlob = a.Get("video-blob") ?? paths.Video(id, "video.mp4"); // --video-blob: a video stored elsewhere (e.g. index-test/mockup/<id>/video.mp4)
                        var video = Path.Combine(work, "video.mp4");
                        P(stage, "fetching video.mp4");
                        var rest = store as RestStore ?? (store as RedirectStore)?.Inner as RestStore;
                        if (rest != null) await NexisAccess.PrepareAsync(rest, videoBlob, ct); // ghosted video: hydrate (or read its snapshot) first
                        await store.DownloadToFileAsync(videoBlob, video, ct);
                        double fetch = sw.Elapsed.TotalSeconds;
                        var duration = await Frames.DurationAsync(tools, video, ct);
                        var st = FrameSettings.From(cfg) with { Keyframes = a.Has("keyframes") };
                        var r = await Frames.ProduceAsync(tools, video, work, st, threads, duration, m => P(stage, m),
                            (n, b, type) => store.PutAsync(paths.Video(id, n), b, null, null, false, ct, type), ct);
                        frameSet = null;
                        Console.WriteLine($"frames: {r.Kept}/{r.Sampled} frames kept, {r.FrameBytes / 1048576.0:F1} MB, {duration:F0}s of video; fetch {fetch:F1}s, total {sw.Elapsed.TotalSeconds:F1}s");
                        break;
                    }
                    case "transcribe":
                    case "text":
                    {
                        var video = await Video(); double fetch = sw.Elapsed.TotalSeconds;
                        if (stage == "transcribe" && a.Get("asr-piece") is { } ap) Transcribe.PieceSeconds = int.Parse(ap);
                        var msg = stage == "transcribe" ? await StageSteps.TranscribeAsync(tools, http, video, 0, threads, Put, m => P(stage, m), ct)
                                                        : await StageSteps.TextAsync(tools, http, video, 0, threads, Put, m => P(stage, m), ct);
                        Console.WriteLine($"{stage}: {msg}; fetch {fetch:F1}s, total {sw.Elapsed.TotalSeconds:F1}s");
                        break;
                    }
                    case "cast":
                    {
                        P(stage, "fetching frames"); var fs = await FrameSet(); var shots = (await store.GetAsync(paths.Video(id, "frames/shots.json"), ct))?.Data;
                        if (!StageSteps.FramesAreJpeg(fs, shots)) throw new InvalidOperationException($"{id}: cast needs the JPEG frames and frames/shots.json (run the frames stage)");
                        var duration = (await store.GetAsync(paths.Video(id, "info.json"), ct))?.Data is { } ib && StoredJson.Parse(ib, $"{id}/info.json")?["duration"]?.GetValue<double>() is { } dd ? dd : 0;
                        var msg = await StageSteps.CastAsync(tools, http, fs, shots, duration, threads, work, a.Get("out") is { } od ? (n, b, _) => { var f = Path.Combine(od, n); Directory.CreateDirectory(Path.GetDirectoryName(f)!); return File.WriteAllBytesAsync(f, b, ct); } : Put,
                            m => P(stage, m), ct, a.Get("face-det"), a.Get("face-rec"), a.Get("face-alt"), (await store.GetAsync(paths.Video(id, "ocr/lines.json"), ct))?.Data);
                        Console.WriteLine($"cast: {msg}; total {sw.Elapsed.TotalSeconds:F1}s");
                        break;
                    }
                    case "sync":
                    {
                        var r = await StageSteps.SyncAsync((await store.GetAsync(paths.Video(id, "ocr/lines.json"), ct))?.Data, (await store.GetAsync(paths.Video(id, "asr/" + Transcribe.ModelName + ".zh.json"), ct))?.Data, Put);
                        Console.WriteLine(r == null ? "sync: nothing to measure (no OCR subtitles or no transcript)" : $"sync: offset {r.Offset:+0.00;-0.00} s, drift {r.Drift:0.00} s{(r.Suspect ? " (SUSPECT)" : "")}, {r.Matches} matches");
                        break;
                    }
                    case "describe":
                    {
                        var ai = AiConfig.Resolve(cfg.Ai, k => a.Get(k), Environment.GetEnvironmentVariable);
                        if (!ai.Configured) throw new InvalidOperationException("describe needs an AI endpoint (ai.* in the config)");
                        var d = await DescribeJob.GatherAsync(new StoreJob(store, paths, id), Path.Combine(work, "describe"), ct);
                        var bytes = await DescribeJob.RunAsync(new AiClient(http, ai), ai, d, ct);
                        if (bytes != null) await Put("describe.json", bytes, "application/json");
                        Console.WriteLine($"describe: {(bytes == null ? "skipped" : "written")}; total {sw.Elapsed.TotalSeconds:F1}s");
                        break;
                    }
                    case "dino":
                    {
                        var dt = new Dino.Timing(); bool i8 = a.Has("int8");
                        var n = await Dino.RunAsync(tools, http, store, paths, id, threads, m => P(stage, m), ct, dt, i8, a.Get("model"));
                        Console.WriteLine($"dino{(i8 ? " (int8)" : "")}: {n} vectors in {sw.Elapsed.TotalSeconds:F1}s, {threads} threads, device {OrtOptions.Device} ({dt})");
                        break;
                    }
                    case "thumbs":
                    {
                        var tm = new Thumbs.Timing();
                        var n = await Thumbs.RunStoredAsync(tools, store, paths, id, a.Get("threads") is { } tt ? int.Parse(tt) : Environment.ProcessorCount, m => P(stage, m), ct, tm);
                        Console.WriteLine($"thumbs: {n} thumbnails in {sw.Elapsed.TotalSeconds:F1}s ({tm})");
                        break;
                    }
                    case "sscd":
                    {
                        P(stage, "fetching frames"); var fs = await FrameSet(); double fetch = sw.Elapsed.TotalSeconds;
                        var model = a.Get("model") ?? await Sscd.EnsureModelAsync(cfg, http, ct);
                        var dir = Path.Combine(work, "frames-jpg"); if (!Directory.Exists(dir)) await fs.ExtractAsync(tools, dir, ct);
                        double extract = sw.Elapsed.TotalSeconds - fetch;
                        P(stage, $"{fs.Count} frames fetched in {fetch:F1}s, images ready in {extract:F1}s; model {Path.GetFileName(model)}, device {OrtOptions.Device}, {threads} threads");
                        var box = await FrameChunks.EnsureBoxAsync(store, paths, id, fs, dir, ct); int every = Math.Max(1, fs.Count / 10);
                        var bytes = await Sscd.DescribeAsync(tools, model, dir, fs.Count, threads, m =>
                        {   // progress roughly every 10%
                            var n = m.Split('/')[0]; if (int.TryParse(n, out var k) && (k % every < 64 || k == fs.Count)) P(stage, m);
                        }, ct, box);
                        var fset = FrameSettings.From(cfg);
                        await FrameChunks.PutVectorsAsync(store, paths, id, "sscd", fs, bytes, Sscd.Dim, new { model = Sscd.ModelId(model), inputSize = Sscd.ShortSide, prune = new { cosine = fset.SscdCosine, lookback = fset.LookbackSeconds } }, ct);
                        var (keep, rep) = FrameChunks.SscdPass(fs.Times, bytes, Sscd.Dim, fset.SscdCosine, fset.LookbackSeconds);
                        await FrameChunks.ApplySscdPassAsync(store, paths, id, fs, rep, keep, fset.SscdCosine, fset.LookbackSeconds, ct);
                        Console.WriteLine($"sscd: {fs.Count} frames, {fs.Count / Math.Max(0.001, sw.Elapsed.TotalSeconds - fetch - extract):F1} frames/s in the model loop; fetch {fetch:F1}s, images {extract:F1}s, total {sw.Elapsed.TotalSeconds:F1}s; {keep.Count} kept after the SSCD pass");
                        break;
                    }
                }
                P(stage, $"done in {sw.Elapsed.TotalSeconds:F1}s");
            }
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine($"{all.Elapsed.TotalSeconds,6:F1}s [{current}] TIMED OUT after {timeout:F0}s");
            return 124;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
