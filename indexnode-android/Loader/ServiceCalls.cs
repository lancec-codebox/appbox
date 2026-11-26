using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode.Hosting;

/// <summary>A host HTTP call (AndroidHost.HttpAsync): no credential is added by the host; this class adds the service token.</summary>
public delegate Task<HostResponse> HostHttp(string method, string url, IReadOnlyDictionary<string, string>? headers, byte[]? body, int maxBytes, CancellationToken ct);

/// <summary>
/// 0.1.6: the Search tab's calls to `indexnode serve` (screenshot search, the frames around a hit, actor lookup), without Android so it is tested on Linux.
/// The page sends {"op": ..., ...}; only the operations in <see cref="Ops"/> exist, each mapped here to one fixed route, so the page cannot make the host call anything else.
/// Where the service is: the check server of the setup (address and token in the app's preferences), or, when the setup has none or it cannot be reached, the address the store names in
/// index/service.json ({"url": "https://...", "token": "..."}; read once per app start, only when needed). The service sleeps when idle: a call that meets "starting" (503 with Retry-After),
/// a gateway error or no connection is tried again until <see cref="WakeFor"/> has passed.
/// </summary>
public sealed class ServiceCalls(HostHttp http, Func<(string? Url, string? Token)> saved, Func<CancellationToken, Task<string?>> storeFile, Action<string> log)
{
    public static readonly string[] Ops = ["status", "search", "actors", "frames", "actor", "shots", "holds", "holdframes", "verdict"];
    /// <summary>The file's path inside the store container; the host prefixes the container's own path.</summary>
    public const string StoreFile = "/index/service.json";
    public const int MaxImageBytes = 8 * 1024 * 1024, MaxAnswerBytes = 16 * 1024 * 1024;
    public TimeSpan WakeFor { get; set; } = TimeSpan.FromSeconds(150);
    /// <summary>Waits between tries (tests replace it).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
    /// <summary>Progress for the page while a call waits ("Waking the service…").</summary>
    public string? Waiting { get; private set; }
    (string? Url, string? Token)? fromStore;

    /// <summary>The address and token index/service.json names; (null, null) when the store has no such file or it does not parse. Read once.</summary>
    public async Task<(string? Url, string? Token)> FromStoreAsync(CancellationToken ct)
    {
        if (fromStore is { } have) return have;
        (string? Url, string? Token) got = (null, null);
        try
        {
            if (await storeFile(ct) is { Length: > 0 } text && JsonNode.Parse(text) is JsonObject o && Str(o, "url") is { } u && OkUrl(u)) got = (u.TrimEnd('/'), Str(o, "token"));
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException) { log("service: the store's service.json could not be read: " + e.Message); }
        fromStore = got; return got;
    }

    static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    /// <summary>https, or http on this phone (a test service); nothing else is called.</summary>
    public static bool OkUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || (u.Scheme == "http" && u.Host is "127.0.0.1" or "localhost"));

    /// <summary>The request an operation becomes: method, path with query, and the multipart body when it carries a picture.</summary>
    public static (string Method, string Path, byte[]? Body, string? ContentType) Build(JsonObject a)
    {
        // 0.1.8, the general form: {"method": "GET"|"POST", "path": "/v1/...", "json": {...} | "image": base64 [, "fields": {...}]}. Any route of the service under /v1/, and nothing else:
        // the host and the token are the app's, the page only names the path. A new route of the service needs no new app.
        if (a["op"] == null && Str(a, "path") is { } p)
        {
            string method = (Str(a, "method") ?? "GET").ToUpperInvariant();
            if (method is not ("GET" or "POST")) throw new ArgumentException("method: GET or POST");
            if (!p.StartsWith("/v1/", StringComparison.Ordinal) || p.Length > 2000 || p.Contains("..") || p.Contains("//") || p.Any(c => c <= ' ' || c > '~' || c is '\\' or '#' or '@')) throw new ArgumentException("path: a route under /v1/ is expected");
            if (a["image"] != null)
            {
                if (method != "POST") throw new ArgumentException("a picture goes with POST");
                byte[] img; try { img = Convert.FromBase64String(Str(a, "image") ?? ""); } catch (FormatException) { throw new ArgumentException("the picture is not base64"); }
                if (img.Length == 0 || img.Length > MaxImageBytes) throw new ArgumentException($"the picture is empty or over {MaxImageBytes / (1024 * 1024)} MB");
                var fs = (a["fields"] as JsonObject)?.Where(kv => kv.Value is JsonValue && System.Text.RegularExpressions.Regex.IsMatch(kv.Key, "^[A-Za-z0-9_]{1,40}$")).Select(kv => (kv.Key, kv.Value!.ToString().Replace("\r", " ").Replace("\n", " "))).ToList() ?? [];
                var (mb, mt) = Multipart(img, fs);
                return (method, p, mb, mt);
            }
            if (a["json"] is JsonNode j) return method == "POST" ? (method, p, Encoding.UTF8.GetBytes(j.ToJsonString()), "application/json") : throw new ArgumentException("a body goes with POST");
            return (method, p, null, null);
        }
        string op = Str(a, "op") ?? throw new ArgumentException("no op");
        string Id(string k) { var v = Str(a, k) ?? throw new ArgumentException($"{op}: no {k}"); if (v.Length > 64 || v.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '#'))) throw new ArgumentException($"{op}: {k} is not an id"); return v; }
        string Num(string k, double d, double min, double max) { double v = a[k] is JsonValue jv && jv.TryGetValue<double>(out var x) ? x : d; return Math.Clamp(v, min, max).ToString("0.###", CultureInfo.InvariantCulture); }
        switch (op)
        {
            case "status": return ("GET", "/v1/health", null, null);
            case "frames": return ("GET", $"/v1/frames?upload={Uri.EscapeDataString(Id("upload"))}&at={Num("at", 0, 0, 1e6)}&around={Num("around", 2, 0, 6)}&step={Num("step", 3, 1, 600)}&width={Num("width", 320, 64, 720)}", null, null);
            case "actor": return ("GET", "/v1/actors/" + Uri.EscapeDataString(Id("actor")), null, null);
            case "shots": return ("GET", $"/v1/actors/shots?upload={Uri.EscapeDataString(Id("upload"))}&unit={Uri.EscapeDataString(Id("unit"))}&count={Num("count", 6, 1, 12)}&width={Num("width", 240, 64, 720)}", null, null);
            // 0.1.7: the owner's review of AI holds. "verdict" is the one operation that changes the store (through the service: the same as `indexnode ai set`)
            case "holds": return ("GET", "/v1/ai/holds", null, null);
            case "holdframes": return ("GET", $"/v1/ai/frames?upload={Uri.EscapeDataString(Id("upload"))}&count={Num("count", 6, 2, 12)}&width={Num("width", 240, 64, 720)}", null, null);
            case "verdict":
            {
                string v = Str(a, "verdict") is "real" or "ai" ? Str(a, "verdict")! : throw new ArgumentException("verdict: real or ai");
                var o = new JsonObject { ["upload"] = Id("upload"), ["verdict"] = v, ["reason"] = Str(a, "reason") is { } r ? r[..Math.Min(200, r.Length)] : null, ["by"] = Str(a, "by") is { } b ? b[..Math.Min(60, b.Length)] : null };
                return ("POST", "/v1/ai/verdict", Encoding.UTF8.GetBytes(o.ToJsonString()), "application/json");
            }
            case "search":
            case "actors":
            {
                byte[] image; try { image = Convert.FromBase64String(Str(a, "image") ?? throw new ArgumentException($"{op}: no picture")); } catch (FormatException) { throw new ArgumentException($"{op}: the picture is not base64"); }
                if (image.Length == 0 || image.Length > MaxImageBytes) throw new ArgumentException($"{op}: the picture is empty or over {MaxImageBytes / (1024 * 1024)} MB");
                var fields = new List<(string, string)>();
                if (op == "search") { fields.Add(("trim", "true")); fields.Add(("top", Num("top", 40, 1, 200))); }   // a phone screenshot has the player's bars around the picture
                else if (a["upload"] != null) fields.Add(("upload", Id("upload")));
                var (body, type) = Multipart(image, fields);
                return ("POST", op == "search" ? "/v1/search" : "/v1/actors/search", body, type);
            }
            default: throw new ArgumentException("unknown op " + op);
        }
    }

    /// <summary>multipart/form-data with the picture as "image" (its type from its first bytes) and the given text fields.</summary>
    public static (byte[] Body, string ContentType) Multipart(byte[] image, IEnumerable<(string Name, string Value)> fields)
    {
        string b = "----indexnode" + Guid.NewGuid().ToString("N");
        var (type, ext) = image.Length > 3 && image[0] == 0xFF && image[1] == 0xD8 ? ("image/jpeg", "jpg") : image.Length > 8 && image[0] == 0x89 && image[1] == 'P' ? ("image/png", "png") : image.Length > 12 && image[8] == 'W' && image[9] == 'E' ? ("image/webp", "webp") : ("application/octet-stream", "bin");
        var ms = new MemoryStream();
        void W(string s) { var x = Encoding.UTF8.GetBytes(s); ms.Write(x, 0, x.Length); }
        foreach (var (n, v) in fields) W($"--{b}\r\nContent-Disposition: form-data; name=\"{n}\"\r\n\r\n{v}\r\n");
        W($"--{b}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"picture.{ext}\"\r\nContent-Type: {type}\r\n\r\n"); ms.Write(image, 0, image.Length); W($"\r\n--{b}--\r\n");
        return (ms.ToArray(), "multipart/form-data; boundary=" + b);
    }

    /// <summary>One operation. Returns the service's JSON; a refusal or an unreachable service throws with a sentence the page can show.</summary>
    public async Task<JsonNode?> CallAsync(string argJson, CancellationToken ct)
    {
        var a = JsonNode.Parse(argJson) as JsonObject ?? throw new ArgumentException("the call is not a JSON object");
        var (method, path, body, contentType) = Build(a);
        var first = saved(); if (!OkUrl(first.Url)) first = (null, null);
        var where = first.Url != null ? first : await FromStoreAsync(ct);
        if (where.Url == null) throw new InvalidOperationException("No service is set up: scan a setup QR that carries the check server (Setup, step 3).");
        bool status = Str(a, "op") == "status" || Str(a, "path") == "/v1/health";
        try { return await SendAsync(where, method, path, body, contentType, status, ct); }
        catch (HttpRequestException) when (first.Url != null && !status)
        {   // the saved address did not answer in all that time: the store may name a newer one (the service was moved)
            var alt = await FromStoreAsync(ct);
            if (alt.Url == null || string.Equals(alt.Url, first.Url!.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) throw;
            log("service: the saved address did not answer; using the one the store names");
            return await SendAsync(alt, method, path, body, contentType, status, ct);
        }
        finally { Waiting = null; }
    }

    async Task<JsonNode?> SendAsync((string? Url, string? Token) where, string method, string path, byte[]? body, string? contentType, bool once, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["Accept"] = "application/json" };
        if (!string.IsNullOrEmpty(where.Token)) headers["Authorization"] = "Bearer " + where.Token;
        if (contentType != null) headers["Content-Type"] = contentType;
        double waited = 0; string last = "";   // the waits are added up (not the clock): the time a call itself took is the service working, not sleeping
        for (int attempt = 1; ; attempt++)
        {
            double wait = 5;
            try
            {
                var r = await http(method, where.Url!.TrimEnd('/') + path, headers, body, MaxAnswerBytes, ct);
                JsonNode? j = null; try { j = r.Body.Length > 0 ? JsonNode.Parse(r.Text) : null; } catch (JsonException) { }
                if (r.Status is >= 200 and < 300) return j ?? throw new InvalidOperationException("The service answered with nothing.");
                string said = j is JsonObject jo && Str(jo, "error") is { } e ? e : "HTTP " + r.Status;
                if (r.Status == 401) throw new InvalidOperationException("The service refused this phone's token. Scan a new setup QR (Setup, step 3).");
                if (r.Status is not (502 or 503 or 504)) throw new InvalidOperationException(said);
                last = said;
                if (j is JsonObject w && w["retryAfterSeconds"] is JsonValue rv && rv.TryGetValue<double>(out var ra)) wait = Math.Clamp(ra, 1, 15);
            }
            catch (HttpRequestException e) { last = e.Message; }
            if (once || waited + wait > WakeFor.TotalSeconds) throw new HttpRequestException($"The service did not answer{(once ? "" : $" in {WakeFor.TotalSeconds:0} s")}: {last}");
            Waiting = $"Waking the service… ({waited:0} s)";
            if (attempt == 1) log("service: not ready (" + last + "); waiting for it");
            await Delay(TimeSpan.FromSeconds(wait), ct); waited += wait;
        }
    }
}
