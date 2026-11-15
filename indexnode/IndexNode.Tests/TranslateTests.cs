using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IndexNode;
using Xunit;

namespace IndexNode.Tests;

public class TranslateTests
{
    /// <summary>A fake model: the glossary call answers a fixed list; a translation call answers "EN&lt;id&gt;" for each TRANSLATE line (optionally dropping or breaking some on the first ask).</summary>
    private sealed class Fake : IChat
    {
        public readonly ConcurrentBag<string> Prompts = [];
        public int GlossaryCalls, BatchCalls; public int[] Asked = [];
        public Func<int, int, string?>? Override; // (id, attempt) -> text, null: default
        public ConcurrentDictionary<int, int> Attempts = new();
        public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> JsonAsync(string model, JsonNode content, string tool, CancellationToken ct)
        {
            var p = content.GetValue<string>(); Prompts.Add(p);
            if (tool == "submit_glossary")
            {
                Interlocked.Increment(ref GlossaryCalls);
                return Task.FromResult(JsonNode.Parse("""{"plot":"Huo Zhan secretly is the prince; his wife is the bride.","entries":[{"zh":"霍湛","en":"Wrong Name","kind":"character"},{"zh":"嫂嫂","en":"Sister-in-law","kind":"address","refers":"Su Wan"},{"zh":"昭姐姐","en":"Sister Zhao","kind":"address","refers":"Gu Zhao"},{"zh":"侯府","en":"Marquis Manor","kind":"place"}]}""")!.AsObject());
            }
            Interlocked.Increment(ref BatchCalls);
            var arr = new JsonArray();
            foreach (Match m in Regex.Matches(p, @"^TRANSLATE \[(\d+)\] ", RegexOptions.Multiline))
            {
                int id = int.Parse(m.Groups[1].Value); int at = Attempts.AddOrUpdate(id, 1, (_, v) => v + 1);
                var text = Override?.Invoke(id, at) ?? "Line " + id;
                if (text == "<drop>") continue;
                if (text == "<skip>") { arr.Add(new JsonObject { ["i"] = id, ["en"] = "", ["skip"] = true }); continue; }
                arr.Add(new JsonObject { ["i"] = id, ["en"] = text });
            }
            return Task.FromResult(new JsonObject { ["cues"] = arr });
        }
    }

    static AiConfig Ai => new() { BaseUrl = "http://x", TextModel = "m" };

    static List<ShapeCue> Zh(int n, double step = 3.0, string text = "你好，世界")
        => Enumerable.Range(0, n).Select(i => new ShapeCue(i * step, i * step + 2.0, text + i)).ToList();

    static JsonObject Describe() => JsonNode.Parse("""
        {"title":{"en":"Marquis Bride"},"synopsis":{"en":"A bride and a marquis."},
         "cast":[{"cluster":0,"character":{"zh":"霍湛","pinyin":"Huo Zhan","en":"Huo Zhan"},"label":{"en":"Huo Zhan, the marquis"},"roleAtStart":"male lead"},{"cluster":1,"character":{"zh":"苏婉","pinyin":"Su Wan","en":"Su Wan"},"label":{"en":"Su Wan, the bride"},"roleAtStart":"female lead"}],
         "relations":[{"from":0,"to":1,"type":"spouse","label":{"en":"secretly the prince's wife"},"spoiler":true}]}
        """)!.AsObject();

    static TranslateInput Input(int n, JsonObject? describe = null) => new() { JobId = "j", Ocr = Zh(n), Asr = [new ShapeCue(0, n * 3.0, "说话")], Describe = describe };

    static async Task<(TranslateResult R, Fake F)> Run(TranslateInput i, Fake? f = null, byte[]? existing = null, bool force = false)
    {
        f ??= new Fake();
        var r = await Translate.RunAsync(f, Ai, i, existing, force, _ => { }, default);
        return (r, f);
    }

    [Fact]
    public async Task Glossary_names_appear_in_every_batch_and_describe_spelling_wins()
    {
        var (r, f) = await Run(Input(450, Describe()));
        Assert.Equal(3, r.Batches); // 450 cues -> 200 + 200 + 50
        Assert.Equal(1, f.GlossaryCalls); Assert.Equal(3, f.BatchCalls); Assert.Equal(4, r.Calls);
        var batch = f.Prompts.Where(p => p.Contains("GLOSSARY:")).ToList();
        Assert.Equal(3, batch.Count);
        foreach (var p in batch) { Assert.Contains("霍湛 => Huo Zhan", p); Assert.DoesNotContain("Wrong Name", p); Assert.Contains("嫂嫂 => Sister-in-law", p); Assert.Contains("A bride and a marquis.", p); Assert.Contains("[REVEAL]", p); Assert.DoesNotContain("Plot of the whole drama", p); }
        Assert.DoesNotContain("Wrong Name", System.Text.Encoding.UTF8.GetString(r.Json!));
        // the glossary prompt carries describe's English names and roles
        var gp = f.Prompts.Single(p => p.Contains("preparing the name glossary"));
        Assert.Contains("Huo Zhan", gp); Assert.Contains("male lead", gp);
    }

    [Fact]
    public async Task Batches_carry_context_only_neighbours()
    {
        var (_, f) = await Run(Input(450));
        var second = f.Prompts.Single(p => p.Contains("TRANSLATE [200] "));
        Assert.Contains("CONTEXT [190] ", second); Assert.DoesNotContain("CONTEXT [189] ", second); Assert.Contains("TRANSLATE [399] ", second);
        Assert.Contains("CONTEXT [400] ", second); Assert.Contains("CONTEXT [409] ", second); Assert.DoesNotContain("[410] ", second);
    }

    [Fact]
    public async Task A_boundary_cue_sees_the_next_batch_as_context_for_at_least_30_s_or_10_cues()
    {
        // dense cues: 1 s apart, so 30 s is 30 cues (more than 10)
        var i = new TranslateInput { JobId = "j", Ocr = Zh(450, 1.0), Asr = [new ShapeCue(0, 450, "说话")] };
        var (r, f) = await Run(i);
        var first = f.Prompts.Single(p => p.Contains("TRANSLATE [0] "));
        Assert.Contains("TRANSLATE [199] ", first); Assert.DoesNotContain("TRANSLATE [200] ", first);
        Assert.Contains("CONTEXT [200] ", first); Assert.Contains("CONTEXT [229] ", first); Assert.DoesNotContain("[231] ", first);
        var second = f.Prompts.Single(p => p.Contains("TRANSLATE [200] "));
        Assert.Contains("CONTEXT [199] ", second); Assert.Contains("CONTEXT [170] ", second); Assert.Contains("CONTEXT [400] ", second); Assert.Contains("CONTEXT [429] ", second);
        // each cue is translated in exactly one batch
        for (int k = 0; k < 450; k++) Assert.Equal(1, f.Prompts.Count(p => p.Contains($"TRANSLATE [{k}] ")));
        // sparse cues (10 s apart): ten cues reach further than 30 s
        var sparse = new TranslateInput { JobId = "j", Ocr = Zh(450, 10.0), Asr = Zh(450, 10.0) };
        var (_, f2) = await Run(sparse);
        var s1 = f2.Prompts.Single(p => p.Contains("TRANSLATE [0] "));
        Assert.Contains("CONTEXT [209] ", s1); Assert.DoesNotContain("[210] ", s1);
    }

    [Fact]
    public async Task The_sidecar_keeps_every_cue_pair_before_shaping()
    {
        var f = new Fake { Override = (id, _) => id == 1 ? "A very long line that will be split into several cues when it is shaped for the screen, yes" : null };
        var (r, _) = await Run(Input(4), f);
        var cues = JsonNode.Parse(r.Json!)!["cues"]!.AsArray();
        Assert.Equal(4, cues.Count);
        Assert.Equal(3.0, cues[1]!["start"]!.GetValue<double>()); Assert.Equal(5.0, cues[1]!["end"]!.GetValue<double>());
        Assert.Equal("你好，世界1", cues[1]!["zh"]!.GetValue<string>());
        Assert.StartsWith("A very long line", cues[1]!["en"]!.GetValue<string>()); // before shaping: not split
        Assert.Equal(Translate.CleanHash("你好世界1"), cues[1]!["hash"]!.GetValue<string>()); // punctuation does not change it
        Assert.Equal(16, cues[0]!["hash"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task Every_cue_is_answered_and_a_missing_id_is_asked_again()
    {
        var f = new Fake { Override = (id, at) => id == 7 && at == 1 ? "<drop>" : id == 9 && at == 1 ? "" : null };
        var (r, _) = await Run(Input(30), f);
        Assert.Equal(1 + 2, r.Calls); // glossary + batch + one re-ask
        var vtt = System.Text.Encoding.UTF8.GetString(r.Vtt!);
        for (int i = 0; i < 30; i++) Assert.Contains("Line " + i, vtt);
        // the re-ask names only the two bad ids
        var re = f.Prompts.Where(p => p.Contains("TRANSLATE [7] ") && !p.Contains("TRANSLATE [0] ")).ToList();
        Assert.Single(re); Assert.Contains("TRANSLATE [9] ", re[0]); Assert.DoesNotContain("TRANSLATE [8] ", re[0]);
    }

    [Fact]
    public async Task Step_fails_after_two_reasks()
    {
        var f = new Fake { Override = (id, _) => id == 3 ? "<drop>" : null };
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Input(10), f));
        Assert.Contains("1 of 10 cue(s) unresolved", e.Message); // 10% unresolved, over the 2% allowance
        Assert.Equal(1 + 3, f.GlossaryCalls + f.BatchCalls); // glossary + first ask + 2 re-asks
    }

    static readonly string LongZh = "他走进了那间昏暗的屋子桌上放着一封信，他犹豫了很久才伸手去拿"; // 30 characters

    [Fact]
    public async Task One_unusable_cue_never_fails_the_drama_the_last_answer_is_kept_and_counted()
    {
        // -8ZsrYNsVlk: "batch 9/27 still has 1 cue(s) missing or unusable after 3 calls: far too short"
        var f = new Fake { Override = (id, _) => id == 5 ? "Hi" : id == 6 ? "<drop>" : "A long enough line number " + id };
        var i = new TranslateInput { JobId = "j", Ocr = Zh(200, 3.0, LongZh), Asr = [new ShapeCue(0, 600, "说话")] };
        var (r, _) = await Run(i, f); // 2 of 200 = 1%: under the allowance
        var j = JsonNode.Parse(r.Json!)!;
        Assert.Equal(2, j["unresolved"]!.GetValue<int>());
        var u = j["unresolvedCues"]!.AsArray();
        Assert.Equal(5, u[0]!["i"]!.GetValue<int>()); Assert.True(u[0]!["kept"]!.GetValue<bool>());
        Assert.Equal(6, u[1]!["i"]!.GetValue<int>()); Assert.False(u[1]!["kept"]!.GetValue<bool>());
        Assert.Equal("Hi", j["cues"]![5]!["en"]!.GetValue<string>()); Assert.Equal("", j["cues"]![6]!["en"]!.GetValue<string>());
        Assert.Equal(1, j["dropped"]!.GetValue<int>());
    }

    [Fact]
    public void More_than_two_percent_unresolved_fails()
    {
        Assert.False(Translate.TooManyUnresolved(2, 100)); Assert.True(Translate.TooManyUnresolved(3, 100)); Assert.False(Translate.TooManyUnresolved(0, 5)); Assert.True(Translate.TooManyUnresolved(1, 5));
    }

    [Fact]
    public async Task A_batch_that_comes_back_wholly_unusable_is_split_in_halves_then_quarters()
    {
        // -h3uSj7Jdmk / 14Y_adwhw1A: a whole batch of 200 came back unusable after 3 calls
        var f = new Fake { Override = (_, at) => at <= 3 ? "<drop>" : null };
        var (r, _) = await Run(Input(40), f);
        Assert.Equal(1 + 3 + 2, r.Calls); // glossary + 3 whole-batch asks + the two halves
        var vtt = System.Text.Encoding.UTF8.GetString(r.Vtt!);
        for (int k = 0; k < 40; k++) Assert.Contains("Line " + k, vtt);
        Assert.Equal(0, JsonNode.Parse(r.Json!)!["unresolved"]!.GetValue<int>());
        // the second half still carries the first half as context
        Assert.Contains(f.Prompts, p => p.Contains("TRANSLATE [20] ") && p.Contains("CONTEXT [19] ") && !p.Contains("TRANSLATE [19] "));
    }

    [Fact]
    public async Task A_whole_batch_that_never_answers_fails_after_splitting_with_the_reason()
    {
        var f = new Fake { Override = (_, _) => "<drop>" };
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Input(8), f));
        Assert.Contains("unresolved", e.Message);
    }

    [Fact]
    public async Task Output_keeps_cue_times_and_is_shaped()
    {
        var f = new Fake { Override = (id, _) => id == 2 ? "This is a very long translated sentence that certainly does not fit in two short lines of forty two characters at all, so it has to be split by words" : null };
        var i5 = Input(5); i5.Ocr = Zh(5, text: "这是一句很长的话需要拆开来翻译成英文字幕");
        var (r, _) = await Run(i5, f);
        var cues = CueShaper.ParseVtt(System.Text.Encoding.UTF8.GetString(r.Vtt!));
        Assert.True(cues.Count > 5); // the long one was split
        Assert.Equal(0, r.Stats!.Over6s); Assert.Equal(0, r.Stats.OverChars); Assert.Equal(0, r.Stats.Overlaps);
        var again = CueShaper.Stats(cues); Assert.Equal(0, again.Overlaps); Assert.Equal(0, again.OverChars);
        // cue 0 and 4 start exactly where the Chinese ones did; the split pieces of cue 2 stay within its time (6 s .. 9 s, hold up to the next start)
        Assert.Equal(0.0, cues[0].Start, 3); Assert.Equal(12.0, cues.Single(c => c.Text.StartsWith("Line 4")).Start, 3);
        var pieces = cues.Where(c => c.Start >= 6 && c.Start < 9).ToList(); Assert.True(pieces.Count >= 2); Assert.True(pieces[^1].End <= 9.0 + 1e-6);
        Assert.Contains("\"over6s\": 0", System.Text.Encoding.UTF8.GetString(r.Json!));
    }

    [Fact]
    public void Source_choice_by_coverage()
    {
        // speech 0-100 s; OCR covers 0-80 (80%) -> OCR
        var asr = new[] { new ShapeCue(0, 100, "说") };
        var good = Enumerable.Range(0, 40).Select(i => new ShapeCue(i * 2, i * 2 + 2, "字幕" + i)).ToList();
        var a = Translate.ChooseSource(good, asr, 0);
        Assert.Equal("ocr", a.Track); Assert.InRange(a.Coverage!.Value, 0.79, 0.85);
        // OCR covers 0-30 -> transcript
        var b = Translate.ChooseSource(good.Take(15).ToList(), asr, 0);
        Assert.Equal("asr", b.Track); Assert.InRange(b.Coverage!.Value, 0.29, 0.35); Assert.Equal(asr[0].Text, b.Cues[0].Text);
        // no OCR at all -> transcript; no transcript -> OCR
        Assert.Equal("asr", Translate.ChooseSource([], asr, 0).Track);
        Assert.Equal("ocr", Translate.ChooseSource(good, [], 0).Track);
        Assert.Equal("none", Translate.ChooseSource([], [], 0).Track);
        // the sync offset moves the OCR on the speech clock: shifted 60 s past the end it covers nothing
        Assert.Equal("asr", Translate.ChooseSource(good, asr, 100).Track);
        // the source keeps its own cue times and the hash follows the text
        Assert.Equal(good[3].Start, a.Cues[3].Start);
        Assert.NotEqual(a.Hash, Translate.ChooseSource(good.Select(c => c with { Text = c.Text + "!" }).ToList(), asr, 0).Hash);
    }

    [Fact]
    public async Task Source_is_the_chinese_never_existing_english()
    {
        var f = new Fake(); var i = Input(5);
        await Run(i, f);
        Assert.All(f.Prompts, p => Assert.DoesNotContain("Hello, world", p));
        Assert.Contains(f.Prompts, p => p.Contains("你好，世界0"));
    }

    [Fact]
    public async Task Garbled_ocr_marked_skip_is_dropped_and_counted_and_nothing_is_invented()
    {
        var f = new Fake { Override = (id, _) => id == 2 ? "<skip>" : null };
        var i = Input(6); i.Ocr = i.Ocr.Select((c, k) => k == 2 ? c with { Text = "未" } : c).ToList();
        var (r, _) = await Run(i, f);
        var vtt = System.Text.Encoding.UTF8.GetString(r.Vtt!);
        Assert.DoesNotContain("Line 2", vtt); Assert.Contains("Line 1", vtt); Assert.Contains("Line 3", vtt);
        var j = JsonNode.Parse(r.Json!)!; Assert.Equal(1, j["dropped"]!.GetValue<int>()); Assert.Equal("未", j["droppedCues"]![0]!["zh"]!.GetValue<string>());
        Assert.Equal(6, j["cues"]!.AsArray().Count); Assert.Equal("", j["cues"]![2]!["en"]!.GetValue<string>());
        Assert.Equal(2, r.Calls); // a skip is an answer, not a re-ask
        var sr = j["shortReadBy"]!; Assert.Equal(j["shape"]!["shortRead"]!.GetValue<int>(), sr["upTo0_5s"]!.GetValue<int>() + sr["from0_5To1s"]!.GetValue<int>() + sr["over1s"]!.GetValue<int>());
    }

    [Fact]
    public async Task Address_forms_are_kept_apart_from_names_in_the_glossary_and_the_prompts()
    {
        var (r, f) = await Run(Input(10, Describe()));
        var gp = f.Prompts.Single(p => p.Contains("preparing the name glossary"));
        Assert.Contains("Never put a form of address", gp); Assert.Contains("\"refers\"", gp);
        foreach (var p in f.Prompts.Where(p => p.Contains("GLOSSARY:")))
        {
            Assert.Contains("昭姐姐 => Sister Zhao [address form; refers to Gu Zhao, context only]", p);
            Assert.Contains("NEVER swap in the person's full name", p); Assert.Contains("Interjections and laughs are speech", p); Assert.Contains("TRANSLATE it whenever its meaning is evident", p); Assert.Contains("\"skip\":true", p);
        }
        var e = JsonNode.Parse(r.Json!)!["glossary"]!.AsArray().Single(x => x!["zh"]!.GetValue<string>() == "昭姐姐")!;
        Assert.Equal("address", e["kind"]!.GetValue<string>()); Assert.Equal("Gu Zhao", e["refers"]!.GetValue<string>());
    }

    [Fact]
    public async Task Redo_only_when_source_or_glossary_seed_changes()
    {
        var (r1, _) = await Run(Input(20, Describe()));
        Assert.False(r1.Skipped);
        // same source, same describe -> skipped, no AI call
        var f = new Fake();
        var (r2, _) = await Run(Input(20, Describe()), f, r1.Json);
        Assert.True(r2.Skipped); Assert.Equal(0, f.GlossaryCalls + f.BatchCalls); Assert.Equal(0, r2.Calls);
        // --steps translate (force) redoes it
        var (r3, f3) = await Run(Input(20, Describe()), null, r1.Json, force: true);
        Assert.False(r3.Skipped); Assert.Equal(1, f3.GlossaryCalls);
        // changed source text -> redone
        var changed = Input(20, Describe()); changed.Ocr = changed.Ocr.Select((c, k) => k == 5 ? c with { Text = "完全不同" } : c).ToList();
        Assert.False((await Run(changed, null, r1.Json)).R.Skipped);
        // changed describe names -> redone
        var d2 = Describe(); d2["cast"]![0]!["character"]!["en"] = "Zhan Huo";
        Assert.False((await Run(Input(20, d2), null, r1.Json)).R.Skipped);
        // garbage sidecar -> redone
        Assert.False((await Run(Input(20), null, "nope"u8.ToArray())).R.Skipped);
    }

    [Fact]
    public async Task Sidecar_records_what_was_used_and_no_describe_still_works()
    {
        var (r, _) = await Run(Input(10));
        var j = JsonNode.Parse(r.Json!)!;
        Assert.Equal("ocr", j["sourceTrack"]!.GetValue<string>()); Assert.Equal("m", j["model"]!.GetValue<string>());
        Assert.NotNull(j["glossaryHash"]); Assert.NotNull(j["sourceHash"]); Assert.Equal(1, j["batches"]!.GetValue<int>()); Assert.Equal(2, j["aiCalls"]!.GetValue<int>());
        Assert.False(j["describeUsed"]!.GetValue<bool>()); Assert.Equal(10, j["shape"]!["cues"]!.GetValue<int>());
        Assert.Equal(4, j["glossary"]!.AsArray().Count);
    }

    [Fact]
    public void Accept_rules()
    {
        Assert.Equal("", Translate.Accept("（音乐）", "", false, out _));
        Assert.Null(Translate.Accept("你好吗", "", false, out var why)); Assert.StartsWith("empty", why);
        Assert.Equal("", Translate.Accept("未", "", true, out _)); // marked skip: garbled OCR is dropped, not invented
        Assert.Equal("Hello there", Translate.Accept("你好", "Hello [softly] there", false, out _));
        Assert.Null(Translate.Accept("你好", "你好", false, out _));
    }

    [Fact]
    public async Task Gather_reads_the_job_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tr-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "ocr")); Directory.CreateDirectory(Path.Combine(dir, "asr"));
            File.WriteAllText(Path.Combine(dir, "ocr", "subs.zh-Hans.vtt"), "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n你好\n");
            File.WriteAllText(Path.Combine(dir, "ocr", "subs.en.vtt"), "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nHello\n");
            File.WriteAllText(Path.Combine(dir, "asr", "sensevoice.zh.json"), """{"segments":[{"start":1,"end":3,"text":"你好"}]}""");
            File.WriteAllText(Path.Combine(dir, "sync.json"), """{"offset":0.4}""");
            var i = await Translate.GatherAsync(new LocalJob(dir, "j"), default);
            Assert.Single(i.Ocr); Assert.Equal("你好", i.Ocr[0].Text); Assert.Single(i.Asr); Assert.Equal(0.4, i.SyncOffset); Assert.Null(i.Describe);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_model_object_with_repeated_or_junk_keys_still_parses_first_wins()
    {
        // 1_ZMLz6R9RQ: "An item with the same key has already been added. Key: }"
        var o = IndexNode.Describe.ParseObject("{\"entries\":[{\"zh\":\"霍湛\",\"en\":\"Huo Zhan\",\"en\":\"Other\",\"}\":1,\"}\":2,\"\":3,\"...\":4}],\"plot\":\"a\",\"plot\":\"b\"}");
        var e = o["entries"]![0]!.AsObject();
        Assert.Equal("Huo Zhan", e["en"]!.GetValue<string>()); Assert.Equal(2, e.Count); Assert.Equal("a", o["plot"]!.GetValue<string>());
    }
}
