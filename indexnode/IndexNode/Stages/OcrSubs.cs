using System.Diagnostics;
using System.Globalization;

namespace IndexNode;

public sealed record OcrResult(IReadOnlyList<Cue> Zh, IReadOnlyList<Cue> En, int BandY, int BandH, int Width, int FramesRead, double Seconds);

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

    /// <summary>One segment per core of the step's share, each with one intra-op thread (two threads gave 1.17x per segment, and more threads than cores with spinning halved the throughput): the sum of the OCR threads is the share.</summary>
    public const int IntraThreads = 1;
    public static int Segments(int share, int cores) => Math.Max(1, share);

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
        double start, double duration, int workers, Action<string> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ffmpeg = await tools.FfmpegAsync(ct);
        var plan = PlanSegments(duration, Segments(workers, Environment.ProcessorCount)).Select(s => (Start: start + s.Start, End: start + s.End)).ToList();
        var perSeg = new List<FrameText>[plan.Count];
        int frameBytes = w * bandH * 3, total = 0;
        await Task.WhenAll(plan.Select((seg, k) => Task.Run(async () =>
        {
            using var eng = new OcrEngine(ocrDet, ocrRec, dict, IntraThreads);
            var list = new List<FrameText>(); perSeg[k] = list;
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-ss", seg.Start.ToString("F3", CultureInfo.InvariantCulture), "-t", (seg.End - seg.Start).ToString("F3", CultureInfo.InvariantCulture),
                "-i", video, "-an", "-vf", $"fps={OcrCues.Fps},crop={w}:{bandH}:0:{bandY}", "-f", "rawvideo", "-pix_fmt", "bgr24", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => Proc.Kill(p));
            _ = p.StandardError.ReadToEndAsync();
            var buf = new byte[frameBytes]; var prev = new byte[frameBytes]; List<OcrBox>? prevLines = null;
            for (int i = 0; ; i++)
            {
                int off = 0;
                while (off < frameBytes) { int n = await p.StandardOutput.BaseStream.ReadAsync(buf.AsMemory(off, frameBytes - off), ct); if (n == 0) break; off += n; }
                if (off < frameBytes) break;
                ct.ThrowIfCancellationRequested();
                // a band byte-identical to the previous sampled one reads the same: reuse its lines (exact match only, so no cue is lost)
                var lines = prevLines != null && buf.AsSpan().SequenceEqual(prev) ? prevLines : eng.Recognize(buf, w, bandH).Select(l => new OcrBox(l.X0, l.Y0 + bandY, l.X1, l.Y1 + bandY, l.Text, l.Score)).ToList();
                Buffer.BlockCopy(buf, 0, prev, 0, frameBytes); prevLines = lines;
                list.Add(new FrameText(Math.Round(seg.Start + i / OcrCues.Fps, 3), lines));
                var n2 = Interlocked.Increment(ref total);
                if (n2 % 200 == 0) progress($"{n2} frames");
            }
            await p.WaitForExitAsync(CancellationToken.None);
        }, ct)).ToArray());

        Log.Info($"ocrsubs: {total} frames in {sw.Elapsed.TotalSeconds:F0}s = {total / Math.Max(0.001, sw.Elapsed.TotalSeconds):F1} frames/s on {plan.Count} segments (ffmpeg decodes the full frame, the band is cropped in the filter)");
        var frames = perSeg.SelectMany(x => x).OrderBy(f => f.T).ToList();
        var zh = OcrCues.Merge(frames, false);
        var en = OcrCues.Merge(frames, true);
        OcrCues.Simplify(zh, OpenCc.T2S);
        OcrCues.Simplify(en, OpenCc.T2S);
        return new OcrResult(zh, en, bandY, bandH, w, frames.Count, sw.Elapsed.TotalSeconds);
    }
}
