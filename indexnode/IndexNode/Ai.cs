using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// A minimal OpenAI-compatible client over chat completions or the Responses API (ai.api: chat, responses, auto). Nothing calls it from the
/// pipeline yet; `indexnode ai test` is its only user. In auto mode a model that answers "unsupported_api_for_model" on chat is retried once on
/// Responses, and the choice is remembered per model for the life of the client.
/// </summary>
public sealed class AiClient(HttpClient http, AiConfig cfg) : IChat
{
    /// <summary>A 64x64 PNG: a red disc on white, which `ai test` asks the vision model to name the colour of.</summary>
    public static readonly byte[] TestPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAq0lEQVR42u3ZwQ2AQAgFUYrwbP9F2YtaAq4b+MAQC5iXeFCwu/gYAAAAAAAAAACAMOA6zkqAN9fzKAKc6dsZlpK+kWGJ6VsYplD/x2Ai9csG06lfM5hU/YJhGCCg/qthEiCs/pNhDCC43m8AAADABEBKvdPAKwQAAIAaAD6nBQD8UgoAWKsIAMpv5jrsRjtspzvcBzpcaJrcyJpcKZvcieMHAAAAAAAAAJA5DzBSU7R9DBxDAAAAAElFTkSuQmCC");

    public Traffic Counters { get; set; } = Traffic.Current;

    private readonly ConcurrentDictionary<string, string> chosen = new();

    /// <summary>The API in use for a model in this run ("chat" or "responses"); null in auto mode before the first call.</summary>
    public string? ApiFor(string model) => cfg.EffectiveApi == AiConfig.ApiAuto ? chosen.GetValueOrDefault(model) : cfg.EffectiveApi;

    private Uri Endpoint(string path) => new(cfg.BaseUrl!.TrimEnd('/') + path);

    private sealed record Reply(bool Ok, int Status, string Body);

    private async Task<Reply> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint(path)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(cfg.Key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Key);
        Counters.Add(Traffic.AiOut, req.Content!.Headers.ContentLength ?? 0);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        Counters.Add(Traffic.AiIn, Encoding.UTF8.GetByteCount(text));
        return new Reply(res.IsSuccessStatusCode, (int)res.StatusCode, text);
    }

    /// <summary>Output budget per call: describe replies are whole JSON documents (64 cut them off before the closing brace).</summary>
    public const int MaxOutputTokens = 8192;

    /// <summary>A body that is really an error ({"error":{...}} or {"error":"..."}): its message, else null.</summary>
    public static string? ErrorMessage(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject o || o["error"] is not { } e) return null;
            return (e is JsonObject eo ? (eo["code"]?.ToString() + ": " + eo["message"]?.ToString()) : e.ToString()).Trim(' ', ':');
        }
        catch (JsonException) { return null; }
    }

    private static InvalidOperationException Fail(Reply r) =>
        new($"{r.Status} from the AI endpoint: {Redact.Sas(r.Body.Length > 300 ? r.Body[..300] : r.Body)}");

    /// <summary>True for the gateway's "model X is not accessible via the /chat/completions endpoint" (code unsupported_api_for_model).</summary>
    public static bool IsUnsupportedApi(string body)
    {
        if (body.Contains("unsupported_api_for_model", StringComparison.Ordinal)) return true;
        try { return JsonNode.Parse(body)?["error"]?["code"]?.GetValue<string>() == "unsupported_api_for_model"; } catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>Chat content as Responses input content: text -> input_text, image_url -> input_image.</summary>
    private static JsonArray ToResponsesContent(JsonNode content)
    {
        var arr = new JsonArray();
        if (content is not JsonArray parts) { arr.Add(new JsonObject { ["type"] = "input_text", ["text"] = content.GetValue<string>() }); return arr; }
        foreach (var p in parts)
        {
            if (p?["type"]?.GetValue<string>() == "image_url")
                arr.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = p["image_url"]?["url"]?.GetValue<string>() });
            else
                arr.Add(new JsonObject { ["type"] = "input_text", ["text"] = p?["text"]?.GetValue<string>() ?? "" });
        }
        return arr;
    }

    private sealed class UnsupportedApiException : Exception;

    private async Task<string> ViaChatAsync(string model, JsonNode content, CancellationToken ct, bool detectUnsupported)
    {
        var body = new JsonObject { ["model"] = model, ["max_tokens"] = MaxOutputTokens, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content.DeepClone() }) };
        var r = await PostAsync("/chat/completions", body, ct);
        if (!r.Ok || ErrorMessage(r.Body) != null) { if (detectUnsupported && IsUnsupportedApi(r.Body)) throw new UnsupportedApiException(); throw Fail(r); }
        return (JsonNode.Parse(r.Body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "").Trim();
    }

    private async Task<string> ViaResponsesAsync(string model, JsonNode content, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model, ["max_output_tokens"] = MaxOutputTokens,
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = ToResponsesContent(content) }),
        };
        var r = await PostAsync("/responses", body, ct);
        if (!r.Ok || ErrorMessage(r.Body) != null) throw Fail(r);
        var root = JsonNode.Parse(r.Body);
        var sb = new StringBuilder();
        if (root?["output"] is JsonArray output)
            foreach (var item in output)
                if (item?["content"] is JsonArray cs)
                    foreach (var c in cs) if (c?["text"]?.GetValue<string>() is { } t) sb.Append(t);
        return sb.ToString().Trim();
    }

    public async Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct)
    {
        var mode = cfg.EffectiveApi;
        if (mode == AiConfig.ApiAuto && !chosen.ContainsKey(model) && model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) chosen[model] = AiConfig.ApiResponses; // gateway: gpt-* models answer only on /responses
        if (mode == AiConfig.ApiResponses || (mode == AiConfig.ApiAuto && chosen.GetValueOrDefault(model) == AiConfig.ApiResponses)) return await ViaResponsesAsync(model, content, ct);
        if (mode == AiConfig.ApiChat) return await ViaChatAsync(model, content, ct, false);
        try
        {
            var reply = await ViaChatAsync(model, content, ct, true);
            chosen[model] = AiConfig.ApiChat;
            return reply;
        }
        catch (UnsupportedApiException)
        {
            var reply = await ViaResponsesAsync(model, content, ct); // one retry; a failure here is the real error
            chosen[model] = AiConfig.ApiResponses;
            return reply;
        }
    }

    public Task<string> TextAsync(CancellationToken ct) => ChatAsync(cfg.TextModel!, JsonValue.Create("Reply with the single word: ok"), ct);

    public Task<string> VisionAsync(CancellationToken ct) => ChatAsync(cfg.VisionModel!, new JsonArray(
        new JsonObject { ["type"] = "text", ["text"] = "What colour is the shape in this image? Answer with one word." },
        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(TestPng) } }), ct);
}
