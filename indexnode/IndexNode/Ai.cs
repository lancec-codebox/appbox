using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>A minimal OpenAI-compatible chat client. Nothing calls it from the pipeline yet; `indexnode ai test` is its only user.</summary>
public sealed class AiClient(HttpClient http, AiConfig cfg)
{
    public static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEElEQVR4nGP8z8Dwn4EIAABeKAH/p9OZZAAAAABJRU5ErkJggg==");

    private Uri Endpoint() => new(cfg.BaseUrl!.TrimEnd('/') + "/chat/completions");

    public async Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = model, ["max_tokens"] = 64, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }) };
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint()) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(cfg.Key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Key);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)res.StatusCode} from the AI endpoint: {Redact.Sas(text.Length > 300 ? text[..300] : text)}");
        var reply = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        return (reply ?? "").Trim();
    }

    public Task<string> TextAsync(CancellationToken ct) => ChatAsync(cfg.TextModel!, JsonValue.Create("Reply with the single word: ok"), ct);

    public Task<string> VisionAsync(CancellationToken ct) => ChatAsync(cfg.VisionModel!, new JsonArray(
        new JsonObject { ["type"] = "text", ["text"] = "What colour is this tiny image? One word." },
        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(TinyPng) } }), ct);
}
