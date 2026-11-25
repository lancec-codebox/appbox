using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>The downloader's only two calls to the download service: where the work queue is (bootstrap) and one claimed item when the work queue has nothing ready.</summary>
public static class DownloadServiceClient
{
    public static async Task<NextClaim?> NextAsync(HttpClient http, string url, string? token, string node, string[] stages, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/v1/download/next") { Content = new StringContent(new JsonObject { ["node"] = node, ["stages"] = new JsonArray(stages.Select(x => (JsonNode?)x).ToArray()) }.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var res = await http.SendAsync(req, cts.Token);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"the download service answered {(int)res.StatusCode}");
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(cts.Token)) as JsonObject;
        if (j == null || j["none"] is not null || (string?)j["subject"] is not { } s) return null;
        return new NextClaim(s, (string?)j["stage"] ?? "download", (int)(j["epoch"] ?? 0), (string?)j["lease"] ?? "");
    }

    /// <summary>0.13.49: POST /v1/download/sampled: the download of the upload this node just sampled, claimed for it, or why there is none. A service older than 0.13.49 answers 404 (an HttpRequestException with that status).</summary>
    public static async Task<(NextClaim? Claim, string Outcome)> SampledAsync(HttpClient http, string url, string? token, string node, string id, TimeSpan timeout, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/v1/download/sampled") { Content = new StringContent(new JsonObject { ["node"] = node, ["id"] = id }.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        using var res = await http.SendAsync(req, cts.Token);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"the download service answered {(int)res.StatusCode}", null, res.StatusCode);
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(cts.Token)) as JsonObject;
        var outcome = (string?)j?["outcome"] ?? "no answer";
        if (j == null || (string?)j["subject"] is not { } s) return (null, outcome);
        return (new NextClaim(s, (string?)j["stage"] ?? "download", (int)(j["epoch"] ?? 0), (string?)j["lease"] ?? ""), outcome);
    }

    public static async Task<(string Container, string Path)?> QueueAsync(HttpClient http, string url, string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/v1/download/queue");
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct); if (!res.IsSuccessStatusCode) return null;
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject; return j == null ? null : ((string?)j["container"] ?? "", (string?)j["path"] ?? "");
    }
}
