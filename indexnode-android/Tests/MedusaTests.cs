using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IndexNode.Hosting.Medusa;

namespace IndexNode.Android.Tests;

/// <summary>A fake Medusa on loopback: /v2 with a token, a workspace with Range files, runs that finish after a few polls; /v1 jobs and media.</summary>
public sealed class FakeMedusa : IDisposable
{
    readonly HttpListener l = new();
    public readonly string Url;
    public const string Token = "tok-123";
    public readonly Dictionary<string, byte[]> Files = new();
    public readonly List<string> Requests = new();
    public readonly List<JsonNode> RunBodies = new();
    public int PairPolls, RunPolls, CancelCalls, WorkspaceDeletes;
    public bool ApprovePairing = true, FailRun;
    public bool IgnoreRange;
    public byte[] V1Video = Encoding.UTF8.GetBytes("V1VIDEO");
    readonly CancellationTokenSource stop = new();

    public FakeMedusa()
    {
        int port; using (var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { t.Start(); port = ((IPEndPoint)t.LocalEndpoint).Port; }
        Url = $"http://127.0.0.1:{port}"; l.Prefixes.Add(Url + "/"); l.Start();
        _ = Task.Run(async () => { while (!stop.IsCancellationRequested) { try { var c = await l.GetContextAsync(); _ = Task.Run(() => Handle(c)); } catch { return; } } });
    }

    static void Send(HttpListenerContext c, int status, string body, string type = "application/json") { var b = Encoding.UTF8.GetBytes(body); Send(c, status, b, type); }
    static void Send(HttpListenerContext c, int status, byte[] b, string type) { c.Response.StatusCode = status; c.Response.ContentType = type; c.Response.OutputStream.Write(b); c.Response.Close(); }

    void Handle(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath; var m = c.Request.HttpMethod;
        lock (Requests) Requests.Add(m + " " + path);
        string body = new StreamReader(c.Request.InputStream).ReadToEnd();
        if (path.StartsWith("/v2/") && c.Request.Headers["X-Medusa-Token"] != Token && !path.StartsWith("/v2/pair")) { Send(c, 401, "{}"); return; }
        if (path == "/v2/pair" && m == "POST") { Send(c, 200, """{"requestId":"r1"}"""); return; }
        if (path == "/v2/pair/r1") { PairPolls++; Send(c, 200, ApprovePairing && PairPolls >= 2 ? $$"""{"state":"approved","token":"{{Token}}"}""" : ApprovePairing ? """{"state":"pending"}""" : """{"state":"denied"}"""); return; }
        if (path == "/v2/capabilities") { Send(c, 200, """{"api":2,"medusa":"1.10.0","tools":[{"name":"yt-dlp","available":true}],"limits":{"priorities":["interactive","background"],"maxConcurrentHeavyRuns":1,"maxConcurrentLightRuns":2}}"""); return; }
        if (path == "/v2/workspaces" && m == "POST") { Send(c, 200, """{"workspace":"w1"}"""); return; }
        if (path == "/v2/workspaces/w1" && m == "DELETE") { WorkspaceDeletes++; Send(c, 200, "{}"); return; }
        if (path == "/v2/workspaces/w1/files" && m == "GET") { Send(c, 200, new JsonArray(Files.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Key, ["size"] = f.Value.Length }).ToArray()).ToJsonString()); return; }
        if (path.StartsWith("/v2/workspaces/w1/files/"))
        {
            var name = Uri.UnescapeDataString(path["/v2/workspaces/w1/files/".Length..]);
            if (m == "PUT") { Files[name] = Encoding.UTF8.GetBytes(body); Send(c, 200, "{}"); return; }
            if (!Files.TryGetValue(name, out var data)) { Send(c, 404, "{}"); return; }
            var rng = c.Request.Headers["Range"];
            if (rng != null && !IgnoreRange)
            {
                var parts = rng["bytes=".Length..].Split('-'); long from = long.Parse(parts[0]); long to = parts[1] == "" ? data.Length - 1 : Math.Min(long.Parse(parts[1]), data.Length - 1);
                if (from >= data.Length) { c.Response.Headers["Content-Range"] = $"bytes */{data.Length}"; Send(c, 416, "", "text/plain"); return; }
                c.Response.Headers["Content-Range"] = $"bytes {from}-{to}/{data.Length}";
                Send(c, 206, data[(int)from..(int)(to + 1)], "application/octet-stream"); return;
            }
            Send(c, 200, data, "application/octet-stream"); return;
        }
        if (path == "/v2/runs" && m == "POST")
        {
            RunBodies.Add(JsonNode.Parse(body)!);
            var args = RunBodies[^1]["args"]!.AsArray().Select(x => (string)x!).ToList();
            if (!FailRun) { var o = args[args.IndexOf("-o") + 1]; Files[o.Replace("%(ext)s", "mp4")] = new byte[10_000_000]; }
            Send(c, 200, """{"runId":"run1","state":"queued"}"""); return;
        }
        if (path == "/v2/runs/run1") { RunPolls++; Send(c, 200, RunPolls < 2 ? """{"state":"queued","weight":"heavy","priority":"background","queue":{"position":1,"waitingFor":["index-node download 12ab"],"message":"Waiting for index-node download 12ab"}}""" : RunPolls < 3 ? """{"state":"running","weight":"heavy","priority":"background","progress":42.5}""" : FailRun ? """{"state":"failed","exitCode":1,"tail":["ERROR: boom"]}""" : """{"state":"succeeded","exitCode":0}"""); return; }
        if (path == "/v2/runs/run1/log") { c.Response.Headers["X-Log-Next"] = RunPolls < 2 ? "1" : "2"; c.Response.Headers["X-Run-State"] = "running"; Send(c, 200, RunPolls < 2 ? "[download]  10.0% of 9MiB\n" : "[download] 100% of 9MiB\n", "text/plain"); return; }
        if (path == "/v2/runs/run1/cancel") { CancelCalls++; Send(c, 200, "{}"); return; }
        // ---- /v1 ----
        if (path == "/v1/jobs" && m == "POST") { Send(c, 200, """{"id":"j1"}"""); return; }
        if (path == "/v1/jobs/j1") { Send(c, 200, """{"id":"j1","state":"done","progress":1}"""); return; }
        if (path == "/v1/jobs/j1/result") { Send(c, 200, """{"mediaId":"m1","title":"T","durationSec":12.5,"videoId":"abcdefghijk","extractor":"youtube","video":{"path":"/v1/media/m1/video","contentType":"video/mp4","bytes":7}}"""); return; }
        if (path == "/v1/media/m1/video") { Send(c, 200, V1Video, "video/mp4"); return; }
        if (path == "/v1/media/m1" && m == "DELETE") { Send(c, 200, """{"deleted":true}"""); return; }
        Send(c, 404, "{}");
    }

    public void Dispose() { stop.Cancel(); l.Close(); }
}

public class MedusaTests
{
    static string Tmp() => Directory.CreateTempSubdirectory("md").FullName;

    [Fact]
    public async Task Pairing_returns_the_token_and_v2_calls_need_it()
    {
        using var m = new FakeMedusa(); var c = new MedusaClient(new HttpClient(), m.Url);
        var id = await c.StartPairingAsync("IndexNode", default);
        Assert.Equal("pending", (await c.PollPairingAsync(id, default)).State);
        var st = await c.PollPairingAsync(id, default);
        Assert.Equal("approved", st.State); Assert.Equal(FakeMedusa.Token, st.Token);
        await Assert.ThrowsAsync<MedusaException>(() => c.CapabilitiesAsync(default));   // no token yet: 401
        c.Token = st.Token;
        Assert.NotNull(await c.CapabilitiesAsync(default));
    }

    [Fact]
    public async Task A_denied_pairing_has_no_token()
    {
        using var m = new FakeMedusa { ApprovePairing = false }; var c = new MedusaClient(new HttpClient(), m.Url);
        var s = await c.PollPairingAsync(await c.StartPairingAsync("IndexNode", default), default);
        Assert.Equal("denied", s.State); Assert.Null(s.Token);
    }

    [Fact]
    public async Task A_run_goes_queued_to_done_with_progress_lines_and_a_range_read_file()
    {
        using var m = new FakeMedusa(); var tools = new MedusaV2Tools(new MedusaClient(new HttpClient(), m.Url, FakeMedusa.Token), Tmp()) { PollInterval = TimeSpan.FromMilliseconds(10) };
        var seen = new List<string>(); var queue = new List<string>(); tools.OnQueue = q => { if (q != null) queue.Add(q); };
        await using (var ws = await tools.CreateWorkspaceAsync("job1", default))
        {
            var r = await tools.RunAsync(ws, "yt-dlp", ["-o", "video.%(ext)s", "https://youtu.be/x"], default, seen.Add);
            Assert.Equal(0, r.ExitCode);
            Assert.Contains(seen, l => l.Contains("10.0%")); Assert.Contains(seen, l => l.Contains("100%"));
            Assert.Equal("w1", (string)m.RunBodies[0]["workspace"]!); Assert.Equal("yt-dlp", (string)m.RunBodies[0]["tool"]!);
            Assert.Equal("background", (string)m.RunBodies[0]["priority"]!);   // the node always sends background
            Assert.Contains("Waiting for index-node download 12ab", queue);
            Assert.True(await ws.ExistsAsync("video.mp4", default));
            Assert.Equal(10_000_000, await ws.SizeAsync("video.mp4", default));
            var local = await ws.LocalPathAsync("video.mp4", default);
            Assert.Equal(10_000_000, new FileInfo(local).Length);   // three 4 MB Range reads
            Assert.True(m.Requests.Count(x => x.StartsWith("GET /v2/workspaces/w1/files/video.mp4")) >= 3);
            await ws.WriteTextAsync("note.txt", "hi", default);
            Assert.Equal("hi", await ws.ReadTextAsync("note.txt", default));
            Assert.Equal(["video.mp4"], await ws.ListAsync("", "*.mp4", default));
        }
        Assert.Equal(1, m.WorkspaceDeletes);
    }

    [Fact]
    public async Task A_failed_run_reports_the_exit_code_and_the_tail()
    {
        using var m = new FakeMedusa { FailRun = true }; var tools = new MedusaV2Tools(new MedusaClient(new HttpClient(), m.Url, FakeMedusa.Token), Tmp()) { PollInterval = TimeSpan.FromMilliseconds(10) };
        await using var ws = await tools.CreateWorkspaceAsync("job2", default);
        var r = await tools.RunAsync(ws, "yt-dlp", ["-o", "video.%(ext)s", "https://youtu.be/x"], default);
        Assert.Equal(1, r.ExitCode); Assert.Contains("boom", r.Stderr);
    }

    [Fact]
    public async Task Cancelling_a_run_cancels_it_on_the_phone()
    {
        using var m = new FakeMedusa(); var tools = new MedusaV2Tools(new MedusaClient(new HttpClient(), m.Url, FakeMedusa.Token), Tmp()) { PollInterval = TimeSpan.FromMilliseconds(200) };
        await using var ws = await tools.CreateWorkspaceAsync("job3", default);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.RunAsync(ws, "yt-dlp", ["-o", "video.%(ext)s", "https://youtu.be/x"], cts.Token));
        Assert.Equal(1, m.CancelCalls);
    }

    [Fact]
    public async Task A_server_that_ignores_Range_still_gives_the_whole_file()
    {
        using var m = new FakeMedusa { IgnoreRange = true }; m.Files["a.bin"] = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var c = new MedusaClient(new HttpClient(), m.Url, FakeMedusa.Token);
        var (d, total) = await c.GetRangeAsync("w1", "a.bin", 100, 199, default);
        Assert.Equal(1000, total); Assert.Equal(900, d.Length); Assert.Equal(100, d[0]);   // the caller truncates by what it needs; position arithmetic stays right
        var (empty, t2) = await new MedusaClient(new HttpClient(), m.Url, FakeMedusa.Token).GetRangeAsync("w1", "a.bin", 5000, null, default);
        Assert.Empty(empty); Assert.Equal(1000, t2);
    }

    [Fact]
    public async Task The_v1_fallback_downloads_a_plain_video_and_nothing_else()
    {
        using var m = new FakeMedusa(); var tools = new MedusaV1Tools(new HttpClient(), Tmp(), m.Url) { PollInterval = TimeSpan.FromMilliseconds(10) };
        await using var ws = await tools.CreateWorkspaceAsync("v1job", default);
        var r = await tools.RunAsync(ws, "yt-dlp", ["-f", "bv*[height<=480]+ba", "-o", "video.%(ext)s", "https://www.youtube.com/watch?v=abcdefghijk"], default);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("V1VIDEO", await ws.ReadTextAsync("video.mp4", default));
        Assert.Contains("12.5", (await tools.RunAsync(ws, "ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", "video.mp4"], default)).Stdout);
        Assert.Equal(0, (await tools.RunAsync(ws, "yt-dlp", ["--skip-download", "--write-subs", "https://x/y"], default)).ExitCode);   // no subtitles over /v1: not an error
        Assert.NotEqual(0, (await tools.RunYtDlpAsync(["--flat-playlist", "https://x/y"], default)).ExitCode);   // listings are not supported
        Assert.Contains("DELETE /v1/media/m1", m.Requests);
    }
}
