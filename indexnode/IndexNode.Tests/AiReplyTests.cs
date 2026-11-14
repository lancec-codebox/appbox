using System.Text.Json.Nodes;

namespace IndexNode.Tests;

public class AiReplyTests
{
    const string Err = """{"error":{"message":"model \"gpt-6-luna\" is not accessible via the /chat/completions endpoint","code":"unsupported_api_for_model"}}""";

    [Fact]
    public void Error_body_is_an_error_with_its_message()
    {
        Assert.Contains("unsupported_api_for_model", AiClient.ErrorMessage(Err)!);
        Assert.Equal("boom", AiClient.ErrorMessage("""{"error":"boom"}"""));
        Assert.Null(AiClient.ErrorMessage("""{"choices":[{"message":{"content":"{}"}}]}"""));
        Assert.Null(AiClient.ErrorMessage("not json"));
    }

    [Fact]
    public void ParseObject_reports_error_bodies_and_logs_the_start_of_a_reply_without_json()
    {
        var e = Assert.Throws<FormatException>(() => Describe.ParseObject(Err));
        Assert.Contains("unsupported_api_for_model", e.Message);
        var n = Assert.Throws<FormatException>(() => Describe.ParseObject("Sorry, I cannot help with that."));
        Assert.Contains("Sorry, I cannot help", n.Message);
        Assert.Equal(1, (int)Describe.ParseObject("{\"a\":1}")["a"]!);
    }

    sealed class Handler(Func<string, (int, string)> f) : HttpMessageHandler
    {
        public List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Paths.Add(r.RequestUri!.AbsolutePath); var (code, body) = f(r.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)code) { Content = new StringContent(body) });
        }
    }

    static AiClient Client(Handler h) => new(new HttpClient(h), new AiConfig { BaseUrl = "http://x/v1", TextModel = "DeepSeek-V4.1-Flash", VisionModel = "gpt-6-luna" });
    const string Chat = """{"choices":[{"message":{"content":"{\"ok\":true}"}}]}""";
    const string Resp = """{"output":[{"type":"message","content":[{"type":"output_text","text":"{\"ok\":true}"}]}]}""";

    [Fact]
    public async Task Auto_sends_gpt_models_to_responses_and_others_to_chat()
    {
        var h = new Handler(path => path.EndsWith("/responses") ? (200, Resp) : (200, Chat)); var c = Client(h);
        Assert.Equal("""{"ok":true}""", await c.ChatAsync("gpt-6-luna", JsonValue.Create("hi")!, default));
        Assert.Equal("""{"ok":true}""", await c.ChatAsync("DeepSeek-V4.1-Flash", JsonValue.Create("hi")!, default));
        Assert.Equal(["/v1/responses", "/v1/chat/completions"], h.Paths);
    }

    [Fact]
    public async Task Auto_falls_back_to_responses_on_unsupported_api_even_with_a_200_error_body()
    {
        foreach (var code in new[] { 400, 200 })
        {
            var h = new Handler(path => path.EndsWith("/responses") ? (200, Resp) : (code, Err)); var c = Client(h);
            Assert.Equal("""{"ok":true}""", await c.ChatAsync("vision-x", JsonValue.Create("hi")!, default));
            Assert.Equal(["/v1/chat/completions", "/v1/responses"], h.Paths);
        }
    }

    [Fact]
    public async Task A_200_error_body_is_thrown_not_returned_as_a_reply()
    {
        var c = Client(new Handler(_ => (200, """{"error":{"message":"quota","code":"rate"}}""")));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => c.ChatAsync("DeepSeek-V4.1-Flash", JsonValue.Create("hi")!, default));
        Assert.Contains("quota", e.Message);
    }
}
