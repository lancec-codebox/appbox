using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace IndexNode;

public sealed class CdpException(string message) : Exception(message);

/// <summary>A page the node can drive: navigate, evaluate, close. CdpPage is the real one; tests use fakes.</summary>
public interface IPage
{
    Task NavigateAsync(string url, CancellationToken ct);
    /// <summary>Evaluates an expression (promises are awaited) and returns its value.</summary>
    Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct);
    Task CloseAsync();
}

/// <summary>Raw Chrome DevTools Protocol over one WebSocket (no Playwright: its Node driver may be blocked by App Control).</summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> calls = new();
    private readonly List<(string Method, string? Session, TaskCompletionSource<JsonElement> Tcs)> waiters = new();
    private readonly CancellationTokenSource stop = new();
    private Task? reader;
    private int nextId;

    public static async Task<CdpConnection> ConnectAsync(Uri url, CancellationToken ct)
    {
        var c = new CdpConnection();
        await c.ws.ConnectAsync(url, ct);
        c.reader = Task.Run(c.ReadLoop);
        return c;
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, string? sessionId = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls[id] = tcs;
        var msg = new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new { } };
        if (sessionId != null) msg["sessionId"] = sessionId;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task;
        }
        finally { calls.TryRemove(id, out _); }
    }

    /// <summary>Registers for the next event; do it before the command that causes it, so the event cannot be missed.</summary>
    public Task<JsonElement> ExpectEvent(string method, string? sessionId)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (waiters) waiters.Add((method, sessionId, tcs));
        return tcs.Task;
    }

    private async Task ReadLoop()
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, stop.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement.Clone();
                if (root.TryGetProperty("id", out var idEl) && calls.TryGetValue(idEl.GetInt32(), out var tcs))
                {
                    if (root.TryGetProperty("error", out var err)) tcs.TrySetException(new CdpException(err.TryGetProperty("message", out var m) ? m.GetString() ?? "cdp error" : err.ToString()));
                    else tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res : default);
                }
                else if (root.TryGetProperty("method", out var meth))
                {
                    var name = meth.GetString();
                    var session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                    var ev = root.TryGetProperty("params", out var p) ? p : default;
                    lock (waiters)
                        foreach (var w in waiters.Where(w => w.Method == name && (w.Session == null || w.Session == session)).ToList())
                        { w.Tcs.TrySetResult(ev); waiters.Remove(w); }
                }
            }
        }
        catch (Exception) { }
        finally
        {
            var dead = new CdpException("DevTools connection closed");
            foreach (var c in calls.Values) c.TrySetException(dead);
            lock (waiters) foreach (var w in waiters) w.Tcs.TrySetException(dead);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { if (ws.State == WebSocketState.Open) await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", new CancellationTokenSource(1000).Token); } catch { }
        ws.Dispose();
        if (reader != null) try { await reader; } catch { }
    }
}

public sealed class CdpPage(CdpConnection conn, string targetId, string sessionId) : IPage
{
    public string TargetId { get; } = targetId;
    public string SessionId { get; } = sessionId;
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(60);
    private bool closed;

    public async Task NavigateAsync(string url, CancellationToken ct)
    {
        var loaded = conn.ExpectEvent("Page.loadEventFired", SessionId);
        var r = await conn.SendAsync("Page.navigate", new { url }, SessionId, ct);
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("errorText", out var et)) throw new CdpException("navigation failed: " + et.GetString());
        await loaded.WaitAsync(LoadTimeout, ct);
    }

    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
    {
        var r = await conn.SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, SessionId, ct);
        if (r.TryGetProperty("exceptionDetails", out var ex))
            throw new CdpException("script error: " + (ex.TryGetProperty("text", out var t) ? t.GetString() : ex.ToString()));
        return r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) ? v.Clone() : default;
    }

    public async Task CloseAsync()
    {
        if (closed) return;
        closed = true;
        try { await conn.SendAsync("Target.closeTarget", new { targetId = TargetId }, null, new CancellationTokenSource(5000).Token); } catch { }
    }
}

public static class BrowserLauncher
{
    public static string[] Args(BrowserConfig cfg, bool headless, params string[] urls)
    {
        var a = new List<string>();
        if (headless) a.Add("--headless=new");
        a.Add("--user-data-dir=" + cfg.ResolveProfileDir());
        a.Add("--remote-debugging-port=" + cfg.ResolvePort());
        a.Add("--no-first-run");
        if (headless && OperatingSystem.IsLinux() && Environment.UserName == "root") a.Add("--no-sandbox");
        a.AddRange(urls);
        return a.ToArray();
    }

    public static Process Start(BrowserConfig cfg, bool headless, params string[] urls)
    {
        var psi = new ProcessStartInfo(cfg.ResolveExe()) { UseShellExecute = false, CreateNoWindow = headless };
        foreach (var x in Args(cfg, headless, urls)) psi.ArgumentList.Add(x);
        Directory.CreateDirectory(cfg.ResolveProfileDir());
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start " + cfg.ResolveExe());
    }
}

/// <summary>
/// The dedicated browser profile over CDP. Uses the browser already running with the debugging port (e.g. after "browser login");
/// otherwise starts it headless with the same profile and port and stops it again on dispose. Never touches the normal profile.
/// </summary>
public sealed class CdpBrowser : IAsyncDisposable
{
    private readonly CdpConnection conn;
    private readonly Process? started;
    public bool StartedHeadless => started != null;

    private CdpBrowser(CdpConnection c, Process? p) { conn = c; started = p; }

    public static async Task<Uri?> TryGetEndpointAsync(HttpClient http, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cts.Token));
            return doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var u) && u.GetString() is { } s ? new Uri(s) : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    public static async Task<CdpBrowser> OpenAsync(BrowserConfig cfg, HttpClient http, CancellationToken ct, TimeSpan? startTimeout = null)
    {
        var port = cfg.ResolvePort();
        var ep = await TryGetEndpointAsync(http, port, ct);
        Process? proc = null;
        if (ep == null)
        {
            Log.Info($"browser: not running, starting it headless (profile {cfg.ResolveProfileDir()}, port {port})");
            try { proc = BrowserLauncher.Start(cfg, true, "about:blank"); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { throw new InvalidOperationException($"cannot start the browser '{cfg.ResolveExe()}' ({e.Message}); set browser.exe"); }
            var deadline = DateTimeOffset.UtcNow + (startTimeout ?? TimeSpan.FromSeconds(30));
            while (ep == null && DateTimeOffset.UtcNow < deadline)
            {
                if (proc.HasExited) throw new InvalidOperationException("the browser exited at start (is the profile open in a browser without the debugging port?)");
                await Task.Delay(250, ct);
                ep = await TryGetEndpointAsync(http, port, ct);
            }
            if (ep == null) { Proc.Kill(proc); throw new InvalidOperationException("the browser did not open its debugging port in time"); }
        }
        try { return new CdpBrowser(await CdpConnection.ConnectAsync(ep, ct), proc); }
        catch { if (proc != null) Proc.Kill(proc); throw; }
    }

    public async Task<CdpPage> NewPageAsync(CancellationToken ct, string url = "about:blank")
    {
        var created = await conn.SendAsync("Target.createTarget", new { url }, null, ct);
        var targetId = created.GetProperty("targetId").GetString()!;
        var attached = await conn.SendAsync("Target.attachToTarget", new { targetId, flatten = true }, null, ct);
        var session = attached.GetProperty("sessionId").GetString()!;
        await conn.SendAsync("Page.enable", null, session, ct);
        return new CdpPage(conn, targetId, session);
    }

    public async ValueTask DisposeAsync()
    {
        await conn.DisposeAsync();
        if (started != null) { Proc.Kill(started); try { started.WaitForExit(5000); } catch { } started.Dispose(); }
    }
}
