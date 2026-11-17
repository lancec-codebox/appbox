using System.Text.Json;

namespace IndexNode.Tests;

public class BarsTests
{
    /// <summary>A w x h gray frame: rows [y0, y1) at luminance <paramref name="v"/>, the rest at <paramref name="rest"/>.</summary>
    static byte[] Frame(int w, int h, byte rest, params (int Y0, int Y1, byte V)[] rows)
    {
        var f = new byte[w * h]; Array.Fill(f, rest);
        foreach (var (y0, y1, v) in rows) for (int y = y0; y < y1; y++) Array.Fill(f, v, y * w, w);
        return f;
    }
    static byte[] Cols(int w, int h, byte rest, params (int X0, int X1, byte V)[] cols)
    {
        var f = new byte[w * h]; Array.Fill(f, rest);
        foreach (var (x0, x1, v) in cols) for (int y = 0; y < h; y++) for (int x = x0; x < x1; x++) f[y * w + x] = v;
        return f;
    }
    static List<byte[]> Many(int n, Func<int, byte[]> make) => Enumerable.Range(0, n).Select(make).ToList();

    [Fact]
    public void Letterbox_bars_on_every_frame_are_cut()
    {
        var fr = Many(24, i => Frame(64, 36, (byte)(80 + i * 5), (0, 4, 0), (32, 36, 5)));
        Assert.Equal(new BarCrop(4, 4, 0, 0), Bars.Detect(fr, 64, 36));
    }

    [Fact]
    public void Uneven_bars_keep_an_even_size_and_the_picture_one_row_more()
    {
        // 29 + 18 on 360 rows leaves 313: the cropped height is made even by giving back one bar row
        var fr = Many(24, i => Frame(640, 360, 120, (0, 29, 0), (342, 360, 10)));
        var c = Bars.Detect(fr, 640, 360);
        Assert.Equal(29, c.Top); Assert.Equal(17, c.Bottom); Assert.Equal(0, c.Height(360) % 2);
    }

    [Fact]
    public void Portrait_pillar_bars_left_and_right_are_cut()
    {
        var fr = Many(24, i => Cols(36, 64, (byte)(90 + i), (0, 3, 0), (33, 36, 0)));
        Assert.Equal(new BarCrop(0, 0, 3, 3), Bars.Detect(fr, 36, 64));
    }

    [Fact]
    public void A_dark_edge_that_is_dark_in_only_some_frames_is_no_bar()
    {
        var fr = Many(24, i => i % 2 == 0 ? Frame(64, 36, 120, (0, 6, 0)) : Frame(64, 36, 120));
        Assert.Equal(BarCrop.None, Bars.Detect(fr, 64, 36));
        var one = Many(24, i => i == 11 ? Frame(64, 36, 120) : Frame(64, 36, 120, (0, 6, 0), (30, 36, 0)));
        Assert.Equal(BarCrop.None, Bars.Detect(one, 64, 36));
    }

    [Fact]
    public void A_bar_run_stops_at_the_first_row_that_is_not_dark_in_every_frame()
    {
        // subtitles inside the bottom bar: rows with text are bright in some frame, so the bottom crop is partial
        var fr = Many(24, i => i % 3 == 0 ? Frame(100, 100, 120, (0, 10, 0), (90, 100, 0), (95, 97, 230)) : Frame(100, 100, 120, (0, 10, 0), (90, 100, 0)));
        var c = Bars.Detect(fr, 100, 100);
        Assert.Equal(10, c.Top); Assert.Equal(2, c.Bottom); // 3 dark rows below the text; one is given back to keep the height (87) even
    }

    [Fact]
    public void Edges_are_judged_by_the_mean_luminance_below_20()
    {
        Assert.Equal(new BarCrop(5, 5, 0, 0), Bars.Detect(Many(24, _ => Frame(40, 100, 128, (0, 5, 19), (95, 100, 19))), 40, 100));
        Assert.Equal(BarCrop.None, Bars.Detect(Many(24, _ => Frame(40, 100, 128, (0, 5, 20), (95, 100, 20))), 40, 100));
    }

    [Fact]
    public void A_pair_under_2_percent_is_left_alone_and_exactly_2_percent_is_cut()
    {
        Assert.Equal(BarCrop.None, Bars.Detect(Many(24, _ => Frame(40, 200, 128, (0, 2, 0), (199, 200, 0))), 40, 200));   // 3 of 200 rows
        Assert.Equal(new BarCrop(2, 2, 0, 0), Bars.Detect(Many(24, _ => Frame(40, 200, 128, (0, 2, 0), (198, 200, 0))), 40, 200)); // 4 of 200 = 2%
    }

    [Fact]
    public void An_all_dark_or_nearly_all_dark_picture_is_never_cropped()
    {
        Assert.Equal(BarCrop.None, Bars.Detect(Many(24, _ => Frame(64, 36, 0)), 64, 36));
        Assert.Equal(BarCrop.None, Bars.Detect(Many(24, _ => Frame(64, 36, 12)), 64, 36));
        // only 15% would remain: no crop
        Assert.Equal(BarCrop.None, Bars.Detect(Many(24, _ => Frame(40, 100, 0, (40, 55, 200))), 40, 100));
        // 25% remains (an odd 25 rows, one given back): cut
        Assert.Equal(new BarCrop(40, 34, 0, 0), Bars.Detect(Many(24, _ => Frame(40, 100, 0, (40, 65, 200))), 40, 100));
    }

    [Fact]
    public void Samples_are_24_evenly_between_5_and_95_percent_of_the_duration()
    {
        var t = Bars.SampleTimes(1000);
        Assert.Equal(24, t.Length); Assert.Equal(50, t[0], 6); Assert.Equal(950, t[^1], 6); Assert.Equal(t[1] - t[0], t[12] - t[11], 6);
    }

    [Fact]
    public void Fewer_than_two_frames_never_crop()
    {
        Assert.Equal(BarCrop.None, Bars.Detect([Frame(64, 36, 100, (0, 4, 0))], 64, 36));
        Assert.Equal(BarCrop.None, Bars.Detect([], 64, 36));
    }

    [Fact]
    public void The_subtitle_band_follows_the_crop_of_the_stored_frames()
    {
        var lines = """{"band":{"y":300,"h":40,"width":640},"zh":[{"start":0,"end":3,"text":"a","boxes":[{"x0":100,"y0":320,"x1":500,"y1":340,"text":"a","score":1}]}],"en":[]}""";
        var plain = SubtitleBand.Parse(System.Text.Encoding.UTF8.GetBytes(lines))!.At(1, 640)!;
        Assert.Equal([100, 320, 400, 20], plain);
        var sb = SubtitleBand.Parse(System.Text.Encoding.UTF8.GetBytes(lines))!; sb.Top = 44; sb.Left = 0;
        Assert.Equal([100, 276, 400, 20], sb.At(1, 640)!);
        var o = System.Text.Json.Nodes.JsonNode.Parse("""{"source":{"width":640,"cropLeft":0,"cropTop":44}}""")!.AsObject();
        Assert.Equal(276, SubtitleBand.Parse(System.Text.Encoding.UTF8.GetBytes(lines))!.WithCropOf(o)!.At(1, 640)![1]);
    }
}

/// <summary>The frames stage on synthetic ffmpeg videos (lavfi, no store).</summary>
public class FramesBarsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "indexnode-bars-" + Guid.NewGuid().ToString("N")[..8]);
    public FramesBarsTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    /// <summary>The real ffmpeg and ffprobe (other tests put fakes first on PATH): the first of each that answers -version.</summary>
    static async Task<(string Ff, string Probe)?> RealAsync()
    {
        async Task<string?> Find(string name)
        {
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = Path.Combine(d, OperatingSystem.IsWindows() ? name + ".exe" : name);
                if (!File.Exists(f)) continue;
                try { var r = await Proc.RunAsync(f, ["-version"], default); if (r.ExitCode == 0 && r.Stdout.Contains(" version")) return f; } catch { }
            }
            return null;
        }
        var ff = await Find("ffmpeg"); var pr = await Find("ffprobe");
        return ff != null && pr != null ? (ff, pr) : null;
    }

    async Task<string> MakeAsync((string Ff, string Probe) t, string name, string filter, double seconds = 12)
    {
        var path = Path.Combine(dir, name);
        var r = await Proc.RunAsync(t.Ff, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", filter + $",format=yuv420p", "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "mpeg4", "-q:v", "2", path], default);
        Assert.True(r.ExitCode == 0, r.Stderr);
        return path;
    }

    async Task<(JsonElement Manifest, Dictionary<string, byte[]> Put)> RunAsync((string Ff, string Probe) t, string video, double duration = 12)
    {
        var tools = new Tools(new HttpClient()); tools.UseFfmpeg(t.Ff, t.Probe);
        var put = new Dictionary<string, byte[]>(); var work = Path.Combine(dir, "work-" + Path.GetFileNameWithoutExtension(video)); Directory.CreateDirectory(work);
        await Frames.ProduceAsync(tools, video, work, new FrameSettings(), 1, duration, _ => { }, (n, b, _) => { lock (put) put[n] = b; return Task.CompletedTask; }, default);
        return (JsonDocument.Parse(put["frames/manifest.json"]).RootElement.Clone(), put);
    }

    static (int T, int B, int L, int R) CropOf(JsonElement m) { var c = m.GetProperty("crop"); return (c.GetProperty("top").GetInt32(), c.GetProperty("bottom").GetInt32(), c.GetProperty("left").GetInt32(), c.GetProperty("right").GetInt32()); }
    static (int W, int H) SizeOf(JsonElement m) => (m.GetProperty("width").GetInt32(), m.GetProperty("height").GetInt32());

    [Fact]
    public async Task A_letterboxed_video_is_stored_without_its_bars_and_the_manifest_says_so()
    {
        if (await RealAsync() is not { } t) return;
        var v = await MakeAsync(t, "bars.mp4", "testsrc=s=640x272:r=10,pad=640:360:0:44:black");
        var (m, put) = await RunAsync(t, v);
        Assert.Equal((640, 272), SizeOf(m)); Assert.Equal((44, 44, 0, 0), CropOf(m));
        Assert.Equal(Frames.FramesVersion, m.GetProperty("framesVersion").GetInt32());
        var src = m.GetProperty("source"); Assert.Equal(640, src.GetProperty("width").GetInt32()); Assert.Equal(360, src.GetProperty("height").GetInt32());
        Assert.Equal(360, src.GetProperty("scaledHeight").GetInt32());
        // the chunk index and the stored JPEGs have the cropped size too
        var chunk = put.Keys.Single(k => k.StartsWith("frames/") && k.EndsWith(".json") && !k.Contains("manifest") && !k.Contains("shots"));
        var idx = JsonDocument.Parse(put[chunk]).RootElement; Assert.Equal(272, idx.GetProperty("height").GetInt32());
        var first = put[chunk.Replace(".json", ".mjpeg")]; int len = idx.GetProperty("frames")[0].GetProperty("len").GetInt32();
        Assert.Equal("640x272", JpegSize(first.AsSpan(0, len).ToArray()));
        // a later stage opening the stored set learns the crop
        var od = Path.Combine(dir, "opened"); Directory.CreateDirectory(od); foreach (var (n, b) in put) File.WriteAllBytes(Path.Combine(od, Path.GetFileName(n)), b);
        var cd = ChunkDir.Open(od); Assert.Equal(44, cd.CropTop); Assert.Equal(0, cd.CropLeft); Assert.Equal(272, cd.H);
    }

    [Fact]
    public async Task A_video_without_bars_is_stored_as_before_with_a_zero_crop()
    {
        if (await RealAsync() is not { } t) return;
        var v = await MakeAsync(t, "clean.mp4", "testsrc=s=640x360:r=10");
        var (m, _) = await RunAsync(t, v);
        Assert.Equal((640, 360), SizeOf(m)); Assert.Equal((0, 0, 0, 0), CropOf(m));
    }

    [Fact]
    public async Task An_all_black_or_very_dark_video_is_not_cropped()
    {
        if (await RealAsync() is not { } t) return;
        foreach (var (name, filter) in new[] { ("black.mp4", "color=c=black:s=640x360:r=10"), ("dark.mp4", "color=c=0x101010:s=640x360:r=10") })
        {
            var (m, _) = await RunAsync(t, await MakeAsync(t, name, filter));
            Assert.Equal((640, 360), SizeOf(m)); Assert.Equal((0, 0, 0, 0), CropOf(m));
        }
    }

    [Fact]
    public async Task A_dark_edge_in_only_some_frames_is_not_a_bar()
    {
        if (await RealAsync() is not { } t) return;
        var v = await MakeAsync(t, "vignette.mp4", "testsrc=s=640x360:r=10,drawbox=x=0:y=0:w=640:h=40:color=black:t=fill:enable='lt(t,6)',drawbox=x=0:y=320:w=640:h=40:color=black:t=fill:enable='gt(t,4)'");
        var (m, _) = await RunAsync(t, v);
        Assert.Equal((640, 360), SizeOf(m)); Assert.Equal((0, 0, 0, 0), CropOf(m));
    }

    [Fact]
    public async Task Subtitles_in_the_bottom_bar_stay_readable_by_the_subtitle_reader()
    {
        if (await RealAsync() is not { } t) return;
        // a white "subtitle" box inside the bottom bar (rows 322-337 of 360) on some frames
        var v = await MakeAsync(t, "subs.mp4", "testsrc=s=640x272:r=10,pad=640:360:0:44:black,drawbox=x=120:y=322:w=400:h=16:color=white:t=fill:enable='between(t,2,9)'");
        var (m, _) = await RunAsync(t, v);
        var (T, B, _, _) = CropOf(m);
        // the bar run stops at the subtitle rows: the top bar goes, the bottom crop is partial (22 rows below the box) and the stored frame still holds the box
        Assert.Equal(44, T); Assert.True(B <= 22, $"bottom crop {B}"); Assert.Equal(360 - T - B, SizeOf(m).H);
        // the reader works on the source video at full size, never on the stored frames: its size and its band pixels are those of the source
        var tools = new Tools(new HttpClient()); tools.UseFfmpeg(t.Ff, t.Probe);
        Assert.Equal((640, 360), await OcrSubs.SizeAsync(tools, v, default));
        var band = await Proc.RunAsync(t.Ff, ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", "5", "-i", v, "-frames:v", "1", "-vf", "crop=640:40:0:320,format=gray", "-f", "rawvideo", "-"], default);
        Assert.True(band.ExitCode == 0);
    }

    static string JpegSize(byte[] b)
    {
        for (int i = 2; i + 9 < b.Length;)
        {
            if (b[i] != 0xFF) { i++; continue; }
            int m = b[i + 1];
            if (m is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return $"{b[i + 7] << 8 | b[i + 8]}x{b[i + 5] << 8 | b[i + 6]}";
            i += 2 + (b[i + 2] << 8 | b[i + 3]);
        }
        return "";
    }
}
