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
}
