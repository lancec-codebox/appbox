using System.Text;
using System.Text.Json.Nodes;
namespace IndexNode.Tests;

public class DownloadEndpointTests
{
    static readonly Paths P = new("index/"); static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
    const string Token = "tok";

    static async Task<(Microsoft.AspNetCore.Builder.WebApplication App, string Url, PreCheckServer Server, FakeStore S)> Start(bool service)
    {
        var s = new FakeStore { Clock = () => T0 }; var http = new HttpClient(); var cfg = new NodeConfig(); var tools = new Tools(http, cfg); var dir = Path.Combine(Path.GetTempPath(), "de-" + Guid.NewGuid().ToString("N")[..8]);
        var cache = new SearchIndexCache(Path.Combine(dir, "cache"), TimeSpan.FromMinutes(5));
        var server = new PreCheckServer(new PreCheckAnalyzer(tools, cfg, http, s, P, cache), cache, s, P, new BlockCache(1 << 20), new PreCheckSettings(), 2, "t") { Tools = tools, Token = Token };
        if (service)
        {
            var full = new JobQueue(s, P.DownloadQueue, "svc", () => T0); await full.LoadAsync(default); var work = new JobQueue(s, P.DownloadWork, "svc", () => T0); await work.LoadAsync(default); var proc = new JobQueue(s, P.ProcessQueue, "svc", () => T0); await proc.LoadAsync(default);
            await full.AppendLinesAsync([QLine.Add("v1", "download", new JobDescriptor { Stage = "download", JobId = "v1", AddedAt = "x", Pub = "2026-11-01T00:00:00Z" }, Protocol.Utc(T0))], default);
            server.Downloads = new DownloadService(full, work, proc, (_, _) => Task.FromResult<JsonObject?>(null), 10); server.ContainerUrl = "https://acct.blob.core.windows.net/c";
        }
        var app = server.Build("http://127.0.0.1:0"); await app.StartAsync(); return (app, app.Urls.First().Replace("[::]", "127.0.0.1"), server, s);
    }
    static async Task<(int, JsonObject?)> Call(string url, HttpMethod m, string path, string? bearer, string? body = null)
    {
        using var http = new HttpClient(); using var req = new HttpRequestMessage(m, url + path); if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json"); if (bearer != null) req.Headers.Authorization = new("Bearer", bearer);
        using var res = await http.SendAsync(req); return ((int)res.StatusCode, JsonNode.Parse(await res.Content.ReadAsStringAsync()) as JsonObject);
    }

    [Fact]
    public async Task The_endpoints_answer_401_without_the_token_404_without_the_service_and_otherwise_the_queue_location_a_claim_then_none()
    {
        var off = await Start(false);
        try { Assert.Equal(401, (await Call(off.Url, HttpMethod.Get, "/v1/download/queue", null)).Item1); Assert.Equal(404, (await Call(off.Url, HttpMethod.Get, "/v1/download/queue", Token)).Item1); Assert.Equal(404, (await Call(off.Url, HttpMethod.Post, "/v1/download/next", Token, "{\"node\":\"n\"}")).Item1); }
        finally { await off.App.StopAsync(); }
        var on = await Start(true);
        try
        {
            var (c, q) = await Call(on.Url, HttpMethod.Get, "/v1/download/queue", Token); Assert.Equal(200, c); Assert.Equal("index/jobs/download/work/", (string?)q!["path"]); Assert.Equal("https://acct.blob.core.windows.net/c", (string?)q["container"]); Assert.DoesNotContain("sig", q.ToJsonString());
            Assert.Equal(401, (await Call(on.Url, HttpMethod.Post, "/v1/download/next", "bad", "{\"node\":\"n\"}")).Item1);
            var (c2, n) = await Call(on.Url, HttpMethod.Post, "/v1/download/next", Token, "{\"node\":\"dl1\",\"stages\":[\"download\",\"sample\"]}");
            Assert.Equal(200, c2); Assert.Equal("v1", (string?)n!["subject"]); Assert.Equal("download", (string?)n["stage"]); Assert.False(string.IsNullOrEmpty((string?)n["lease"])); Assert.Equal(1, (int)n["epoch"]!);
            var (_, none) = await Call(on.Url, HttpMethod.Post, "/v1/download/next", Token, "{\"node\":\"dl2\"}"); Assert.True(none!["none"] is not null); Assert.True((int)none["retryAfterSeconds"]! > 0);
            Assert.Equal(400, (await Call(on.Url, HttpMethod.Post, "/v1/download/next", Token, "{}")).Item1);
        }
        finally { await on.App.StopAsync(); }
    }

    [Fact]
    public async Task The_downloader_client_turns_the_answers_into_a_claim_or_null()
    {
        var on = await Start(true);
        try
        {
            using var http = new HttpClient();
            var got = await DownloadServiceClient.NextAsync(http, on.Url, Token, "dl1", ["download"], default); Assert.Equal("v1", got!.Subject);
            Assert.Null(await DownloadServiceClient.NextAsync(http, on.Url, Token, "dl1", ["download"], default));   // none
            await Assert.ThrowsAsync<HttpRequestException>(() => DownloadServiceClient.NextAsync(http, on.Url, "bad", "dl1", ["download"], default));
            Assert.Equal(("https://acct.blob.core.windows.net/c", "index/jobs/download/work/"), (await DownloadServiceClient.QueueAsync(http, on.Url, Token, default))!.Value);
        }
        finally { await on.App.StopAsync(); }
    }
}
