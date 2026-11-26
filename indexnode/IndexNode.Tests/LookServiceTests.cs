using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
namespace IndexNode.Tests;

/// <summary>0.13.62: the routes a person's search page uses (GET /v1/frames, /v1/actors/...). The face models and a real actor index are not here: those were checked against the store before the release.</summary>
public class LookServiceTests
{
    static readonly Paths P = new("index/");
    const string Token = "tok", Id = "vid00000001";

    static Rgb Square(int w, int h, int k)
    {
        var d = new byte[w * h * 3]; Array.Fill(d, (byte)30);
        int x0 = 10 + k % 4 * 40, y0 = 10 + k / 4 * 60;
        for (int y = y0; y < y0 + 40; y++) for (int x = x0; x < x0 + 30; x++) { int p = (y * w + x) * 3; d[p] = 240; d[p + 1] = (byte)(40 * k % 255); d[p + 2] = 60; }
        return new Rgb(d, w, h);
    }

    /// <summary>One 600 s chunk of stored JPEG frames, one every 100 s, each with its byte range.</summary>
    static async Task StoreFrames(FakeStore s)
    {
        var ms = new MemoryStream(); var frames = new List<ChunkFrame>();
        for (int k = 0; k < 6; k++) { var jpg = Imaging.EncodeJpeg(Square(180, 320, k), 90); frames.Add(new ChunkFrame(k, k * 100, k, [], null, ms.Position, jpg.Length)); ms.Write(jpg); }
        var n = FrameChunks.Name(0);
        await s.PutAsync(P.Video(Id, $"frames/{n}.json"), FrameChunks.JpegIndexJson(n, 0, 180, 320, 90, frames), null, null, false, default);
        await s.PutAsync(P.Video(Id, $"frames/{n}.mjpeg"), ms.ToArray(), null, null, false, default, "application/octet-stream");
        await s.PutAsync(P.Video(Id, "frames/manifest.json"), FrameChunks.ManifestJson("frames", 180, 320, 0, 6, [(n, 0, 6, ms.Length)], container: "mjpeg", codec: "jpeg"), null, null, false, default);
    }

    static async Task<(Microsoft.AspNetCore.Builder.WebApplication App, string Url)> Start(FakeStore s, string? searchToken = null)
    {
        var http = new HttpClient(); var cfg = new NodeConfig(); var tools = new Tools(http, cfg);
        var cache = new SearchIndexCache(Path.Combine(Path.GetTempPath(), "look-" + Guid.NewGuid().ToString("N")[..8], "cache"), TimeSpan.FromMinutes(5));
        var server = new PreCheckServer(new PreCheckAnalyzer(tools, cfg, http, s, P, cache), cache, s, P, new BlockCache(1 << 20), new PreCheckSettings(), 2, "t") { Tools = tools, Token = Token, SearchToken = searchToken, Look = new LookServer(s, P, tools, http, cache, 2) };
        var app = server.Build("http://127.0.0.1:0"); await app.StartAsync();
        return (app, app.Urls.First().Replace("[::]", "127.0.0.1"));
    }

    static async Task<(HttpStatusCode Code, JsonObject Body)> Get(string url, string? token)
    {
        using var c = new HttpClient(); var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var r = await c.SendAsync(req); var text = await r.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrEmpty(text), $"{url} answered {(int)r.StatusCode} with an empty body");   // the route's answer was thrown away (a lambda bound to RequestDelegate)
        return (r.StatusCode, (JsonObject)JsonNode.Parse(text)!);
    }

    [Fact]
    public async Task Every_look_route_needs_a_token_and_answers_with_a_body()
    {
        var s = new FakeStore(); await StoreFrames(s);
        var (app, url) = await Start(s, searchToken: "site");
        try
        {
            foreach (var path in new[] { $"/v1/frames?upload={Id}&at=300", "/v1/actors/A000001", $"/v1/actors/shots?upload={Id}&unit={Id}%230" })
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await Get(url + path, null)).Code);
                Assert.Equal(HttpStatusCode.Unauthorized, (await Get(url + path, "wrong")).Code);
            }
            Assert.Equal(HttpStatusCode.OK, (await Get(url + $"/v1/frames?upload={Id}&at=300", Token)).Code);     // the serve token
            Assert.Equal(HttpStatusCode.OK, (await Get(url + $"/v1/frames?upload={Id}&at=300", "site")).Code);    // or the search token
            // no actor index in this store: said plainly, not an empty answer
            var (code, body) = await Get(url + "/v1/actors/A000001", Token);
            Assert.Equal(HttpStatusCode.NotFound, code); Assert.Contains("no actor index", (string)body["error"]!);
            Assert.Equal(HttpStatusCode.NotFound, (await Get(url + $"/v1/actors/shots?upload={Id}&unit={Id}%230", Token)).Code);   // no units file
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Frames_gives_the_moment_and_the_frames_around_it()
    {
        var s = new FakeStore(); await StoreFrames(s);
        var (app, url) = await Start(s);
        try
        {
            var (code, body) = await Get(url + $"/v1/frames?upload={Id}&at=310&around=1&step=100&width=90", Token);
            Assert.Equal(HttpStatusCode.OK, code);
            var frames = ((JsonArray)body["frames"]!).Select(f => (T: (double)f!["t"]!, Hit: (bool)f["hit"]!, Im: Imaging.DecodeJpeg(Convert.FromBase64String((string)f["jpeg"]!)))).ToList();
            Assert.Equal([200.0, 300.0, 400.0], frames.Select(f => f.T).ToArray());                        // in time order: one before, the moment, one after
            Assert.Equal([false, true, false], frames.Select(f => f.Hit).ToArray());
            Assert.All(frames, f => Assert.Equal((90, 160), (f.Im.W, f.Im.H)));                            // no wider than asked for
            Assert.True(SheetCommand.Similarity(frames[1].Im, Thumbs.BoxResize(Square(180, 320, 3), 90, 160)) > 0.9);
            Assert.Equal(Id, (string)body["info"]!["upload"]!); Assert.Equal("0:05:00", (string)((JsonArray)body["frames"]!)[1]!["timestamp"]!);
            // the first frame has nothing before it; neighbours that land on the same stored frame are given once
            var (_, first) = await Get(url + $"/v1/frames?upload={Id}&at=0&around=2&step=30", Token);
            Assert.Equal([0.0, 100.0], ((JsonArray)first["frames"]!).Select(f => (double)f!["t"]!).ToArray());
            // not a video id, and an upload with no stored frames
            Assert.Equal(HttpStatusCode.BadRequest, (await Get(url + "/v1/frames?upload=../x&at=1", Token)).Code);
            Assert.Equal(HttpStatusCode.NotFound, (await Get(url + "/v1/frames?upload=nothere0000&at=1", Token)).Code);
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
