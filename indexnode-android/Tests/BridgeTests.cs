using System.Text.Json.Nodes;
using IndexNode.Hosting;

namespace IndexNode.Android.Tests;

public class BridgeTests
{
    const string Origin = "https://appassets.androidplatform.net";
    static Bridge Make(List<string> log, List<(string, string)>? calls = null) =>
        new((m, a) => { calls?.Add((m, a)); return m == "boom" ? throw new InvalidOperationException("x") : Task.FromResult<JsonNode?>(new JsonObject { ["m"] = m }); }, () => Origin, log.Add);

    [Fact]
    public async Task A_known_method_from_the_main_frame_is_answered()
    {
        var calls = new List<(string, string)>();
        var r = await Make([], calls).HandleAsync("""{"id":7,"m":"getState","a":""}""", Origin + "/", true);
        var j = JsonNode.Parse(r!)!;
        Assert.Equal(7, (int)j["id"]!); Assert.True((bool)j["ok"]!); Assert.Equal("getState", (string)j["result"]!["m"]!);
        Assert.Single(calls);
    }

    [Fact]
    public async Task A_sub_frame_or_a_foreign_origin_is_dropped()
    {
        var log = new List<string>();
        Assert.Null(await Make(log).HandleAsync("""{"id":1,"m":"getState"}""", Origin, false));
        Assert.Null(await Make(log).HandleAsync("""{"id":1,"m":"getState"}""", "https://evil.example", true));
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public async Task An_unknown_method_and_garbage_are_refused()
    {
        var log = new List<string>();
        var r = JsonNode.Parse((await Make(log).HandleAsync("""{"id":2,"m":"formatDisk"}""", Origin, true))!)!;
        Assert.False((bool)r["ok"]!); Assert.Contains("unknown method", (string)r["error"]!);
        Assert.Null(await Make(log).HandleAsync("not json", Origin, true));
        Assert.Null(await Make(log).HandleAsync("""{"m":"getState"}""", Origin, true));
    }

    [Fact]
    public void Only_https_or_loopback_ui_urls_get_the_bridge()
    {
        Assert.Equal("https://ui.example.com", Bridge.OriginOf("https://ui.example.com/app/index.html"));
        Assert.Equal("http://127.0.0.1:5173", Bridge.OriginOf("http://127.0.0.1:5173/"));
        Assert.Null(Bridge.OriginOf("http://ui.example.com/"));
        Assert.Null(Bridge.OriginOf("file:///sdcard/x.html"));
    }
}
