using System.Diagnostics;

namespace IndexNode;

/// <summary>Developer benchmark: <c>thumbs-bench --dir &lt;folder with NNNN.mjpeg + NNNN.json&gt; --chunk 0001 [--old &lt;0.8.0 thumbs NNNN.mjpeg&gt;] [--threads N] [--ffmpeg path]</c>. Times the routes on one chunk and compares each with the 0.8.0 thumbnails.</summary>
public static class ThumbsBench
{
    public static async Task<int> RunAsync(Args a)
    {
        var dir = a.Get("dir") ?? "."; var n = a.Get("chunk") ?? "0001"; int threads = a.Get("threads") is { } t ? int.Parse(t) : Environment.ProcessorCount;
        var data = File.ReadAllBytes(Path.Combine(dir, n + ".mjpeg")); var c = FrameChunks.ParseChunk(n, File.ReadAllBytes(Path.Combine(dir, n + ".json")), n + ".json");
        var first = Imaging.DecodeJpeg(data.AsSpan((int)c.Frames[0].Off, c.Frames[0].Len).ToArray()); var (tw, th) = Thumbs.SizeFor(first.W, first.H);
        Console.WriteLine($"chunk {n}: {c.Frames.Count} frames {first.W}x{first.H} -> {tw}x{th}, {threads} threads");
        List<byte[]>? old = a.Get("old") is { } op ? FrameChunks.SplitJpegs(File.ReadAllBytes(op)) is var rows ? rows.Select(r => File.ReadAllBytes(op).AsSpan(r.Off, r.Len).ToArray()).ToList() : null : null;
        void Report(string name, List<byte[]> th2, long ms)
        {
            Console.Write($"{name,-22} {ms,6} ms  {ms * 1000.0 / c.Frames.Count,7:F0} us/frame  {th2.Sum(x => (long)x.Length) / (double)th2.Count,6:F0} B/thumb");
            if (old != null && old.Count == th2.Count)
            {
                double sum = 0; int max = 0; long cnt = 0;
                for (int i = 0; i < old.Count; i++) { var x = Imaging.DecodeJpeg(old[i]).Data; var y = Imaging.DecodeJpeg(th2[i]).Data; for (int k = 0; k < x.Length; k++) { int d = Math.Abs(x[k] - y[k]); sum += d; if (d > max) max = d; cnt++; } }
                Console.Write($"  vs 0.8.0: mean abs {sum / cnt:F2} max {max}");
            }
            Console.WriteLine();
        }
        var sw = Stopwatch.StartNew();
        var full = new byte[c.Frames.Count][]; foreach (var f in c.Frames.Take(a.Has("quick") ? 50 : int.MaxValue).Select((f, i) => (f, i))) full[f.i] = Thumbs.Thumb(Imaging.DecodeJpeg(data.AsSpan((int)f.f.Off, f.f.Len).ToArray()));
        var fl = full.Where(x => x != null).ToList(); long msFull = sw.ElapsedMilliseconds; Console.WriteLine($"0.8.0 full decode, 1 thread: {fl.Count} frames, {msFull} ms, {msFull * 1000.0 / fl.Count:F0} us/frame");
        sw.Restart(); var par = Thumbs.ChunkManaged(data, c, tw, th, threads, n); Report($"managed full, {threads} thr", par, sw.ElapsedMilliseconds);
        var ff = a.Get("ffmpeg") ?? "ffmpeg";
        foreach (var th3 in new[] { 1, 2, threads }.Distinct()) { sw.Restart(); var f1 = await Thumbs.ChunkFfmpegParallelAsync(ff, data, c, tw, th, th3, n, default); Report($"ffmpeg lowres3 x{th3}", f1, sw.ElapsedMilliseconds); }
        return 0;
    }
}
