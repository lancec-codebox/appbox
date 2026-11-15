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
    public void Expression_scores_are_cleaned_and_read_back()
    {
        var o = Describe.CleanExpression(JsonNode.Parse("""{"0":[11,-3,7.6,"x",null],"poster":[8]}"""));
        Assert.Equal("[10,0,8,null,null]", o["0"]!.ToJsonString());
        var d = new JsonObject { ["expression"] = o };
        Assert.Equal([10, 0, 8, null, null], Publish.ExpressionScores(d, "0")!.ToArray());
        Assert.Null(Publish.ExpressionScores(new JsonObject(), "0"));
        Assert.Empty(Describe.CleanExpression(null));
    }

    [Fact]
    public void Focus_is_the_face_centre_as_fractions_of_the_cover_and_follows_the_clamped_crop()
    {
        Assert.Equal([0.5, 0.5], Publish.CoverFocus(1920, 1080, null, null));
        var f = Publish.CoverFocus(1920, 1080, 960, 400);           // crop is 607.5 wide, centred: face dead centre horizontally
        Assert.Equal(0.5, f[0], 3); Assert.Equal(400.0 / 1080, f[1], 3);
        var e = Publish.CoverFocus(1920, 1080, 100, 540);           // near the left edge the crop clamps to x=0: face sits left of centre
        Assert.Equal(100 / 607.5, e[0], 3);
        var r = Publish.CoverFocus(1920, 1080, 1900, 540);          // right edge
        Assert.Equal((1900 - (1920 - 607.5)) / 607.5, r[0], 3);
    }

    [Fact]
    public void Jpeg_size_is_read_from_the_frame_header()
    {
        byte[] j = [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0x04, 0x38, 0x07, 0x80, 3, 0, 0, 0];
        Assert.Equal((1920, 1080), Publish.JpegSize(j));
        Assert.Null(Publish.JpegSize([0xFF, 0xD8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
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
    public void Cover_is_cut_in_process_360x640_centred_on_the_face_and_clamped()
    {
        foreach (var (w, h, cx, cy) in new (int, int, double?, double?)[] { (1280, 720, 900, 300), (1280, 720, null, null), (1280, 720, 5, 5), (720, 1280, 360, 400), (1000, 1000, 990, 990) })
        {
            // left half red, right half blue: the crop window must follow the face centre
            var im = Rgb.Blank(w, h); for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int o = (y * w + x) * 3; if (x < w / 2) im.Data[o] = 255; else im.Data[o + 2] = 255; }
            var jpg = Publish.CropCover(Imaging.EncodeJpeg(im), cx, cy);
            Assert.Equal("360x640", JpegSize(jpg));
            var c = Publish.CoverCrop(w, h, cx, cy); var o2 = Imaging.DecodeJpeg(jpg);
            double splitAt = (w / 2.0 - c[0] * w) / (c[2] * w); // where the colour edge falls across the crop, 0..1
            var left = o2.Data[(o2.H / 2 * o2.W + 2) * 3]; var right = o2.Data[(o2.H / 2 * o2.W + o2.W - 3) * 3 + 2];
            Assert.Equal(splitAt > 0.02, left > 128); Assert.Equal(splitAt < 0.98, right > 128);
        }
    }

    [Fact]
    public void SoloRule_SkipsCrowdedFrame_AndFallbackCropsTight()
    {
        var faces = System.Text.Json.Nodes.JsonNode.Parse("""{"faces":[{"t":10,"box":[100,100,100,100]},{"t":10,"box":[220,100,100,100]},{"t":20,"box":[100,100,100,100]},{"t":20,"box":[300,10,20,20]}]}""")!.AsObject();
        var c = Cands("""[{"k":0,"t":10,"quality":0.9,"masked":false,"box":[100,100,100,100]},{"k":1,"t":20,"quality":0.5,"masked":false,"box":[100,100,100,100]}]""");
        Publish.MarkSolo(c, faces);
        Assert.False(Publish.IsCrowded(c[1] as System.Text.Json.Nodes.JsonObject)); // 4% background face is fine
        Assert.True(Publish.IsCrowded(c[0] as System.Text.Json.Nodes.JsonObject));
        Assert.Equal(1, Publish.PickByExpression(c, 0, 1000, 9080, new int?[] { 9, 7 })!.Value.K);
        Assert.Equal(1, Publish.PickLeadShot(c, 0)!.Value.K);
        // none solo: today's choice, window excludes the neighbour
        var only = Cands("""[{"k":0,"t":10,"quality":0.9,"masked":false,"box":[100,100,100,100]}]"""); Publish.MarkSolo(only, faces);
        Assert.Equal(0, Publish.PickByExpression(only, 0, 1000, 9080, new int?[] { 9 })!.Value.K);
        var (crop, _) = Publish.TightWindow([100, 100, 100, 100], Publish.OtherBoxes(only[0] as System.Text.Json.Nodes.JsonObject), 1.25, 250, 0.45, 360, 640);
        Assert.True((crop[0] + crop[2]) * 360 <= 220.01);
    }

    [Fact]
    public void Cast_candidates_come_from_solo_frames_first()
    {
        var emb = (int n) => { var v = new float[8]; v[n % 8] = 1; return v; };
        var faces = Enumerable.Range(0, 20).Select(i => new FaceRec { T = i * 100, Quality = i < 10 ? 0.95 : 0.5, Other = i < 10 ? 1.0 : 0, Emb = emb(i), Cluster = 0 }).ToList();
        var pick = CastLogic.Diverse(faces, 12);
        Assert.Equal(12, pick.Count);
        Assert.Equal(10, pick.Count(f => f.Solo));            // all ten solo frames, though lower quality
        Assert.Equal(2, pick.Count(f => !f.Solo));            // crowded ones only fill the gap
        Assert.All(CastLogic.Diverse(faces.Take(10).Concat(faces.Skip(10).Take(15)).ToList(), 5), f => Assert.True(f.Solo));
    }
}
