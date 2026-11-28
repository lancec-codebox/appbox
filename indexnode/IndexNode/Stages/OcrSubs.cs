using System.Diagnostics;
using System.Globalization;

namespace IndexNode;

public sealed record OcrResult(IReadOnlyList<Cue> Zh, IReadOnlyList<Cue> En, int BandY, int BandH, int Width, int FramesRead, double Seconds, IReadOnlyList<FrameText>? Frames = null);

/// <summary>The ocrsubs step: find the band, then read every frame of it at 4 fps with PP-OCRv5 mobile through ONNX Runtime.</summary>
public static class OcrSubs
{
    public static async Task<(int W, int H)> SizeAsync(Tools tools, string video, CancellationToken ct)
    {
        var probe = await tools.FfprobeAsync(ct);
        var r = await Proc.RunAsync(probe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", video], ct);
        var wh = r.Stdout.Trim().Split('x');
        return (int.Parse(wh[0]), int.Parse(wh[1]));
    }

    /// <summary>Sampled frame times: <paramref name="count"/> evenly spread over [start, start+duration), skipping the first and last 2%.</summary>
    public static double[] SampleTimes(double start, double duration, int count) =>
        Enumerable.Range(0, count).Select(i => start + duration * (0.02 + 0.96 * (i + 0.5) / count)).ToArray();

    private static async Task<byte[]?> OneFrameAsync(string ffmpeg, string video, double t, int w, int h, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-ss", t.ToString("F3", CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgr24", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => Proc.Kill(p));
        _ = p.StandardError.ReadToEndAsync();
        var buf = new byte[w * h * 3]; int off = 0;
        while (off < buf.Length) { int k = await p.StandardOutput.BaseStream.ReadAsync(buf.AsMemory(off), ct); if (k == 0) break; off += k; }
        await p.WaitForExitAsync(CancellationToken.None);
        return off == buf.Length ? buf : null;
    }

    /// <summary>~200 frames across the video, text boxes by the detector alone, then <see cref="BandFinder"/>.</summary>
    public static async Task<(int Y, int H)?> FindBandAsync(Tools tools, string ocrDet, string ocrRec, string dict, string video, int w, int h, double start, double duration, int workers, Action<string> progress, CancellationToken ct)
    {
        var ffmpeg = await tools.FfmpegAsync(ct);
        var times = SampleTimes(start, duration, BandFinder.Samples);
        var boxes = new IReadOnlyList<Rect>[times.Length];
        int next = -1, done = 0;
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            using var eng = new OcrEngine(ocrDet, ocrRec, dict, 1);
            int i;
            while ((i = Interlocked.Increment(ref next)) < times.Length)
            {
                ct.ThrowIfCancellationRequested();
                var f = await OneFrameAsync(ffmpeg, video, times[i], w, h, ct);
                // detection at 960 so that a small band in a tall frame keeps its detail; boxes below are in frame pixels
                boxes[i] = f == null ? [] : eng.Detect(f, w, h, 960).Select(b => new Rect((int)b.MinX, (int)b.MinY, (int)b.MaxX, (int)b.MaxY)).Where(b => b.Y1 - b.Y0 >= 8).ToList();
                progress($"band {Interlocked.Increment(ref done)}/{times.Length}");
            }
        }, ct)));
        return BandFinder.Find(boxes, h) is { } r ? (r.Y, r.Height) : null;
    }

    /// <summary>Segments = max(the step's thread share, cores/2), each with one intra-op thread. OCR is the long pole, and with ORT spinning off mild oversubscription costs nothing (16 one-thread segments ran at full rate), whereas a small share (3 while seven heavy steps run) starved it: 21 fps against 50 fps on 0.5.2.</summary>
    public const int IntraThreads = 1;
    /// <summary>The style gate's safety net: a full read every this many samples (5 s at 2 fps), whatever the mask says.</summary>
    public const int Safety = 20; // 10 s: on the test window 61% of safety reads at 5 s read the same text again, and none found a style miss
    /// <summary>With an exact thread budget, the window is cut into this many parts per thread, run from a queue.</summary>
    public const int QueueParts = 4;
    /// <summary>Reader width factor for subtitles (OcrEngine.Squeeze). Owner 2026-11-14: x0.8, no re-read. On the test window the reader took 21% less time;
    /// Chinese cues unchanged (228/228), ~9 of 229 English cues lost a space or similar. x0.67 saved 36% but hurt English letters; a full-width re-read of
    /// non-Chinese lines cost as much as it saved (English lines outnumber Chinese ones).</summary>
    public const double Squeeze = 0.8;
    /// <summary>Margin around the mask's text pixels for the pre-cropped line finder (outline, strokes the mask misses).</summary>
    public const int CropPad = 16;

    /// <summary>The normal read on just the box around the text (from the style mask), lines moved back to band coordinates.</summary>
    static List<OcrLine> ReadCropped(OcrEngine eng, byte[] buf, int w, int h, (int X0, int Y0, int X1, int Y1)? box)
    {
        if (box is not { } b) return eng.Recognize(buf, w, h);
        int cw = b.X1 - b.X0, ch = b.Y1 - b.Y0; var sub = new byte[cw * ch * 3];
        for (int y = 0; y < ch; y++) Buffer.BlockCopy(buf, ((b.Y0 + y) * w + b.X0) * 3, sub, y * cw * 3, cw * 3);
        return eng.Recognize(sub, cw, ch).Select(l => l with { X0 = l.X0 + b.X0, X1 = l.X1 + b.X0, Y0 = l.Y0 + b.Y0, Y1 = l.Y1 + b.Y0 }).ToList();
    }
    public static int Segments(int share, int cores) => Math.Max(1, Math.Max(share, cores / 2));

    /// <summary>The segments for the parallel read: equal parts whose boundaries are snapped to the sampling grid (multiples of 1/fps from the start), so every segment samples exactly the instants the serial read would, and the merged frames are identical to it.</summary>
    public static List<(double Start, double End)> PlanSegments(double duration, int workers, double fps = OcrCues.Fps)
    {
        var raw = FrameMath.Plan(duration, workers, 4);
        var list = new List<(double Start, double End)>();
        double prev = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            double end = i == raw.Count - 1 ? duration : Math.Round(raw[i].End * fps) / fps;
            if (end <= prev) continue;
            list.Add((prev, end)); prev = end;
        }
        return list;
    }

    /// <summary>Every frame of the band at 4 fps, in parallel segments (ffmpeg -threads 1 each, one OCR worker per segment).</summary>
    public static async Task<OcrResult> ReadAsync(Tools tools, string video, string ocrDet, string ocrRec, string dict, int w, int h, int bandY, int bandH,
        double start, double duration, int workers, Action<string> progress, CancellationToken ct, bool exactSegments = false, bool useGate = true, bool preCrop = false, double squeeze = Squeeze, bool reread = false,
        IReadOnlyList<BandPart>? bandParts = null)
    {
        var sw = Stopwatch.StartNew();
        var ffmpeg = await tools.FfmpegAsync(ct);
        // 0.13.94: band parts the frames step decoded (OcrBandTap): the segments are those parts, and each is read from its small lossless file instead of the video
        var tapParts = bandParts?.OrderBy(p => p.Start).ToList();
        var plan = tapParts != null ? tapParts.Select(p => (Start: p.Start, End: p.End)).ToList()
            : PlanSegments(duration, exactSegments ? Math.Max(1, workers) * QueueParts : Segments(workers, Environment.ProcessorCount)).Select(s => (Start: start + s.Start, End: start + s.End)).ToList();
        var perSeg = new List<FrameText>[plan.Count];
        var gate = new StyleGate(w, bandH); // one style per drama, learned from the first lines any segment reads
        int frameBytes = w * bandH * 3, total = 0, nEmpty = 0, nReused = 0, nRead = 0, nSafety = 0, nMiss = 0, nSameRead = 0, nSameSafety = 0;
        static string Txt(List<OcrLine> ls) => string.Join("|", ls.Select(l => l.Text));
        // exact thread budget: at most `workers` parts at once, from a queue of QueueParts x workers short parts (a part with more text no longer straggles); one engine per running part, pooled
        using var slots = new SemaphoreSlim(exactSegments ? Math.Max(1, workers) : plan.Count);
        var pool = new System.Collections.Concurrent.ConcurrentBag<OcrEngine>();
        await Task.WhenAll(plan.Select((seg, k) => Task.Run(async () =>
        {
            await slots.WaitAsync(ct);
            var eng = pool.TryTake(out var pe) ? pe : new OcrEngine(ocrDet, ocrRec, dict, IntraThreads) { Squeeze = squeeze, Reread = reread };
            try {
            var list = new List<FrameText>(); perSeg[k] = list;
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in tapParts != null ? new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-i", tapParts[k].File, "-an", "-f", "rawvideo", "-pix_fmt", "bgr24", "-" }
                : new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-ss", seg.Start.ToString("F3", CultureInfo.InvariantCulture), "-t", (seg.End - seg.Start).ToString("F3", CultureInfo.InvariantCulture),
                "-i", video, "-an", "-vf", $"fps={OcrCues.Fps},crop={w}:{bandH}:0:{bandY}", "-f", "rawvideo", "-pix_fmt", "bgr24", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => Proc.Kill(p));
            _ = p.StandardError.ReadToEndAsync();
            var buf = new byte[frameBytes]; byte[]? anchorM = null; int anchorCnt = 0; List<OcrLine> anchorRaw = []; int since = 0;
            for (int i = 0; ; i++)
            {
                int off = 0;
                while (off < frameBytes) { int n = await p.StandardOutput.BaseStream.ReadAsync(buf.AsMemory(off, frameBytes - off), ct); if (n == 0) break; off += n; }
                if (off < frameBytes) break;
                ct.ThrowIfCancellationRequested();
                // read each line once: learn the style from the first reads, then read only when the text mask changed (StyleGate)
                List<OcrLine> raw;
                if (!useGate) { raw = eng.Recognize(buf, w, bandH); Interlocked.Increment(ref nRead); }
                else if (!gate.Learned) { raw = eng.Recognize(buf, w, bandH); gate.Learn(buf, raw); Interlocked.Increment(ref nRead); anchorM = null; }
                else
                {
                    var m = gate.Mask(buf, out int cnt); since++;
                    if (since >= Safety) { raw = eng.Recognize(buf, w, bandH); since = 0; Interlocked.Increment(ref nSafety); if (anchorM != null && Txt(raw) == Txt(anchorRaw)) Interlocked.Increment(ref nSameSafety); if (raw.Count > 0 && cnt < StyleGate.MinPx) Interlocked.Increment(ref nMiss); anchorM = m; anchorCnt = cnt; anchorRaw = raw; }
                    else if (cnt < StyleGate.MinPx) { raw = []; Interlocked.Increment(ref nEmpty); anchorM = m; anchorCnt = cnt; anchorRaw = raw; }
                    else if (anchorM != null && gate.SameText(m, cnt, anchorM, anchorCnt, anchorRaw)) { raw = anchorRaw; Interlocked.Increment(ref nReused); }
                    else { raw = preCrop ? ReadCropped(eng, buf, w, bandH, gate.Bounds(m, CropPad)) : eng.Recognize(buf, w, bandH); Interlocked.Increment(ref nRead); if (anchorM != null && Txt(raw) == Txt(anchorRaw)) Interlocked.Increment(ref nSameRead); anchorM = m; anchorCnt = cnt; anchorRaw = raw; }
                }
                var lines = raw.Select(l => new OcrBox(l.X0, l.Y0 + bandY, l.X1, l.Y1 + bandY, l.Text, l.Score)).ToList();
                list.Add(new FrameText(Math.Round(seg.Start + i / OcrCues.Fps, 3), lines));
                var n2 = Interlocked.Increment(ref total);
                if (n2 % 200 == 0) progress($"{n2} frames");
            }
            await p.WaitForExitAsync(CancellationToken.None);
            } finally { pool.Add(eng); slots.Release(); }
        }, ct)).ToArray());
        double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
        Log.Info($"ocrsubs: model time (all threads): line finder {Ms(pool.Sum(e => e.TPre + e.TDet + e.TPost)) / 1000:F1}s, reader {Ms(pool.Sum(e => e.TCrop + e.TRecPre + e.TRec + e.TCtc)) / 1000:F1}s");
        Log.Info($"ocrsubs: reader width x{squeeze}: {pool.Sum(e => e.SqueezedReads)} lines read squeezed, {pool.Sum(e => e.FullRereads)} read again at full width");
        foreach (var e in pool) e.Dispose();

        Log.Info($"ocrsubs: {total} frames in {sw.Elapsed.TotalSeconds:F0}s = {total / Math.Max(0.001, sw.Elapsed.TotalSeconds):F1} frames/s on {plan.Count} segments (thread share {workers}, {Environment.ProcessorCount} cores, 1 intra-op thread each; ffmpeg decodes the full frame, the band is cropped in the filter){(OrtOptions.Cuda ? $"; cuda: conv search {OcrEngine.ConvSearch ?? "provider default"}, reader width bucket {OcrEngine.RecBucket}" : "")}");
        Log.Info($"ocrsubs: style gate: {nRead} read, {nReused} reused, {nEmpty} empty, {nSafety} safety reads, {nMiss} style misses; redundant (same text as the reading before): {nSameRead} of the reads, {nSameSafety} of the safety reads");
        var (frames, marks) = OcrCues.StripWatermarks(perSeg.SelectMany(x => x).OrderBy(f => f.T).ToList());
        if (marks.Count > 0) Log.Info($"ocrsubs: watermarks dropped before merging: {string.Join(" | ", marks)}");
        var zh = OcrCues.Merge(frames, false);
        var en = OcrCues.Merge(frames, true);
        OcrCues.Simplify(zh, OpenCc.T2S);
        OcrCues.Simplify(en, OpenCc.T2S);
        return new OcrResult(zh, en, bandY, bandH, w, frames.Count, sw.Elapsed.TotalSeconds, frames);
    }
}
