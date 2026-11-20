using System.Text.Json.Nodes;
using IndexNode;
using Xunit;

namespace IndexNode.Tests;

public class DescribeShapeTests
{
    private sealed class Chat(params string[] replies) : IChat
    {
        int n;
        public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) => Task.FromResult(replies[n++]);
    }

    static JsonObject Res() => JsonNode.Parse("""{"title":{"en":"t"},"teaser":{"en":"A teaser."},"cast":[{"cluster":4,"roleAtStart":"old","description":"old d","label":{"en":"old l"}}],"relations":[]}""")!.AsObject();

    [Fact]
    public async Task Rewrite_answer_with_a_label_object_or_nested_fields_is_read_not_thrown_on()
    {
        // 1PZHcJ_KKi0: "The node must be of type 'JsonValue'" -- a field that is an object where the code read a string
        var check = """{"teaserReveals":true,"flaggedCast":[{"cluster":4}],"flaggedRelations":[],"reasons":["x"]}""";
        var rewrite = """{"teaser":{"en":"New teaser."},"cast":[{"cluster":"4","roleAtStart":{"en":"A senior sister."},"description":["A senior disciple."],"label":{"en":"Lin Xue"}}]}""";
        var res = Res();
        var calls = await Describe.SpoilerCheckAsync(new Chat(check, rewrite, """{"teaserReveals":false,"flaggedCast":[],"flaggedRelations":[]}"""), "m", res, new DescribeInput { Duration = 600 }, default);
        Assert.Equal(3, calls);
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
    {"title":{"en":"t"},"teaser":{"en":"A teaser."},"relations":[],"cast":[
     {"cluster":0,"character":{"zh":"苏醒眠","pinyin":"Su Xingmian","en":"Su Xingmian"},"roleAtStart":"A scholar's daughter.","description":"Clever and kind.","label":{"en":"Su Xingmian"}},
     {"cluster":1,"character":{"zh":"萧临渊","pinyin":"Xiao Linyuan","en":"Xiao Linyuan"},"roleAtStart":"The emperor.","description":"A cold ruler.","label":{"en":"Emperor Xiao Linyuan"}},
     {"cluster":2,"character":null,"roleAtStart":"supporting","description":"A maid.","label":{"en":"Maid"}}]}
    """)!.AsObject();

    static async Task<JsonObject> Run(string flagged, string rewrite)
    {
        var res = Two();
        await Describe.SpoilerCheckAsync(new Chat($$"""{"teaserReveals":false,"flaggedCast":{{flagged}},"flaggedRelations":[],"reasons":["x"]}""", rewrite, """{"teaserReveals":false,"flaggedCast":[],"flaggedRelations":[]}"""), "m", res, new DescribeInput { Duration = 600 }, default);
        return res;
    }
    static string Lab(JsonObject r, int i) => (string)r["cast"]![i]!["label"]!["en"]!;
    static string Role(JsonObject r, int i) => (string)r["cast"]![i]!["roleAtStart"]!;
    static JsonNode Round(JsonObject r) => r["spoilerCheck"]!["log"]![0]!;

    const string MaleAs0 = """{"cast":[{"cluster":0,"roleAtStart":"The ruler of the land.","description":"A cold ruler.","label":{"en":"Emperor Xiao Linyuan"}}]}""";

    [Fact]
    public async Task An_answer_for_an_unflagged_cluster_is_ignored()
    {
        var r = await Run("[1]", MaleAs0);
        Assert.Equal("Su Xingmian", Lab(r, 0)); Assert.Equal("A scholar's daughter.", Role(r, 0)); // cluster 0 untouched
        Assert.Equal("Emperor Xiao Linyuan", Lab(r, 1)); // flagged 1 got no answer: original text
        Assert.Equal("not flagged", (string)Round(r)["ignored"]![0]!["reason"]!);
        Assert.Equal(1, Round(r)["flagged"]!.AsArray().Count); Assert.Empty(Round(r)["answered"]!.AsArray());
    }

    [Fact]
    public async Task The_male_leads_text_for_a_flagged_female_lead_is_rejected_by_the_identity_guard()
    {
        var r = await Run("[0,1]", MaleAs0);
        Assert.Equal("Su Xingmian", Lab(r, 0)); Assert.Equal("A scholar's daughter.", Role(r, 0)); Assert.Equal("Clever and kind.", (string)r["cast"]![0]!["description"]!);
        Assert.Contains("names cluster 1", (string)Round(r)["rejected"]![0]!["reason"]!);
        Assert.Equal(0, (int)Round(r)["rejected"]![0]!["cluster"]!);
    }

    [Fact]
    public async Task Same_roleAtStart_or_description_as_another_entry_is_rejected()
    {
        var r = await Run("[0]", """{"cast":[{"cluster":0,"roleAtStart":"The emperor.","description":"Clever.","label":{"en":"The scholar"}}]}""");
        Assert.Equal("A scholar's daughter.", Role(r, 0)); Assert.Contains("roleAtStart equals cluster 1", (string)Round(r)["rejected"]![0]!["reason"]!);
        var r2 = await Run("[0]", """{"cast":[{"cluster":0,"roleAtStart":"A girl.","description":"a cold ruler","label":{"en":"The scholar"}}]}""");
        Assert.Equal("Clever and kind.", (string)r2["cast"]![0]!["description"]!); Assert.Contains("description equals", (string)Round(r2)["rejected"]![0]!["reason"]!);
    }

    [Fact]
    public async Task A_repeated_id_is_applied_once()
    {
        var r = await Run("[0]", """{"cast":[{"cluster":0,"roleAtStart":"A young scholar.","description":"First.","label":{"en":"The scholar"}},{"cluster":0,"roleAtStart":"Second.","description":"Second.","label":{"en":"Second"}}]}""");
        Assert.Equal("A young scholar.", Role(r, 0)); Assert.Equal("The scholar", Lab(r, 0));
        Assert.Equal("duplicate", (string)Round(r)["ignored"]![0]!["reason"]!);
    }

    [Fact]
    public async Task A_flagged_id_with_no_answer_keeps_its_text()
    {
        var r = await Run("[0,2]", """{"cast":[{"cluster":2,"roleAtStart":"A servant.","description":"Loyal.","label":{"en":"Servant"}}]}""");
        Assert.Equal("A scholar's daughter.", Role(r, 0)); Assert.Equal("Su Xingmian", Lab(r, 0)); Assert.Equal("A servant.", Role(r, 2));
        Assert.Equal(new[] { 2 }, Round(r)["answered"]!.AsArray().Select(x => (int)x!).ToArray());
    }

    [Fact]
    public async Task A_valid_rewrite_is_applied()
    {
        var r = await Run("[0]", """{"cast":[{"cluster":0,"roleAtStart":"A young scholar.","description":"Quiet and clever.","label":{"en":"Su Xingmian"}}]}""");
        Assert.Equal("A young scholar.", Role(r, 0)); Assert.Equal("Quiet and clever.", (string)r["cast"]![0]!["description"]!); Assert.Empty(Round(r)["rejected"]!.AsArray());
    }

    [Fact]
    public async Task A_relation_label_naming_another_character_with_a_possessive_passes()
    {
        var r = await Run("[2]", """{"cast":[{"cluster":2,"roleAtStart":"A maid.","description":"Serves.","label":{"en":"Xiao Linyuan's maid"}}]}""");
        Assert.Equal("Xiao Linyuan's maid", Lab(r, 2)); Assert.Empty(Round(r)["rejected"]!.AsArray());
        var r2 = await Run("[2]", """{"cast":[{"cluster":2,"roleAtStart":"A maid.","description":"Serves.","label":{"en":"萧临渊的侍女"}}]}""");
        Assert.Equal("萧临渊的侍女", Lab(r2, 2));
        var r3 = await Run("[2]", """{"cast":[{"cluster":2,"roleAtStart":"A maid.","description":"Serves.","label":{"en":"Xiao Linyuan"}}]}""");
        Assert.Equal("Maid", Lab(r3, 2)); // the bare name of another character is rejected
    }

    [Fact]
    public void The_rewrite_prompt_has_no_concrete_cluster_id_and_names_the_flagged_ones()
    {
        var res = Two(); var check = JsonNode.Parse("""{"flaggedCast":[1],"reasons":["x"]}""")!.AsObject();
        var pr = Describe.BuildRewrite(res, check, "m").Prompt;
        Assert.DoesNotContain("\"cluster\":0,\"roleAtStart\":\"\"", pr); Assert.Contains("[1]", pr); Assert.Contains("Xiao Linyuan", pr);
        Assert.Equal("describe-8", Describe.PromptVersion);
    }
}
