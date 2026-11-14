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
