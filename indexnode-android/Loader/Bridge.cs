using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode.Hosting;

/// <summary>
/// The WebView bridge without Android: a message {id, m, a} from the page becomes a call on the handler, the answer is {id, ok, result|error}.
/// Only the main frame of the allowed origin may call; anything else is dropped (null reply). Plain net10.0, so it is tested on Linux.
/// </summary>
public sealed class Bridge(Func<string, string, Task<JsonNode?>> handler, Func<string> allowedOrigin, Action<string> log)
{
    public static readonly string[] Methods =
    [
        "getState", "getLog", "runNow", "stopRun", "saveSettings", "importSetup", "pairMedusa", "pairStatus", "checkPluginUpdate", "setUiUrl", "clearLog", "shareLog", "getDiagnostics", "clearDiagnostics", "checkAppUpdate", "installAppUpdate", "reloadUi",
        "checkUiUpdate",           // 0.1.8: the page itself updates from the rolling release (see UiManager)
        "service", "takeShared",   // 0.1.6: the Search tab (see ServiceCalls) and a picture another app shared to this one
    ];

    /// <summary>The reply text to post back, or null when the message was refused.</summary>
    public async Task<string?> HandleAsync(string? messageJson, string? sourceOrigin, bool isMainFrame)
    {
        var o = sourceOrigin?.TrimEnd('/');
        if (!isMainFrame || !string.Equals(o, allowedOrigin()?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) { log("bridge: rejected message from " + (o ?? "(no origin)")); return null; }
        int id = 0;
        try
        {
            using var doc = JsonDocument.Parse(messageJson ?? "");
            id = doc.RootElement.GetProperty("id").GetInt32();
            var m = doc.RootElement.GetProperty("m").GetString() ?? "";
            var arg = doc.RootElement.TryGetProperty("a", out var av) ? (av.ValueKind == JsonValueKind.String ? av.GetString() ?? "" : av.GetRawText()) : "";
            if (!Methods.Contains(m)) throw new InvalidOperationException("unknown method " + m);
            return new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = await handler(m, arg) }.ToJsonString();
        }
        catch (JsonException) { log("bridge: not a JSON message"); return null; }
        catch (KeyNotFoundException) { log("bridge: message without id or method"); return null; }
        catch (Exception e)
        {
            log("bridge error: " + e.GetType().Name + ": " + e.Message);
            return new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = e.GetType().Name + ": " + e.Message }.ToJsonString();
        }
    }

    /// <summary>The origin of a UI URL the bridge may serve: https, or http on loopback. Null otherwise.</summary>
    public static string? OriginOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        bool loopback = u.Host is "127.0.0.1" or "localhost";
        if (u.Scheme != "https" && !(u.Scheme == "http" && loopback)) return null;
        return u.GetLeftPart(UriPartial.Authority);
    }
}
