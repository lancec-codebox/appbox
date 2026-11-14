using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode.Tests;

file sealed class FakeChat(params string[] replies) : IChat
{
    public readonly List<(string Model, JsonNode Content)> Calls = [];
    int i;
    public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) { Calls.Add((model, content.DeepClone())); return Task.FromResult(replies[Math.Min(i++, replies.Length - 1)]); }
}

public class CastLogicTests
{
    static float[] Unit(params float[] v) { var n = MathF.Sqrt(v.Sum(x => x * x)); return v.Select(x => x / n).ToArray(); }

    [Fact]
    public void Average_linkage_cosine_splits_identities_at_the_threshold()
    {
        var embs = new List<float[]> { Unit(1, 0, 0), Unit(1, .05f, 0), Unit(.98f, 0, .1f), Unit(0, 1, 0), Unit(.05f, 1, 0), Unit(0, 0, 1) };
        var ids = CastLogic.Cluster(embs, 0.5);
        Assert.Equal(ids[0], ids[1]); Assert.Equal(ids[0], ids[2]); Assert.Equal(ids[3], ids[4]);
        Assert.NotEqual(ids[0], ids[3]); Assert.NotEqual(ids[3], ids[5]); Assert.Equal(3, ids.Distinct().Count());
        Assert.Single(CastLogic.Cluster(embs, 1.1).Distinct());       // above every distance: one cluster
        Assert.Equal(6, CastLogic.Cluster(embs, 0.0001).Distinct().Count()); // below every distance: singletons
    }

    [Fact]
    public void Each_model_has_its_own_threshold_and_buffalo_l_is_the_default()
    {
        Assert.Equal("buffalo_l", CastLogic.ModelFor(null).Id);
        Assert.Equal("buffalo_l", CastLogic.ModelFor("buffalo_l").Id);
        Assert.Equal("yunet-sface", CastLogic.ModelFor("yunet-sface").Id);
        Assert.Equal(512, CastLogic.Default.Dim); Assert.Equal(128, CastLogic.YuNetSFace.Dim);
        Assert.NotEqual(CastLogic.Default.Threshold, CastLogic.YuNetSFace.Threshold);
        Assert.Contains(Models.YuNetDet, Models.FaceYuNet); Assert.DoesNotContain(Models.FaceYuNet, m => m.File.Contains("det_10g"));
        Assert.Contains(Models.ScrfdDet, CastStage.ModelFiles(null!)); Assert.Contains(Models.YuNetDet, CastStage.ModelFiles("yunet-sface"));
    }

    [Fact]
    public void Filters_reject_small_low_score_and_profile_faces()
    {
        var ok = new FaceRec { H = 100, Score = 0.9f, Blur = 200, Yaw = 0.1 };
        Assert.True(CastLogic.Passes(ok));
        Assert.False(CastLogic.Passes(new FaceRec { H = 100, Score = 0.9f, Blur = 5, Yaw = 0.1 }));
        Assert.False(CastLogic.Passes(new FaceRec { H = 100, Score = 0.9f, Blur = 200, Yaw = 0.9 }));
    }

    [Fact]
    public void Portrait_prefers_unmasked_even_when_the_masked_face_has_better_quality()
    {
        var masked = new FaceRec { Quality = 0.95, Masked = true }; var bare = new FaceRec { Quality = 0.4 }; var bare2 = new FaceRec { Quality = 0.3 };
        Assert.Same(bare, CastLogic.PortraitFace([masked, bare, bare2]));
        Assert.Same(masked, CastLogic.PortraitFace([masked, new FaceRec { Quality = 0.5, Masked = true }])); // only masked faces: the best of them
    }

    [Fact]
    public void Mask_decision_is_zero_shot_logit_comparison()
    {
        Assert.True(CastLogic.IsMasked(3.1, 1.0)); Assert.False(CastLogic.IsMasked(1.0, 3.1));
    }

    [Fact]
    public void Credits_keep_lines_with_the_marker_words_from_the_first_and_last_three_minutes()
    {
        var cues = new List<(double, double, string)> { (10, 12, "主演:某某"), (500, 502, "主演:远处"), (9000, 9002, "李四 饰 王"), (20, 21, "你好") };
        var r = CastLogic.CreditLines(cues, 9080);
        Assert.Equal(2, r.Count);
    }
}

public class DescribeTests
{
    static DescribeInput Input(string dir)
    {
        File.WriteAllBytes(Path.Combine(dir, "s0.jpg"), [1, 2, 3]); File.WriteAllBytes(Path.Combine(dir, "s1.jpg"), [4, 5]); File.WriteAllBytes(Path.Combine(dir, "t.jpg"), [9]); File.WriteAllBytes(Path.Combine(dir, "p.jpg"), [7]);
        return new DescribeInput
        {
            JobId = "j1", Title = "新娘重生 #shorts FULL", Description = "d", Duration = 1000, Cues = Enumerable.Range(0, 300).Select(i => new ShapeCue(i * 3, i * 3 + 2, "第" + i + "句")).ToList(),
            Credits = ["主演：张三"], Clusters = [new(0, 500, 10, false), new(1, 400, 20, false), new(2, 100, 90, true)], Thumb = Path.Combine(dir, "t.jpg"),
            Sheets = new Dictionary<int, string> { [0] = Path.Combine(dir, "s0.jpg"), [1] = Path.Combine(dir, "s1.jpg") }, PosterSheet = Path.Combine(dir, "p.jpg"), PosterCount = 5,
        };
    }

    const string Main = """
        ```json
        {"title":{"en":"Reborn Bride","zh":"新娘重生"},"fullTitle":{"en":"Reborn Bride Full"},"teaser":{"en":"A bride returns. Gu is the villain."},"synopsis":{"en":"Everything. She wins."},
         "tags":["rebirth","复仇","Villain plot","revenge"],"era":"ancient","fantasy":false,
         "cast":[{"cluster":0,"character":{"zh":"陆","pinyin":"Lù","en":"Lu"},"role":"female lead","roleAtStart":"bride","description":"The bride.","actor":"张三","label":{"en":"Lu"}},
                 {"cluster":1,"character":null,"role":"female lead","roleAtStart":"supporting","description":"Her rival, secretly her sister","actor":"王五","label":{"en":"Lu's sister"}},
                 {"cluster":9,"role":"main"}],
         "relations":[{"from":0,"to":1,"type":"sister","label":{"en":"sister"},"since":900,"until":null,"spoiler":false,"implied":true},{"from":0,"to":0,"type":"x"},{"from":0,"to":1,"type":"rival","label":{"en":"rival"},"since":5,"until":null,"spoiler":false,"implied":false}],
         "picks":{"0":{"portrait":1,"stills":[0,2]}},"poster":3}
        ```
        """;

    [Fact]
    public async Task One_batched_vision_call_carries_all_images_and_the_text_track_with_a_check_after_it()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var chat = new FakeChat(Main, """{"teaserReveals":false,"reasons":[],"flaggedCast":[],"flaggedRelations":[]}""");
        var res = await Describe.RunAsync(chat, "text-m", "vision-m", Input(dir), default);
        Assert.Equal(2, chat.Calls.Count); // 1 batched vision call + 1 spoiler check: never one per cue
        Assert.Equal("vision-m", chat.Calls[0].Model); Assert.Equal("text-m", chat.Calls[1].Model);
        var arr = chat.Calls[0].Content.AsArray();
        Assert.Equal(1 + 2 * 4, arr.Count); // prompt + (label, image) x (thumbnail, 2 sheets, poster sheet)
        Assert.Equal(4, arr.Count(x => x!["type"]!.GetValue<string>() == "image_url"));
        var prompt = arr[0]!["text"]!.GetValue<string>();
        Assert.Contains("新娘重生 #shorts FULL", prompt); Assert.Contains("主演：张三", prompt); Assert.Contains("第7句", prompt); Assert.Contains("ENGLISH ONLY", prompt); Assert.Contains("never invent", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("describe-2", (string)res["promptVersion"]!); Assert.Equal("vision-m", (string)res["models"]!["vision"]!); Assert.Equal(2, (int)res["aiCalls"]!);
    }

    [Fact]
    public async Task Reply_is_normalised_no_villain_english_tags_one_lead_credits_only_actor()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var res = Describe.Normalize(Describe.ParseObject(Main), Input(dir));
        Assert.DoesNotContain("villain", res.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "rebirth", "revenge" }, res["tags"]!.AsArray().Select(t => (string)t!).ToArray().Where(t => t != "plot"));
        var cast = res["cast"]!.AsArray(); Assert.Equal(2, cast.Count); // unknown cluster 9 dropped
        Assert.Equal("female lead", (string)cast[0]!["role"]!); Assert.Equal("supporting", (string)cast[1]!["role"]!); // the second 'female lead' is demoted
        Assert.Equal("张三", (string)cast[0]!["actor"]!); Assert.Null(cast[1]!["actor"]); // 王五 is not in the credits
        var rel = res["relations"]!.AsArray(); Assert.Equal(2, rel.Count); // the self-relation is dropped
        Assert.True((bool)rel[0]!["spoiler"]!); // since 900 > cutoff 120
        Assert.False((bool)rel[1]!["spoiler"]!);
        Assert.Equal("ancient", (string)res["era"]!); Assert.False((bool)res["fantasy"]!);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Spoiler_check_rewrites_flagged_teaser_and_cast_and_marks_flagged_relations()
    {
        var dir = Directory.CreateTempSubdirectory().FullName; var d = Input(dir);
        var res = Describe.Normalize(Describe.ParseObject(Main), d);
        var chat = new FakeChat(
            """{"teaserReveals":true,"reasons":["teaser reveals the sister twist"],"flaggedCast":[1],"flaggedRelations":[1]}""",
            """{"teaser":"A bride returns to her wedding day with a plan.","cast":[{"cluster":1,"roleAtStart":"a guest","description":"A guest at the wedding.","label":"Lu's guest"}]}""",
            """{"teaserReveals":false,"reasons":[],"flaggedCast":[],"flaggedRelations":[]}""");
        var calls = await Describe.SpoilerCheckAsync(chat, "m", res, d, default);
        Assert.Equal(3, calls);
        Assert.Equal("A bride returns to her wedding day with a plan.", (string)res["teaser"]!["en"]!);
        Assert.Equal("a guest", (string)res["cast"]![1]!["roleAtStart"]!); Assert.Equal("Lu's guest", (string)res["cast"]![1]!["label"]!["en"]!);
        Assert.True((bool)res["relations"]![1]!["spoiler"]!);
        Assert.True((bool)res["spoilerCheck"]!["clean"]!);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Without_ai_the_step_is_skipped_not_failed_and_the_dry_request_holds_the_whole_prompt()
    {
        var dir = Directory.CreateTempSubdirectory().FullName; var d = Input(dir);
        Assert.Null(await DescribeJob.RunAsync(null, null, d, default));
        Assert.Null(await DescribeJob.RunAsync(new FakeChat("{}"), new AiConfig(), d, default));
        var dry = JsonDocument.Parse(DescribeJob.Dry(d, null)).RootElement;
        Assert.Equal(2, dry.GetProperty("calls").GetArrayLength());
        var imgs = dry.GetProperty("calls")[0].GetProperty("images"); Assert.Equal(4, imgs.GetArrayLength());
        Assert.Contains("第7句", dry.GetProperty("calls")[0].GetProperty("prompt").GetString());
        Assert.Contains(ProcessStage.AiSteps, s => s == "describe");
        Assert.Contains("describe", ProcessStage.SkippedAiSteps(null));
    }

    [Fact]
    public void Text_track_takes_ocr_first_and_transcript_only_for_gaps()
    {
        var ocr = new List<ShapeCue> { new(10, 13, "屏幕字幕") }; var asr = new List<ShapeCue> { new(10.2, 12.8, "语音一"), new(20, 23, "语音二") };
        var m = TextTrack.Merge(ocr, asr);
        Assert.Contains(m, c => c.Text.Contains("屏幕字幕")); Assert.DoesNotContain(m, c => c.Text.Contains("语音一")); Assert.Contains(m, c => c.Text.Contains("语音二"));
        Assert.Equal(m.OrderBy(c => c.Start).Select(c => c.Start), m.Select(c => c.Start));
        Assert.Contains("语音二", TextTrack.Merge([], asr)[^1].Text);
    }

    [Fact]
    public void Long_tracks_are_compressed_evenly()
    {
        var cues = Enumerable.Range(0, 5000).Select(i => new ShapeCue(i, i + 1, "line number " + i)).ToList();
        var s = TextTrack.Compress(cues, 20000); Assert.True(s.Length <= 21000); Assert.Contains("line number", s); Assert.True(s.Split('\n').Length < 5000);
    }
}

public class PublishTests
{
    static string Fixture()
    {
        var d = Directory.CreateTempSubdirectory().FullName; Directory.CreateDirectory(Path.Combine(d, "cast")); Directory.CreateDirectory(Path.Combine(d, "ocr")); Directory.CreateDirectory(Path.Combine(d, "asr"));
        File.WriteAllText(Path.Combine(d, "processed.json"), """{"v":1,"asrModel":"sensevoice-small"}""");
        File.WriteAllText(Path.Combine(d, "downloaded.json"), """{"durationSeconds":300.0,"embeddable":true}""");
        File.WriteAllText(Path.Combine(d, "info.json"), """{"id":"yt123","title":"原标题 #shorts","channel":"Chan","duration":300}""");
        var times = Enumerable.Range(0, 100).Select(i => i * 3.0 + 0.4).ToList(); // a frame every 3 s
        File.WriteAllText(Path.Combine(d, "frames.json"), JsonSerializer.Serialize(times.Select(t => new { t, kind = "key" })));
        var sscd = new float[100][]; var clip = new float[100][];
        for (int i = 0; i < 100; i++) { sscd[i] = new float[512]; sscd[i][i % 512] = 1; clip[i] = new float[Clip.Dim]; clip[i][i < 40 ? 0 : i < 70 ? 1 : 2] = 1; }
        File.WriteAllBytes(Path.Combine(d, "sscd.f16"), Sscd.ToF16Bytes(sscd)); File.WriteAllBytes(Path.Combine(d, "clip.f16"), Clip.ToF16Bytes(clip));
        File.WriteAllText(Path.Combine(d, "ocr", "subs.zh.vtt"), "WEBVTT\n\n00:00:10.000 --> 00:00:13.000\n你好，世界\n");
        File.WriteAllText(Path.Combine(d, "asr", Transcribe.ModelName + ".zh.vtt"), "WEBVTT\n\n00:01:00.000 --> 00:01:03.000\n后面的话\n");
        File.WriteAllText(Path.Combine(d, "ocr", "subs.en.vtt"), "WEBVTT\n\n00:00:10.000 --> 00:00:13.000\nHello, world\n");
        File.WriteAllBytes(Path.Combine(d, "thumb.jpg"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(d, "cast", "clusters.json"), """{"clusters":[{"id":0,"screenSeconds":100.5,"first":12.0,"masked":false},{"id":1,"screenSeconds":80,"first":30,"masked":true}]}""");
        var jpg = Convert.FromBase64String("/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjEuMTAyAP/bAEMACAQEBAQEBQUFBQUFBgYGBgYGBgYGBgYGBgcHBwgICAcHBwYGBwcICAgICQkJCAgICAkJCgoKDAwLCw4ODhERFP/EAEoAAQAAAAAAAAAAAAAAAAAAAAABAQAAAAAAAAAAAAAAAAAAAAAQAQAAAAAAAAAAAAAAAAAAAAARAQAAAAAAAAAAAAAAAAAAAAD/wAARCAAoACADASIAAhEAAxEA/9oADAMBAAIRAxEAPwAAAAAAAAAAAAD/2Q==");
        File.WriteAllBytes(Path.Combine(d, "cast", "portrait_0.jpg"), jpg); File.WriteAllBytes(Path.Combine(d, "cast", "portrait_1.jpg"), jpg);
        File.WriteAllBytes(Path.Combine(d, "cast", "cand_0_0.jpg"), jpg); File.WriteAllBytes(Path.Combine(d, "cast", "cand_0_2.jpg"), jpg);
        File.WriteAllText(Path.Combine(d, "cast", "cand_0.json"), """[{"k":0,"t":20.5},{"k":2,"t":200.0}]""");
        File.WriteAllText(Path.Combine(d, "describe.json"), """
            {"v":1,"promptVersion":"describe-2","jobId":"j","models":{"text":"t","vision":"v"},"cutoffSeconds":36,"title":{"en":"Reborn","zh":"重生"},"fullTitle":{"en":"Reborn Full"},"teaser":{"en":"A premise."},"synopsis":{"en":"All of it."},
             "tags":["rebirth"],"era":"ancient","fantasy":true,
             "cast":[{"cluster":0,"character":{"zh":"陆","pinyin":"Lù","en":"Lu"},"role":"female lead","roleAtStart":"bride","description":"The bride.","actor":null,"label":{"en":"Lu"}},
                     {"cluster":1,"character":null,"role":"male lead","roleAtStart":"groom","description":"The groom.","actor":null,"label":{"en":"Lu's groom"}}],
             "relations":[{"from":1,"to":0,"type":"spouse","label":{"en":"husband"},"since":20,"until":null,"spoiler":false,"implied":false}],
             "picks":{"0":{"portrait":0,"stills":[0,2]}},"poster":null}
            """);
        return d;
    }

    [Fact]
    public void One_fps_rows_pick_the_nearest_stored_frame_per_second()
    {
        var rows = Publish.OneFps([0.4, 3.4, 6.4, 9.4]);
        Assert.Equal([0, 1, 2, 3], rows); // each stored frame once: frames.json carries the times, a repeated row would only repeat a descriptor
        Assert.Equal([0, 1, 2], Publish.OneFps([0.0, 1.0, 2.0]));
        Assert.Equal([0, 1, 2, 3], Publish.OneFps([0.0, 0.9, 1.6, 3.0]));
    }

    [Fact]
    public void SamePerson_groups_keep_the_cluster_with_most_screen_time_and_ignore_junk()
    {
        var d = new DescribeInput { Clusters = [new(0, 100, 0, false), new(1, 300, 5, false), new(2, 50, 9, false)] };
        var map = Describe.SamePerson(System.Text.Json.Nodes.JsonNode.Parse("[[0,1],[7,2],[2],[1,2]]"), [0, 1, 2], d);
        Assert.Equal(new Dictionary<int, int> { [0] = 1 }, map); // 7 unknown, [2] single, [1,2]: 1 already used
    }

    [Fact]
    public void Poster_two_shot_must_show_both_lead_clusters()
    {
        var c = System.Text.Json.Nodes.JsonNode.Parse("""[{"k":0,"clusters":[2,5],"score":0.9},{"k":1,"clusters":[0,1],"score":0.8},{"k":2,"clusters":[1,0],"score":0.7}]""")!.AsArray();
        Assert.Equal(1, Publish.PickTwoShot(c, 0, 1, null));   // best-scoring candidate with both leads
        Assert.Equal(2, Publish.PickTwoShot(c, 0, 1, 2));      // describe's pick, when it has both leads
        Assert.Equal(1, Publish.PickTwoShot(c, 0, 1, 0));      // describe's pick lacks a lead: ignored
        Assert.Null(Publish.PickTwoShot(c, 0, 7, null));       // no candidate has both: portrait fallback
    }

    [Fact]
    public void Cluster_merge_joins_close_centroids_and_borderline_ones_only_when_the_crops_agree()
    {
        FaceRec F(int cl, float[] e, float[]? v) => new() { Cluster = cl, Emb = e, Vis = v };
        float[] N(float x, float y) { var v = new[] { x, y }; FaceMath.L2(v); return v; }
        // clusters 0 and 1: the same person (centroids 0.01 apart); 2: borderline (0.3 away) with agreeing crops; 3: borderline with different crops; 4: far
        var faces = new List<FaceRec>();
        void Add(int c, float[] e, float[] v) { for (int i = 0; i < 4; i++) faces.Add(F(c, e, v)); }
        Add(0, N(1, 0), N(1, 0)); Add(1, N(1, 0.02f), N(1, 0)); Add(2, N(1, 0.45f), N(1, 0.01f)); Add(3, N(1, 1f), N(0, 1)); Add(4, N(0, 1), N(1, -1));
        var m = CastLogic.MergeClusters(faces, 0.05, 0.4, 0.93);
        Assert.Equal(3, faces.Select(f => f.Cluster).Distinct().Count()); // {0,1,2}, {3}, {4}
        Assert.All(faces.Take(12), f => Assert.Equal(faces[0].Cluster, f.Cluster));
        Assert.NotEqual(faces[0].Cluster, faces[12].Cluster);
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public void Shot_rows_are_the_frame_nearest_each_shots_middle()
    {
        var rows = new float[][] { [1, 0], [1, 0], [1, 0], [0, 1], [0, 1] }; var times = new[] { 0.0, 1, 2, 10, 12 };
        var pick = Publish.ShotMiddles(rows, times);
        Assert.Equal(2, pick.Count); Assert.Equal(1, pick[0]); Assert.Contains(pick[1], new[] { 3, 4 });
    }

    [Fact]
    public async Task Pack_layout_matches_PACK_md_and_has_no_villain_and_is_incremental()
    {
        var job = Fixture(); var outDir = Directory.CreateTempSubdirectory().FullName; var tools = new Tools(new HttpClient(), new NodeConfig());
        var r = await Publish.PublishOneAsync(tools, new LocalJob(job, "j1"), outDir, false, default);
        Assert.True(r.Written, r.Skipped);
        var p = Path.Combine(outDir, "j1");
        foreach (var f in new[] { "drama.json", "frames.json", "sscd.f16", "clip.f16", "clipFrames.json", "subs.zh.vtt", "subs.en.vtt", "thumb.jpg", "cast/c0.jpg", "cast/c1.jpg", "cast/c0_s1.jpg", "cast/c0_s2.jpg" }) Assert.True(File.Exists(Path.Combine(p, f)), f);
        Assert.False(File.Exists(Path.Combine(p, "video.mp4"))); Assert.False(Directory.Exists(Path.Combine(p, "frames")));
        // sscd: 100 stored frames ~3 s apart -> one row per stored frame (no duplicated rows to fill every second)
        var frames = JsonDocument.Parse(File.ReadAllText(Path.Combine(p, "frames.json"))).RootElement.GetArrayLength();
        Assert.Equal(frames, new FileInfo(Path.Combine(p, "sscd.f16")).Length / (512 * 2)); Assert.Equal(100, frames);
        // clip: 3 shots, one row each
        Assert.Equal(3, JsonDocument.Parse(File.ReadAllText(Path.Combine(p, "clipFrames.json"))).RootElement.GetArrayLength());
        Assert.Equal(3, new FileInfo(Path.Combine(p, "clip.f16")).Length / (Clip.Dim * 2));
        var drama = File.ReadAllText(Path.Combine(p, "drama.json")); Assert.DoesNotContain("villain", drama, StringComparison.OrdinalIgnoreCase);
        var j = JsonNode.Parse(drama)!;
        Assert.Equal("yt123", (string)j["youtubeId"]!); Assert.True((bool)j["embeddable"]!); Assert.Equal("Chan", (string)j["channel"]!); Assert.Equal(300, (double)j["durationSeconds"]!);
        Assert.Equal("原标题 #shorts", (string)j["originalTitle"]!); Assert.Equal("ancient", (string)j["era"]!); Assert.True((bool)j["fantasy"]!);
        Assert.Equal("ocr", (string)j["sources"]!["text"]!); Assert.Equal("c0", (string)j["cast"]![0]!["id"]!); Assert.Equal(100.5, (double)j["cast"]![0]!["screenSeconds"]!);
        var st = j["cast"]![0]!["stills"]!.AsArray(); Assert.Equal(2, st.Count);
        Assert.False((bool)st[0]!["spoiler"]!); Assert.True((bool)st[1]!["spoiler"]!); // t 200 > cutoff 36
        Assert.Equal("c1", (string)j["relations"]![0]!["from"]!); Assert.Null(j["teaser"]!["zh"]);
        var zh = File.ReadAllText(Path.Combine(p, "subs.zh.vtt")); Assert.Contains("你好", zh); Assert.Contains("后面的话", zh);
        Assert.Contains("Hello", File.ReadAllText(Path.Combine(p, "subs.en.vtt")));
        // incremental: same inputs skip, changed describe.json rewrites
        Assert.Equal("up to date", (await Publish.PublishOneAsync(tools, new LocalJob(job, "j1"), outDir, false, default)).Skipped);
        File.WriteAllText(Path.Combine(job, "describe.json"), File.ReadAllText(Path.Combine(job, "describe.json")).Replace("A premise.", "A new premise."));
        Assert.True((await Publish.PublishOneAsync(tools, new LocalJob(job, "j1"), outDir, false, default)).Written);
        Publish.WriteIndex(outDir); Assert.Equal("j1", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "packs.json")))!["packs"]![0]!["packId"]!);
    }

    [Fact]
    public async Task A_job_without_describe_json_is_not_published()
    {
        var job = Fixture(); File.Delete(Path.Combine(job, "describe.json"));
        var r = await Publish.PublishOneAsync(new Tools(new HttpClient(), new NodeConfig()), new LocalJob(job, "j"), Directory.CreateTempSubdirectory().FullName, false, default);
        Assert.False(r.Written); Assert.Equal("no describe.json", r.Skipped);
    }
}

public class MedusaSetupTests
{
    const string Sas = "https://acct.blob.core.windows.net/c?sv=2023-01-01&sig=SECRETSIG%2Babc123&se=2030-01-01";

    [Fact]
    public void Link_round_trips_to_the_settings_json()
    {
        var o = new MedusaSetup.Options(Sas, "phone-1", "wifi", 12, 50, "0 2 * * *", "3h", 360);
        var link = MedusaSetup.Link(MedusaSetup.Build(o));
        Assert.StartsWith("medusa://node-setup?d=", link); Assert.DoesNotContain("=", link[22..]); Assert.DoesNotContain("+", link);
        var j = MedusaSetup.Parse(link);
        Assert.Equal(1, (int)j["v"]!); Assert.Equal("medusa-node-settings", (string)j["kind"]!); Assert.Equal(Sas, (string)j["sas"]!); Assert.Equal("phone-1", (string)j["nodeName"]!);
        Assert.Equal("wifi", (string)j["conditions"]!); Assert.Equal(12, (int)j["dailyCap"]!); Assert.Equal(50, (int)j["budget"]!); Assert.Equal("0 2 * * *", (string)j["schedule"]!); Assert.Equal("3h", (string)j["window"]!); Assert.Equal(360, (int)j["maxHeight"]!);
        var d = MedusaSetup.Parse(MedusaSetup.Link(MedusaSetup.Build(new MedusaSetup.Options(Sas, "n"))));
        Assert.Equal("wifi+charging", (string)d["conditions"]!); Assert.Equal(20, (int)d["dailyCap"]!); Assert.Null(d["budget"]); Assert.Equal(480, (int)d["maxHeight"]!);
        Assert.Throws<ArgumentException>(() => MedusaSetup.Build(new MedusaSetup.Options(Sas, "n", "always")));
        // the https hand-off link carries the same payload and round-trips too
        var web = MedusaSetup.WebLink(MedusaSetup.Build(o)); Assert.StartsWith("https://lancec-codebox.github.io/appbox/setup/#d=", web);
        Assert.Equal(Sas, (string)MedusaSetup.Parse(web)["sas"]!); Assert.Equal(link[22..], web.Substring(web.IndexOf("#d=") + 3));
    }

    [Fact]
    public void The_sas_never_appears_in_terminal_output_only_in_the_png_and_file_and_the_warning_is_shown()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory().FullName, "setup"); var file = Path.Combine(dir, "n.medusa-node.json");
        var text = MedusaSetup.Run(new MedusaSetup.Options(Sas, "phone"), true, file, dir, false);
        Assert.DoesNotContain("SECRETSIG", text); Assert.DoesNotContain("blob.core.windows.net", text); Assert.DoesNotContain("sv=2023", text);
        Assert.Contains("contains your storage key; delete after use", text); Assert.Contains("█", text);
        var png = File.ReadAllBytes(Path.Combine(dir, "medusa-node-setup.png")); Assert.Equal(0x89, png[0]); Assert.Equal((byte)'P', png[1]);
        Assert.Contains("SECRETSIG", File.ReadAllText(file));
        if (!OperatingSystem.IsWindows()) { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file)); Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "medusa-node-setup.png"))); }
    }

    [Fact]
    public void A_name_is_required_when_the_sas_is_included()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Assert.Throws<ArgumentException>(() => MedusaSetup.Run(new MedusaSetup.Options(Sas, null), true, null, dir, false));
        Assert.Throws<ArgumentException>(() => MedusaSetup.Run(new MedusaSetup.Options(Sas, " "), true, null, dir, false));
        MedusaSetup.Run(new MedusaSetup.Options(null, null), false, Path.Combine(dir, "x.medusa-node.json"), dir, true); // --no-sas needs no name
    }

    [Fact]
    public void No_sas_shares_settings_without_the_secret_and_needs_no_warning()
    {
        var dir = Directory.CreateTempSubdirectory().FullName; var file = Path.Combine(dir, "a.medusa-node.json");
        var text = MedusaSetup.Run(new MedusaSetup.Options(Sas, "phone"), false, file, dir, true);
        Assert.DoesNotContain("storage key", text); Assert.Contains("link: https://lancec-codebox.github.io/appbox/setup/#d=", text); Assert.DoesNotContain("SECRETSIG", File.ReadAllText(file)); Assert.Null(JsonNode.Parse(File.ReadAllText(file))!["sas"]);
        Assert.Throws<InvalidOperationException>(() => MedusaSetup.Run(new MedusaSetup.Options(null, "p"), true, null, dir, false));
    }

    [Fact]
    public void A_link_with_a_realistic_sas_fits_a_qr_at_level_m()
    {
        var long_ = Sas + "&sp=racwdl&spr=https&sr=c&sip=0.0.0.0-255.255.255.255&" + new string('x', 150);
        var link = MedusaSetup.Link(MedusaSetup.Build(new MedusaSetup.Options(long_, "phone")));
        Assert.True(link.Length > 450); Assert.NotEmpty(MedusaSetup.QrPng(link));
    }
}

public class StatusSnapshotTests
{
    [Fact]
    public async Task Snapshot_reports_control_nodes_queue_leases_finished_failures_and_throughput_from_reads_only()
    {
        var store = new FakeStore(); var paths = new Paths("index/"); var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        string Ago(int min) => now.AddMinutes(-min).ToString("yyyy-MM-ddTHH:mm:ssZ");
        await store.AddJob(paths, "process", "a", Ago(300)); await store.AddJob(paths, "process", "b", Ago(300)); await store.AddJob(paths, "download", "c", Ago(300)); await store.AddJob(paths, "download", "d", Ago(300));
        foreach (var (id, stage, state) in new[] { ("a", "process", "done"), ("b", "process", "failed"), ("c", "download", "done") }) store.Blobs[paths.Job(stage, id)].Tags[Tags.State] = state;
        var ja = store.Job(paths.Job("process", "a")); ja.FinishedAt = Ago(10); ja.NodeId = "n1"; ja.ElapsedSeconds = 42; store.Blobs[paths.Job("process", "a")].Data = ja.ToBytes();
        var jc = store.Job(paths.Job("download", "c")); jc.FinishedAt = Ago(600); jc.NodeId = "n1"; jc.ElapsedSeconds = 5; store.Blobs[paths.Job("download", "c")].Data = jc.ToBytes();
        var jb = store.Job(paths.Job("process", "b")); jb.LastError = "boom https://x.blob.core.windows.net/c?sv=1&sig=TOPSECRET&se=2"; jb.Attempts = 3; jb.Permanent = true; jb.NodeId = "n2"; store.Blobs[paths.Job("process", "b")].Data = jb.ToBytes();
        store.Blobs[paths.Job("download", "d")].Lease = "L"; store.Blobs[paths.Job("download", "d")].Meta = new() { ["node"] = "n1", ["progress"] = "download 40%", ["heartbeat"] = Ago(1) };
        store.Blobs[paths.Node("n1")] = new FakeStore.Blob { Data = new NodeInfo { NodeId = "n1", Name = "desk", Kind = "desktop", Stages = ["download", "process"], LastSeen = Ago(0), State = "working", Running = [new RunningJob { JobId = "d", Stage = "download", Step = "download", Progress = "40%", Worker = 1 }] }.ToBytes() };
        store.Blobs[paths.Control] = new FakeStore.Blob { Data = new ControlFile { Mode = "run", Nodes = new() { ["n1"] = new NodeLimits { Budget = 5 } } }.ToBytes() };
        var before = store.Puts.Values.Sum();
        var s = await StatusSnapshot.BuildAsync(store, paths, now, default);
        Assert.Equal(before, store.Puts.Values.Sum()); // read only
        Assert.Equal("run", (string)s["control"]!["mode"]!);
        var n = s["nodes"]![0]!; Assert.Equal("desk", (string)n["name"]!); Assert.Equal("working", (string)n["state"]!); Assert.Equal("d", (string)n["workers"]![0]!["jobId"]!);
        Assert.Equal(1, (int)s["queue"]!["process"]!["done"]!); Assert.Equal(1, (int)s["queue"]!["process"]!["failed"]!); Assert.Equal(1, (int)s["queue"]!["download"]!["leased"]!);
        Assert.Equal(1, (int)s["leased"]!.AsArray().Count); Assert.Equal(60, (double)s["leased"]![0]!["heartbeatAgeSeconds"]!);
        Assert.Equal(2, s["finished"]!.AsArray().Count); Assert.Equal("a", (string)s["finished"]![0]!["jobId"]!); Assert.Equal(42, (double)s["finished"]![0]!["elapsedSeconds"]!);
        var f = s["failures"]![0]!; Assert.Equal(3, (int)f["attempts"]!); Assert.True((bool)f["permanent"]!); Assert.DoesNotContain("TOPSECRET", f.ToJsonString());
        Assert.Equal(1, (int)s["throughput"]!["process"]!["lastHour"]!); Assert.Equal(1, (int)s["throughput"]!["download"]!["last24h"]!); Assert.Equal(0, (int)s["throughput"]!["download"]!["lastHour"]!);
        Assert.Equal(3, (int)n["budgetRemaining"]!); // 5 - 2 finished by n1
        Assert.Equal(1, (double)n["finishedPerHour"]!); Assert.Equal(3.0, (double)n["etaBudgetHours"]!);
    }

    [Fact]
    public void Atomic_write_replaces_the_file_and_leaves_no_temp()
    {
        var f = Path.Combine(Directory.CreateTempSubdirectory().FullName, "s", "status.json");
        StatusSnapshot.WriteAtomic(f, "{\"a\":1}"); StatusSnapshot.WriteAtomic(f, "{\"a\":2}");
        Assert.Equal("{\"a\":2}", File.ReadAllText(f)); Assert.False(File.Exists(f + ".tmp"));
        Assert.Equal(TimeSpan.FromSeconds(60), StatusSnapshot.ParseEvery("60s")); Assert.Equal(TimeSpan.FromMinutes(5), StatusSnapshot.ParseEvery("5m")); Assert.Equal(TimeSpan.FromSeconds(60), StatusSnapshot.ParseEvery(null));
    }
}
