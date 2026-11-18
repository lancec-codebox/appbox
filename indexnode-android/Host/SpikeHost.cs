using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spike.Contract;
using Spike.Loader;

namespace Host;

/// <summary>The only logic in the app: capabilities for plugins (IHost) and the plugin lifecycle behind the JS bridge.</summary>
public sealed class SpikeHost : IHost
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    readonly List<string> _log = new();
    readonly object _lock = new();
    LoadedPlugin? _plugin;
    readonly List<(string ver, WeakReference wr)> _zombies = new();
    public Action<string>? StatusChanged;
    public string Status { get; private set; } = "idle";
    public string CacheDir { get; }
    public long FirstScreenMs { get; set; }
    public Func<object?>? MemoryProbe;

    public SpikeHost(string cacheDir) { CacheDir = cacheDir; Directory.CreateDirectory(Path.Combine(cacheDir, "plugins")); }

    // ---- IHost ----
    public void Log(string m)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + m;
        lock (_lock) { _log.Add(line); if (_log.Count > 500) _log.RemoveAt(0); }
        Android.Util.Log.Info("spike", m);
    }
    public void SetStatus(string t) { Status = t; StatusChanged?.Invoke(t); }

    public Task<HostResponse> GetAsync(string url, IDictionary<string, string>? headers = null, int maxBytes = 1 << 20) =>
        SendAsync("GET", url, null, headers, maxBytes);
    public Task<HostResponse> PutAsync(string url, byte[] body, string contentType = "application/octet-stream") =>
        SendCore("PUT", url, new ByteArrayContent(body) { Headers = { ContentType = new(contentType) } }, null, 1 << 20);
    public Task<HostResponse> SendAsync(string method, string url, string? body, IDictionary<string, string>? headers = null, int maxBytes = 1 << 20) =>
        SendCore(method, url, body == null ? null : new StringContent(body, Encoding.UTF8, headers != null && headers.TryGetValue("Content-Type", out var ct) ? ct : "text/plain"), headers, maxBytes);

    async Task<HostResponse> SendCore(string method, string url, HttpContent? content, IDictionary<string, string>? headers, int maxBytes)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), url) { Content = content };
        if (headers != null) foreach (var (k, v) in headers) if (!k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation(k, v);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        await using var s = await resp.Content.ReadAsStreamAsync();
        var ms = new MemoryStream();
        var buf = new byte[16384];
        while (ms.Length < maxBytes)
        {
            int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, maxBytes - ms.Length)));
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }
        var hdr = string.Join("\n", resp.Headers.Concat(resp.Content.Headers).Select(h => h.Key + ": " + string.Join(",", h.Value)));
        return new HostResponse { Status = (int)resp.StatusCode, ContentType = resp.Content.Headers.ContentType?.ToString() ?? "", Headers = hdr, Body = ms.ToArray() };
    }

    // ---- bridge methods: each returns a JSON value ----
    public async Task<JsonNode?> HandleAsync(string method, string arg)
    {
        switch (method)
        {
            case "loadPlugin": return await LoadAsync(arg);
            case "runPlugin": return await RunAsync(arg);
            case "unload": return Unload();
            case "getLog": lock (_lock) return new JsonArray(_log.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            case "getInfo": return Info();
            default: throw new InvalidOperationException("unknown method " + method);
        }
    }

    async Task<JsonNode?> LoadAsync(string manifestUrl)
    {
        var swAll = Stopwatch.StartNew();
        string? swapped = null;
        if (_plugin != null) { swapped = _plugin.Version; Unload(); }   // swap: v1 goes before v2 arrives
        Log("fetching " + manifestUrl);
        var sw = Stopwatch.StartNew();
        var (m, dll) = await PluginLoader.FetchAsync(Http, manifestUrl);
        var fetchMs = sw.ElapsedMilliseconds;
        File.WriteAllBytes(Path.Combine(CacheDir, "plugins", "plugin-" + m.version + ".dll"), dll);   // cache copy; the load itself is from memory
        sw.Restart();
        _plugin = PluginLoader.VerifyAndLoad(m, dll);
        Log($"loaded plugin v{m.version}: {dll.Length} bytes, fetch {fetchMs} ms, verify+load {sw.ElapsedMilliseconds} ms (type load {_plugin.LoadMs} ms)");
        SetStatus("loaded v" + m.version);
        return new JsonObject { ["version"] = m.version, ["bytes"] = dll.Length, ["fetchMs"] = fetchMs, ["verifyLoadMs"] = sw.ElapsedMilliseconds, ["unloadedFirst"] = swapped, ["memory"] = Mem() };
    }

    async Task<JsonNode?> RunAsync(string args)
    {
        var p = _plugin ?? throw new InvalidOperationException("no plugin loaded");
        var sw = Stopwatch.StartNew();
        var res = await p.Plugin!.RunAsync(this, args);
        Log($"run v{p.Version} done in {sw.ElapsedMilliseconds} ms");
        JsonNode? parsed; try { parsed = JsonNode.Parse(res); } catch { parsed = JsonValue.Create(res); }
        return new JsonObject { ["ms"] = sw.ElapsedMilliseconds, ["result"] = parsed };
    }

    JsonNode? Unload()
    {
        if (_plugin == null) return new JsonObject { ["unloaded"] = false };
        var ver = _plugin.Version;
        var before = Mem();
        var wr = DropAndUnload();
        lock (_zombies) _zombies.Add((ver, wr));
        var sw = Stopwatch.StartNew();
        int i = 0;
        for (; i < 10 && wr.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(50); }
        Log($"unloaded v{ver}: collected={!wr.IsAlive} after {i} GC rounds ({sw.ElapsedMilliseconds} ms)");
        return new JsonObject { ["unloaded"] = true, ["version"] = ver, ["contextCollected"] = !wr.IsAlive, ["gcRounds"] = i, ["before"] = before, ["after"] = Mem() };
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    WeakReference DropAndUnload() { var wr = _plugin!.Unload(); _plugin = null; return wr; }

    JsonObject Mem()
    {
        var mi = new Android.OS.Debug.MemoryInfo();
        Android.OS.Debug.GetMemoryInfo(mi);
        return new JsonObject { ["pssKb"] = mi.TotalPss, ["managedKb"] = GC.GetTotalMemory(false) / 1024, ["assembliesLoaded"] = AppDomain.CurrentDomain.GetAssemblies().Length };
    }

    JsonNode? Info()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        string unloaded; lock (_zombies) unloaded = string.Join(",", _zombies.Select(z => "v" + z.ver + (z.wr.IsAlive ? ":alive" : ":collected")));
        return InfoCore(unloaded);
    }

    JsonNode? InfoCore(string unloaded) => new JsonObject
    {
        ["unloadedContexts"] = unloaded,
        ["firstScreenMs"] = FirstScreenMs,
        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["arch"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        ["monoVm"] = Type.GetType("Mono.RuntimeStructs") != null || System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.Contains("Mono"),
        ["dynamicCode"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
        ["plugin"] = _plugin?.Version,
        ["memory"] = Mem(),
        ["cacheDir"] = CacheDir,
    };
}
