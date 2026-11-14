using System.Text.Json;

namespace IndexNode.Tests;

public class AiConfigTests
{
    private static string? None(string _) => null;

    [Fact]
    public void Precedence_is_flag_then_environment_then_config_per_field()
    {
        var file = new AiConfig { BaseUrl = "http://file", Key = "file-key-1234", TextModel = "file-text", VisionModel = "file-vision" };
        var env = new Dictionary<string, string> { ["INDEXNODE_AI_BASE_URL"] = "http://env", ["INDEXNODE_AI_TEXT_MODEL"] = "env-text", ["INDEXNODE_AI_KEY"] = "env-key-5678" };
        var flags = new Dictionary<string, string> { ["ai-base-url"] = "http://flag", ["ai-vision-model"] = "flag-vision" };
        var r = AiConfig.Resolve(file, k => flags.GetValueOrDefault(k), k => env.GetValueOrDefault(k));
        Assert.Equal("http://flag", r.BaseUrl);        // flag beats env and file
        Assert.Equal("env-text", r.TextModel);         // env beats file
        Assert.Equal("env-key-5678", r.Key);
        Assert.Equal("flag-vision", r.VisionModel);
        var onlyFile = AiConfig.Resolve(file, None, None);
        Assert.Equal("http://file", onlyFile.BaseUrl); Assert.Equal("file-vision", onlyFile.VisionModel);
        Assert.False(AiConfig.Resolve(null, None, None).Configured);
        Assert.True(onlyFile.Configured);
    }

    [Fact]
    public void Blank_values_fall_through_to_the_next_source()
    {
        var r = AiConfig.Resolve(new AiConfig { BaseUrl = "http://file" }, k => k == "ai-base-url" ? "  " : null, k => k == "INDEXNODE_AI_BASE_URL" ? "" : null);
        Assert.Equal("http://file", r.BaseUrl);
    }

    [Fact]
    public void The_key_is_redacted_in_logs_errors_and_the_description()
    {
        const string key = "sk-test-SECRETKEY-0123456789";
        Redact.Register(key);
        try
        {
            Assert.DoesNotContain("SECRETKEY", Redact.Sas($"401 for key {key} at the endpoint"));
            Assert.DoesNotContain("SECRETKEY", Redact.Sas("Authorization: Bearer abcdef-SECRETKEY-xyz"));
            Assert.DoesNotContain("SECRETKEY", new AiConfig { BaseUrl = "http://x", TextModel = "m", Key = key }.Describe());
            var sw = new StringWriter(); var old = Log.Out; Log.Out = sw;
            try { Log.Info("calling with " + key); } finally { Log.Out = old; }
            Assert.DoesNotContain("SECRETKEY", sw.ToString()); Assert.Contains("REDACTED", sw.ToString());
        }
        finally { Redact.ClearKnown(); }
    }

    [Fact]
    public void The_key_is_stored_in_the_config_file_but_never_printed_by_Describe()
    {
        var c = new NodeConfig { Ai = new AiConfig { Key = "k-abcdef123456", TextModel = "m", BaseUrl = "http://x" } };
        Assert.Contains("k-abcdef123456", JsonSerializer.Serialize(c, NodeConfig.Json)); // file is user-only (mode 600)
        Assert.DoesNotContain("k-abcdef", c.Ai.Describe());
    }

    [Fact]
    public async Task Ai_test_sends_one_text_and_one_vision_call_with_the_bearer_key()
    {
        var seen = new List<(string Auth, string Body)>();
        var http = new HttpClient(new Stub(req => { seen.Add((req.Headers.Authorization?.ToString() ?? "", req.Content!.ReadAsStringAsync().Result)); return "{\"choices\":[{\"message\":{\"content\":\" ok \"}}]}"; }));
        var c = new AiClient(http, new AiConfig { BaseUrl = "http://gw/v1/", Key = "K123456", TextModel = "t", VisionModel = "v" });
        Assert.Equal("ok", await c.TextAsync(default));
        Assert.Equal("ok", await c.VisionAsync(default));
        Assert.Equal(2, seen.Count);
        Assert.Equal("Bearer K123456", seen[0].Auth);
        Assert.Contains("\"model\":\"t\"", seen[0].Body); Assert.Contains("\"model\":\"v\"", seen[1].Body);
        Assert.Contains("data:image/png;base64,", seen[1].Body);
    }

    private sealed class Fake(Func<HttpRequestMessage, string, (int Status, string Body)> f) : HttpMessageHandler
    {
        public readonly List<(string Path, string Body)> Seen = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content!.ReadAsStringAsync(ct).Result; Seen.Add((r.RequestUri!.AbsolutePath, body));
            var (st, b) = f(r, body);
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)st) { Content = new StringContent(b) });
        }
    }

    private const string Unsupported = "{\"error\":{\"message\":\"model \\\"gpt-6-luna\\\" is not accessible via the /chat/completions endpoint\",\"code\":\"unsupported_api_for_model\"}}";

    private static (int, string) LunaServer(HttpRequestMessage r, string body) =>
        r.RequestUri!.AbsolutePath.EndsWith("/chat/completions") && body.Contains("gpt-6-luna") ? (400, Unsupported)
        : r.RequestUri.AbsolutePath.EndsWith("/responses") ? (200, "{\"output\":[{\"type\":\"reasoning\",\"summary\":[]},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\" red \"}]}]}")
        : (200, "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");

    [Fact]
    public async Task Auto_retries_once_on_responses_when_chat_says_unsupported_and_remembers_the_model()
    {
        var fake = new Fake(LunaServer); var c = new AiClient(new HttpClient(fake), new AiConfig { BaseUrl = "http://gw/v1", VisionModel = "gpt-6-luna", TextModel = "plain" });
        Assert.Equal("red", await c.VisionAsync(default));
        Assert.Equal(["/v1/chat/completions", "/v1/responses"], fake.Seen.Select(s => s.Path));
        var resp = fake.Seen[1].Body;
        Assert.Contains("\"input_text\"", resp); Assert.Contains("\"input_image\"", resp); Assert.Contains("data:image/png;base64,", resp);
        Assert.Equal("responses", c.ApiFor("gpt-6-luna"));
        Assert.Equal("red", await c.VisionAsync(default)); // remembered: straight to responses
        Assert.Equal(3, fake.Seen.Count); Assert.EndsWith("/responses", fake.Seen[2].Path);
        Assert.Equal("ok", await c.TextAsync(default)); // another model still uses chat
        Assert.EndsWith("/chat/completions", fake.Seen[3].Path); Assert.Equal("chat", c.ApiFor("plain"));
    }

    [Fact]
    public async Task Chat_mode_does_not_fall_back_and_responses_mode_goes_straight_there()
    {
        var f1 = new Fake(LunaServer);
        var chat = new AiClient(new HttpClient(f1), new AiConfig { BaseUrl = "http://gw/v1", VisionModel = "gpt-6-luna", Api = "chat" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.VisionAsync(default));
        Assert.Contains("unsupported_api_for_model", ex.Message); Assert.Single(f1.Seen);
        var f2 = new Fake(LunaServer);
        var resp = new AiClient(new HttpClient(f2), new AiConfig { BaseUrl = "http://gw/v1", VisionModel = "gpt-6-luna", Api = "responses" });
        Assert.Equal("red", await resp.VisionAsync(default));
        Assert.Equal(["/v1/responses"], f2.Seen.Select(s => s.Path));
    }

    [Fact]
    public async Task Other_chat_errors_do_not_trigger_the_fallback()
    {
        var f = new Fake((_, _) => (400, "{\"error\":{\"message\":\"Could not process image\"}}"));
        var c = new AiClient(new HttpClient(f), new AiConfig { BaseUrl = "http://gw/v1", VisionModel = "v" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.VisionAsync(default));
        Assert.Single(f.Seen);
    }

    [Fact]
    public void Ai_api_resolves_flag_over_env_over_file_and_defaults_to_auto()
    {
        Assert.Equal("auto", AiConfig.Resolve(null, _ => null, _ => null).EffectiveApi);
        var r = AiConfig.Resolve(new AiConfig { Api = "chat" }, k => k == "ai-api" ? "Responses" : null, k => k == "INDEXNODE_AI_API" ? "chat" : null);
        Assert.Equal("responses", r.Api);
        Assert.Equal("chat", AiConfig.Resolve(new AiConfig { Api = "responses" }, _ => null, k => k == "INDEXNODE_AI_API" ? "chat" : null).Api);
        Assert.Equal("responses", AiConfig.Resolve(new AiConfig { Api = "responses" }, _ => null, _ => null).Api);
        Assert.False(AiConfig.ValidApi("bogus"));
    }

    [Fact]
    public void The_test_image_is_a_real_64x64_png()
    {
        var p = AiClient.TestPng;
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, p[..4]);
        Assert.Equal(64, (p[16] << 24) | (p[17] << 16) | (p[18] << 8) | p[19]); Assert.Equal(64, (p[20] << 24) | (p[21] << 16) | (p[22] << 8) | p[23]);
    }

    private sealed class Stub(Func<HttpRequestMessage, string> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage { Content = new StringContent(f(r)) });
    }

    [Fact]
    public void Processed_json_lists_skipped_ai_steps_only_when_no_ai_is_configured()
    {
        Assert.Equal(ProcessStage.AiSteps, ProcessStage.SkippedAiSteps(null));
        Assert.Empty(ProcessStage.SkippedAiSteps(new AiConfig { BaseUrl = "http://x", TextModel = "m" }));
    }
}
