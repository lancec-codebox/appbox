using System.Text;
using System.Text.Json.Nodes;
using IndexNode.Hosting;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.1.6: the Search tab's calls to the service (Loader/ServiceCalls.cs): which request each operation becomes, where the service is, and the wait while it wakes.</summary>
public class ServiceCallsTests
{
    static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    sealed class Fake
    {
        public List<(string Method, string Url, string? Auth, string? Type, byte[]? Body)> Calls = [];
        public Queue<Func<string, HostResponse>> Answers = new();
        public int StoreReads; public string? StoreText; public List<TimeSpan> Waits = [];
        public (string? Url, string? Token) Saved = ("https://svc.example/", "tok");
        public static HostResponse R(int status, string json) => new() { Status = status, ContentType = "application/json", Body = Encoding.UTF8.GetBytes(json) };
        public ServiceCalls Make() => new((m, u, h, b, max, ct) =>
        {
            Calls.Add((m, u, h?.GetValueOrDefault("Authorization"), h?.GetValueOrDefault("Content-Type"), b));
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue()(u) : R(200, "{\"ok\":true}"));
        }, () => Saved, _ => { StoreReads++; return Task.FromResult(StoreText); }, _ => { }) { Delay = (t, _) => { Waits.Add(t); return Task.CompletedTask; } };
    }

    static string Arg(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    [Fact]
    public async Task Each_operation_is_one_fixed_route_with_the_token()
    {
        var f = new Fake(); var s = f.Make();
        await s.CallAsync(Arg(new { op = "frames", upload = "vru2pDOii0I", at = 476.2, around = 2, step = 3, width = 240 }), default);
        await s.CallAsync(Arg(new { op = "actor", actor = "A011117" }), default);
        await s.CallAsync(Arg(new { op = "shots", upload = "vru2pDOii0I", unit = "vru2pDOii0I#2", count = 6 }), default);
        await s.CallAsync(Arg(new { op = "search", image = Convert.ToBase64String(Jpeg) }), default);
        await s.CallAsync(Arg(new { op = "actors", image = Convert.ToBase64String(Jpeg), upload = "vru2pDOii0I" }), default);
        Assert.Equal(["GET https://svc.example/v1/frames?upload=vru2pDOii0I&at=476.2&around=2&step=3&width=240", "GET https://svc.example/v1/actors/A011117",
            "GET https://svc.example/v1/actors/shots?upload=vru2pDOii0I&unit=vru2pDOii0I%232&count=6&width=240", "POST https://svc.example/v1/search", "POST https://svc.example/v1/actors/search"], f.Calls.Select(c => c.Method + " " + c.Url).ToArray());
        Assert.All(f.Calls, c => Assert.Equal("Bearer tok", c.Auth));
        // the picture travels as multipart "image" with its own type; a search asks for the bars to be trimmed, an actor search names the drama when it is known
        var search = Encoding.Latin1.GetString(f.Calls[3].Body!); var actors = Encoding.Latin1.GetString(f.Calls[4].Body!);
        Assert.StartsWith("multipart/form-data; boundary=", f.Calls[3].Type);
        Assert.Contains("name=\"image\"; filename=\"picture.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n" + Encoding.Latin1.GetString(Jpeg), search);
        Assert.Contains("name=\"trim\"\r\n\r\ntrue", search); Assert.Contains("name=\"upload\"\r\n\r\nvru2pDOii0I", actors); Assert.DoesNotContain("name=\"trim\"", actors);
        Assert.Equal(0, f.StoreReads);   // the setup names the service: the store is not asked
    }

    [Fact]
    public async Task Nothing_but_the_known_operations_and_plain_ids_gets_through()
    {
        var s = new Fake().Make();
        foreach (var bad in new object[] { new { op = "delete" }, new { op = "frames", upload = "../x", at = 1 }, new { op = "actor", actor = "A1/../../v1/check" }, new { op = "frames", at = 1 }, new { op = "search", image = "***" }, new { op = "search", image = "" }, new { } })
            await Assert.ThrowsAsync<ArgumentException>(() => s.CallAsync(Arg(bad), default));
        // numbers are kept inside what the service takes
        var (_, path, _, _) = ServiceCalls.Build(JsonNode.Parse(Arg(new { op = "frames", upload = "abc", at = -5, around = 99, step = 0, width = 5000 }))!.AsObject());
        Assert.Equal("/v1/frames?upload=abc&at=0&around=6&step=1&width=720", path);
    }

    [Fact]
    public async Task A_sleeping_service_is_waited_for_and_a_refusal_is_said_plainly()
    {
        var f = new Fake(); var s = f.Make();
        f.Answers.Enqueue(_ => throw new HttpRequestException("connection refused"));
        f.Answers.Enqueue(_ => Fake.R(503, "{\"error\":\"starting\",\"ready\":false,\"retryAfterSeconds\":2}"));
        f.Answers.Enqueue(_ => Fake.R(200, "{\"hits\":[]}"));
        var r = await s.CallAsync(Arg(new { op = "search", image = Convert.ToBase64String(Jpeg) }), default);
        Assert.NotNull(r!["hits"]); Assert.Equal(3, f.Calls.Count); Assert.Equal([5.0, 2.0], f.Waits.Select(w => w.TotalSeconds).ToArray()); Assert.Null(s.Waiting);
        // a wrong token and a bad request are not waited on
        f.Answers.Enqueue(_ => Fake.R(401, "{\"error\":\"unauthorized\"}"));
        var e1 = await Assert.ThrowsAsync<InvalidOperationException>(() => s.CallAsync(Arg(new { op = "actor", actor = "A1" }), default)); Assert.Contains("token", e1.Message);
        f.Answers.Enqueue(_ => Fake.R(404, "{\"error\":\"the actor index (v189) has no actor A1\"}"));
        var e2 = await Assert.ThrowsAsync<InvalidOperationException>(() => s.CallAsync(Arg(new { op = "actor", actor = "A1" }), default)); Assert.Equal("the actor index (v189) has no actor A1", e2.Message);
        Assert.Equal(5, f.Calls.Count);
        // the status line never waits: one try
        f.Answers.Enqueue(_ => Fake.R(503, "{\"ready\":false}"));
        await Assert.ThrowsAsync<HttpRequestException>(() => s.CallAsync(Arg(new { op = "status" }), default)); Assert.Equal(6, f.Calls.Count);
    }

    [Fact]
    public async Task The_store_names_the_service_when_the_setup_does_not_or_its_address_is_dead()
    {
        // no check server in the setup: index/service.json is read once and used
        var f = new Fake { Saved = (null, null), StoreText = "{\"v\":1,\"url\":\"https://new.example\",\"token\":\"t2\"}" }; var s = f.Make();
        await s.CallAsync(Arg(new { op = "actor", actor = "A1" }), default); await s.CallAsync(Arg(new { op = "actor", actor = "A2" }), default);
        Assert.Equal(["https://new.example/v1/actors/A1", "https://new.example/v1/actors/A2"], f.Calls.Select(c => c.Url).ToArray()); Assert.Equal("Bearer t2", f.Calls[0].Auth); Assert.Equal(1, f.StoreReads);
        // neither: said plainly
        var none = new Fake { Saved = (null, null), StoreText = null }.Make();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => none.CallAsync(Arg(new { op = "actor", actor = "A1" }), default)); Assert.Contains("No service is set up", e.Message);
        // the saved address never answers: after the wait the store's address is tried
        var g = new Fake { StoreText = "{\"url\":\"https://moved.example\",\"token\":\"t3\"}" }; var sg = g.Make(); sg.WakeFor = TimeSpan.FromSeconds(11);
        for (int i = 0; i < 3; i++) g.Answers.Enqueue(_ => throw new HttpRequestException("no route to host"));
        g.Answers.Enqueue(u => { Assert.StartsWith("https://moved.example/", u); return Fake.R(200, "{\"actor\":\"A1\"}"); });
        Assert.Equal("A1", (string?)(await sg.CallAsync(Arg(new { op = "actor", actor = "A1" }), default))!["actor"]);
        Assert.Equal("Bearer t3", g.Calls[^1].Auth);
        // a file that names something that is not https is ignored
        Assert.False(ServiceCalls.OkUrl("http://evil.example")); Assert.True(ServiceCalls.OkUrl("https://x.example")); Assert.True(ServiceCalls.OkUrl("http://127.0.0.1:5312"));
    }
}
