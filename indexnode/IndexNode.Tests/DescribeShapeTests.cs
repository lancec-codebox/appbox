using System.Text.Json.Nodes;
using IndexNode;
using Xunit;

namespace IndexNode.Tests;

public class DescribeShapeTests
{
    private sealed class Chat(params string[] replies) : IChat
    {
        int n; public int Calls => n; public string? LastPrompt;
        public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) { LastPrompt = content.ToJsonString(); return Task.FromResult(replies[n++]); }
    }

    static JsonObject Res() => JsonNode.Parse("""{"title":{"en":"t"},"teaser":{"en":"A teaser."},"synopsis":{"en":"Full story."},"cast":[{"cluster":4,"roleAtStart":"old","description":"old d","label":{"en":"old l"}}],"relations":[]}""")!.AsObject();

    [Fact]
    public async Task Twist_check_answer_with_a_label_object_or_nested_fields_is_read_not_thrown_on()
    {
        // 1PZHcJ_KKi0: "The node must be of type 'JsonValue'" -- a field that is an object where the code read a string
        var check = """{"revealsTwist":true,"reasons":["x"],"teaser":{"en":"New teaser."},"cast":[{"cluster":"4","roleAtStart":{"en":"A senior sister."},"description":["A senior disciple."],"label":{"en":"Lin Xue"}}]}""";
        var res = Res(); var chat = new Chat(check);
        var calls = await Describe.TwistCheckAsync(chat, "m", res, new DescribeInput { Duration = 600 }, default);
        Assert.Equal(1, calls); Assert.Equal(1, chat.Calls);
        Assert.Equal("New teaser.", (string)res["teaser"]!["en"]!);
        var c = res["cast"]![0]!;
        Assert.Equal("A senior sister.", (string)c["roleAtStart"]!); Assert.Equal("A senior disciple.", (string)c["description"]!); Assert.Equal("Lin Xue", (string)c["label"]!["en"]!);
    }

    [Fact]
    public void Text_fields_accept_string_object_or_array_and_treat_the_rest_as_missing()
    {
        Assert.Equal("a", Describe.Txt(JsonNode.Parse("\"a\"")));
        Assert.Equal("b", Describe.Txt(JsonNode.Parse("{\"en\":\"b\"}")));
        Assert.Equal("c", Describe.Txt(JsonNode.Parse("[\"c\",\"d\"]")));
        Assert.Null(Describe.Txt(JsonNode.Parse("{\"x\":1}"))); Assert.Null(Describe.Txt(null)); Assert.Null(Describe.Txt(JsonNode.Parse("true")));
    }

    // two leads: 0 = the female lead (Su Xingmian), 1 = the male lead (Xiao Linyuan)
    static JsonObject Two() => JsonNode.Parse("""
    {"title":{"en":"t"},"teaser":{"en":"A teaser."},"synopsis":{"en":"Full story."},"relations":[],"cast":[
     {"cluster":0,"character":{"zh":"苏醒眠","pinyin":"Su Xingmian","en":"Su Xingmian"},"roleAtStart":"A scholar's daughter.","description":"Clever and kind.","label":{"en":"Su Xingmian"}},
     {"cluster":1,"character":{"zh":"萧临渊","pinyin":"Xiao Linyuan","en":"Xiao Linyuan"},"roleAtStart":"The emperor.","description":"A cold ruler.","label":{"en":"Emperor Xiao Linyuan"}},
     {"cluster":2,"character":null,"roleAtStart":"supporting","description":"A maid.","label":{"en":"Maid"}}]}
    """)!.AsObject();

    static string Lab(JsonObject r, int i) => (string)r["cast"]![i]!["label"]!["en"]!;
    static string Role(JsonObject r, int i) => (string)r["cast"]![i]!["roleAtStart"]!;
    static JsonNode Round(JsonObject r) => r["twistCheck"]!;

    const string MaleAs0 = """[{"cluster":0,"roleAtStart":"The ruler of the land.","description":"A cold ruler.","label":{"en":"Emperor Xiao Linyuan"}}]""";

    static async Task<JsonObject> Run(string castFixes)
    {
        var res = Two(); var chat = new Chat($$"""{"revealsTwist":true,"reasons":["x"],"teaser":null,"cast":{{castFixes}}}""");
        Assert.Equal(1, await Describe.TwistCheckAsync(chat, "m", res, new DescribeInput { Duration = 600 }, default)); Assert.Equal(1, chat.Calls);
        return res;
    }
    [Fact]
    public async Task An_answer_for_a_cluster_not_in_the_cast_is_ignored_and_omitted_ones_keep_their_text()
    {
        var r = await Run("""[{"cluster":9,"roleAtStart":"x","description":"y","label":{"en":"z"}}]""");
        Assert.Equal("Su Xingmian", Lab(r, 0)); Assert.Equal("A scholar's daughter.", Role(r, 0)); Assert.Equal("Emperor Xiao Linyuan", Lab(r, 1));
        Assert.Equal("not in the cast", (string)Round(r)["ignored"]![0]!["reason"]!); Assert.Empty(Round(r)["castFixed"]!.AsArray());
    }

    [Fact]
    public async Task The_male_leads_text_for_the_female_lead_is_rejected_by_the_identity_guard()
    {
        var r = await Run(MaleAs0);
        Assert.Equal("Su Xingmian", Lab(r, 0)); Assert.Equal("A scholar's daughter.", Role(r, 0)); Assert.Equal("Clever and kind.", (string)r["cast"]![0]!["description"]!);
        Assert.Contains("names cluster 1", (string)Round(r)["rejected"]![0]!["reason"]!);
        Assert.Equal(0, (int)Round(r)["rejected"]![0]!["cluster"]!);
    }

    [Fact]
    public async Task Same_roleAtStart_or_description_as_another_entry_is_rejected()
    {
        var r = await Run("""[{"cluster":0,"roleAtStart":"The emperor.","description":"Clever.","label":{"en":"Su Xingmian"}}]""");
        Assert.Equal("A scholar's daughter.", Role(r, 0)); Assert.Contains("roleAtStart equals cluster 1", (string)Round(r)["rejected"]![0]!["reason"]!);
        var r2 = await Run("""[{"cluster":0,"roleAtStart":"A girl.","description":"a cold ruler","label":{"en":"Su Xingmian"}}]""");
        Assert.Equal("Clever and kind.", (string)r2["cast"]![0]!["description"]!); Assert.Contains("description equals", (string)Round(r2)["rejected"]![0]!["reason"]!);
    }

    [Fact]
    public async Task A_repeated_id_is_applied_once()
    {
        var r = await Run("""[{"cluster":0,"roleAtStart":"A young scholar.","description":"First.","label":{"en":"Su Xingmian"}},{"cluster":0,"roleAtStart":"Second.","description":"Second.","label":{"en":"Second"}}]""");
        Assert.Equal("A young scholar.", Role(r, 0)); Assert.Equal("Su Xingmian", Lab(r, 0));
        Assert.Equal("duplicate", (string)Round(r)["ignored"]![0]!["reason"]!);
    }

    [Fact]
    public async Task A_valid_fix_is_applied_and_the_rest_keeps_its_text()
    {
        var r = await Run("""[{"cluster":0,"roleAtStart":"A young scholar.","description":"Quiet and clever.","label":{"en":"Su Xingmian"}}]""");
        Assert.Equal("A young scholar.", Role(r, 0)); Assert.Equal("Quiet and clever.", (string)r["cast"]![0]!["description"]!); Assert.Empty(Round(r)["rejected"]!.AsArray());
        Assert.Equal("The emperor.", Role(r, 1)); Assert.Equal("A teaser.", (string)r["teaser"]!["en"]!); // teaser null: kept
    }

    [Fact]
    public async Task A_clean_check_changes_nothing_even_if_it_sends_fixes()
    {
        var res = Two(); var chat = new Chat("""{"revealsTwist":false,"reasons":[],"teaser":"Other.","cast":[{"cluster":0,"roleAtStart":"Z","description":"Z","label":"Z"}]}""");
        await Describe.TwistCheckAsync(chat, "m", res, new DescribeInput(), default);
        Assert.Equal("A teaser.", (string)res["teaser"]!["en"]!); Assert.Equal("A scholar's daughter.", Role(res, 0)); Assert.False((bool)res["twistCheck"]!["revealed"]!);
    }

    [Fact]
    public async Task A_relation_label_naming_another_character_with_a_possessive_passes()
    {
        var r = await Run("""[{"cluster":2,"roleAtStart":"A maid.","description":"Serves.","label":{"en":"Xiao Linyuan's maid"}}]""");
        Assert.Equal("Xiao Linyuan's maid", Lab(r, 2)); Assert.Empty(Round(r)["rejected"]!.AsArray());
        var r2 = await Run("""[{"cluster":2,"roleAtStart":"A maid.","description":"Serves.","label":{"en":"萧临渊的侍女"}}]""");
        Assert.Equal("Maid", Lab(r2, 2)); // Chinese in an English field is refused
        var r3 = await Run("""[{"cluster":2,"roleAtStart":"A maid.","description":"Serves.","label":{"en":"Xiao Linyuan"}}]""");
        Assert.Equal("Maid", Lab(r3, 2)); // the bare name of another character is rejected
    }

    static DescribeInput In(string title = "Reborn as the Villainess") => new() { Title = title, Duration = 600, Clusters = [new(0, 100, 5, false), new(1, 80, 9, false), new(2, 60, 12, false), new(3, 50, 14, false)] };
    const string Reply = """
    {"title":{"en":"Reborn as the Villainess","zh":"重生"},"fullTitle":{"en":"Reborn as the Villainess (Full Version)"},"teaser":{"en":"Reborn, she vows to marry the crown prince. The villain Gu schemes."},"synopsis":{"en":"Everything."},
     "tags":["villain","antagonist","reborn as the villainess"],"era":"ancient","fantasy":false,
     "cast":[{"cluster":0,"role":"female lead","roleAtStart":"A noble girl.","description":"d0","label":{"en":"Lin"}},
             {"cluster":1,"role":"female lead","roleAtStart":"A second heroine.","description":"d1","label":{"en":"Mei"}},
             {"cluster":2,"role":"supporting","roleAtStart":"The grandmother.","description":"d2","label":{"en":"Grandmother"}},
             {"cluster":3,"role":"main","roleAtStart":"A friend.","description":"d3","label":{"en":"Friend"}}],
     "relations":[{"from":2,"to":0,"kind":"Grandparent","type":"grandmother","label":{"en":"her grandmother"},"since":0,"until":null,"spoiler":true,"implied":false},{"from":0,"to":3,"kind":"pals","type":"x","label":{"en":"x"},"since":0,"until":null,"implied":true}]}
    """;

    [Fact]
    public void The_villainess_survives_in_title_fullTitle_and_tags_and_dual_leads_are_kept()
    {
        var n = Describe.Normalize(JsonNode.Parse(Reply)!.AsObject(), In());
        Assert.Equal("Reborn as the Villainess", (string)n["title"]!["en"]!); Assert.Equal("Reborn as the Villainess (Full Version)", (string)n["fullTitle"]!["en"]!);
        Assert.Equal(new[] { "villain", "antagonist", "reborn as the villainess" }, n["tags"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Contains("The villain Gu schemes.", (string)n["teaser"]!["en"]!);
        Assert.Equal(new[] { "female lead", "female lead", "supporting", "main" }, n["cast"]!.AsArray().Select(x => (string)x!["role"]!).ToArray()); // both leads kept
    }

    [Fact]
    public void Role_kinds_are_validated_there_is_no_villain_field_and_a_grandmother_is_supporting()
    {
        var n = Describe.Normalize(JsonNode.Parse(Reply)!.AsObject(), In());
        foreach (var c in n["cast"]!.AsArray()) Assert.False(((JsonObject)c!).ContainsKey("villain"));
        Assert.Equal("grandparent", (string)n["relations"]![0]!["kind"]!); Assert.Equal("other", (string)n["relations"]![1]!["kind"]!);
        foreach (var r in n["relations"]!.AsArray()) Assert.False(((JsonObject)r!).ContainsKey("spoiler"));
        Assert.Equal("other", Describe.CleanKind(null)); Assert.Equal("aunt-uncle", Describe.CleanKind("Aunt_Uncle"));
        var g = JsonNode.Parse(Reply.Replace("\"cluster\":2,\"role\":\"supporting\"", "\"cluster\":2,\"role\":\"grandmother\""))!.AsObject();
        Assert.Equal("supporting", (string)Describe.Normalize(g, In())["cast"]![2]!["role"]!);
    }

    // clusters: 0 = 100 s, 1 = 80 s (the lower lead), 2 = 60 s, 3 = 50 s; floor = 0.25 x 80 = 20 s
    [Fact]
    public void A_low_screen_time_main_is_demoted_and_a_high_one_is_kept()
    {
        var d = new DescribeInput { Title = "t", Duration = 600, Clusters = [new(0, 100, 5, false), new(1, 80, 9, false), new(2, 60, 12, false), new(3, 10, 14, false)] };
        var r = JsonNode.Parse(Reply.Replace("\"cluster\":2,\"role\":\"supporting\"", "\"cluster\":2,\"role\":\"main\"")).AsObject();   // 2: main with 60 s; 3: main with 10 s
        var n = Describe.Normalize(r, d);
        Assert.Equal("main", (string)n["cast"]![2]!["role"]!); Assert.False(((JsonObject)n["cast"]![2]!).ContainsKey("demoted"));
        Assert.Equal("supporting", (string)n["cast"]![3]!["role"]!); Assert.Equal("screen time", (string)n["cast"]![3]!["demoted"]!);
        var j = new JsonArray(new JsonObject { ["cluster"] = 0, ["role"] = "male lead" }, new JsonObject { ["cluster"] = 3, ["role"] = "main" });
        Describe.DemoteLowScreenMains(j, d, 0.05); Assert.Equal("main", (string)j[1]!["role"]!);   // 10 s >= 0.05 x 100 s: the factor is a setting
    }

    [Fact]
    public void The_main_prompt_has_no_cutoff_no_word_ban_and_the_new_rules()
    {
        var pr = Describe.BuildMain(In(), "v").Prompt;
        Assert.DoesNotContain("cutoff", pr, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("NEVER use the words", pr); Assert.DoesNotContain("spoiler is", pr); Assert.DoesNotContain("\"spoiler\"", pr);
        Assert.Contains("lead-in", pr); Assert.Contains("grandmother", pr); Assert.Contains("ex-husband", pr); Assert.DoesNotContain("\"villain\"", pr); Assert.Contains("BOTH significant screen time AND", pr); Assert.Contains("aunt-uncle", pr); Assert.Contains("dual leads", pr);
        Assert.DoesNotContain("exactly one 'female lead'", pr);
    }

    [Fact]
    public async Task The_describe_run_makes_exactly_one_check_call_and_applies_its_rewrite()
    {
        var chat = new Chat(Reply, """{"revealsTwist":true,"reasons":["x"],"teaser":"Reborn, she vows to marry the prince.","cast":[{"cluster":2,"roleAtStart":"An elder.","description":"Old.","label":{"en":"Granny"}}]}""");
        var res = await Describe.RunAsync(chat, "text", "vision", In(), default);
        Assert.Equal(2, chat.Calls); Assert.Equal(2, (int)res["aiCalls"]!); // the describe call and ONE twist check
        Assert.Equal("Reborn, she vows to marry the prince.", (string)res["teaser"]!["en"]!); Assert.Equal("Granny", (string)res["cast"]![2]!["label"]!["en"]!);
        Assert.False(res.ContainsKey("cutoffSeconds")); Assert.False(res.ContainsKey("spoilerCheck")); Assert.Contains("synopsis: Everything.", chat.LastPrompt!);
        Assert.Equal("describe-9", (string)res["promptVersion"]!);
        var pr = Describe.BuildTwistCheck(res, "m").Prompt; Assert.Contains("Everything.", pr); Assert.Contains("twist", pr);
    }

    [Fact]
    public async Task The_twist_check_prompt_carries_the_titles_and_calls_title_arcs_and_a_past_life_premise()
    {
        var res = Two(); res["title"] = new JsonObject { ["en"] = "Reborn, She Marries Her Ex's Brother" }; res["fullTitle"] = new JsonObject { ["en"] = "Reborn, She Marries Her Exs Brother (Full)" };
        res["teaser"] = new JsonObject { ["en"] = "Reborn, she vows to marry the scumbag's brother this time." };
        var chat = new Chat("""{"revealsTwist":false,"wrongCharacter":false,"reasons":[],"teaser":null,"cast":[]}""");
        await Describe.TwistCheckAsync(chat, "m", res, new DescribeInput(), default);
        Assert.Contains("Reborn, She Marries Her Exs Brother (Full)", chat.LastPrompt!); Assert.Contains("past life and its death", chat.LastPrompt!); Assert.Contains("anything the title states", chat.LastPrompt!);
        Assert.Equal("Reborn, she vows to marry the scumbag's brother this time.", (string)res["teaser"]!["en"]!); // a title-stated arc is not flagged: nothing changes
    }

    [Fact]
    public async Task A_name_is_never_turned_into_a_description_and_a_label_has_no_trailing_period()
    {
        var r = await Run("""[{"cluster":0,"roleAtStart":"A scholar's daughter.","description":"Clever.","label":{"en":"The reborn bride"}}]""");
        Assert.Equal("Su Xingmian", Lab(r, 0)); Assert.Equal("label would replace the character's name", (string)Round(r)["rejected"]![0]!["reason"]!.ToString().Trim('"'));
        var r2 = await Run("""[{"cluster":2,"roleAtStart":"A maid.","description":"Serves.","label":{"en":"Xiao Linyuan's maid."}}]""");
        Assert.Equal("Xiao Linyuan's maid", Lab(r2, 2));
        var pr = Describe.BuildMain(new DescribeInput { Title = "t", Duration = 60 }, "v").Prompt;
        Assert.Contains("label.en IS the character's name", pr); Assert.Contains("No trailing period", pr);
    }

    [Fact]
    public void Chinese_in_an_english_field_is_found_but_not_in_the_zh_fields()
    {
        var ok = JsonNode.Parse("""{"title":{"en":"t","zh":"重生"},"teaser":{"en":"A bride."},"synopsis":{"en":"s"},"cast":[{"cluster":0,"character":{"zh":"苏","en":"Su"},"roleAtStart":"x","description":"d","label":{"en":"Su"}}]}""")!.AsObject();
        Assert.Null(Describe.CjkProblem(ok));
        var bad = JsonNode.Parse("""{"title":{"en":"t"},"teaser":{"en":"Reborn in the 侯府."},"synopsis":{"en":"s"},"cast":[{"cluster":3,"roleAtStart":"x","description":"d","label":{"en":"乖乖"}}]}""")!.AsObject();
        var why = Describe.CjkProblem(bad)!; Assert.Contains("teaser.en", why); Assert.Contains("cast 3 label.en", why);
    }

    [Fact]
    public async Task A_reply_with_chinese_in_an_english_field_is_retried_once_with_the_error()
    {
        var chat = new Chat(Reply.Replace("Reborn, she vows", "Reborn in the 侯府, she vows"), Reply, """{"revealsTwist":false,"reasons":[],"teaser":null,"cast":[]}""");
        var res = await Describe.RunAsync(chat, "text", "vision", In(), default);
        Assert.Equal(3, chat.Calls); Assert.Equal(3, (int)res["aiCalls"]!); Assert.DoesNotContain("侯府", (string)res["teaser"]!["en"]!);
    }

    [Fact]
    public async Task A_cast_field_describing_another_character_is_flagged_and_corrected_in_the_same_reply()
    {
        var res = Two(); res["cast"]![0]!["description"] = "A powerful prince and military commander.";
        var chat = new Chat("""{"revealsTwist":false,"wrongCharacter":true,"reasons":["cluster 0 describes the prince"],"teaser":null,"cast":[{"cluster":0,"roleAtStart":"A scholar's daughter.","description":"A clever young woman of a scholar's house.","label":{"en":"Su Xingmian"}}]}""");
        await Describe.TwistCheckAsync(chat, "m", res, new DescribeInput(), default);
        Assert.Equal(1, chat.Calls); Assert.Equal("A clever young woman of a scholar's house.", (string)res["cast"]![0]!["description"]!);
        Assert.Contains("describes a different character", chat.LastPrompt!);
    }

    [Fact]
    public void The_title_block_has_the_16_examples_and_the_rules_and_titleSource_is_validated()
    {
        var pr = Describe.BuildMain(In(), "v").Prompt;
        Assert.Contains("He Fell First", pr); Assert.Contains("Betrayed After Giving Everything to the Emperor, She Rises from the Ashes and Returns for Revenge", pr); Assert.Contains("EP1-80", pr);
        Assert.Contains("titleSource", pr);
        var yt = Describe.Normalize(JsonNode.Parse("""{"title":{"en":"Reborn Bride Marries the Sickly Heir","zh":"重生新娘"},"titleSource":"youtube","fullTitle":{"en":"x"},"cast":[]}""")!.AsObject(), In("【Drama】 Reborn Bride Marries the Sickly Heir EP1-80 full #shortdrama"));
        Assert.Equal("youtube", (string)yt["titleSource"]!); Assert.Equal("Reborn Bride Marries the Sickly Heir", (string)yt["title"]!["en"]!);
        var zh = Describe.Normalize(JsonNode.Parse("""{"title":{"en":"Reborn, I Tear Apart the Scam","zh":"重生撕渣"},"titleSource":"Translated","fullTitle":{"en":"x"},"cast":[]}""")!.AsObject(), In("重生撕渣"));
        Assert.Equal("translated", (string)zh["titleSource"]!); Assert.Equal("重生撕渣", (string)zh["title"]!["zh"]!);
        Assert.Null(Describe.Normalize(JsonNode.Parse("""{"title":{"en":"t"},"titleSource":"guessed","cast":[]}""")!.AsObject(), In())["titleSource"]);
    }
}
