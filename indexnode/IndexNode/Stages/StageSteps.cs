using System.Diagnostics;
using System.Text.Json;

namespace IndexNode;

/// <summary>
/// The stages as single functions, shared by the job path (ProcessStage) and `indexnode stages run`, so a local or canary run
/// executes exactly what production executes. Each writes its outputs through <paramref name="put"/> (store-relative names).
/// </summary>
public static class StageSteps
{
    public delegate Task Put(string name, byte[] data, string contentType);

    static string TypeOf(string f) => f.EndsWith(".json") ? "application/json" : f.EndsWith(".jpg") ? "image/jpeg" : f.EndsWith(".vtt") ? "text/vtt" : "application/octet-stream";

    /// <summary>Speech to Chinese text: audio read in parallel parts, voice detection on pieces, segments decoded by <paramref name="threads"/> workers sharing one model.</summary>
    public static async Task<string> TranscribeAsync(Tools tools, HttpClient http, string video, double duration, int threads, Put put, Action<string> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var m = await Models.EnsureAsync(Models.AsrModel, http, ct); var tk = await Models.EnsureAsync(Models.AsrTokens, http, ct); var vad = await Models.EnsureAsync(Models.Vad, http, ct);
        if (duration <= 0) duration = await Frames.DurationAsync(tools, video, ct);
        var audio = await Transcribe.ReadAudioParallelAsync(tools, video, duration, threads, ct);
        progress($"audio read in {sw.Elapsed.TotalSeconds:F1}s ({audio.Length / (double)Transcribe.SampleRate:F0}s)");
        var segs = Transcribe.RunParallel(m, tk, vad, audio, Math.Max(1, threads), 1, 1, progress, ct);
        var (lang, dubbed) = Transcribe.Dubbing(segs);
        await put("asr/" + Transcribe.ModelName + ".zh.vtt", System.Text.Encoding.UTF8.GetBytes(Transcribe.Vtt(segs)), "text/vtt");
        await put("asr/" + Transcribe.ModelName + ".zh.json", Transcribe.ToJson(segs, lang, dubbed, audio.Length / (double)Transcribe.SampleRate, sw.Elapsed.TotalSeconds), "application/json");
        return $"{segs.Count} segments, language {lang}, dubbed {dubbed}, {sw.Elapsed.TotalSeconds:F1}s";
    }

    /// <summary>Burned-in subtitles: find the band, read it every 0.5 s with the style gate, at most <paramref name="threads"/> parts at once.</summary>
    public static async Task<string> TextAsync(Tools tools, HttpClient http, string video, double duration, int threads, Put put, Action<string> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var det = await Models.EnsureAsync(Models.OcrDet, http, ct); var rec = await Models.EnsureAsync(Models.OcrRec, http, ct); var dict = await Models.EnsureAsync(Models.OcrDict, http, ct);
        if (duration <= 0) duration = await Frames.DurationAsync(tools, video, ct);
        var (w, h) = await OcrSubs.SizeAsync(tools, video, ct);
        int workers = Math.Max(1, threads);
        var band = await OcrSubs.FindBandAsync(tools, det, rec, dict, video, w, h, 0, duration, workers, _ => { }, ct);
        OcrResult res;
        if (band == null) { progress("no subtitle band: no burned-in subtitles"); res = new OcrResult([], [], 0, 0, w, 0, 0); }
        else
        {
            progress($"subtitle band y={band.Value.Y} h={band.Value.H} in {sw.Elapsed.TotalSeconds:F1}s");
            res = await OcrSubs.ReadAsync(tools, video, det, rec, dict, w, h, band.Value.Y, band.Value.H, 0, duration, workers, progress, ct, exactSegments: true);
        }
        var enc = System.Text.Encoding.UTF8;
        await put("ocr/subs.zh.vtt", enc.GetBytes(OcrCues.ToVtt(res.Zh, false)), "text/vtt");
        await put("ocr/subs.zh-Hans.vtt", enc.GetBytes(OcrCues.ToVtt(res.Zh, true)), "text/vtt");
        await put("ocr/lines.json", OcrCues.ToJson(res.Zh, res.En, res.BandY, res.BandH, res.Width), "application/json");
        if (res.En.Count > 0) await put("ocr/subs.en.vtt", enc.GetBytes(OcrCues.ToVtt(res.En, false)), "text/vtt");
        return $"{res.Zh.Count} zh cues, {res.En.Count} en cues, {sw.Elapsed.TotalSeconds:F1}s";
    }

    /// <summary>Faces from the stored frames (one per shot, buffalo_s), plus the credits read from the stored frames of the first and last 3 minutes.</summary>
    public static async Task<string> CastAsync(Tools tools, HttpClient http, ChunkDir frames, byte[]? shots, double duration, int threads, string work, Put put, Action<string> progress, CancellationToken ct,
        string? det = null, string? rec = null, string? alsoRec = null)
    {
        var sw = Stopwatch.StartNew();
        if (det == null || rec == null) { var s = await Models.EnsureBuffaloSAsync(http, ct); det ??= s.Det; rec ??= s.Rec; }
        var cm = CastLogic.BuffaloS; var outDir = Path.Combine(work, "cast-out"); if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        var msg = await CastFrames.RunAsync(tools, frames, shots, outDir, new CastFrames.Settings(det, rec, cm.Id, cm.Threshold, cm.Merge, Math.Max(1, threads), AlsoRec: alsoRec), progress, ct);
        var credits = await CastFrames.CreditsAsync(tools, http, frames, duration, Math.Max(1, threads), progress, ct);
        await File.WriteAllTextAsync(Path.Combine(outDir, "cast", "credits.json"), JsonSerializer.Serialize(new { v = 1, source = "frames", lines = credits.Select(c => new { start = c.Start, end = c.End, text = c.Text }) }), ct);
        foreach (var f in Directory.GetFiles(Path.Combine(outDir, "cast"))) await put("cast/" + Path.GetFileName(f), await File.ReadAllBytesAsync(f, ct), TypeOf(f));
        return $"{msg}, {credits.Count} credit lines, {sw.Elapsed.TotalSeconds:F1}s";
    }

    /// <summary>True when the job's frames are the JPEG chunks with a shot list (0.7 frames): cast can read them instead of the video.</summary>
    public static bool FramesAreJpeg(ChunkDir frames, byte[]? shots) => frames.Container == "mjpeg" && shots != null;

    /// <summary>The A/V offset from the OCR subtitles against the transcript; null when either is missing.</summary>
    public static async Task<SyncResult?> SyncAsync(byte[]? lines, byte[]? asr, Put put)
    {
        if (lines == null || asr == null || AvSync.FromFiles(lines, asr) is not { } r) return null;
        await put("sync.json", AvSync.ToJson(r), "application/json");
        return r;
    }
}
