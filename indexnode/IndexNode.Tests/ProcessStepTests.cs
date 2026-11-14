namespace IndexNode.Tests;

public class OcrPostTests
{
    [Fact]
    public void Db_boxes_from_a_synthetic_probability_map()
    {
        // 40x20 map: one bright 20x6 block at x 10..29, y 7..12, one weak blob, one tiny speck
        int h = 20, w = 40; var p = new float[h * w];
        for (int y = 7; y <= 12; y++) for (int x = 10; x <= 29; x++) p[y * w + x] = 0.9f;
        for (int y = 1; y <= 4; y++) for (int x = 2; x <= 9; x++) p[y * w + x] = 0.4f;   // above thresh 0.3 but mean below box thresh 0.5
        p[17 * w + 35] = 0.95f;                                                            // a single pixel
        var boxes = DbPostProcess.Process(p, h, w, w, h);
        var b = Assert.Single(boxes);
        Assert.True(b.Score > 0.85);
        // grown by area*1.6/perimeter around the 19x5 block (min-area box of pixel centres): ~2.5 px each side, clamped to the map
        Assert.InRange(b.MinX, 6, 9); Assert.InRange(b.MaxX, 31, 34);
        Assert.InRange(b.MinY, 3, 6); Assert.InRange(b.MaxY, 14, 17);
        // corner order tl, tr, br, bl
        Assert.True(b.Corners[0].X < b.Corners[1].X && b.Corners[0].Y < b.Corners[3].Y);
    }

    [Fact]
    public void Db_boxes_scale_to_the_destination_size()
    {
        int h = 20, w = 40; var p = new float[h * w];
        for (int y = 7; y <= 12; y++) for (int x = 10; x <= 29; x++) p[y * w + x] = 0.9f;
        var b = Assert.Single(DbPostProcess.Process(p, h, w, 80, 40));
        Assert.InRange(b.MinX, 12, 18); Assert.InRange(b.MaxX, 62, 68);
    }

    [Fact]
    public void Mini_box_of_a_rotated_rectangle_and_the_empty_map()
    {
        var pts = new List<Pt>(); // 10x2 rectangle rotated 90 degrees (2 wide, 10 tall)
        for (int y = 0; y < 10; y++) for (int x = 0; x < 2; x++) pts.Add(new Pt(x, y));
        var (box, side) = DbPostProcess.MiniBox(pts);
        Assert.Equal(1, side, 3);
        Assert.Equal(9, box.Max(q => q.Y) - box.Min(q => q.Y), 3);
        Assert.Empty(DbPostProcess.Process(new float[100], 10, 10, 10, 10));
    }

    [Fact]
    public void Ctc_decoding_merges_repeats_drops_blanks_and_keeps_a_repeat_after_a_blank()
    {
        var dec = new CtcDecoder(["你", "好", "a"]);                 // classes: 0 blank, 1 你, 2 好, 3 a, 4 space
        Assert.Equal(5, dec.ClassCount);
        float[] Row(int k, float p) { var r = new float[5]; Array.Fill(r, (1 - p) / 4); r[k] = p; return r; }
        // 你 你 blank 你 好 好 blank a  -> 你你好a
        var seq = new[] { Row(1, .9f), Row(1, .8f), Row(0, .99f), Row(1, .7f), Row(2, .6f), Row(2, .6f), Row(0, .9f), Row(3, .5f) };
        var (text, score) = dec.Decode(seq.SelectMany(r => r).ToArray(), seq.Length, 5);
        Assert.Equal("你你好a", text);
        Assert.Equal((.9 + .7 + .6 + .5) / 4, score, 3);
        Assert.Equal(("", 0), dec.Decode(new[] { Row(0, .9f), Row(0, .9f) }.SelectMany(r => r).ToArray(), 2, 5));
        Assert.Equal(" ", dec.Decode(Row(4, .9f), 1, 5).Text);        // the trailing space class
    }

    [Fact]
    public void Detector_input_is_native_size_with_limit_max_480()
    {
        Assert.Equal((480, 160), OcrEngine.DetSize(480, 160));      // the test band: not scaled at all
        Assert.Equal((480, 192), OcrEngine.DetSize(480, 190));
        Assert.Equal((480, 224), OcrEngine.DetSize(960, 450));      // longer side above 480 is scaled down to it
        Assert.Equal((32, 32), OcrEngine.DetSize(10, 10));
    }

    [Fact]
    public void Opencc_longest_match_beats_single_characters()
    {
        var cc = new OpenCc(["# comment\n乾坤\t乾坤\n乾\t干\n發財\t发财\n發\t发 髮\n髮\t发\n"]);
        Assert.Equal("干净", cc.Convert("乾净"));
        Assert.Equal("乾坤", cc.Convert("乾坤"));          // the phrase maps to itself and wins over 乾 -> 干
        Assert.Equal("发财a发", cc.Convert("發財a發"));     // first of several values; ASCII untouched
        Assert.Equal("", cc.Convert(""));
    }

    [Fact]
    public void Opencc_with_the_shipped_dictionaries()
    {
        var cc = OpenCc.T2S;
        Assert.Equal("夫君顾长渊奉旨出征", cc.Convert("夫君顧長淵奉旨出征"));
        Assert.Equal("干杯", cc.Convert("乾杯"));       // 乾杯 is not a phrase; 乾 -> 干
        Assert.Equal("皇后", cc.Convert("皇后"));
        Assert.Equal("头发", cc.Convert("頭髮"));
    }

    private static FrameText F(double t, params (string Text, int Y)[] lines) =>
        new(t, lines.Select(l => new OcrBox(10, l.Y, 400, l.Y + 20, l.Text, 0.9)).ToList());

    [Fact]
    public void Cues_merge_runs_with_edit_distance_up_to_two_and_pick_the_majority_text()
    {
        var frames = new List<FrameText> { F(0, ("你好吗朋友", 5)), F(.25, ("你好吗朋友", 5)), F(.5, ("你好吗朋支", 5)), F(.75, ("你好吗朋友", 5)), F(1.0, ("再见朋友们吧", 5)), F(1.25, ("再见朋友们吧", 5)) };
        var cues = OcrCues.Merge(frames, false);
        Assert.Equal(2, cues.Count);
        Assert.Equal(("你好吗朋友", 0.0, 1.0), (cues[0].Text, cues[0].Start, cues[0].End));
        Assert.Equal(("再见朋友们吧", 1.0, 1.5), (cues[1].Text, cues[1].Start, cues[1].End));
        Assert.Equal(4, cues[0].Frames);
    }

    [Fact]
    public void A_distance_of_three_starts_a_new_cue_a_single_frame_is_dropped_and_one_empty_frame_is_bridged()
    {
        var frames = new List<FrameText> { F(0, ("abc你好", 5)), F(.25, ("abc你好", 5)), F(.5), F(.75, ("abc你好", 5)), F(1, ("xyz你好", 5)), F(1.25), F(1.5), F(1.75, ("单独一帧", 5)) };
        var cues = OcrCues.Merge(frames, false);
        // "abc你好" x3 with a one-frame gap bridged; "xyz你好" is one frame: flicker; later single frame: flicker
        var c = Assert.Single(cues);
        Assert.Equal((0.0, 1.0), (c.Start, c.End));
        Assert.Equal(3, c.Frames);
    }

    [Fact]
    public void Two_empty_frames_end_a_cue_and_english_lines_stay_separate()
    {
        var frames = new List<FrameText>
        {
            F(0, ("你好世界啊", 5), ("Hello there", 30)), F(.25, ("你好世界啊", 5), ("Hello there", 30)), F(.5), F(.75),
            F(1, ("你好世界啊", 5), ("Hello there", 30)), F(1.25, ("你好世界啊", 5), ("Hello there", 30)),
        };
        var zh = OcrCues.Merge(frames, false); var en = OcrCues.Merge(frames, true);
        Assert.Equal(2, zh.Count);
        Assert.All(zh, c => Assert.Equal("你好世界啊", c.Text));
        Assert.Equal(2, en.Count);
        Assert.All(en, c => Assert.Equal("Hello there", c.Text));
        Assert.DoesNotContain("Hello", OcrCues.ToVtt(zh, false));
    }

    [Fact]
    public void Two_chinese_lines_join_top_to_bottom_and_the_vtt_has_both_scripts()
    {
        var f = new[] { F(0, ("第二行", 40), ("第一行", 5)), F(.25, ("第二行", 40), ("第一行", 5)) };
        var cues = OcrCues.Merge(f, false);
        Assert.Equal("第一行第二行", cues[0].Text);
        cues[0].Text = "顧長淵"; OcrCues.Simplify(cues, OpenCc.T2S);
        Assert.Equal("WEBVTT\n\n00:00:00.000 --> 00:00:01.200\n顧長淵\n\n", OcrCues.ToVtt(cues, false));
        Assert.Contains("顾长渊", OcrCues.ToVtt(cues, true));
        Assert.Equal("01:02:03.050", OcrCues.Timestamp(3723.05));
    }

    [Theory]
    [InlineData("", "", 0)] [InlineData("abc", "abd", 1)] [InlineData("你好", "你", 1)] [InlineData("kitten", "sitting", 3)]
    public void Edit_distance(string a, string b, int d) => Assert.Equal(d, OcrCues.Levenshtein(a, b));

    private static List<Rect> R(params (int y0, int y1)[] r) => r.Select(x => new Rect(40, x.y0, 440, x.y1)).ToList();

    [Fact]
    public void Band_is_where_text_lines_recur_and_a_static_watermark_is_excluded()
    {
        var samples = new List<IReadOnlyList<Rect>>();
        for (int i = 0; i < 100; i++)
        {
            var boxes = new List<Rect> { new(400, 20, 470, 40) };       // watermark: same place in every frame
            if (i % 3 != 0) boxes.Add(new Rect(60 + i % 7 * 9, 610 + i % 3 * 4, 300 + i * 2, 640 + i % 3 * 4)); // subtitle rows ~ 610-644, every line a different width
            if (i % 10 == 0) boxes.Add(new Rect(100, 300 + i, 200, 330 + i));              // scene text, rarely, anywhere
            samples.Add(boxes);
        }
        var band = BandFinder.Find(samples, 854)!.Value;
        Assert.InRange(band.Y, 590, 615);
        Assert.InRange(band.Y + band.Height, 640, 660);
        Assert.Contains(new Rect(400, 20, 470, 40), BandFinder.StaticBoxes(samples));
    }

    [Fact]
    public void No_recurring_text_means_no_band()
    {
        var samples = Enumerable.Range(0, 100).Select(i => (IReadOnlyList<Rect>)(i % 40 == 0 ? R((100 + i, 130 + i)) : [])).ToList();
        Assert.Null(BandFinder.Find(samples, 854));
        Assert.Null(BandFinder.Find([], 854));
    }
}

public class ClipTests
{
    private static float[] V(params float[] x) { Sscd.L2Normalize(x); return x; }

    [Fact]
    public void Results_collapse_to_the_best_frame_per_shot()
    {
        // rows 0-2 one shot (cos >= .92 between neighbours), rows 3-4 another, row 5 a third
        var rows = new List<float[]> { V(1, 0, 0), V(1, .1f, 0), V(1, .2f, 0), V(0, 1, 0), V(0, 1, .1f), V(0, 0, 1) };
        var times = new double[] { 0, 1, 2, 3, 4, 5 };
        Assert.Equal([0, 0, 0, 1, 1, 2], Clip.ShotIds(rows));
        var hits = Clip.Search(rows, times, V(1, .1f, 0), 8);
        Assert.Equal(3, hits.Count);                                  // not 6: neighbours count once
        Assert.Equal(1, hits[0].Row);                                  // best frame of the first shot
        Assert.Equal(3, hits[0].ShotFrames);
        Assert.Equal([1, 3, 5], hits.Select(h => h.Row).OrderBy(x => x));
        Assert.Single(Clip.Search(rows, times, V(1, 0, 0), 1));
    }

    [Fact]
    public void A_slow_drift_still_cuts_where_neighbours_differ_and_f16_round_trips()
    {
        var rows = new List<float[]>(); var r = new Random(1);
        for (int i = 0; i < 4; i++) { var v = new float[Clip.Dim]; for (int d = 0; d < Clip.Dim; d++) v[d] = (float)r.NextDouble() - .5f; Sscd.L2Normalize(v); rows.Add(v); }
        Assert.Equal([0, 1, 2, 3], Clip.ShotIds(rows));
        var back = Clip.FromF16Bytes(Clip.ToF16Bytes(rows));
        Assert.Equal(4, back.Length);
        for (int i = 0; i < 4; i++) Assert.True(Clip.Dot(rows[i], back[i]) > 0.9999);
        Assert.Equal(4 * Clip.Dim * 2, Clip.ToF16Bytes(rows).Length);
    }

    [Fact]
    public void Clip_json_names_the_model_size_and_preprocessing()
    {
        var j = System.Text.Json.JsonDocument.Parse(Clip.InfoJson()).RootElement;
        Assert.Equal("onnx-community/siglip2-base-patch16-224-ONNX", j.GetProperty("model").GetString());
        Assert.Equal(224, j.GetProperty("inputSize").GetInt32());
        Assert.Contains("no padding", j.GetProperty("preprocessing").GetString());
    }
}

public class DubbingTests
{
    private static AsrSegment S(double a, double b, string lang) => new(a, b, "x", lang, []);

    [Fact]
    public void Mostly_chinese_speech_is_not_dubbed()
    {
        var (l, d) = Transcribe.Dubbing([S(0, 60, "zh"), S(60, 70, "en"), S(70, 80, "yue")]);
        Assert.Equal(("zh", false), (l, d));
    }

    [Fact]
    public void Mostly_english_speech_is_dubbed_and_the_language_is_the_majority()
    {
        var (l, d) = Transcribe.Dubbing([S(0, 10, "zh"), S(10, 100, "en"), S(100, 105, "und")]);
        Assert.Equal(("en", true), (l, d));
    }

    [Fact]
    public void Time_not_segment_count_decides_and_nothing_detected_is_not_dubbed()
    {
        Assert.True(Transcribe.Dubbing(Enumerable.Range(0, 5).Select(i => S(i, i + 1, "zh")).Append(S(10, 100, "ko"))).Dubbed);
        Assert.Equal(("und", false), Transcribe.Dubbing([]));
        Assert.Equal(("und", false), Transcribe.Dubbing([S(0, 5, "und")]));
        Assert.False(Transcribe.Dubbing([S(0, 50, "zh"), S(50, 100, "en")]).Dubbed); // exactly half is not "mostly"
    }

    [Theory]
    [InlineData("<|zh|>", "zh")] [InlineData("<|EN|>", "en")] [InlineData("", "und")] [InlineData("<|nospeech|>", "und")] [InlineData(null, "und")]
    public void Language_tags(string? raw, string want) => Assert.Equal(want, Transcribe.LangCode(raw));

    [Fact]
    public void Transcript_files_carry_segments_languages_and_the_dubbing_verdict()
    {
        var segs = new[] { new AsrSegment(1.5, 3.25, "Hello.", "en", [new AsrChar(1.6, "Hel"), new AsrChar(2.0, "lo")]), new AsrSegment(4, 5, "你好。", "zh", []) };
        var (l, d) = Transcribe.Dubbing(segs);
        var j = System.Text.Json.JsonDocument.Parse(Transcribe.ToJson(segs, l, d, 10, 1)).RootElement;
        Assert.Equal("en", j.GetProperty("language").GetString());
        Assert.True(j.GetProperty("dubbed").GetBoolean());
        Assert.Equal(["en", "zh"], j.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("language").GetString()!));
        Assert.Equal(2, j.GetProperty("segments")[0].GetProperty("words").GetArrayLength());
        Assert.Equal("WEBVTT\n\n00:00:01.600 --> 00:00:03.100\nHello.\n\n00:00:04.000 --> 00:00:05.500\n你好。\n\n", Transcribe.Vtt(segs));
    }
}

public class StepAdvertisingTests
{
    private sealed class Fh(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    private static string Dir() { var d = Path.Combine(Path.GetTempPath(), "models-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void A_node_without_a_steps_model_does_not_advertise_it_and_never_claims_its_jobs()
    {
        var cfg = new NodeConfig { ModelsAutoDownload = false };
        var dir = Dir();
        var none = ProcessStage.SupportedSteps(cfg, dir);
        Assert.Equal(["frames", "sscd", "compress"], none);
        var job = JobDescriptor.NewProcess("abcdefghij1");
        Assert.False(ProcessStage.CanRun(job, none));
        // install the clip model files: clip appears, transcribe and ocrsubs still do not
        foreach (var f in Models.Clip) { Directory.CreateDirectory(Path.GetDirectoryName(Models.PathOf(f, dir))!); File.WriteAllText(Models.PathOf(f, dir), "x"); }
        var some = ProcessStage.SupportedSteps(cfg, dir);
        Assert.Contains("clip", some); Assert.DoesNotContain("transcribe", some); Assert.DoesNotContain("ocrsubs", some);
        job.Steps = ["frames", "sscd", "clip"];
        Assert.True(ProcessStage.CanRun(job, some));
        job.Steps = ["frames", "clip", "ocrsubs"];
        Assert.False(ProcessStage.CanRun(job, some));
    }

    [Fact]
    public void With_automatic_downloads_every_step_is_supported_and_unknown_steps_never_are()
    {
        var all = ProcessStage.SupportedSteps(new NodeConfig(), Dir());
        Assert.Equal(ProcessStage.KnownSteps, all);
        var job = JobDescriptor.NewProcess("x"); job.Steps = ["frames", "faces"];
        Assert.False(ProcessStage.CanRun(job, all));
    }

    [Fact]
    public async Task The_engine_leaves_a_job_it_cannot_run_to_another_node_without_an_attempt_and_takes_the_next()
    {
        var s = new FakeStore(); var P = new Paths("index-test/");
        await s.AddJob(P, "process", "needsocr", Protocol.Utc(DateTimeOffset.UtcNow.AddMinutes(-20)));
        await s.AddJob(P, "process", "plain", Protocol.Utc(DateTimeOffset.UtcNow.AddMinutes(-10)));
        var needs = s.Job(P.Job("process", "needsocr")); needs.Steps = ["frames", "ocrsubs"];
        await s.PutAsync(P.Job("process", "needsocr"), needs.ToBytes(), Tags.Job("process", "pending", needs.AddedAt), null, false, default);
        var plain = s.Job(P.Job("process", "plain")); plain.Steps = ["frames"];
        await s.PutAsync(P.Job("process", "plain"), plain.ToBytes(), Tags.Job("process", "pending", plain.AddedAt), null, false, default);
        var ran = new List<string>();
        var e = new NodeEngine(s, P, new EngineOptions
        {
            NodeId = "n-1", Stages = ["process"], Jobs = new() { ["process"] = 1 }, MaxJobs = 1, Steps = ["frames", "sscd"], CanRun = j => ProcessStage.CanRun(j, ["frames", "sscd"]),
            PollInterval = TimeSpan.FromMilliseconds(30), ControlInterval = TimeSpan.FromMilliseconds(40), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromSeconds(30),
        }, new Dictionary<string, IStageHandler> { ["process"] = new Fh(ctx => { ran.Add(ctx.Job.JobId); return Task.CompletedTask; }) });
        await e.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["plain"], ran);
        Assert.Equal("pending", s.State(P.Job("process", "needsocr")));
        Assert.Equal(0, s.Job(P.Job("process", "needsocr")).Attempts);
        Assert.Null(s.LeaseOf(P.Job("process", "needsocr")));
        var reg = System.Text.Json.JsonDocument.Parse(s[P.Node("n-1")].Data).RootElement;
        Assert.Equal(["frames", "sscd"], reg.GetProperty("steps").EnumerateArray().Select(x => x.GetString()!));
    }

    [Fact]
    public void Every_model_has_a_pinned_https_url_and_a_sha256()
    {
        foreach (var m in Models.All)
        {
            Assert.StartsWith("https://", m.Url);
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.True(m.Size > 0);
        }
        Assert.Equal(Models.All.Length, Models.All.Select(m => m.File).Distinct().Count());
    }
}
