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
    static readonly string[] Known = ["frames", "sscd", "cast", "transcribe"];

    public static async Task<int> RunAsync(Args a)
    {
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
                        var videoBlob = paths.Video(id, "video.mp4"); var video = Path.Combine(work, "video.mp4");
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
                    {
                        var videoBlob = paths.Video(id, "video.mp4"); var video = Path.Combine(work, "video.mp4");
                        if (!File.Exists(video))
                        {
                            P(stage, "fetching video.mp4");
                            var rest = store as RestStore ?? (store as RedirectStore)?.Inner as RestStore;
                            if (rest != null) await NexisAccess.PrepareAsync(rest, videoBlob, ct);
                            await store.DownloadToFileAsync(videoBlob, video, ct);
                        }
                        double fetch = sw.Elapsed.TotalSeconds;
                        var m = await Models.EnsureAsync(Models.AsrModel, http, ct); var tk = await Models.EnsureAsync(Models.AsrTokens, http, ct); var vadm = await Models.EnsureAsync(Models.Vad, http, ct);
                        var duration = await Frames.DurationAsync(tools, video, ct);
                        var audio = await Transcribe.ReadAudioParallelAsync(tools, video, duration, threads, ct);
                        P(stage, $"audio read in {sw.Elapsed.TotalSeconds - fetch:F1}s ({audio.Length / 16000.0:F0}s)");
                        Transcribe.SharedModel = true;
                        if (a.Get("asr-piece") is { } ap) Transcribe.PieceSeconds = int.Parse(ap);
                        var segs = Transcribe.RunParallel(m, tk, vadm, audio, threads, 1, 1, msg => P(stage, msg), ct);
                        var (lang, dubbed) = Transcribe.Dubbing(segs);
                        var vtt = System.Text.Encoding.UTF8.GetBytes(Transcribe.Vtt(segs)); var json = Transcribe.ToJson(segs, lang, dubbed, audio.Length / (double)Transcribe.SampleRate, sw.Elapsed.TotalSeconds - fetch);
                        if (a.Get("out") is { } od) { Directory.CreateDirectory(od); await File.WriteAllBytesAsync(Path.Combine(od, Transcribe.ModelName + ".zh.vtt"), vtt, ct); await File.WriteAllBytesAsync(Path.Combine(od, Transcribe.ModelName + ".zh.json"), json, ct); }
                        else
                        {
                            await store.PutAsync(paths.Video(id, "asr/" + Transcribe.ModelName + ".zh.vtt"), vtt, null, null, false, ct, "text/vtt");
                            await store.PutAsync(paths.Video(id, "asr/" + Transcribe.ModelName + ".zh.json"), json, null, null, false, ct);
                        }
                        Console.WriteLine($"transcribe: {segs.Count} segments, {duration:F0}s of audio, language {lang}, dubbed {dubbed}; fetch {fetch:F1}s, total {sw.Elapsed.TotalSeconds:F1}s");
                        break;
                    }
                    case "cast":
                    {
                        P(stage, "fetching frames"); var fs = await FrameSet(); var shots = (await store.GetAsync(paths.Video(id, "frames/shots.json"), ct))?.Data;
                        await Models.EnsureAllAsync([.. Models.FaceDefault, Models.ClipVision, Models.ClipText, Models.ClipTokenizer], http, ct);
                        double D(string k, double d) => a.Get(k) is { } v ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : d;
                        var cm = CastLogic.ModelFor(a.Get("model-id") ?? (a.Get("face-rec") is { } fr && fr.Contains("mbf") ? "buffalo_s" : "buffalo_l"));
                        var cs = new CastFrames.Settings(a.Get("face-det") ?? Models.PathOf(Models.ScrfdDet, null), a.Get("face-rec") ?? Models.PathOf(Models.ArcFaceRec, null),
                            cm.Id, D("threshold", cm.Threshold), D("merge", cm.Merge), threads, AlsoRec: a.Get("face-alt"));
                        var outDir = a.Get("out") ?? Path.Combine(work, "cast-out");
                        var msg = await CastFrames.RunAsync(tools, fs, shots, outDir, cs, m => P(stage, m), ct);
                        if (a.Get("out") == null)
                            foreach (var f in Directory.GetFiles(Path.Combine(outDir, "cast")))
                                await store.PutAsync(paths.Video(id, "cast/" + Path.GetFileName(f)), await File.ReadAllBytesAsync(f, ct), null, null, false, ct, f.EndsWith(".json") ? "application/json" : f.EndsWith(".jpg") ? "image/jpeg" : "application/octet-stream");
                        Console.WriteLine($"cast: {msg}; total {sw.Elapsed.TotalSeconds:F1}s");
                        break;
                    }
                    case "sscd":
                    {
                        P(stage, "fetching frames"); var fs = await FrameSet(); double fetch = sw.Elapsed.TotalSeconds;
                        var model = a.Get("model") ?? await Sscd.EnsureModelAsync(cfg, http, ct);
                        var dir = Path.Combine(work, "frames-jpg"); if (!Directory.Exists(dir)) await fs.ExtractAsync(tools, dir, ct);
                        double extract = sw.Elapsed.TotalSeconds - fetch;
                        P(stage, $"{fs.Count} frames fetched in {fetch:F1}s, images ready in {extract:F1}s; model {Path.GetFileName(model)}, device {OrtOptions.Device}, {threads} threads");
                        int every = Math.Max(1, fs.Count / 10);
                        var bytes = await Sscd.DescribeAsync(tools, model, dir, fs.Count, threads, m =>
                        {   // progress roughly every 10%
                            var n = m.Split('/')[0]; if (int.TryParse(n, out var k) && (k % every < 64 || k == fs.Count)) P(stage, m);
                        }, ct);
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
