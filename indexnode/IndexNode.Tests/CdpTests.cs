using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace IndexNode.Tests;

/// <summary>A fake DevTools endpoint: /json/version plus one WebSocket that answers the commands the client uses.</summary>
public sealed class FakeCdpServer : IDisposable
{
    public int Port { get; }
    public readonly List<string> Methods = new();
    public readonly List<string> Navigated = new();
    private HttpListener? listener;
    private readonly CancellationTokenSource stop = new();

    public FakeCdpServer(int? port = null)
    {
        if (port == null) { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        else Port = port.Value;
    }

    public static int FreePort() => new FakeCdpServer().Port;

    public FakeCdpServer Start()
    {
        listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        });
        return this;
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        if (ctx.Request.IsWebSocketRequest)
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            var ws = wsCtx.WebSocket;
            var buf = new byte[65536];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, stop.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    using var doc = JsonDocument.Parse(buf.AsMemory(0, r.Count));
                    var root = doc.RootElement;
                    var id = root.GetProperty("id").GetInt32();
                    var method = root.GetProperty("method").GetString()!;
                    var p = root.GetProperty("params");
                    var session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                    lock (Methods) Methods.Add(method + (session != null ? "@" + session : ""));
                    async Task Send(object o) => await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text, true, stop.Token);
                    switch (method)
                    {
                        case "Target.createTarget": await Send(new { id, result = new { targetId = "TARGET1" } }); break;
                        case "Target.attachToTarget": await Send(new { id, result = new { sessionId = "SESSION1" } }); break;
                        case "Page.navigate":
                            lock (Navigated) Navigated.Add(p.GetProperty("url").GetString()!);
                            if (p.GetProperty("url").GetString()!.Contains("bad.example")) { await Send(new { id, result = new { frameId = "F", errorText = "net::ERR_NAME_NOT_RESOLVED" } }); break; }
                            await Send(new { id, result = new { frameId = "F" } });
                            await Send(new { method = "Page.loadEventFired", @params = new { timestamp = 1.0 }, sessionId = session });
                            break;
                        case "Runtime.evaluate":
                            var expr = p.GetProperty("expression").GetString()!;
                            if (expr == "throw") await Send(new { id, result = new { exceptionDetails = new { text = "Uncaught" } } });
                            else if (expr == "nonexistent") await Send(new { id, error = new { code = -32000, message = "boom" } });
                            else await Send(new { id, result = new { result = new { type = "string", value = "value of " + expr } } });
                            break;
                        default: await Send(new { id, result = new { } }); break;
                    }
                }
            }
            catch { }
            return;
        }
        if (ctx.Request.Url!.AbsolutePath == "/json/version")
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Browser = "Fake/1", webSocketDebuggerUrl = $"ws://127.0.0.1:{Port}/devtools/browser/fake" }));
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
            return;
        }
        ctx.Response.StatusCode = 404; ctx.Response.Close();
    }

    public void Dispose() { stop.Cancel(); try { listener?.Close(); } catch { } }
}

public class CdpTests
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Target_lifecycle_navigate_and_evaluate()
    {
        using var server = new FakeCdpServer().Start();
        var cfg = new BrowserConfig { Port = server.Port, ProfileDir = Path.Combine(Path.GetTempPath(), "cdp-test-profile") };
        await using (var browser = await CdpBrowser.OpenAsync(cfg, Http, default))
        {
            Assert.False(browser.StartedHeadless); // it was already running: reused, never stopped by us
            var page = await browser.NewPageAsync(default);
            await page.NavigateAsync("https://www.youtube.com/feed/library", default);
            var v = await page.EvaluateAsync("1+1", default);
            Assert.Equal("value of 1+1", v.GetString());
            await page.CloseAsync();
        }
        Assert.Equal(["Target.createTarget", "Target.attachToTarget", "Page.enable@SESSION1", "Page.navigate@SESSION1", "Runtime.evaluate@SESSION1", "Target.closeTarget"], server.Methods);
        Assert.Equal(["https://www.youtube.com/feed/library"], server.Navigated);
    }

    [Fact]
    public async Task Script_exception_protocol_error_and_navigation_failure_surface_as_CdpException()
    {
        using var server = new FakeCdpServer().Start();
        await using var browser = await CdpBrowser.OpenAsync(new BrowserConfig { Port = server.Port }, Http, default);
        var page = await browser.NewPageAsync(default);
        await Assert.ThrowsAsync<CdpException>(() => page.EvaluateAsync("throw", default));
        var e = await Assert.ThrowsAsync<CdpException>(() => page.EvaluateAsync("nonexistent", default));
        Assert.Contains("boom", e.Message);
        await Assert.ThrowsAsync<CdpException>(() => page.NavigateAsync("https://bad.example/", default));
    }

    [Fact]
    public async Task Close_twice_sends_one_closeTarget()
    {
        using var server = new FakeCdpServer().Start();
        await using var browser = await CdpBrowser.OpenAsync(new BrowserConfig { Port = server.Port }, Http, default);
        var page = await browser.NewPageAsync(default);
        await page.CloseAsync(); await page.CloseAsync();
        Assert.Single(server.Methods, m => m == "Target.closeTarget");
    }

    [Fact]
    public async Task Missing_browser_gives_a_clear_error()
    {
        var cfg = new BrowserConfig { Port = FakeCdpServer.FreePort(), Exe = "/nonexistent/brave", ProfileDir = Path.Combine(Path.GetTempPath(), "cdp-test-profile2") };
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => CdpBrowser.OpenAsync(cfg, Http, default));
        Assert.Contains("browser.exe", e.Message);
    }

    [Fact]
    public async Task Starts_the_browser_headless_with_profile_and_port_and_stops_it_afterwards()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "cdp-fake-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "fakebrowser.sh");
        File.WriteAllText(script, "#!/bin/bash\necho $$ > \"" + dir + "/pid\"\necho \"$@\" > \"" + dir + "/args\"\nexec sleep 120\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_101_101);
        var port = FakeCdpServer.FreePort();
        var cfg = new BrowserConfig { Exe = script, ProfileDir = Path.Combine(dir, "profile"), Port = port };
        using var server = new FakeCdpServer(port); // comes up a moment after the browser process started
        _ = Task.Run(async () => { await Task.Delay(600); server.Start(); });
        int pid;
        await using (var b = await CdpBrowser.OpenAsync(cfg, Http, default))
        {
            Assert.True(b.StartedHeadless);
            pid = int.Parse(File.ReadAllText(Path.Combine(dir, "pid")).Trim());
            var args = File.ReadAllText(Path.Combine(dir, "args"));
            Assert.Contains("--headless=new", args);
            Assert.Contains("--user-data-dir=" + Path.Combine(dir, "profile"), args);
            Assert.Contains("--remote-debugging-port=" + port, args);
            Assert.Contains("--no-first-run", args);
            Assert.True(Directory.Exists("/proc/" + pid));
        }
        for (int i = 0; i < 50 && Directory.Exists("/proc/" + pid) && !File.ReadAllText("/proc/" + pid + "/stat").Contains(" Z "); i++) await Task.Delay(100);
        Assert.True(!Directory.Exists("/proc/" + pid) || File.ReadAllText("/proc/" + pid + "/stat").Contains(" Z "));
    }

    [Fact]
    public void Login_command_line_is_visible_with_profile_port_and_sign_in_pages()
    {
        var cfg = new BrowserConfig { ProfileDir = "/p", Port = 9333 };
        var a = BrowserLauncher.Args(cfg, false, "https://accounts.google.com", "https://www.youtube.com");
        Assert.DoesNotContain(a, x => x.StartsWith("--headless"));
        Assert.Equal(["--user-data-dir=/p", "--remote-debugging-port=9333", "--no-first-run", "https://accounts.google.com", "https://www.youtube.com"], a);
        Assert.Contains("--headless=new", BrowserLauncher.Args(cfg, true));
    }

    [Fact]
    public void Browser_defaults()
    {
        var c = new BrowserConfig();
        Assert.Equal(9333, c.ResolvePort());
        Assert.EndsWith(Path.Combine("indexnode", "brave-profile"), c.ResolveProfileDir());
        Assert.Equal("/x", new BrowserConfig { ProfileDir = "/x" }.ResolveProfileDir());
    }
}
