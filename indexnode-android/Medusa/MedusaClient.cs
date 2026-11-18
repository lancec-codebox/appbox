using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode.Hosting.Medusa;

public sealed class MedusaException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record PairState(string RequestId, string State, string? Token);

/// <summary>
/// Medusa's local API on 127.0.0.1:38917: /v2 (capabilities, runs, workspaces, device, pairing) with the X-Medusa-Token header.
/// Written against the brief's endpoint list; docs/CAPABILITY-API.md was not on main yet, so response parsing is tolerant (see each Parse*).
/// </summary>
public sealed class MedusaClient
{
    public const string DefaultBase = "http://127.0.0.1:38917";
    private readonly HttpClient http;
    public string BaseUrl { get; }
    public string? Token { get; set; }

    public MedusaClient(HttpClient http, string? baseUrl = null, string? token = null)
    {
        this.http = http; BaseUrl = (baseUrl ?? DefaultBase).TrimEnd('/'); Token = token;
    }

    private HttpRequestMessage Req(HttpMethod m, string path)
    {
        var r = new HttpRequestMessage(m, BaseUrl + path);
        if (!string.IsNullOrEmpty(Token)) r.Headers.TryAddWithoutValidation("X-Medusa-Token", Token);
        return r;
    }

    private static async Task<string> BodyAsync(HttpResponseMessage r) { try { return await r.Content.ReadAsStringAsync(); } catch { return ""; } }

    public async Task<JsonNode?> JsonAsync(HttpMethod m, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = Req(m, path);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        var text = await BodyAsync(resp);
        if (!resp.IsSuccessStatusCode) throw new MedusaException((int)resp.StatusCode, $"{m} {path}: HTTP {(int)resp.StatusCode} {Trim(text)}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    // ---- pairing ----
    public async Task<string> StartPairingAsync(string appName, CancellationToken ct)
    {
        var j = await JsonAsync(HttpMethod.Post, "/v2/pair", new JsonObject { ["appName"] = appName }, ct);
        return (string?)j?["requestId"] ?? (string?)j?["id"] ?? throw new MedusaException(0, "pairing answer has no requestId");
    }

    /// <summary>state: "pending", "approved" (Token set) or "denied".</summary>
    public async Task<PairState> PollPairingAsync(string requestId, CancellationToken ct)
    {
        var j = await JsonAsync(HttpMethod.Get, "/v2/pair/" + Uri.EscapeDataString(requestId), null, ct);
        var token = (string?)j?["token"];
        var state = ((string?)j?["state"] ?? (string?)j?["status"] ?? (token != null ? "approved" : "pending")).ToLowerInvariant();
        if (state is "granted" or "ok") state = "approved";
        return new(requestId, state, token);
    }

    public async Task<JsonNode?> CapabilitiesAsync(CancellationToken ct) => await JsonAsync(HttpMethod.Get, "/v2/capabilities", null, ct);
    public async Task<JsonNode?> DeviceAsync(CancellationToken ct) => await JsonAsync(HttpMethod.Get, "/v2/device", null, ct);

    /// <summary>The /v1 status (no token): is Medusa running at all.</summary>
    public async Task<JsonNode?> V1StatusAsync(CancellationToken ct) => await JsonAsync(HttpMethod.Get, "/v1/status", null, ct);

    // ---- runs ----
    public async Task<string> StartRunAsync(string tool, IReadOnlyList<string> args, string workspace, int timeoutSec, string priority, CancellationToken ct)
    {
        var a = new JsonArray(); foreach (var x in args) a.Add(x);
        var j = await JsonAsync(HttpMethod.Post, "/v2/runs", new JsonObject { ["tool"] = tool, ["args"] = a, ["workspace"] = workspace, ["timeoutSec"] = timeoutSec, ["priority"] = priority }, ct);
        return (string?)j?["id"] ?? (string?)j?["runId"] ?? throw new MedusaException(0, "run answer has no id");
    }

    /// <summary>Final /v2 states: queued, running, succeeded, failed, cancelled, timed_out ("done" is still accepted). Queue is set while waiting.</summary>
    public sealed record RunStatus(string State, int? Position, double? Progress, int? ExitCode, string? Tail, string? QueueMessage = null, string? Weight = null, string? Priority = null)
    {
        public bool Finished => State is "succeeded" or "done" or "failed" or "cancelled" or "timed_out";
        public bool Succeeded => State is "succeeded" or "done";
    }

    public async Task<RunStatus> GetRunAsync(string id, CancellationToken ct)
    {
        var j = await JsonAsync(HttpMethod.Get, "/v2/runs/" + id, null, ct);
        var q = j?["queue"] as JsonObject;
        string? tail = j?["tail"] is JsonArray ta ? string.Join("\n", ta.Select(x => (string?)x)) : (string?)j?["tail"];
        return new((string?)j?["state"] ?? "queued", (int?)q?["position"] ?? (int?)j?["position"], (double?)j?["progress"], (int?)j?["exitCode"], tail,
            (string?)q?["message"], (string?)j?["weight"], (string?)j?["priority"]);
    }

    /// <summary>Log lines from line N on: { next, lines: [ "text" | { stream, text } ] } (a bare array or text/plain with one line per row is accepted too).</summary>
    public async Task<(int Next, List<(string Stream, string Text)> Lines)> GetLogAsync(string id, int from, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Get, $"/v2/runs/{id}/log?from={from}");
        using var resp = await http.SendAsync(req, ct);
        var text = await BodyAsync(resp);
        if (!resp.IsSuccessStatusCode) throw new MedusaException((int)resp.StatusCode, $"GET log: HTTP {(int)resp.StatusCode} {Trim(text)}");
        var lines = new List<(string, string)>();
        int next = from;
        var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (ctype.Contains("json"))
        {
            var j = JsonNode.Parse(text);
            var arr = j as JsonArray ?? j?["lines"] as JsonArray ?? new JsonArray();
            foreach (var l in arr)
            {
                if (l is JsonValue v && v.TryGetValue<string>(out var s)) lines.Add(("stdout", s));
                else if (l is JsonObject o) lines.Add(((string?)o["stream"] ?? "stdout", (string?)o["text"] ?? (string?)o["line"] ?? ""));
            }
            next = (int?)(j as JsonObject)?["next"] ?? from + arr.Count;
        }
        else
        {
            foreach (var l in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) lines.Add(("stdout", l.TrimEnd('\r')));
            next = from + lines.Count;
            if (resp.Headers.TryGetValues("X-Log-Next", out var xn) && int.TryParse(xn.FirstOrDefault(), out var xi)) next = xi;
        }
        return (next, lines);
    }

    public Task CancelRunAsync(string id, CancellationToken ct) => JsonAsync(HttpMethod.Post, $"/v2/runs/{id}/cancel", null, ct);

    // ---- workspaces ----
    public async Task<string> CreateWorkspaceAsync(string name, CancellationToken ct)
    {
        var j = await JsonAsync(HttpMethod.Post, "/v2/workspaces", new JsonObject { ["name"] = name }, ct);
        return (string?)j?["id"] ?? (string?)j?["workspace"] ?? throw new MedusaException(0, "workspace answer has no id");
    }

    public Task DeleteWorkspaceAsync(string id, CancellationToken ct) => JsonAsync(HttpMethod.Delete, "/v2/workspaces/" + id, null, ct);

    /// <summary>[ { path, size } ] or { files: [...] }; a path may be a bare string.</summary>
    public async Task<List<(string Path, long Size)>> ListFilesAsync(string ws, CancellationToken ct)
    {
        var j = await JsonAsync(HttpMethod.Get, $"/v2/workspaces/{ws}/files", null, ct);
        var arr = j as JsonArray ?? j?["files"] as JsonArray ?? new JsonArray();
        var list = new List<(string, long)>();
        foreach (var f in arr)
        {
            if (f is JsonValue v && v.TryGetValue<string>(out var s)) list.Add((s, -1));
            else if (f is JsonObject o) list.Add(((string?)o["path"] ?? (string?)o["name"] ?? "", (long?)o["size"] ?? (long?)o["bytes"] ?? -1));
        }
        return list;
    }

    private static string FileUrl(string ws, string path) => $"/v2/workspaces/{ws}/files/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>Bytes from..to (inclusive, to null = to the end) of a workspace file with a Range request. Returns the bytes and the file's total size when the server says.</summary>
    public async Task<(byte[] Data, long? Total)> GetRangeAsync(string ws, string path, long from, long? to, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Get, FileUrl(ws, path));
        req.Headers.Range = new RangeHeaderValue(from, to);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return ([], resp.Content.Headers.ContentRange?.Length);
        if (!resp.IsSuccessStatusCode) throw new MedusaException((int)resp.StatusCode, $"GET {path}: HTTP {(int)resp.StatusCode}");
        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        long? total = resp.Content.Headers.ContentRange?.Length ?? (resp.StatusCode == HttpStatusCode.OK ? data.Length : null);
        if (resp.StatusCode == HttpStatusCode.OK && from > 0) data = from >= data.Length ? [] : data[(int)from..];   // a server that ignores Range
        return (data, total);
    }

    public async Task PutFileAsync(string ws, string path, byte[] data, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Put, FileUrl(ws, path));
        req.Content = new ByteArrayContent(data);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new MedusaException((int)resp.StatusCode, $"PUT {path}: HTTP {(int)resp.StatusCode} {Trim(await BodyAsync(resp))}");
    }
}
