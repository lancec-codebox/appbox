using System.Text.Json.Nodes;

namespace IndexNode.Tests;

/// <summary>0.8.7: with --ai-session-id set, every AI request carries x-session-id (one place: AiClient.PostAsync), whatever the call type or API.</summary>
public class AiSessionHeaderTests
{
    sealed class Rec : HttpMessageHandler
    {
        public readonly List<(string Path, string? Session, string Body)> Calls = [];
        public bool RefuseTools;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content!.ReadAsStringAsync(ct).Result;
            Calls.Add((r.RequestUri!.AbsolutePath, r.Headers.TryGetValues("x-session-id", out var v) ? string.Join(",", v) : null, body));
            var chat = r.RequestUri.AbsolutePath.EndsWith("/chat/completions");
            if (RefuseTools && body.Contains("\"tools\"")) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"tools are not supported\"}}") });
            const string json = "{\\\"entries\\\":[],\\\"ok\\\":true}";
            var reply = chat
                ? "{\"choices\":[{\"message\":{\"content\":\"" + json + "\"}}]}"
                : "{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + json + "\"}]}]}";
            return Task.FromResult(new HttpResponseMessage { Content = new StringContent(reply) });
        }
    }

    static AiClient Client(Rec h, string api) => new(new HttpClient(h), new AiConfig { BaseUrl = "http://gw/v1", TextModel = "text-m", VisionModel = "vision-m", Api = api, SessionId = "drama-batch-7" });

    static readonly string[] Tools = ["submit_description", "submit_check", "submit_rewrite", "submit_glossary", "submit_translation", "submit_classification"];

    [Theory]
    [InlineData("chat", false)]
    [InlineData("responses", false)]
    [InlineData("auto", false)]
    [InlineData("chat", true)]   // an endpoint that refuses tool calls: the text fallback
    public async Task Every_call_type_carries_the_session_header(string api, bool refuseTools)
    {
        var h = new Rec { RefuseTools = refuseTools }; var c = Client(h, api);
        foreach (var t in Tools) await c.JsonAsync("text-m", JsonValue.Create("prompt for " + t)!, t, default);
        await c.TextAsync(default); await c.VisionAsync(default);
        await c.ChatAsync("gpt-x", JsonValue.Create("hi")!, default);
        Assert.True(h.Calls.Count >= Tools.Length + 3);
        Assert.All(h.Calls, x => Assert.Equal("drama-batch-7", x.Session));
    }

    [Fact]
    public async Task The_glossary_describe_spoiler_and_translate_batch_calls_carry_it()
    {
        var h = new Rec(); var c = Client(h, "auto");
        var src = new TranslateSource("ocr", 1.0, 4.0, [new ShapeCue(0, 2, "你好"), new ShapeCue(2, 4, "再见")], "hash");
        await Translate.BuildGlossaryAsync(c, "text-m", src, null, default);
        Assert.NotEmpty(h.Calls); Assert.Contains(h.Calls, x => x.Body.Contains("submit_glossary"));
        Assert.All(h.Calls, x => Assert.Equal("drama-batch-7", x.Session));
        // the same client, a spoiler-check call and a rewrite call
        h.Calls.Clear();
        await c.JsonAsync("text-m", JsonValue.Create("check")!, "submit_check", default); await c.JsonAsync("text-m", JsonValue.Create("rewrite")!, "submit_rewrite", default);
        Assert.Equal(2, h.Calls.Count); Assert.All(h.Calls, x => Assert.Equal("drama-batch-7", x.Session));
    }

    [Fact]
    public async Task Without_a_session_id_no_header_is_sent()
    {
        var h = new Rec(); var c = new AiClient(new HttpClient(h), new AiConfig { BaseUrl = "http://gw/v1", TextModel = "t" });
        await c.TextAsync(default);
        Assert.All(h.Calls, x => Assert.Null(x.Session));
    }
}
