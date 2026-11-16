namespace IndexNode;

/// <summary>Developer check: <c>dino-bench --dir &lt;folder with NNNN.mjpeg + NNNN.json&gt; --chunk 0001 [--count 20] [--out vectors.f16] [--model path] [--threads N]</c>: the first --count frames of a chunk through the step's code; writes the float16 rows.</summary>
public static class DinoBench
{
    public static async Task<int> RunAsync(Args a)
    {
        if (a.Get("fetch") is { } vid)
        {   // download frames/ chunks of a stored job (hydrating ghosts): dino-bench --fetch <id> --dir <folder> [--chunks 0001,0002]
            var cfg = NodeConfig.Load(); var h0 = Commands.NewHttp(); var store = new RestStore(h0, cfg.Sas); var ps = new Paths(a.Get("prefix") ?? "index/"); var d0 = a.Get("dir") ?? ".";
            Directory.CreateDirectory(d0);
            foreach (var cn in (a.Get("chunks") ?? "0001").Split(','))
            {
                File.WriteAllBytes(Path.Combine(d0, cn + ".json"), await FrameChunks.ReadAsync(store, ps.Video(vid, $"frames/{cn}.json"), default, false) ?? throw new InvalidOperationException("no " + cn + ".json"));
                File.WriteAllBytes(Path.Combine(d0, cn + ".mjpeg"), await FrameChunks.ReadAsync(store, ps.Video(vid, $"frames/{cn}.mjpeg"), default) ?? throw new InvalidOperationException("no " + cn + ".mjpeg"));
                Console.WriteLine($"{vid} {cn}: {new FileInfo(Path.Combine(d0, cn + ".mjpeg")).Length} bytes");
            }
            return 0;
        }
        var dir = a.Get("dir") ?? "."; var n = a.Get("chunk") ?? "0001"; int threads = a.Get("threads") is { } t ? int.Parse(t) : Environment.ProcessorCount;
        var data = File.ReadAllBytes(Path.Combine(dir, n + ".mjpeg")); var c = FrameChunks.ParseChunk(n, File.ReadAllBytes(Path.Combine(dir, n + ".json")), n + ".json");
        int count = a.Get("count") is { } cs ? Math.Min(int.Parse(cs), c.Frames.Count) : c.Frames.Count; c = c with { Frames = c.Frames.Take(count).ToList() };
        var first = Imaging.DecodeJpeg(data.AsSpan((int)c.Frames[0].Off, c.Frames[0].Len).ToArray()); var (w, h) = Dino.InputSize(first.W, first.H);
        var tools = new Tools(new HttpClient()); var ffmpeg = await tools.FfmpegAsync(default); using var http = new HttpClient();
        var mf = a.Has("int8") ? Models.DinoSmallInt8 : Models.DinoSmall; var path = a.Get("model") ?? await Models.EnsureAsync(mf, http, default);
        using var session = OrtOptions.Session(path, threads); var tm = new Dino.Timing(); var sw = System.Diagnostics.Stopwatch.StartNew();
        var f16 = await Dino.ChunkAsync(ffmpeg, data, c, w, h, 32, threads, Dino.OnnxEmbed(session), "bench", tm, default); tm.Frames = count; tm.WallMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"{mf.Id}: chunk {n} {first.W}x{first.H} -> {w}x{h}, {threads} threads: {tm}");
        if (a.Get("out") is { } o) File.WriteAllBytes(o, f16);
        return 0;
    }
}
