using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.12.0: decision rules, the AI probe port, the search index cache, the duplicate state, the model download lock and the pre-check end to end in local mode (synthetic ffmpeg video, local search index, yt-dlp mocked).</summary>
[Collection("console")]
public class PreCheckTests : IDisposable
{
    static readonly Paths P = new("index/");
    const int D = CentersFile.Dim;
    readonly string root = Path.Combine(Path.GetTempPath(), "pc-" + Guid.NewGuid().ToString("N")[..8]);
    public PreCheckTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    // ------------------------------------------------------------------ rules
    static PreCheckAnalysis A(params (string Up, double Share, double Cos)[] m) =>
        new(null, 1, 100, 0, m.Select(x => new PreCheckMatch(x.Up, x.Share, x.Cos, 10, (int)(x.Share * 100))).ToList(), 0.1, new Dictionary<string, double>());
    static readonly PreCheckSettings S = new();

    static PreCheckAnalysis AL(string? mine, string? theirs) => A(("up1", 0.95, 0.99)) with
    {
        Top = [new PreCheckMatch("up1", 0.95, 0.99, 10, 95, theirs)],
        Speech = mine == null ? null : new SpeechResult(mine, mine != "zh" && mine != "yue", 3, 40, 60, null),
    };

    [Fact]
    public void Language_seconds_is_a_setting_where_zero_or_all_means_the_whole_window()
    {
        Assert.Equal(60, new PreCheckSettings().LanguageSeconds);
        Assert.Equal(0, PreCheckSettings.ParseLanguageSeconds("all")); Assert.Equal(0, PreCheckSettings.ParseLanguageSeconds("ALL")); Assert.Equal(0, PreCheckSettings.ParseLanguageSeconds("0"));
        Assert.Equal(90.5, PreCheckSettings.ParseLanguageSeconds("90.5"));
    }

    static PreCheckAnalyzer SpeechAn(Tools tools, string root, double speechSecs) => new(tools, new NodeConfig(), new HttpClient(), new FakeStore(), P, new SearchIndexCache(Path.Combine(root, "cache-l"), TimeSpan.FromMinutes(10))) { SkipModelCheck = true, SpeechOverride = (_, _) => Task.FromResult<IReadOnlyList<LangChunk>>(speechSecs <= 0 ? [new LangChunk(0, 15, "", "und")] : [new LangChunk(1, speechSecs, "speech", "en"), new LangChunk(20, 15, "speech", "und")]) };

    static LangChunk C(string ev, string lang, double start = 0) => new(start, 15, ev, lang);

    [Fact]
    public void Music_never_makes_a_language_call_and_a_dub_call_needs_three_chunks_and_two_thirds()
    {
        var a = PreCheck.LanguageVerdict([C("speech", "en"), C("bgm", "en"), C("speech", "zh"), C("speech", "zh")], 10);
        Assert.Equal(("zh", false, (string?)null), (a.Language, a.Dubbed, a.Source)); Assert.Equal(3, a.Used); Assert.Equal(1, a.NoSpeech);
        var b = PreCheck.LanguageVerdict([C("speech", "en"), C("speech", "en"), C("speech", "en"), C("speech", "zh")], 10);
        Assert.Equal(("en", true), (b.Language, b.Dubbed));
        var c = PreCheck.LanguageVerdict([C("bgm", "en"), C("bgm", "en"), C("bgm", "en"), C("bgm", "en")], 10);
        Assert.Equal((null, false, "unclear"), (c.Language, c.Dubbed, c.Source));
        var d = PreCheck.LanguageVerdict([C("speech", "en"), C("speech", "en"), C("speech", "zh"), C("speech", "ja")], 10);   // 2 of 4 agree: not enough
        Assert.Equal((null, false, "unclear"), (d.Language, d.Dubbed, d.Source));
        var e = PreCheck.LanguageVerdict([C("speech", "en"), C("speech", "en")], 10);   // only 2 counted chunks
        Assert.Equal((null, false, "unclear"), (e.Language, e.Dubbed, e.Source));
        var f = PreCheck.LanguageVerdict([C("speech", "yue"), C("laughter", "en"), C("speech", "und")], 10);
        Assert.Equal(("zh", false), (f.Language, f.Dubbed));
        var g = PreCheck.LanguageVerdict([C("speech", "und"), C("", "und")], 10);
        Assert.Equal((null, "no-speech"), (g.Language, g.Source));
        // an unclear call counts as zh for the canonical rule
        var an = AL("zh", "en") with { Speech = new SpeechResult(null, false, 4, 60, 300, null) { Source = "unclear" } };
        Assert.Equal(PreCheckDecision.Canonical, PreCheck.Decide(an, 1000, _ => 1000, S).Decision);
        var rec = PreCheck.Record("x", new PreCheckDecision(PreCheckDecision.New, [], null), an with { Speech = an.Speech! with { Chunks = [C("bgm", "en", 30)] } }, new System.Text.Json.Nodes.JsonObject { ["language"] = "en" }, null, (0, 1), 1, S, "n", "v", false, 0, false);
        var sp = (System.Text.Json.Nodes.JsonObject)rec["speech"]!;
        Assert.Equal("unclear", (string?)sp["languageSource"]); Assert.Equal("bgm", (string?)sp["chunks"]![0]!["event"]); Assert.Equal(30.0, (double)sp["chunks"]![0]!["start"]!);
    }

    [Fact]
    public async Task The_language_id_reports_the_speech_seconds_used_and_no_speech_under_ten_seconds()
    {
        var tl = await RealFfmpegAsync(); if (tl is null) return;
        var tools = new Tools(new HttpClient(), new NodeConfig()); tools.UseFfmpeg(tl.Value.Ff, tl.Value.Probe);
        var sample = await MakeVideoAsync(tl.Value, 30);
        var some = await SpeechAn(tools, root, 25).SpeechAsync(sample, 1, 60, 10, default);
        Assert.Null(some.Language); Assert.Equal("unclear", some.Source); Assert.Equal(25, some.SpeechSeconds, 0); Assert.Equal(1, some.ChunksUsed); Assert.Equal(1, some.ChunksNoSpeech); Assert.Equal(2, some.Chunks.Count);   // one English chunk is too little for a dub call
        var few = await SpeechAn(tools, root, 6).SpeechAsync(sample, 1, 60, 10, default);
        Assert.Null(few.Language); Assert.False(few.Dubbed); Assert.Equal(6, few.SpeechSeconds, 0); Assert.Null(few.Unavailable); Assert.Equal("no-speech", few.Source);
        var none = await SpeechAn(tools, root, 0).SpeechAsync(sample, 1, 60, 10, default);
        Assert.Null(none.Language); Assert.Equal(0, none.SpeechSeconds);
    }

    [Fact]
    public void No_speech_leaves_the_language_null_keeps_the_metadata_hint_and_counts_as_zh()
    {
        var a = AL("zh", "en") with { Speech = new SpeechResult(null, false, 1, 4, 300, null) };
        Assert.Equal(PreCheckDecision.Canonical, PreCheck.Decide(a, 1000, _ => 1000, S).Decision);
        var info = new System.Text.Json.Nodes.JsonObject { ["language"] = "en" };
        var rec = PreCheck.Record("x", new PreCheckDecision(PreCheckDecision.New, [], null), a, info, null, (0, 1), 1, S, "n", "v", false, 0, false);
        var sp = (System.Text.Json.Nodes.JsonObject)rec["speech"]!;
        Assert.Null((string?)sp["speechLanguage"]); Assert.Equal("no-speech", (string?)sp["languageSource"]); Assert.Equal("en", (string?)sp["metadataLanguage"]); Assert.Equal(4.0, (double)sp["speechSeconds"]!);
    }

    [Fact]
    public void A_config_that_stored_the_old_default_ai_flag_follows_the_new_default_and_a_chosen_value_is_kept()
    {
        var root = Path.Combine(Path.GetTempPath(), "aiflag-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(root);
        var old = Environment.GetEnvironmentVariable("INDEXNODE_CONFIG_DIR"); Environment.SetEnvironmentVariable("INDEXNODE_CONFIG_DIR", root);
        try
        {
            // what any `config set precheck.*` wrote before 0.13.51: the whole section, the old default among it
            File.WriteAllText(Path.Combine(root, "config.json"), "{\"PreCheck\":{\"Frames\":60,\"AiFlag\":0.479,\"AiStrict\":0.529}}");
            Assert.Equal(0.62, NodeConfig.Load().PreCheck!.AiFlag);
            File.WriteAllText(Path.Combine(root, "config.json"), "{\"PreCheck\":{\"Frames\":60,\"AiFlag\":0.55,\"AiStrict\":0.529}}");
            Assert.Equal(0.55, NodeConfig.Load().PreCheck!.AiFlag);   // somebody's own value stays
            File.WriteAllText(Path.Combine(root, "config.json"), "{}");
            Assert.Equal(0.62, (NodeConfig.Load().PreCheck ?? new()).AiFlag);
        }
        finally { Environment.SetEnvironmentVariable("INDEXNODE_CONFIG_DIR", old); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Frames_height_and_audio_format_are_settings_and_shape_the_sample_call()
    {
        var d = new PreCheckSettings(); Assert.Equal(60, d.Frames); Assert.Equal(0.62, d.AiFlag); Assert.Equal(0.529, d.AiStrict); Assert.Null(PreCheckSettings.AiThresholdWarning(d, 60)); Assert.NotNull(PreCheckSettings.AiThresholdWarning(new PreCheckSettings { Frames = 0 }, 300)); Assert.NotNull(PreCheckSettings.AiThresholdWarning(d, 20));
        var ai = (System.Text.Json.Nodes.JsonObject)PreCheck.Record("x", new PreCheckDecision(PreCheckDecision.New, [], null), new PreCheckAnalysis(null, 1, 300, 0, [], 0.3, new Dictionary<string, double>()), new System.Text.Json.Nodes.JsonObject(), null, (0, 1), 1, new PreCheckSettings { Frames = 0 }, "n", "v", false, 0, false)["ai"]!;
        Assert.False((bool)ai["thresholdsApply"]!); Assert.Equal(60, (int)ai["thresholdFrames"]!); Assert.Equal(300, (int)ai["framesScored"]!); Assert.NotNull((string?)ai["warning"]); Assert.Equal(360, d.VideoHeight); Assert.Equal("wa", d.AudioFormat);
        Assert.Equal(0, PreCheckSettings.ParseFrames("all")); Assert.Equal(40, PreCheckSettings.ParseFrames("40"));
        var a = PreCheck.SampleArgs(new PreCheckSettings { VideoHeight = 240, AudioFormat = "249" }, 10, 20, "u");
        Assert.Equal("18/b[height<=240][vcodec!=none][acodec!=none]/bv*[height<=240]+249/b", a[1]); Assert.Equal("*10-20", a[3]);
        Assert.Equal("18/b[height<=360][vcodec!=none][acodec!=none]/bv*[height<=360]+ba/b", PreCheck.SampleArgs(new PreCheckSettings(), 0, 5, "u")[1]);   // the default audio (wa) becomes ba
        Assert.Equal(Enumerable.Range(0, 7), PreCheck.SubsampleIndices(7, 0)); Assert.Equal(Enumerable.Range(0, 7), PreCheck.SubsampleIndices(7, 50));
        var sub = PreCheck.SubsampleIndices(100, 4); Assert.Equal([12, 37, 62, 87], sub);
    }

    [Fact]
    public async Task Max_frames_embeds_an_even_subsample_of_the_sample()
    {
        var tl = await RealFfmpegAsync(); if (tl is null) return;
        var video = await MakeVideoAsync(tl.Value, 30);
        var cfg = new NodeConfig(); var tools = new Tools(new HttpClient(), cfg); tools.UseFfmpeg(tl.Value.Ff, tl.Value.Probe);
        int seen = 0;
        var an = new PreCheckAnalyzer(tools, cfg, new HttpClient(), new FakeStore(), P, new SearchIndexCache(Path.Combine(root, "cs"), TimeSpan.FromMinutes(10))) { SkipModelCheck = true, EmbedOverride = (dir, n, box, th, pr, ct) => { seen = n; return Task.FromResult(new byte[n * 2]); } };
        Directory.CreateDirectory(Path.Combine(root, "w1")); Directory.CreateDirectory(Path.Combine(root, "w2"));
        var all = await an.VectorsAsync(video, Path.Combine(root, "w1"), 1, _ => { }, new Dictionary<string, double>(), default);
        int total = seen; if (total < 4) return;
        var some = await an.VectorsAsync(video, Path.Combine(root, "w2"), 1, _ => { }, new Dictionary<string, double>(), default, 3);
        Assert.Equal(3, seen); Assert.Equal(3, some.Times.Count); Assert.True(some.Times.SequenceEqual(some.Times.OrderBy(x => x)));
    }

    [Fact]
    public void An_original_of_a_dub_only_drama_is_processed_everything_else_is_a_duplicate()
    {
        var d = PreCheck.Decide(AL("zh", "en"), 1800, _ => 1800, S);                                  // zh new vs dub-only match: process
        Assert.Equal("canonical", d.Decision); Assert.Equal("up1", PreCheck.Summary(d, AL("zh", "en"), false, S)["duplicateOf"]!.ToString());
        Assert.Equal("canonical", PreCheck.Decide(AL("yue", "ja"), 1800, _ => 1800, S).Decision);
        Assert.Equal("duplicate", PreCheck.Decide(AL("zh", "zh"), 1800, _ => 1800, S).Decision);       // zh new vs zh match
        Assert.Equal("duplicate", PreCheck.Decide(AL("yue", "zh"), 1800, _ => 1800, S).Decision);
        Assert.Equal("duplicate", PreCheck.Decide(AL("en", "zh"), 1800, _ => 1800, S).Decision);       // dub new vs zh match
        Assert.Equal("duplicate", PreCheck.Decide(AL("en", "ja"), 1800, _ => 1800, S).Decision);       // dub new vs dub-only match
        Assert.Equal("duplicate", PreCheck.Decide(AL("en", "en"), 1800, _ => 1800, S).Decision);
    }

    [Fact]
    public void A_matched_upload_without_a_language_counts_as_zh_and_language_id_failure_never_changes_the_rule()
    {
        Assert.Equal("duplicate", PreCheck.Decide(AL("zh", null), 1800, _ => 1800, S).Decision);
        Assert.Equal("duplicate", PreCheck.Decide(AL("en", null), 1800, _ => 1800, S).Decision);
        Assert.Equal("duplicate", PreCheck.Decide(AL(null, "en"), 1800, _ => 1800, S).Decision);       // language ID did not run: nothing proves an original
        Assert.Equal("overlap", PreCheck.Decide(AL("zh", "en"), 1800, _ => 3000, S).Decision);         // the duration rule still comes first
    }

    [Fact]
    public async Task Search_index_records_speech_language_from_transcribe_zip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inx-sp-" + Guid.NewGuid().ToString("N")[..6]); var store = new LocalStore(dir); var paths = new Paths("index/");
        try
        {
            var json = Transcribe.ToJson([new AsrSegment(0, 1, "hello", "en", [])], "en", true, 1, 0.1);
            Directory.CreateDirectory(dir); var zip = Path.Combine(dir, "t.zip");
            using (var fs = File.Create(zip)) using (var za = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create)) { var e = za.CreateEntry("asr/" + Transcribe.ModelName + ".zh.json"); using var s = e.Open(); s.Write(json); }
            await store.PutAsync(paths.Video("a", "transcribe.zip"), File.ReadAllBytes(zip), null, null, false, default, "application/zip");
            Assert.Equal(("en", (bool?)true), await VideosBackfill.LegacySpeechAsync(store, paths, "a", default));   // transcribe.zip is read by the backfill only
            Assert.Equal((null, null), await VideosBackfill.LegacySpeechAsync(store, paths, "none", default));
            await store.PutAsync(paths.Video("a", "video.json"), Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"a\",\"speech\":{\"language\":\"en\",\"dubbed\":true,\"source\":\"transcribe\"}}"), null, null, false, default);
            Assert.Equal(("en", (bool?)true), await SearchIndexBuild.ReadSpeechAsync(store, paths, "a", default));
            Assert.Equal((null, null), await SearchIndexBuild.ReadSpeechAsync(store, paths, "none", default));   // 0.13.3: a missing video.json gives no language, it does not stop the index build
            var y = new SearchData { Mean = new float[512], Bits = [], RefUpload = [], RefCenter = [], Uploads = [new SearchUpload("a", "s", "e", 1, 0, 1, 1, 1, 0, "en", true), new SearchUpload("b", "s", "e", 1, 0, 1, 1, 1, 0)] }.UploadsYaml();
            var (_, _, _, ups) = SearchData.ParseUploadsYaml(y);
            Assert.Equal("en", ups[0].SpeechLanguage); Assert.True(ups[0].Dubbed); Assert.Null(ups[1].SpeechLanguage); Assert.Null(ups[1].Dubbed);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void The_window_is_one_contiguous_five_minutes_centred_at_half_clamped_and_whole_when_short()
    {
        Assert.Equal((1350.0, 1650.0, false), PreCheck.Window(3000, 300, 0.5));
        Assert.Equal((0.0, 120.0, true), PreCheck.Window(120, 300, 0.5));
        Assert.Equal((0.0, 300.0, true), PreCheck.Window(300, 300, 0.5));
        Assert.Equal((0.0, 300.0, false), PreCheck.Window(1000, 300, 0.0));      // clamped at the start
        Assert.Equal((700.0, 1000.0, false), PreCheck.Window(1000, 300, 1.0));   // and at the end
        Assert.Equal((1950.0, 2250.0, false), PreCheck.Window(3000, 300, 0.7));  // the position is a setting
        Assert.Equal("*1350-1650", PreCheck.SectionArg(1350, 1650));
        Assert.Equal("*12.5-300", PreCheck.SectionArg(12.5, 300));
    }

    [Fact]
    public void Share_at_or_above_080_with_a_close_duration_is_a_duplicate()
    {
        var d = PreCheck.Decide(A(("up1", 0.80, 0.99)), 1800, _ => 1800, S);
        Assert.Equal("duplicate", d.Decision); Assert.Equal("up1", d.Match!.Upload);
        Assert.Equal("duplicate", PreCheck.Decide(A(("up1", 0.97, 0.99)), 1800 + 59, _ => 1800, S).Decision);   // 0.13.13: longer by at most max(2%, 60 s) of L
        Assert.Equal("duplicate", PreCheck.Decide(A(("up1", 0.97, 0.99)), 10000 - 1400, _ => 10000, S).Decision); // shorter by up to 15% of L (1500 s)
    }

    [Fact]
    public void A_duration_far_from_the_match_or_unknown_never_skips()
    {
        var far = PreCheck.Decide(A(("up1", 0.95, 0.99)), 1800 + 601, _ => 1800, S);
        Assert.Equal("overlap", far.Decision); Assert.Contains("longer than the group's longest copy", far.Reasons[0]);
        Assert.Equal("overlap", PreCheck.Decide(A(("up1", 0.95, 0.99)), 1800, _ => null, S).Decision);
    }

    // ---- 0.13.13: the asymmetric duplicate rule
    static PreCheckAnalysis AG(double share, string up, string group, params (string Id, double Dur)[] members) => A((up, share, 0.99)) with
    {
        UploadGroups = new Dictionary<string, string?> { [up] = group },
        GroupMembers = new Dictionary<string, IReadOnlyList<GroupCopy>> { [group] = members.Select(m => new GroupCopy(m.Id, "zh", false, m.Dur)).ToList() },
    };

    [Fact]
    public void Asymmetric_rule_longer_by_ten_minutes_is_partial_equal_is_duplicate_shorter_by_5_percent_is_duplicate_shorter_by_30_percent_is_partial()
    {
        var longer = PreCheck.Decide(A(("up1", 0.97, 0.99)), 1800 + 600, _ => 1800, S);
        Assert.Equal("overlap", longer.Decision); Assert.Contains("longer than the group's longest copy (2400 s vs 1800 s", longer.Reasons[0]); Assert.Contains("processed", longer.Reasons[0]);
        Assert.Equal(("up1", 1800.0), longer.MatchedLongest);
        Assert.Equal("duplicate", PreCheck.Decide(A(("up1", 0.97, 0.99)), 1800, _ => 1800, S).Decision);
        Assert.Equal("duplicate", PreCheck.Decide(A(("up1", 0.97, 0.99)), 1800 * 0.95, _ => 1800, S).Decision);
        var shorter = PreCheck.Decide(A(("up1", 0.97, 0.99)), 1800 * 0.70, _ => 1800, S);
        Assert.Equal("overlap", shorter.Decision); Assert.Contains("shorter than the group's longest copy", shorter.Reasons[0]);
        Assert.Equal("duplicate", PreCheck.Decide(A(("up1", 0.97, 0.99)), 1800 + 60, _ => 1800, S).Decision);       // the slack: max(2% = 36 s, 60 s)
        Assert.Equal("overlap", PreCheck.Decide(A(("up1", 0.97, 0.99)), 1800 + 61, _ => 1800, S).Decision);
        Assert.Equal("overlap", PreCheck.Decide(A(("up1", 0.97, 0.99)), 1800, _ => null, S).Decision);              // L unknown: processed
    }

    [Fact]
    public void A_group_whose_longest_member_is_longer_than_the_matched_upload_is_the_reference_and_the_record_says_so()
    {
        var a = AG(0.97, "short1", "g1", ("short1", 1500), ("long1", 2400), ("mid1", 2000));
        var d = PreCheck.Decide(a, 2400, _ => null, S);                                   // matched duration comes from the top match
        a = a with { Top = [a.Top[0] with { Duration = 1500 }] };
        d = PreCheck.Decide(a, 2400, _ => null, S);
        Assert.Equal("duplicate", d.Decision); Assert.Equal(("long1", 2400.0), d.MatchedLongest);                         // longer than the matched upload (1500) but equal to L
        Assert.Equal("overlap", PreCheck.Decide(a, 2400 + 600, _ => null, S).Decision);                                  // longer than L + slack
        var rec = PreCheck.Record("new1", d, a, new System.Text.Json.Nodes.JsonObject(), null, (0, 300), 2400, S, "n", "v", false, 0, false);
        Assert.Equal("long1", (string)rec["matchedLongest"]!["id"]!); Assert.Equal(2400.0, (double)rec["matchedLongest"]!["duration"]!);
        Assert.Equal(0.02, (double)rec["settings"]!["durationSlackPercent"]!); Assert.Equal(60.0, (double)rec["settings"]!["durationSlackSeconds"]!);
        // the legacy (symmetric) rule, for what-if comparisons: 2400 is far from the matched 1500 by more than 15% and 10 min
        Assert.Equal("partial", PreCheck.Decide(a, 2400, _ => null, S, legacy: true).Decision);
    }

    [Fact]
    public void Share_in_060_to_080_is_partial_and_below_060_is_new()

    {
        Assert.Equal("overlap", PreCheck.Decide(A(("up1", 0.79, 0.9)), 1800, _ => 1800, S).Decision);
        Assert.Equal("overlap", PreCheck.Decide(A(("up1", 0.60, 0.9)), 1800, _ => 1800, S).Decision);
        var n = PreCheck.Decide(A(("up1", 0.59, 0.9)), 1800, _ => 1800, S); Assert.Equal("new", n.Decision);
        Assert.Equal("new", PreCheck.Decide(A(), 1800, _ => 1800, S).Decision);
    }

    [Fact]
    public void Every_threshold_is_a_setting()
    {
        var s = new PreCheckSettings { SkipShare = 0.95, PartialShare = 0.5, DurationPercent = 0.5, DurationMinSeconds = 0, DurationSlackPercent = 0.5, DurationSlackSeconds = 0 };
        Assert.Equal("overlap", PreCheck.Decide(A(("u", 0.9, 0.9)), 100, _ => 100, s).Decision);
        Assert.Equal("duplicate", PreCheck.Decide(A(("u", 0.96, 0.9)), 140, _ => 100, s).Decision);   // longer by 40% of L: inside the 50% slack setting
        Assert.Equal("overlap", PreCheck.Decide(A(("u", 0.96, 0.9)), 160, _ => 100, s).Decision);
        Assert.Equal("duplicate", PreCheck.Decide(A(("u", 0.96, 0.9)), 55, _ => 100, s).Decision); Assert.Equal("overlap", PreCheck.Decide(A(("u", 0.96, 0.9)), 45, _ => 100, s).Decision);   // shorter: the 50% percent setting
        Assert.Equal("overlap", PreCheck.Decide(A(("u", 0.5, 0.9)), 100, _ => 100, s).Decision);
        Assert.Equal("new", PreCheck.Decide(A(("u", 0.49, 0.9)), 100, _ => 100, s).Decision);
    }

    [Fact]
    public void No_index_or_no_model_records_skipped_with_the_reason_and_downloads_as_before()
    {
        var d = PreCheck.Decide(PreCheckAnalysis.Skipped("no search index"), 1800, _ => 1800, S);
        Assert.Equal("skipped", d.Decision); Assert.Equal("no search index", d.Reasons[0]);
        var sum = PreCheck.Summary(d, null, null, S); Assert.Equal("skipped", (string)sum["decision"]!); Assert.Equal("no search index", (string)sum["reason"]!);
    }

    // ------------------------------------------------------------------ AI probe
    static double Ref(float[] all, double[] x)
    {
        // a straightforward reference: z = b + sum ((x - mu) / sd) * w, p = 1 / (1 + e^-z); the 1537 values are mu[512], sd[512], w[512], b
        double z = all[1536];
        for (int i = 0; i < 512; i++) z += (x[i] - all[i]) / all[512 + i] * all[1024 + i];
        return 1 / (1 + Math.Exp(-z));
    }

    [Fact]
    public void The_ai_probe_port_matches_a_reference_on_e0_and_the_uniform_vector()
    {
        using var s = typeof(AiProbe).Assembly.GetManifestResourceStream("ai-frames-v3.f32")!;
        var raw = new byte[s.Length]; s.ReadExactly(raw); Assert.Equal(1537 * 4, raw.Length);
        var all = new float[1537]; for (int i = 0; i < all.Length; i++) all[i] = BitConverter.ToSingle(raw, i * 4);
        Assert.All(all.Skip(512).Take(512), v => Assert.True(v > 0));   // sd
        var probe = AiProbe.Default;
        var e0 = new float[512]; e0[0] = 1;
        var uni = Enumerable.Repeat((float)(1 / Math.Sqrt(512)), 512).ToArray();
        foreach (var x in new[] { e0, uni })
        {
            var expect = Ref(all, x.Select(v => (double)v).ToArray());
            Assert.Equal(expect, probe.P(x), 9);
            Assert.InRange(expect, 0, 1);
        }
        // the drama score is the mean p over the frames
        Assert.Equal((Ref(all, e0.Select(v => (double)v).ToArray()) + Ref(all, uni.Select(v => (double)v).ToArray())) / 2, probe.Score([e0, uni]), 6);
        Assert.Equal(0, probe.Score([]));
    }

    [Fact]
    public void The_ai_sample_is_seeded_by_the_id_and_stable()
    {
        var a = CentersCommands.AiSample("abcdefghijk", 1000, 230, false); var b = CentersCommands.AiSample("abcdefghijk", 1000, 230, false);
        Assert.Equal(a, b); Assert.Equal(230, a.Length); Assert.Equal(230, a.Distinct().Count()); Assert.NotEqual(a, CentersCommands.AiSample("kjihgfedcba", 1000, 230, false));
        Assert.Equal(100, CentersCommands.AiSample("x", 100, 230, false).Length);   // short: all frames
        Assert.Equal(1000, CentersCommands.AiSample("x", 1000, 230, true).Length);
    }

    // ------------------------------------------------------------------ a local search index
    static float[] Gauss(Random r) { var v = new float[D]; for (int j = 0; j < D; j++) v[j] = (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble())); Vec.Normalize(v); return v; }
    static float[] Make(int n, int groups, int seed, double noise = 0.5)
    {
        var rng = new Random(seed); var dirs = Enumerable.Range(0, groups).Select(_ => Gauss(rng)).ToArray(); var x = new float[n * D];
        for (int i = 0; i < n; i++) { var d = dirs[i % groups]; var n2 = Gauss(rng); var row = new float[D]; for (int j = 0; j < D; j++) row[j] = d[j] + (float)noise * n2[j]; Vec.Normalize(row); row.CopyTo(x, i * D); }
        return x;
    }
    static byte[] F16(float[] x) { var b = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2, 2), (Half)x[i]); return b; }

    /// <summary>Three uploads with sscd.zip in a folder -> centers backfill -> search-index build (the 0.11.0 local flow). Returns the vectors of each upload.</summary>
    async Task<Dictionary<string, float[]>> BuildIndexAsync(string? language = null, bool? dubbed = null)
    {
        var local = new LocalStore(root); var wr = (StageZipStore)StageZipStore.Wrap(local, Path.Combine(root, ".indexnode-zips")); var vecs = new Dictionary<string, float[]>();
        foreach (var (id, seed) in new[] { ("a1", 41), ("b2", 42), ("c3", 43) })
        {
            var n = 300; var x = Make(n, 20, seed); vecs[id] = x;
            var frames = Enumerable.Range(0, n).Select(i => new ChunkFrame(i, i * 1.0, i * 1.0, [], null, i, 1)).ToList();
            await wr.PutAsync(P.Video(id, "frames/0000.json"), FrameChunks.JpegIndexJson("0000", 0, 360, 640, 85, frames), null, null, false, default);
            await wr.PutAsync(P.Video(id, "frames/manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 0, n, [("0000", 0, n, (long)n)], null, "mjpeg", "jpeg"), null, null, false, default);
            await wr.PutAsync(P.Video(id, "sscd/0000.f16"), F16(x), null, null, false, default);
            await wr.PutAsync(P.Video(id, "sscd/manifest.json"), Encoding.UTF8.GetBytes($"{{\"v\":1,\"step\":\"sscd\",\"chunks\":[{{\"chunk\":\"0000\",\"start\":0,\"rows\":{n}}}]}}"), null, null, false, default);
            foreach (var st in new[] { "frames", "sscd" }) Assert.True(await StageZipStore.Stage(wr)!.CommitStageAsync(id, st, "n1", false, false, default));
            await VideoJsonSeed.PutAsync(local, P, id, 1800, language, dubbed);
        }
        var old = Console.Out; Console.SetOut(new StringWriter());
        try
        {
            Assert.Equal(0, await CentersCommands.CentersAsync(Args.Parse(["backfill", "--local", root, "--threads", "2"])));
            Assert.Equal(0, await CentersCommands.SearchIndexAsync(Args.Parse(["build", "--local", root, "--threads", "2"])));
        }
        finally { Console.SetOut(old); }
        return vecs;
    }

    IStore OpenLocal() => CentersCommands.OpenStore(Args.Parse(["--local", root])).Store;

    [Fact]
    public async Task The_index_cache_rechecks_head_at_most_every_ten_minutes_and_keeps_bits_on_disk()
    {
        await BuildIndexAsync(); var store = OpenLocal(); var cdir = Path.Combine(root, "cache"); var now = DateTimeOffset.Parse("2026-11-19T10:00:00Z");
        var c = new SearchIndexCache(cdir, TimeSpan.FromMinutes(10), () => now);
        var i1 = await c.GetAsync(store, P, default); Assert.NotNull(i1); Assert.Equal(3, i1!.Uploads.Count);
        Assert.Equal((1, 1), (c.HeadReads, c.Downloads));
        now += TimeSpan.FromMinutes(9); Assert.Same(i1, await c.GetAsync(store, P, default)); Assert.Equal(1, c.HeadReads);
        now += TimeSpan.FromMinutes(2); Assert.Same(i1, await c.GetAsync(store, P, default)); Assert.Equal((2, 1), (c.HeadReads, c.Downloads));   // unchanged head: same index, nothing re-downloaded
        Assert.True(File.Exists(Path.Combine(cdir, "index", "search", "v1", "bits.bin")));
        var c2 = new SearchIndexCache(cdir, TimeSpan.FromMinutes(10), () => now);   // a restart: the version is read from the local cache
        Assert.Equal(3, (await c2.GetAsync(store, P, default))!.Uploads.Count); Assert.Equal((1, 0), (c2.HeadReads, c2.Downloads));
        Assert.Null(await new SearchIndexCache(Path.Combine(root, "c3"), TimeSpan.FromMinutes(10)).GetAsync(new FakeStore(), P, default));   // no index
    }

    [Fact]
    public async Task The_batch_lookup_finds_each_planted_frame_with_cosine_near_one_and_reads_each_block_once()
    {
        var v = await BuildIndexAsync(); var store = OpenLocal();
        var idx = (await new SearchIndexCache(Path.Combine(root, "cc"), TimeSpan.FromMinutes(10)).GetAsync(store, P, default))!;
        var rows = AiProbe.RowsFromF16(Take(v["b2"], 40, 12)); foreach (var r in rows) Sscd.L2Normalize(r);
        var (best, st) = await PreCheckSearch.BestPerFrameAsync(store, P, idx, rows, 100, 2, default);
        Assert.All(best, b => { Assert.NotNull(b); Assert.Equal("b2", idx.Uploads[b!.Upload].Id); Assert.True(b.Cosine > 0.99, b.Cosine.ToString()); });
        Assert.Equal(Enumerable.Range(40, 12).Select(i => (double)i), best.Select(b => b!.Time));
        var (m, noHit) = PreCheckSearch.Shares(idx, best, 0.7); Assert.Equal(0, noHit); Assert.Equal("b2", m[0].Upload); Assert.Equal(1.0, m[0].Share);
    }

    // ------------------------------------------------------------------ synthetic video, mocked yt-dlp
    static async Task<(string Ff, string Probe)?> RealFfmpegAsync()
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

    async Task<string> MakeVideoAsync((string Ff, string Probe) t, double seconds, int fps = 10)
    {
        var path = Path.Combine(root, "sample-src.mp4");
        var r = await Proc.RunAsync(t.Ff, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=s=640x360:r={fps},format=yuv420p", "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "mpeg4", "-q:v", "2", path], default);
        Assert.True(r.ExitCode == 0, r.Stderr);
        return path;
    }

    sealed class Rig
    {
        public Tools Tools = null!; public FakeStore Fs = new(); public List<string> Calls = new(); public string SectionArg = "";
        public PreCheckAnalyzer An = null!; public Func<IReadOnlyList<LangChunk>> Speech = () => [new LangChunk(0, 15, "speech", "en"), new LangChunk(15, 15, "speech", "en"), new LangChunk(30, 15, "speech", "en"), new LangChunk(45, 15, "speech", "zh")]; public string Dbg = ""; public Func<int, byte[]> Rows = _ => [];   // the SSCD rows of the sample (count -> f16)
    }

    async Task<(Rig Rig, PreCheckStage Stage, string Id)> SetupAsync(string id, Func<int, byte[]> rows, bool withIndex = true, double infoDuration = 1750)
    {
        var t = await RealFfmpegAsync() ?? throw new InvalidOperationException("no ffmpeg");
        var video = await MakeVideoAsync(t, infoDuration, 1);   // as long as the metadata says: since 0.13.0 the stage cuts its window out of the stored video.mp4
        var rig = new Rig { Rows = rows }; var cfg = new NodeConfig();
        rig.Tools = new Tools(new HttpClient(), cfg); rig.Tools.UseFfmpeg(t.Ff, t.Probe);
        var wsDir = Path.Combine(Path.GetTempPath(), "indexnode", id);
        var info = new JsonObject { ["id"] = id, ["title"] = "Reborn Sisters EP1", ["channel"] = "Drama Channel", ["channel_id"] = "UC1", ["upload_date"] = "20261101", ["duration"] = infoDuration, ["height"] = 720, ["language"] = "en",
            ["subtitles"] = new JsonObject { ["en"] = new JsonArray() }, ["automatic_captions"] = new JsonObject { ["zh-orig"] = new JsonArray() }, ["formats"] = new JsonArray(), ["description"] = "d" }.ToJsonString();
        rig.Tools.YtDlpOverride = (args, ct, _) =>
        {
            var line = string.Join(' ', args); rig.Calls.Add(line); Directory.CreateDirectory(wsDir);
            if (args.Contains("--skip-download") && args.Contains("--write-info-json")) { File.WriteAllText(Path.Combine(wsDir, "precheck.info.json"), info); File.WriteAllBytes(Path.Combine(wsDir, "precheck.jpg"), [1, 2, 3]); }
            else if (args.Contains("--write-subs") || args.Contains("--write-auto-subs")) { var d = Path.Combine(wsDir, args.Contains("--write-subs") ? "manual" : "auto"); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, args.Contains("--write-subs") ? "video.en.vtt" : "video.zh-orig.vtt"), "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nhello\n"); }
            else if (args.Contains("--download-sections")) { rig.SectionArg = args[args.ToList().IndexOf("--download-sections") + 1]; File.Copy(video, Path.Combine(wsDir, "sample.mp4"), true); }
            else if (args.Contains("--write-info-json")) { File.Copy(video, Path.Combine(wsDir, "video.mp4"), true); File.WriteAllText(Path.Combine(wsDir, "video.info.json"), info); }
            return Task.FromResult(new ProcResult(0, "", ""));
        };
        await rig.Fs.PutAsync(P.Video(id, "video.mp4"), await File.ReadAllBytesAsync(video), null, null, false, default);   // 0.13.0: the pre-check reads the stored video
        await rig.Fs.PutAsync(P.Video(id, "info.json"), Encoding.UTF8.GetBytes(info), null, null, false, default);
        await VideoJsonSeed.PutAsync(rig.Fs, P, id, infoDuration);   // 0.13.2: the descriptor the download wrote
        await VideoJsonSeed.PutAsync(rig.Fs, P, "b2", 1800);
        // the downloaded.json of b2: 1800 s
        await rig.Fs.PutAsync(P.Video("b2", "downloaded.json"), Encoding.UTF8.GetBytes("{\"v\":1,\"durationSeconds\":1800}"), null, null, false, default);
        IStore index = withIndex ? OpenLocal() : new FakeStore();
        if (withIndex) await VideoJsonSeed.PutAsync(index, P, "b2", 1800);
        if (withIndex) await index.PutAsync(P.Video("b2", "downloaded.json"), Encoding.UTF8.GetBytes("{\"v\":1,\"durationSeconds\":1800}"), null, null, false, default);   // the analyzer reads the matched duration from the index store
        var an = new PreCheckAnalyzer(rig.Tools, cfg, new HttpClient(), index, P, new SearchIndexCache(Path.Combine(root, "cache-" + id), TimeSpan.FromMinutes(10)))
        {
            SkipModelCheck = true,
            SpeechOverride = (_, _) => Task.FromResult(rig.Speech()),
            EmbedOverride = (dir, count, box, th, prog, ct) => { var b = rig.Rows(count); rig.Dbg = $"count={count} bytes={b.Length}"; return Task.FromResult(b); },
        };
        rig.An = an;
        // the one-window fixtures feed 60 consecutive frames of b2 (times 40..99) as both segments: a 30 s gap instead of the ~700 s a 1750 s video has, so the order rule's tolerance is opened up here (the order rule itself is tested in TwoSegmentPreCheckTests)
        var stage = new PreCheckStage(rig.Tools, an, new PreCheckSettings { OrderToleranceSeconds = 100000 }, "0.13.0-test");
        return (rig, stage, id);
    }

    static JobContext Ctx(FakeStore s, JobDescriptor job) => new()
    {
        Job = job, Stage = "process", BlobName = job.JobId, LeaseId = "L", Worker = 1, Store = s, Paths = P, NodeId = "n-1", Token = default, ThreadShare = () => 2, Report = (_, _) => { },
    };

    static byte[] Take(float[] x, int from, int n) => F16(x.AsSpan(from * D, n * D).ToArray());
    /// <summary>0.13.2: the pre-check record is video.json's "precheck" section (precheck.json is never written): asking for index/videos/&lt;id&gt;/precheck.json returns that section.</summary>
    static JsonElement Json(FakeStore s, string name)
    {
        if (name.EndsWith("/precheck.json", StringComparison.Ordinal)) { Assert.False(s.Exists(name), name + " must not be written"); var vj = JsonDocument.Parse(s[name[..^"precheck.json".Length] + "video.json"].Data).RootElement; return vj.GetProperty("precheck").Clone(); }
        return JsonDocument.Parse(s[name].Data).RootElement.Clone();
    }

    static (string? L, bool D, string S) Lang(string json) { var info = JsonNode.Parse(json)!.AsObject(); return PreCheck.LanguageFromInfo(info, DownloadedFormats.FromInfo(info)); }
    const string Vid = "{\"vcodec\":\"avc1\",\"acodec\":\"none\"}";
    static string Aud(string? lang, string? note) => "{\"vcodec\":\"none\",\"acodec\":\"opus\"" + (lang == null ? "" : ",\"language\":\"" + lang + "\"") + (note == null ? "" : ",\"format_note\":\"" + note + "\"") + "}";

    [Fact]
    public void Language_rule_1_the_original_or_default_audio_format_wins()
    {
        var r = Lang("{\"language\":\"fr\",\"formats\":[" + Vid + "," + Aud("en", "English (US), dubbed-auto, low") + "," + Aud("zh-Hans", "Chinese, original (default), medium") + "]}");
        Assert.Equal(("zh-Hans", false, "format-original"), r);   // a zh original is not a dub, whatever the other tracks and the video say
        var d = Lang("{\"formats\":[" + Aud("en", "English (US), original (default)") + "," + Aud("zh", "Chinese, dubbed-auto") + "]}");
        Assert.Equal(("en", true, "format-original"), d);
        Assert.Equal("format-original", Lang("{\"formats\":[" + Aud("ja", "Japanese (default), low") + "," + Aud("en", "English, dubbed-auto")+ "]}").S);   // "(default)" alone counts
    }

    [Fact]
    public void Language_rule_2_all_audio_formats_carry_exactly_one_language()
    {
        Assert.Equal(("en", true, "format-only"), Lang("{\"language\":\"zh\",\"formats\":[" + Vid + "," + Aud("en", "low") + "," + Aud("en", "medium") + "]}"));   // beats the video's language
        // two languages, none original: rule 2 does not apply, the video's language does (rule 3)
        Assert.Equal(("zh", false, "video"), Lang("{\"language\":\"zh\",\"formats\":[" + Aud("en", "low") + "," + Aud("de", "low") + "]}"));
        // a track without a language: not "all carry exactly one language"
        Assert.Equal("video", Lang("{\"language\":\"en\",\"formats\":[" + Aud("zh", "low") + "," + Aud(null, "medium") + "]}").S);
    }

    [Fact]
    public void Language_rule_3_the_videos_language_when_the_formats_say_nothing()
    {
        Assert.Equal(("en", true, "video"), Lang("{\"language\":\"en\",\"formats\":[" + Vid + "]}"));
        Assert.Equal(("zh-Hant", false, "video"), Lang("{\"language\":\"zh-Hant\"}"));
        Assert.Equal(("en", true, "video"), Lang("{\"language\":\" en \",\"formats\":[" + Aud(null, null) + "]}"));
    }

    [Fact]
    public void Language_rule_4_no_language_anywhere_is_not_dubbed_and_recorded_as_none()
    {
        var r = Lang("{\"formats\":[" + Vid + "," + Aud(null, "medium") + "]}");
        Assert.Equal((null, false, "none"), r);
        Assert.Equal((null, false, "none"), Lang("{\"language\":\"  \"}"));
        var info = JsonNode.Parse("{\"id\":\"x\"}")!.AsObject();
        var rec = PreCheck.Record("x", new PreCheckDecision(PreCheckDecision.New, [], null), new PreCheckAnalysis(null, 1, 1, 0, [], 0, new Dictionary<string, double>()), info, null, (0, 1), 1, new PreCheckSettings(), "n", "v", false, 0, false);
        Assert.Null(rec["speech"]!["speechLanguage"]); Assert.False((bool)rec["speech"]!["dubbed"]!); Assert.Equal("none", (string)rec["speech"]!["languageSource"]!);
    }

    [Fact]
    public async Task Bench_real_language_id_on_speech_files()   // opt-in: INDEXNODE_BENCH=1 and INDEXNODE_LID_FILES=a.wav;b.wav (downloads the SenseVoice and VAD models on first use)
    {
        if (Environment.GetEnvironmentVariable("INDEXNODE_BENCH") != "1" || Environment.GetEnvironmentVariable("INDEXNODE_LID_FILES") is not { } files || await RealFfmpegAsync() is not { } t) return;
        var tools = new Tools(new HttpClient(), new NodeConfig()); tools.UseFfmpeg(t.Ff, t.Probe);
        var an = new PreCheckAnalyzer(tools, new NodeConfig(), new HttpClient(), new FakeStore(), P, new SearchIndexCache(Path.Combine(root, "cache-lid"), TimeSpan.FromMinutes(10)));
        var sb = new StringBuilder();
        foreach (var f in files.Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var th in new[] { 1, 4 })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew(); var r = await an.SpeechAsync(f, th, 60, 10, default);
                sb.AppendLine($"{Path.GetFileName(f)} threads={th}: {sw.Elapsed.TotalSeconds:0.00}s lang={r.Language} dubbed={r.Dubbed} segments={r.Segments} speech={r.SpeechSeconds:0.0}s audio={r.AudioSeconds:0}s unavailable={r.Unavailable}");
            }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "lid-bench.txt"), sb.ToString());
    }

    static AsrSegment Seg(double a, double b, string lang) => new(a, b, "x", lang, []);

    [Fact]
    public void Speech_majority_is_by_speech_time_and_dubbed_means_the_majority_is_not_zh_or_yue()
    {
        Assert.Equal(("en", true, 11.0), Transcribe.Majority([Seg(0, 5, "en"), Seg(6, 10, "en"), Seg(11, 13, "zh")]) is var a ? (a.Language, a.Dubbed, Math.Round(a.SpeechSeconds)) : default);
        Assert.Equal(("zh", false), Transcribe.Majority([Seg(0, 5, "zh"), Seg(6, 7, "en")]) is var b ? (b.Language, b.Dubbed) : default);
        Assert.Equal(("zh", false), Transcribe.Majority([Seg(0, 5, "yue"), Seg(6, 8, "zh"), Seg(9, 10, "en")]) is var c ? (c.Language, c.Dubbed) : default);   // Cantonese is Chinese
        Assert.Equal((null, false), Transcribe.Majority([Seg(0, 5, "und"), Seg(6, 7, "")]) is var d ? (d.Language, d.Dubbed) : default);   // nothing recognised: no claim
        Assert.Equal(("ja", true), Transcribe.Majority([Seg(0, 4, "ja"), Seg(5, 8, "zh"), Seg(9, 12, "en")]) is var e ? (e.Language, e.Dubbed) : default);   // plurality, not 50%
    }

    [Fact]
    public async Task Speech_comes_from_the_audio_not_from_the_metadata_hint()
    {
        if (await RealFfmpegAsync() is null) return;
        var v = await BuildIndexAsync("en", true);   // the indexed copies are dubs, so the language ID can change the decision and runs
        // the metadata says en (the info has language=en), the audio is Chinese: not dubbed
        var (rig, stage, id) = await SetupAsync("zhaudio0001", n => Take(v["b2"], 40, n));
        rig.Speech = () => [C("speech", "yue"), C("speech", "zh", 15), C("speech", "en", 30)];
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=zhaudio0001", 480, null); job.JobId = id;
        try { await stage.RunAsync(Ctx(rig.Fs, job)); } catch (DuplicateFound) { }
        var sp = Json(rig.Fs, P.Video(id, "precheck.json")).GetProperty("speech");
        Assert.Equal("zh", sp.GetProperty("speechLanguage").GetString()); Assert.False(sp.GetProperty("dubbed").GetBoolean()); Assert.Equal("sensevoice", sp.GetProperty("speechSource").GetString());
        Assert.Equal("en", sp.GetProperty("metadataLanguage").GetString()); Assert.Equal(3, sp.GetProperty("segments").GetInt32());
    }

    [Fact]
    public async Task A_missing_speech_model_falls_back_to_the_metadata_and_the_record_says_so()
    {
        if (await RealFfmpegAsync() is null) return;
        var v = await BuildIndexAsync("en", true);
        var (rig, stage, id) = await SetupAsync("nomodel0001", n => Take(v["b2"], 40, n));
        var an = new PreCheckAnalyzer(rig.Tools, new NodeConfig(), new HttpClient(), OpenLocal(), P, new SearchIndexCache(Path.Combine(root, "cache-nm"), TimeSpan.FromMinutes(10)))
        { SkipModelCheck = true, EmbedOverride = (dir, count, box, th, prog, ct) => Task.FromResult(rig.Rows(count)), SpeechOverride = (_, _) => throw new InvalidOperationException("model download failed") };
        stage = new PreCheckStage(rig.Tools, an, new PreCheckSettings { OrderToleranceSeconds = 100000 }, "t");
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=nomodel0001", 480, null); job.JobId = id;
        try { await stage.RunAsync(Ctx(rig.Fs, job)); } catch (DuplicateFound) { }   // a broken language ID never loses the pre-check or the download
        var sp = Json(rig.Fs, P.Video(id, "precheck.json")).GetProperty("speech");
        Assert.Equal("metadata", sp.GetProperty("speechSource").GetString()); Assert.Contains("model download failed", sp.GetProperty("fallbackReason").GetString());
        Assert.Equal("en", sp.GetProperty("speechLanguage").GetString()); Assert.True(sp.GetProperty("dubbed").GetBoolean()); Assert.Equal("video", sp.GetProperty("languageSource").GetString());
    }

    [Fact]
    public async Task The_precheck_command_writes_its_json_to_the_folder_and_nothing_to_the_store()
    {
        if (await RealFfmpegAsync() is null) return;
        var v = await BuildIndexAsync();
        var (rig, _, id) = await SetupAsync("cmdvideo001", n => Take(v["b2"], 40, n));
        rig.Fs.Blobs.Remove(P.Video(id, "downloaded.json")); await rig.Fs.PutAsync(P.Video(id, "downloaded.json"), Encoding.UTF8.GetBytes("{\"v\":1,\"durationSeconds\":1750}"), null, null, false, default);
        var before = rig.Fs.Blobs.ToDictionary(kv => kv.Key, kv => (kv.Value.Version, kv.Value.Data.Length));
        var outDir = Path.Combine(root, "pc-out-" + id);
        var args = Args.Parse([id, "--output-local", outDir, "--keep-sample", "--threads", "3", "--window-position", "0.5", "--order-tolerance-seconds", "100000"]);
        var sw = new StringWriter(); var old = Console.Out; Console.SetOut(sw);
        int code; try { code = await PreCheckCommand.RunAsync(args, rig.Fs, rig.Tools, rig.An); } finally { Console.SetOut(old); }
        Assert.Equal(0, code);
        // the store: no Put of any kind (same blobs, same versions, nothing new: no precheck.json, no job, no downloaded.json)
        Assert.Equal(before.Count, rig.Fs.Blobs.Count);
        foreach (var kv in rig.Fs.Blobs) Assert.Equal(before[kv.Key], (kv.Value.Version, kv.Value.Data.Length));
        Assert.False(rig.Fs.Exists(P.Video(id, "precheck.json")));
        Assert.Empty(rig.Calls);   // 0.13.0: no yt-dlp at all, the window is cut from the stored video
        var rec = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, id, "precheck.json"))).RootElement;
        Assert.Equal("duplicate", rec.GetProperty("decision").GetString()); Assert.Equal(3, rec.GetProperty("threads").GetInt32());
        Assert.True(File.Exists(Path.Combine(outDir, id, "sample0.mp4"))); Assert.True(File.Exists(Path.Combine(outDir, id, "sample1.mp4")));   // 0.13.15: one kept clip per segment
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.StartsWith(id + ":")).ToList(); Assert.Single(lines); var line = lines[0];   // the node log shares stdout
        Assert.StartsWith(id + ": duplicate", line); Assert.Contains("b2 share", line); Assert.Contains("threads=3", line);
        var all = sw.ToString(); foreach (var k in new[] { "frames", "sscd", "lookup", "ai" }) Assert.Contains(k + " ", all); Assert.Contains("time split:", all);   // 0.13.15: the time split is the second line of the record
    }

    [Fact]
    public async Task Exclude_self_drops_the_queried_uploads_own_centers_from_the_lookup()
    {
        if (await RealFfmpegAsync() is null) return;
        var v = await BuildIndexAsync();
        var (rig, stage, id) = await SetupAsync("selfvideo01", n => Take(v["b2"], 40, n));
        rig.An.ExcludeId = "b2";   // the sample is a copy of b2, which is in the index: with it excluded there is no duplicate
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=selfvideo01", 480, null); job.JobId = id;
        await stage.RunAsync(Ctx(rig.Fs, job));   // no DuplicateFound
        Assert.True(rig.An.Excluded > 0);
        var rec = Json(rig.Fs, P.Video(id, "precheck.json"));
        Assert.NotEqual("duplicate", rec.GetProperty("decision").GetString());
        foreach (var m in rec.GetProperty("top").EnumerateArray()) Assert.NotEqual("b2", m.GetProperty("upload").GetString());
    }






    // ------------------------------------------------------------------ the duplicate state
    static EngineOptions Opts() => new()
    {
        NodeId = "test-abc123", Name = "test", Stages = ["download"], Workers = 1, Threads = 2, Once = true, PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40),
        RegistryInterval = TimeSpan.FromMilliseconds(100), BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, ControlInterval = TimeSpan.FromMilliseconds(40),
    };
    sealed class Handler(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext c) => f(c); }


    // ------------------------------------------------------------------ model download lock
    [Fact]
    public async Task Two_concurrent_requests_for_one_model_download_it_once()
    {
        var body = Encoding.UTF8.GetBytes("pretend onnx"); var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
        var h = new CountingHandler(body); using var http = new HttpClient(h);
        var m = new ModelFile("lock-test", "lock-test.onnx", "https://example.invalid/m.onnx", sha, body.Length, "test");
        var dir = Path.Combine(root, "tools");
        var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Models.EnsureAsync(m, http, default, dir))));
        Assert.Equal(1, h.Requests); Assert.All(all, p => Assert.Equal(all[0], p));
        Assert.Equal(body, await File.ReadAllBytesAsync(all[0])); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(all[0])!, "*.part*"));
        await Models.EnsureAsync(m, http, default, dir); Assert.Equal(1, h.Requests);   // present: no request
    }

    sealed class CountingHandler(byte[] body) : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests); await Task.Delay(300, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }

    // ------------------------------------------------------------------ ai score command
    [Fact]
    public async Task Ai_score_writes_ai_json_from_the_stored_vectors_and_skips_what_is_scored()
    {
        await BuildIndexAsync();
        var old = Console.Out; var sw = new StringWriter(); Console.SetOut(sw);
        try
        {
            Assert.Equal(0, await CentersCommands.AiScoreAsync(Args.Parse(["--local", root])));
            Assert.Equal(0, await CentersCommands.AiScoreAsync(Args.Parse(["--local", root])));
        }
        finally { Console.SetOut(old); }
        Assert.Contains("3 scored, 0 already scored", sw.ToString()); Assert.Contains("0 scored, 3 already scored", sw.ToString());
        var ai = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "index", "videos", "b2", "ai.json"))).RootElement;
        Assert.Equal("ai-frames-v3", ai.GetProperty("model").GetString()); Assert.Equal(300, ai.GetProperty("frames").GetInt32()); Assert.Equal(230, ai.GetProperty("sampled").GetInt32());
        var rows = AiProbe.RowsFromF16(File.Exists(Path.Combine(root, "x")) ? [] : F16(Make(300, 20, 42)));
        var expect = AiProbe.Default.Score(CentersCommands.AiSample("b2", 300, 230, false).Select(i => rows[i]).ToList());
        Assert.Equal(expect, ai.GetProperty("score").GetDouble(), 3);
        Assert.Equal(expect >= new PreCheckSettings().AiFlag, ai.GetProperty("flag").GetBoolean());
    }

    // ------------------------------------------------------------------ timing (opt-in: INDEXNODE_BENCH=1)
    [Fact]
    public async Task Timing_of_a_synthetic_five_minute_sample()
    {
        if (Environment.GetEnvironmentVariable("INDEXNODE_BENCH") != "1" || await RealFfmpegAsync() is not { } t) return;
        var video = await MakeVideoAsync(t, 300);
        var cfg = new NodeConfig(); var tools = new Tools(new HttpClient(), cfg); tools.UseFfmpeg(t.Ff, t.Probe);
        var an = new PreCheckAnalyzer(tools, cfg, new HttpClient(), new FakeStore(), P, new SearchIndexCache(Path.Combine(root, "bc"), TimeSpan.FromMinutes(10)));
        var secs = new Dictionary<string, double>(); var work = Path.Combine(root, "bw"); Directory.CreateDirectory(work);
        var (f16, times) = await an.VectorsAsync(video, work, 4, _ => { }, secs, default);
        Console.Error.WriteLine($"BENCH frames {secs["frames"]:F1}s ({times.Count} frames), sscd {secs.GetValueOrDefault("sscd"):F1}s");
    }

    // ------------------------------------------------------------------ the pre-check service (0.12.0): real Kestrel on a loopback port, synthetic index, no network
    sealed class Svc : IAsyncDisposable
    {
        public Microsoft.AspNetCore.Builder.WebApplication App = null!; public string Url = ""; public PreCheckServer Server = null!; public BlockCache Blocks = new(64L << 20);
        public Func<int, byte[]> Rows = _ => []; public Func<IReadOnlyList<LangChunk>> Speech = () => [];
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }

    async Task<Svc> StartServiceAsync(Dictionary<string, float[]> v, string tag, Func<int, byte[]> rows, Func<IReadOnlyList<LangChunk>>? speech = null)
    {
        var s = new Svc { Rows = rows, Speech = speech ?? (() => [C("speech", "zh"), C("speech", "zh", 15), C("speech", "zh", 30), C("speech", "zh", 45)]) };
        var store = OpenLocal(); var cache = new SearchIndexCache(Path.Combine(root, "svc-cache-" + tag), TimeSpan.FromMinutes(5));
        await store.PutAsync(P.Video("b2", "downloaded.json"), Encoding.UTF8.GetBytes("{\"v\":1,\"durationSeconds\":1800}"), null, null, false, default);   // the matched upload's duration, read by the service from ITS store
        var cfg = new NodeConfig(); var tools = new Tools(new HttpClient(), cfg); if (await RealFfmpegAsync() is { } rf) tools.UseFfmpeg(rf.Ff, rf.Probe);   // other tests put fake ffmpeg/ffprobe first on PATH
        var an = new PreCheckAnalyzer(tools, cfg, new HttpClient(), store, P, cache) { Blocks = s.Blocks, SkipModelCheck = true, SpeechOverride = (_, _) => Task.FromResult(s.Speech()), EmbedOverride = (dir, count, box, th, prog, ct) => Task.FromResult(s.Rows(count)) };
        an.Loaded.AddRange(["sscd-int8", "sensevoice", "ai-frames-v3"]);
        s.Server = new PreCheckServer(an, cache, store, P, s.Blocks, new PreCheckSettings(), 2, "0.12.0-test");
        s.App = s.Server.Build("http://127.0.0.1:0"); await s.App.StartAsync(); s.Url = s.App.Urls.First().Replace("[::]", "127.0.0.1");
        return s;
    }

    static byte[] NoiseJpeg(int seed) { var r = new Random(seed); var px = new byte[64 * 48 * 3]; r.NextBytes(px); return Imaging.EncodeJpeg(new Rgb(px, 64, 48)); }

    static byte[] SilentWav(int seconds) { var b = new byte[44 + seconds * 32000]; Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0); BitConverter.GetBytes(b.Length - 8).CopyTo(b, 4); Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b, 8); BitConverter.GetBytes(16).CopyTo(b, 16); BitConverter.GetBytes((short)1).CopyTo(b, 20); BitConverter.GetBytes((short)1).CopyTo(b, 22); BitConverter.GetBytes(16000).CopyTo(b, 24); BitConverter.GetBytes(32000).CopyTo(b, 28); BitConverter.GetBytes((short)2).CopyTo(b, 32); BitConverter.GetBytes((short)16).CopyTo(b, 34); Encoding.ASCII.GetBytes("data").CopyTo(b, 36); BitConverter.GetBytes(seconds * 32000).CopyTo(b, 40); return b; }

    static MultipartFormDataContent Form(string id, int frames, bool audio = true, bool exclude = false, double duration = 1750)
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent(new JsonObject { ["id"] = id, ["duration"] = duration, ["window"] = new JsonObject { ["start"] = 725, ["end"] = 1025 }, ["sampleDownloadSeconds"] = 4.2,
            ["info"] = new JsonObject { ["id"] = id, ["title"] = "T", ["language"] = "en", ["height"] = 720, ["formats"] = new JsonArray() } }.ToJsonString(), Encoding.UTF8, "application/json"), "meta");
        for (int i = 0; i < frames; i++) { var c = new ByteArrayContent(NoiseJpeg(i)); c.Headers.ContentType = new("image/jpeg"); body.Add(c, "frames", $"{i:D6}.jpg"); }
        if (audio) { var c = new ByteArrayContent(SilentWav(20)); c.Headers.ContentType = new("audio/wav"); body.Add(c, "audio", "clip.wav"); }
        if (exclude) body.Add(new StringContent("true"), "excludeSelf");
        return body;
    }

    static async Task<JsonElement> PostAsync(Svc s, MultipartFormDataContent form)
    {
        using var http = new HttpClient(); using var resp = await http.PostAsync(s.Url + "/v1/precheck", form);
        var text = await resp.Content.ReadAsStringAsync(); Assert.True(resp.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }


    // ---- POST /v1/search: the screenshot query on the same warm index (the search CLI's lookup; the image goes through the CLI's embedder, replaced here by a fake)
    static async Task<JsonElement> SearchPostAsync(Svc s, Action<MultipartFormDataContent> fill, string query = "")
    {
        var body = new MultipartFormDataContent(); fill(body);
        using var http = new HttpClient(); using var resp = await http.PostAsync(s.Url + "/v1/search" + query, body);
        var text = await resp.Content.ReadAsStringAsync(); Assert.True(resp.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Search_finds_a_planted_frame_from_a_vector_f16_and_f32_and_writes_nothing()
    {
        var v = await BuildIndexAsync();
        await using var s = await StartServiceAsync(v, "sv", n => []);
        await SearchPostAsync(s, b => b.Add(new ByteArrayContent(Take(v["b2"], 77, 1)), "vector", "q.bin"));   // the first query loads the index cache (a local file under the test root)
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
        foreach (var f32 in new[] { false, true })
        {
            var res = await SearchPostAsync(s, b =>
            {
                var bytes = f32 ? v["b2"].AsSpan(77 * D, D).ToArray().SelectMany(BitConverter.GetBytes).ToArray() : Take(v["b2"], 77, 1);
                b.Add(new ByteArrayContent(bytes), "vector", "q.bin");
            });
            var top = res.GetProperty("hits")[0];
            Assert.Equal("b2", top.GetProperty("upload").GetString()); Assert.Equal(77.0, top.GetProperty("time").GetDouble()); Assert.Equal("0:01:17", top.GetProperty("timestamp").GetString());
            Assert.True(top.GetProperty("score").GetDouble() > 0.99); Assert.Equal(20, res.GetProperty("hits").GetArrayLength()); Assert.Equal("vector", res.GetProperty("query").GetProperty("kind").GetString());
            Assert.True(res.GetProperty("indexVersion").GetInt32() > 0);
            foreach (var k in new[] { "embedMs", "scanMs", "readMs", "scoreMs", "totalMs" }) Assert.True(res.GetProperty("timings").TryGetProperty(k, out _), k);
        }
        var small = await SearchPostAsync(s, b => b.Add(new ByteArrayContent(Take(v["b2"], 77, 1)), "vector", "q.bin"), "?top=3&topCenters=50");   // the defaults are 20 and 300; both can be set
        Assert.Equal(3, small.GetProperty("hits").GetArrayLength()); Assert.Equal(50, small.GetProperty("query").GetProperty("topCenters").GetInt32());
        Assert.Equal(before, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Search_finds_a_planted_frame_from_an_image_and_passes_trim_to_the_embedder()
    {
        var v = await BuildIndexAsync();
        await using var s = await StartServiceAsync(v, "si", n => []);
        var seen = new List<(bool Trim, long Bytes)>();
        s.Server.ImageEmbedder = (bytes, trim, ct) => { seen.Add((trim, (long)bytes.Length)); return Task.FromResult((v["b2"].AsSpan(200 * D, D).ToArray(), trim ? new BarCrop(40, 40, 0, 0) : BarCrop.None)); };
        var jpg = NoiseJpeg(7);
        JsonElement Run(bool trim) => SearchPostAsync(s, b =>
        {
            var c = new ByteArrayContent(jpg); c.Headers.ContentType = new("image/jpeg"); b.Add(c, "image", "shot.jpg"); if (trim) b.Add(new StringContent("true"), "trim");
        }).GetAwaiter().GetResult();
        var plain = Run(false); var trimmed = Run(true);
        Assert.Equal([(false, (long)jpg.Length), (true, (long)jpg.Length)], seen);   // the uploaded bytes reached the embedder
        foreach (var res in new[] { plain, trimmed })
        {
            var top = res.GetProperty("hits")[0];
            Assert.Equal("b2", top.GetProperty("upload").GetString()); Assert.Equal(200.0, top.GetProperty("time").GetDouble()); Assert.True(top.GetProperty("score").GetDouble() > 0.99);
            Assert.Equal("image", res.GetProperty("query").GetProperty("kind").GetString());
        }
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("query").GetProperty("crop").ValueKind);
        Assert.Equal(40, trimmed.GetProperty("query").GetProperty("crop").GetProperty("top").GetInt32());
        Assert.True((await (await new HttpClient().GetAsync(s.Url + "/v1/health")).Content.ReadAsStringAsync()).Contains("\"searches\":2"));
    }

    [Fact]
    public async Task Search_without_a_query_or_with_a_bad_vector_is_a_400()
    {
        var v = await BuildIndexAsync();
        await using var s = await StartServiceAsync(v, "se", n => []);
        using var http = new HttpClient();
        using (var r = await http.PostAsync(s.Url + "/v1/search", new MultipartFormDataContent { { new StringContent("x"), "other" } })) Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await http.PostAsync(s.Url + "/v1/search", new MultipartFormDataContent { { new ByteArrayContent(new byte[10]), "vector", "q.bin" } })) Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
    }




    [Fact]
    public void Block_cache_is_an_lru_bounded_by_bytes_and_clears()
    {
        var c = new BlockCache(1200); float[] Blk() => new float[100];
        c.Put("a", 1, (Blk(), new float[5])); c.Put("a", 2, (Blk(), new float[5])); c.Put("a", 3, (Blk(), new float[5]));
        Assert.True(c.Stats().Bytes <= 2000); Assert.False(c.TryGet("a", 1, out _)); Assert.True(c.TryGet("a", 3, out _));
        c.Clear(); Assert.Equal(0, c.Stats().Entries); Assert.False(c.TryGet("a", 3, out _));
    }

    sealed class Spy(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(string Url, byte[] Body, string Headers)> Seen = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add((r.RequestUri!.ToString(), r.Content == null ? [] : await r.Content.ReadAsByteArrayAsync(ct), r.Headers.ToString() + (r.Content?.Headers.ToString() ?? "")));
            return await base.SendAsync(r, ct);
        }
    }

    [Fact]
    public async Task A_skip_leaves_download_zip_and_every_blob_byte_identical_and_only_adds_precheck_json()
    {
        if (await RealFfmpegAsync() is null) return;
        var v = await BuildIndexAsync();
        var (rig, stage, id) = await SetupAsync("skipdup00001", n => Take(v["b2"], 40, n));
        await rig.Fs.PutAsync(P.Video(id, "download.zip"), [9, 8, 7, 6], null, null, false, default);
        var before = rig.Fs.Blobs.ToDictionary(kv => kv.Key, kv => kv.Value.Data.ToArray());
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=skipdup00001", 480, null); job.JobId = id;
        DuplicateFound? ex = null;
        try { await stage.RunAsync(Ctx(rig.Fs, job)); } catch (DuplicateFound d) { ex = d; }
        Assert.NotNull(ex); Assert.Equal("b2", ex!.Upload); Assert.False(string.IsNullOrEmpty(ex.Why));
        foreach (var kv in before.Where(k => !k.Key.EndsWith("/video.json", StringComparison.Ordinal))) Assert.Equal(kv.Value, rig.Fs[kv.Key].Data);   // nothing changed, nothing deleted (video.json gains the record)
        var added = rig.Fs.Blobs.Keys.Except(before.Keys).ToList(); Assert.All(added, k => Assert.Equal(VideoAggregate.Name(P), k));   // a skip may add only the aggregate (the dual write of video.json's new record): never precheck.json, never a stretches.json
        Assert.False(rig.Fs.Exists(P.Video(id, "precheck.json"))); Assert.NotNull(Json(rig.Fs, P.Video(id, "precheck.json")));   // 0.13.2: video.json (already there) gains the record; no new blob
        Assert.Empty(rig.Calls);   // no yt-dlp: the window came from the stored video
    }

    // ------------------------------------------------------------------ 0.13.1: "to ('-1') must be a non-negative value" in the pre-check lookup
    [Fact]
    public async Task A_zero_byte_centers_zip_fails_as_a_bad_zip_never_as_a_negative_range()
    {
        var fs = new FakeStore(); await fs.PutAsync(P.Video("ghost", "centers.zip"), [], null, null, false, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => CentersZip.OpenAsync(fs, P.Video("ghost", "centers.zip"), default));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ZipFormat.ReadDirectoryAsync(new BlobZipSource(fs, P.Video("ghost", "centers.zip"), 0, null), default, 2048));
        Assert.Contains("0 bytes", ex.Message);
    }

    [Fact]
    public async Task The_real_store_sends_no_range_request_for_zero_bytes()
    {
        var calls = 0;
        var h = new HitHandler(() => calls++);
        var s = new RestStore(new HttpClient(h), "https://acct.blob.core.windows.net/media?sv=2021-12-02&sp=racwdlfti&sig=x") { RetryDelay = _ => TimeSpan.Zero };
        var r = await s.GetRangeAsync("a/b.zip", 0, 0, "\"e\"", default);   // used to throw ArgumentOutOfRangeException: to ('-1')
        Assert.Equal(0, r.Data!.Length); Assert.False(r.Mismatch);
        Assert.Empty(await s.GetRangeAsync("a/b.zip", 5, 0, default)); Assert.Equal(0, calls);
    }
    sealed class HitHandler(Action hit) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { hit(); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1]) }); }
    }

    // ------------------------------------------------------------------ 0.13.1: the duplicate skip reads the matched duration from the repacked layout; an AI upload is a skip
    static async Task Repack(IStore fs, string id, double duration)
    {   // the repacked (0.10.0+) layout: no downloaded.json blob, the summary is download.zip's idx_summary metadata
        if (fs is FakeStore f0) f0.Blobs.Remove(P.Video(id, "downloaded.json")); else { try { await fs.DeleteAsync(P.Video(id, "downloaded.json"), default); } catch { } }
        await fs.PutAsync(P.Video(id, "download.zip"), [1, 2, 3, 4], null, null, false, default);
        await fs.SetMetadataAsync(P.Video(id, "download.zip"), new Dictionary<string, string> { ["idx_summary"] = "{\"durationSeconds\":" + duration.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}" }, null, default);
    }

    [Fact]
    public async Task The_matched_duration_comes_from_video_json_and_a_missing_one_fails_loudly()
    {
        var fs = new FakeStore();
        await fs.PutAsync(P.Video("new1", "video.json"), Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"new1\",\"youtube\":{\"durationSeconds\":1800}}"), null, null, false, default);
        Assert.Equal(1800, (await CatalogBuild.ReadPrecheckAsync(fs, P, "new1", default)).Duration);
        await fs.PutAsync(P.Video("old1", "video.json"), Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"old1\",\"download\":{\"durationSeconds\":1234}}"), null, null, false, default);
        Assert.Equal(1234, (await CatalogBuild.ReadPrecheckAsync(fs, P, "old1", default)).Duration);
        await Assert.ThrowsAsync<VideoJsonMissingException>(() => CatalogBuild.ReadPrecheckAsync(fs, P, "none", default));   // never a quiet null
        await fs.PutAsync(P.Video("bad", "video.json"), Encoding.UTF8.GetBytes("{not json"), null, null, false, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => CatalogBuild.ReadPrecheckAsync(fs, P, "bad", default));
    }

    [Fact]
    public async Task A_real_duplicate_is_skipped_on_the_repacked_layout()
    {
        if (await RealFfmpegAsync() is null) return;
        var v = await BuildIndexAsync();
        var (rig, stage, id) = await SetupAsync("repackdup01", n => Take(v["b2"], 40, n));
        await Repack(OpenLocal(), "b2", 1800);   // (the index store the analyzer reads) b2's duration is only in download.zip's metadata: before the fix this was always 'duration unknown: not skipped'
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=repackdup01", 480, null); job.JobId = id;
        DuplicateFound? ex = null;
        try { await stage.RunAsync(Ctx(rig.Fs, job)); } catch (DuplicateFound d) { ex = d; }
        Assert.NotNull(ex); Assert.Equal("b2", ex!.Upload); Assert.False(ex.Ai);
        Assert.Equal("duplicate", Json(rig.Fs, P.Video(id, "precheck.json")).GetProperty("decision").GetString());
    }

    [Fact]
    public async Task An_ai_flagged_upload_is_not_skipped_by_the_precheck_stage_and_leaves_its_record_untouched()
    {
        // a repeat of a claim interrupted after the record: the stored record says ai (this part needs no ffmpeg)
        var fs = new FakeStore();
        var rec = new JsonObject { ["decision"] = "new", ["ai"] = new JsonObject { ["flag"] = true, ["score"] = 0.61, ["threshold"] = 0.479 }, ["speech"] = new JsonObject { ["speechLanguage"] = "zh", ["dubbed"] = false } };
        await VideoJsonSeed.PutAsync(fs, P, "aiaiaiai001", 1800, precheck: rec);
        await fs.PutAsync(P.Video("aiaiaiai001", "download.zip"), [9, 9], null, null, false, default);
        var an = new PreCheckAnalyzer(new Tools(new HttpClient(), new NodeConfig()), new NodeConfig(), new HttpClient(), fs, P, new SearchIndexCache(Path.Combine(root, "cache-ai0"), TimeSpan.FromMinutes(10)));
        var stage = new PreCheckStage(new Tools(new HttpClient(), new NodeConfig()), an, new PreCheckSettings(), "t");
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=aiaiaiai001", 480, null); job.JobId = "aiaiaiai001";
        var before = fs.Blobs.ToDictionary(kv => kv.Key, kv => kv.Value.Data.ToArray());
        await stage.RunAsync(Ctx(fs, job));   // 0.13.1: no skip; the engine holds it (record: ai.flag)
        Assert.True(Json(fs, P.Video("aiaiaiai001", "precheck.json")).GetProperty("ai").GetProperty("flag").GetBoolean());
        foreach (var kv in before) Assert.Equal(kv.Value, fs[kv.Key].Data);   // nothing deleted or rewritten
        Assert.Equal(before.Count, fs.Blobs.Count);
    }

    [Fact]
    public async Task An_ai_flagged_new_drama_is_not_skipped_end_to_end()
    {
        if (await RealFfmpegAsync() is null) return;
        var v = await BuildIndexAsync();
        var (rig, _, id) = await SetupAsync("aiflagged01", n => F16(Gauss(new Random(7)).Concat(Gauss(new Random(8))).Take(n * D).ToArray()));
        var s = new PreCheckSettings { AiFlag = -1000, AiStrict = 1000 };   // every score is at or above the flag
        var stage = new PreCheckStage(rig.Tools, rig.An, s, "t");
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=aiflagged01", 480, null); job.JobId = id;
        DuplicateFound? ex = null; try { await stage.RunAsync(Ctx(rig.Fs, job)); } catch (DuplicateFound d) { ex = d; }
        Assert.Null(ex); { Assert.True(Json(rig.Fs, P.Video(id, "precheck.json")).GetProperty("ai").GetProperty("flag").GetBoolean()); }
    }

    // ------------------------------------------------------------------ 0.13.1: the catalog
    async Task<(IStore Store, Dictionary<string, float[]> V)> CatalogRigAsync()
    {
        var v = await BuildIndexAsync(); var st = OpenLocal();
        // 0.13.2: every fact is video.json's (the catalog reads nothing else); a1 and c3 are processed (their AI score was measured on their centers), b2 is processed with a pre-check speech/AI record
        await VideoJsonSeed.PutAsync(st, P, "a1", 900, aiScore: 0.2, aiFlag: false); await VideoJsonSeed.PutAsync(st, P, "c3", 600, aiScore: 0.1, aiFlag: false);
        await VideoJsonSeed.PutAsync(st, P, "b2", 1800, "en", true, title: "B two", channel: "Chan", aiScore: 0.12, aiFlag: false);
        await VideoJsonSeed.PutAsync(st, P, "m9", 300, "zh", false, new JsonObject { ["at"] = "2026-11-01T00:00:00Z" }, title: "skipped", aiScore: 0.7, aiFlag: true);   // AI-held: short-circuited, not processed
        var dupRec = new JsonObject { ["decision"] = "duplicate", ["matchUpload"] = "b2", ["top"] = new JsonArray(new JsonObject { ["upload"] = "b2", ["share"] = 0.93, ["bestCosine"] = 0.88, ["bestTime"] = 12.5 }) };
        await VideoJsonSeed.PutAsync(st, P, "d8", 1795, "en", false, dupRec, title: "dup of b2", channel: "Chan2", aiScore: 0.66, aiFlag: true);   // a duplicate that is also AI-flagged: a duplicate skip, not a hold
        var old = Console.Out; Console.SetOut(new StringWriter());
        try { Assert.Equal(0, await CentersCommands.SearchIndexAsync(Args.Parse(["build", "--local", root, "--threads", "2"]))); } finally { Console.SetOut(old); }
        return (st, v);
    }

    [Fact]
    public async Task The_search_index_build_writes_catalog_json_gz_with_the_version_and_one_entry_per_upload()
    {
        var (st, _) = await CatalogRigAsync();
        var idx = (await new SearchIndexCache(Path.Combine(root, "cat1"), TimeSpan.FromMinutes(10)).GetAsync(st, P, default))!;
        Assert.Equal(2, idx.Version); Assert.True(File.Exists(Path.Combine(root, "index", "search", "v2", "catalog.json.gz")) || await st.HeadAsync(Catalog.Name(P, 2), default) != null);
        var c = idx.Catalog!; Assert.NotNull(c); Assert.Equal(2, c.Version); Assert.Equal(3, c.Groups.Count); Assert.Null(c.Find("m9")!.GroupId);   // 0.13.2: the group section is filled for the indexed uploads; a metadata-only one is in no group
        Assert.Equal(["a1", "b2", "c3", "d8", "m9"], c.Uploads.Select(u => u.Id).OrderBy(x => x, StringComparer.Ordinal));
        var a1 = c.Find("a1")!; Assert.Equal(900, a1.DurationSeconds); Assert.Equal("processed", a1.State); Assert.NotNull(a1.Ai?.Score);   // scored from its centers (no precheck.json)
        Assert.Equal("g-a1", a1.GroupId); Assert.True(a1.Canonical); Assert.False(a1.Mixed); Assert.Equal(c.Groups.Single(g => g.Id == "g-a1").Canonical, "a1");
        var b2 = c.Find("b2")!; Assert.Equal(1800, b2.DurationSeconds); Assert.Equal("en", b2.SpeechLanguage); Assert.True(b2.Dubbed); Assert.Equal(0.12, b2.Ai!.Score); Assert.Equal("B two", b2.Title); Assert.Equal("Chan", b2.Channel);
        Assert.Equal("index", a1.CheckedBy); Assert.Equal("index", b2.CheckedBy); Assert.Equal("full", a1.Data); Assert.Equal("full", b2.Data); Assert.Equal("full", c.Find("c3")!.Data);
        var m9 = c.Find("m9")!; Assert.Equal("metadata-only", m9.State); Assert.Equal("ai", m9.Held); Assert.Equal("precheck", m9.CheckedBy); Assert.Equal("precheck", m9.Data); Assert.Equal(300, m9.DurationSeconds); Assert.True(m9.Ai!.Flag); Assert.Equal(0.7, m9.Ai.Score); Assert.Equal("zh", m9.SpeechLanguage); Assert.Equal("skipped", m9.Title);
        var d8 = c.Find("d8")!; Assert.Equal("metadata-only", d8.State); Assert.Null(d8.Held); Assert.Equal("precheck", d8.CheckedBy); Assert.Equal("precheck", d8.Data); Assert.Equal("duplicate", d8.Decision);   // duplicate first, although AI-flagged
        Assert.Equal(("b2", 0.93, 12.5, 0.88), (d8.MatchUpload, d8.MatchShare, d8.MatchOffset, d8.MatchCosine)); Assert.Equal(1795, d8.DurationSeconds); Assert.Equal("dup of b2", d8.Title); Assert.Equal("Chan2", d8.Channel); Assert.Equal(0.66, d8.Ai!.Score); Assert.Equal("en", d8.SpeechLanguage); Assert.False(d8.Dubbed);
        Assert.Equal(c.Uploads.Count, Catalog.FromGzip(c.ToGzip()).Uploads.Count);
    }

    async Task<PreCheckAnalysis> AnalyzeB2Async(IStore st, Dictionary<string, float[]> v, string tag)
    {
        var rows = new float[40 * D]; Array.Copy(v["b2"], rows, rows.Length);
        var an = new PreCheckAnalyzer(new Tools(new HttpClient(), new NodeConfig()), new NodeConfig(), new HttpClient(), st, P, new SearchIndexCache(Path.Combine(root, "cache-" + tag), TimeSpan.FromMinutes(10))) { SkipModelCheck = true, EmbedOverride = (dir, n, box, th, pr, ct) => Task.FromResult(F16(rows)) };
        return await an.AnalyzeFramesAsync(root, 40, BarCrop.None, null, new PreCheckSettings(), 1, _ => { }, new Dictionary<string, double>(), null, default);
    }

    /// <summary>
    /// 0.13.15 timing fixture: the catalog rig (uploads a1, b2, c3 in the index; 40-frame fixtures), a 60-frame two-segment sample made of b2's own vectors (30 + 30), the node's FramesCache WARM (every frames file already local).
    /// Compute (SSCD stubbed, lookup, per-segment shares, member checks) must make 0 store requests for the frames files and stay far under the 5 s target (the bound here is generous: 8 s, slow runners).
    /// </summary>
    [Fact]
    public async Task A_warm_cache_two_segment_lookup_of_60_frames_makes_no_frames_requests_and_is_fast()
    {
        var (st, v) = await CatalogRigAsync();
        var cacheDir = Path.Combine(root, "frames-cache-warm"); var old = FramesCache.Current; FramesCache.Current = new FramesCache(cacheDir, 1L << 30);
        try
        {
            var rows = new float[60 * D]; for (int i = 0; i < 60; i++) Array.Copy(v["b2"], (i % 40) * D, rows, i * D, D);
            var counting = new CountingStore(st);
            var an = new PreCheckAnalyzer(new Tools(new HttpClient(), new NodeConfig()), new NodeConfig(), new HttpClient(), counting, P, new SearchIndexCache(Path.Combine(root, "cache-timing"), TimeSpan.FromMinutes(10))) { SkipModelCheck = true, EmbedOverride = (dir, n, box, th, pr, ct) => Task.FromResult(F16(rows)) };
            var info = new List<PreCheckAnalyzer.SegInfo> { new(0.30, 540, 840, 30), new(0.70, 1260, 1560, 30) };
            var s = new PreCheckSettings();
            await an.AnalyzeFramesAsync(root, 60, BarCrop.None, null, s, 4, _ => { }, new Dictionary<string, double>(), null, default, info);   // 1st run: fills the frames cache (a GET per file)
            Assert.True(FramesCache.Current.Downloads > 0);
            int downloads = FramesCache.Current.Downloads; counting.Reset();
            var secs = new Dictionary<string, double>(); var sw = System.Diagnostics.Stopwatch.StartNew();
            var a = await an.AnalyzeFramesAsync(root, 60, BarCrop.None, null, s, 4, _ => { }, secs, null, default, info);
            sw.Stop();
            Assert.Equal(downloads, FramesCache.Current.Downloads);                                           // nothing fetched again
            Assert.Equal(0, counting.FramesReads);                                                            // no ranged read of a frames file: the cache serves every one
            Assert.Equal(0, secs["lookupRequests"]);
            Assert.Equal(2, a.Segments!.Count); Assert.Equal(60, a.Frames);
            Assert.Contains("b2", a.Segments[0].Shares.Keys);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"compute took {sw.Elapsed.TotalSeconds:0.00} s");
            Console.WriteLine($"TIMING 60 frames, warm cache: {sw.Elapsed.TotalSeconds:0.00} s; lookup {secs["lookup"]:0.00} s ({secs["lookupRequests"]} requests, {secs["lookupBytes"]} B), members {secs.GetValueOrDefault("members"):0.00} s");
        }
        finally { FramesCache.Current = old; }
    }

    [Fact]
    public async Task A_real_duplicate_is_skipped_with_the_duration_of_the_catalog()
    {
        var (st, v) = await CatalogRigAsync();
        var a = await AnalyzeB2Async(st, v, "dup");
        Assert.Equal("b2", a.Top[0].Upload); Assert.Equal(1800, a.Top[0].Duration);
        var d = PreCheck.Decide(a, 1790, _ => null, new PreCheckSettings());   // no lookup of the matched upload at all
        Assert.Equal("duplicate", d.Decision);
    }

    [Fact]
    public async Task The_canonical_exception_uses_the_language_of_the_catalog()
    {
        var (st, v) = await CatalogRigAsync();
        var a = await AnalyzeB2Async(st, v, "can");
        Assert.Equal("en", a.Top[0].SpeechLanguage);
        var zh = a with { Speech = new SpeechResult("zh", false, 1, 30, 60, null) };
        Assert.Equal("canonical", PreCheck.Decide(zh, 1790, _ => null, new PreCheckSettings()).Decision);   // zh original of an en dub
        var en = a with { Speech = new SpeechResult("en", true, 1, 30, 60, null) };
        Assert.Equal("duplicate", PreCheck.Decide(en, 1790, _ => null, new PreCheckSettings()).Decision);
    }

    [Fact]
    public async Task A_held_upload_that_is_later_processed_gets_its_entry_replaced_by_the_full_one()
    {
        var (st, _) = await CatalogRigAsync();
        var idx = (await new SearchIndexCache(Path.Combine(root, "cat2"), TimeSpan.FromMinutes(10)).GetAsync(st, P, default))!;
        Assert.Equal("ai", idx.Catalog!.Find("m9")!.Held);
        var m9 = idx.Uploads[0] with { Id = "m9" };   // now it has a centers.zip and is in the index
        await using var _ = default(IAsyncDisposable?) as IAsyncDisposable ?? new Nop();
        var rebuilt = await CatalogBuild.BuildAsync(st, P, 3, idx.Uploads.Append(m9).ToList(), idx.Catalog, new PreCheckSettings().AiFlag, null, default);
        var e = rebuilt.Find("m9")!; Assert.Equal("processed", e.State); Assert.Equal("index", e.CheckedBy); Assert.Equal("full", e.Data); Assert.Null(e.Held); Assert.Null(e.Decision);
        Assert.Single(rebuilt.Uploads, u => u.Id == "m9");
    }
    sealed class Nop : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

    [Fact]
    public async Task The_index_build_runs_the_full_check_for_an_upload_without_a_precheck_and_prunes_no_centers()
    {
        var v = await BuildIndexAsync(); var st = OpenLocal();
        var zip = await st.GetAsync(P.FramesFile("a1"), default);
        await VideoJsonSeed.PutAsync(st, P, "a1copy", 900);   // a processed copy of a1: its descriptor has no precheck section (an upload from before the pre-check)
        await st.PutAsync(P.FramesFile("a1copy"), zip!.Data, null, null, false, default);   // a processed copy of a1: no precheck.json at all (an upload from before the pre-check)
        Assert.Equal(0, await CentersCommands.SearchIndexAsync(Args.Parse(["update", "--local", root, "--threads", "2"])));   // (its output goes to the console: no Console.SetOut here, other classes capture it in parallel)
        var idx = (await new SearchIndexCache(Path.Combine(root, "cfull"), TimeSpan.FromMinutes(10)).GetAsync(st, P, default))!;
        var g = (await GroupStore.LoadAsync(st, P, idx.Version, default))!; var of = g.GroupOf();
        Assert.Equal(of["a1"], of["a1copy"]);   // joined by the build's own check
        var m = g.Groups.Single(x => x.Id == of["a1"]).Members.Single(x => x.Upload == "a1copy");
        Assert.Equal("index", m.CheckedBy); Assert.True(m.MatchShare > 0.9, m.MatchShare.ToString()); Assert.Equal(0, m.Offset!.Value, 1);
        Assert.NotEqual(of["a1"], of["b2"]);
        Assert.Equal("index", idx.Catalog!.Find("a1copy")!.CheckedBy); Assert.Equal(of["a1"], idx.Catalog.Find("a1copy")!.GroupId);
        // no center pruning: the table holds every center of every processed upload
        Assert.Equal(idx.Uploads.Sum(u => u.K), idx.Count);
    }
}

/// <summary>Forwards to another store and counts the reads of frames files (index/search/frames/): the warm-cache timing test needs 0.</summary>
public sealed class CountingStore(IStore inner) : IStore
{
    int framesReads; public int FramesReads => Volatile.Read(ref framesReads); public void Reset() => Interlocked.Exchange(ref framesReads, 0);
    void Count(string name) { if (name.Contains("search/frames/", StringComparison.Ordinal)) Interlocked.Increment(ref framesReads); }
    public Task<bool> CreateAppendBlobAsync(string name, bool onlyIfNew, CancellationToken ct) => inner.CreateAppendBlobAsync(name, onlyIfNew, ct);
    public Task<AppendResult> AppendBlockAsync(string name, byte[] data, long appendPos, CancellationToken ct) => inner.AppendBlockAsync(name, data, appendPos, ct);
    public Task<AppendRead> ReadAppendAsync(string name, long offset, int maxLen, CancellationToken ct) => inner.ReadAppendAsync(name, offset, maxLen, ct);
    public Task<bool?> SealAppendBlobAsync(string name, long appendPos, CancellationToken ct) => inner.SealAppendBlobAsync(name, appendPos, ct);
    public Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, CancellationToken ct) => inner.PutIfMatchAsync(name, data, etag, ct);
    public Task<AppendInfo?> GetAppendInfoAsync(string name, CancellationToken ct) => inner.GetAppendInfoAsync(name, ct);
    public Task<BlobProps?> HeadAsync(string name, CancellationToken ct) => inner.HeadAsync(name, ct);
    public Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct) { Count(name); return inner.GetRangeAsync(name, off, len, ifMatch, ct); }
    public Task<bool> PutZipIfMatchAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, string etag, CancellationToken ct) => inner.PutZipIfMatchAsync(name, path, metadata, tags, etag, ct);
    public Task<bool> PutZipAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct) => inner.PutZipAsync(name, path, metadata, tags, onlyIfNew, ct);

    public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct) => inner.FindByTagsAsync(where, ct);
    public Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct) => inner.ListAsync(prefix, withTags, withMetadata, ct);
    public Task<BlobContent?> GetAsync(string name, CancellationToken ct) { Count(name); return inner.GetAsync(name, ct); }
    public Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct) => inner.GetIfChangedAsync(name, etag, ct);
    public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct) => inner.GetTagsAsync(name, ct);
    public Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json") => inner.PutAsync(name, data, tags, leaseId, onlyIfNew, ct, contentType);
    public Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct) => inner.PutFileAsync(name, path, contentType, progress, ct);
    public Task DownloadToFileAsync(string name, string path, CancellationToken ct) { Count(name); return inner.DownloadToFileAsync(name, path, ct); }
    public Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct) => inner.AcquireLeaseAsync(name, seconds, ct);
    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.RenewLeaseAsync(name, leaseId, ct);
    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.ReleaseLeaseAsync(name, leaseId, ct);
    public Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct) => inner.SetMetadataAsync(name, metadata, leaseId, ct);
    public Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct) => inner.SetTagsAsync(name, tags, leaseId, ct);
    public Task DeleteAsync(string name, CancellationToken ct) => inner.DeleteAsync(name, ct);

}
