using System.Text.Json.Nodes;

namespace IndexNode.Tests;

public class CoverTests
{
    private static JsonArray Cands(string json) => (JsonArray)JsonNode.Parse(json)!;

    [Fact]
    public void Lead_shot_is_describes_pick_when_unmasked()
    {
        var c = Cands("""[{"k":0,"t":5,"quality":9,"masked":false,"box":[1,2,3,4]},{"k":1,"t":9,"quality":1,"masked":false,"box":[10,20,30,40]}]""");
        var s = Publish.PickLeadShot(c, 1)!.Value;
        Assert.Equal(1, s.K); Assert.Equal(9, s.T); Assert.Equal([10d, 20, 30, 40], s.Box);
    }

    [Fact]
    public void Lead_shot_skips_a_masked_pick_for_the_best_unmasked_one()
    {
        var c = Cands("""[{"k":0,"t":5,"quality":3,"masked":false},{"k":1,"t":9,"quality":8,"masked":true},{"k":2,"t":12,"quality":6,"masked":false}]""");
        Assert.Equal(2, Publish.PickLeadShot(c, 1)!.Value.K);   // pick 1 is masked: best unmasked by quality
        Assert.Equal(1, Publish.PickLeadShot(Cands("""[{"k":0,"t":1,"quality":1,"masked":true},{"k":1,"t":2,"quality":2,"masked":true}]"""), 1)!.Value.K); // all masked: the pick
        Assert.Null(Publish.PickLeadShot(Cands("[]"), null));
        Assert.Null(Publish.PickLeadShot(null, 0));
    }

    [Fact]
    public void Cover_shots_come_from_the_start_window_then_the_first_half_never_the_last_quarter()
    {
        // 9080 s drama, cutoff 1089.6 s (12%)
        var c = Cands("""[{"k":0,"t":8622.5,"quality":9,"masked":false},{"k":1,"t":900,"quality":2,"masked":false},{"k":2,"t":3000,"quality":8,"masked":false}]""");
        var s = Publish.PickCoverShot(c, 0, 1089.6, 9080)!.Value;           // the late pick is outside every window
        Assert.Equal(1, s.K); Assert.Equal("start", s.Window);
        // nothing in the start window: widen to the first half (3000 s <= 4540 s)
        s = Publish.PickCoverShot(Cands("""[{"k":0,"t":8622.5,"quality":9,"masked":false},{"k":2,"t":3000,"quality":8,"masked":false}]"""), 0, 1089.6, 9080)!.Value;
        Assert.Equal(2, s.K); Assert.Equal("half", s.Window);
        // only late shots: none (never the last 25%)
        Assert.Null(Publish.PickCoverShot(Cands("""[{"k":0,"t":8622.5,"quality":9,"masked":false},{"k":1,"t":6900,"quality":9,"masked":false}]"""), 0, 1089.6, 9080));
        // a masked start-window shot widens instead of being shown
        s = Publish.PickCoverShot(Cands("""[{"k":0,"t":500,"quality":9,"masked":true},{"k":1,"t":2500,"quality":3,"masked":false}]"""), 0, 1089.6, 9080)!.Value;
        Assert.Equal(1, s.K); Assert.Equal("half", s.Window);
    }

    /// <summary>The real ffmpeg: other tests put fake ffmpeg scripts first on PATH, so walk PATH and take the first one that answers -version.</summary>
    private static async Task<string?> RealFfmpegAsync()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (!File.Exists(f)) continue;
            try { var r = await Proc.RunAsync(f, ["-version"], default); if (r.ExitCode == 0 && r.Stdout.Contains("ffmpeg version")) return f; } catch { }
        }
        return null;
    }

    /// <summary>"WxH" from the JPEG's SOF marker.</summary>
    private static string JpegSize(byte[] b)
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

    [Fact]
    public async Task Cover_filter_gives_a_360x640_crop_centred_and_clamped()
    {
        var ff = await RealFfmpegAsync(); if (ff == null) return;
        var dir = Path.Combine(Path.GetTempPath(), "indexnode-cover-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            foreach (var (w, h, cx, cy) in new (int, int, double?, double?)[] { (1280, 720, 900, 300), (1280, 720, null, null), (1280, 720, 5, 5), (720, 1280, 360, 400), (1000, 1000, 990, 990) })
            {
                var src = Path.Combine(dir, "s.jpg"); var o = Path.Combine(dir, $"o{w}x{h}.jpg");
                var r = await Proc.RunAsync(ff, ["-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc=size={w}x{h}:rate=1", "-frames:v", "1", src], default); Assert.Equal(0, r.ExitCode);
                r = await Proc.RunAsync(ff, ["-v", "error", "-y", "-i", src, "-vf", Publish.CoverFilter(cx, cy), "-frames:v", "1", o], default); Assert.True(r.ExitCode == 0, r.Stderr);
                Assert.Equal("360x640", JpegSize(File.ReadAllBytes(o))); // read from the file itself: other tests change PATH, so no ffprobe lookup
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
