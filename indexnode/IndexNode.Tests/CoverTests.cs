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

    // ---- expression score: eyes open and toward the camera, mouth closed or a slight smile; the vision score decides

    [Fact]
    public void Best_expression_wins_inside_the_allowed_window()
    {
        var c = Cands("""[{"k":0,"t":300,"quality":9,"masked":false},{"k":1,"t":600,"quality":2,"masked":false},{"k":2,"t":900,"quality":5,"masked":false},{"k":3,"t":8600,"quality":9,"masked":false}]""");
        int?[] sc = [4, 9, 7, 10];  // k=3 scores best but is in the last quarter
        var s = Publish.PickByExpression(c, 0, 1089.6, 9080, sc)!.Value;
        Assert.Equal(1, s.K); Assert.Equal("start", s.Window);   // highest score in the window beats describe's pick and the quality
    }

    [Fact]
    public void Ties_go_to_describes_pick_then_quality_and_low_scores_are_rejected()
    {
        var c = Cands("""[{"k":0,"t":100,"quality":3,"masked":false},{"k":1,"t":200,"quality":8,"masked":false},{"k":2,"t":300,"quality":5,"masked":false}]""");
        Assert.Equal(2, Publish.PickByExpression(c, 2, 1000, 9080, new int?[] { 8, 8, 8 })!.Value.K);      // tie: describe's pick
        Assert.Equal(1, Publish.PickByExpression(c, null, 1000, 9080, new int?[] { 8, 8, 8 })!.Value.K);   // tie: quality
        Assert.Null(Publish.PickByExpression(c, 0, 1000, 9080, new int?[] { 5, 2, null }));                // mid-speech, startled, unscored: none
    }

    [Fact]
    public void A_start_window_without_a_flattering_shot_widens_to_the_first_half_and_masked_faces_never_win()
    {
        var c = Cands("""[{"k":0,"t":500,"quality":9,"masked":false},{"k":1,"t":2500,"quality":4,"masked":false},{"k":2,"t":900,"quality":9,"masked":true}]""");
        var s = Publish.PickByExpression(c, 0, 1089.6, 9080, new int?[] { 3, 8, 10 })!.Value;   // 0 is blinking (3), 2 is masked
        Assert.Equal(1, s.K); Assert.Equal("half", s.Window);
        Assert.Null(Publish.PickByExpression(Cands("""[{"k":0,"t":6000,"quality":9,"masked":false}]"""), 0, 1089.6, 9080, new int?[] { 10 })); // only after 50% and past the late limit
    }

    [Fact]
    public void Two_shot_needs_both_faces_flattering_and_takes_the_best_score()
    {
        var c = Cands("""[{"k":0,"clusters":[1,2]},{"k":1,"clusters":[1,2]},{"k":2,"clusters":[1,2]},{"k":3,"clusters":[1,5]}]""");
        Assert.Equal(1, Publish.PickTwoShot(c, [1], [2], 0, new int?[] { 6, 9, 9, 10 }));   // k=3 lacks the second lead; 1 and 2 tie, the first listed wins
        Assert.Equal(2, Publish.PickTwoShot(c, [1], [2], 2, new int?[] { 6, 9, 9, 10 }));   // tie: describe's pick
        Assert.Null(Publish.PickTwoShot(c, [1], [2], 0, new int?[] { 5, 3, null, 10 }));    // none flattering: no together cover
        Assert.Equal(0, Publish.PickTwoShot(c, [1], [2], 0));                               // no scores (older describe): the old rule
    }

    [Fact]
    public void Expression_scores_are_cleaned_and_read_back()
    {
        var o = Describe.CleanExpression(JsonNode.Parse("""{"0":[11,-3,7.6,"x",null],"poster":[8]}"""));
        Assert.Equal("[10,0,8,null,null]", o["0"]!.ToJsonString());
        var d = new JsonObject { ["expression"] = o };
        Assert.Equal([10, 0, 8, null, null], Publish.ExpressionScores(d, "0")!.ToArray());
        Assert.Null(Publish.ExpressionScores(new JsonObject(), "0"));
        Assert.Empty(Describe.CleanExpression(null));
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
