using System.Text.Json.Nodes;
using Android.Content;
using IndexNode.Hosting;
using IndexNode.Hosting.Medusa;

namespace IndexNode.Host;

/// <summary>The one place that ties the pieces together for the process: settings, host, plugin manager, Medusa, a run. The UI and WorkManager both call it.</summary>
public sealed class NodeRuntime
{
    public const string HostVersion = "0.1.3";
    public const string AppName = "IndexNode";
    private static NodeRuntime? instance;
    public static NodeRuntime Get(Context ctx) => instance ??= new NodeRuntime(ctx.ApplicationContext!);

    private readonly Context ctx;
    public Prefs Prefs { get; }
    public AndroidHost Host { get; }
    public PluginManager Plugins { get; }
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly object gate = new();
    private CancellationTokenSource? runCts;
    public string State { get; private set; } = "idle";
    public string? Job { get; private set; }
    public double? Progress { get; private set; }
    public string? LastResult { get; private set; }
    public string? LastError { get; private set; }
    public string? PairRequest { get; private set; }
    public event Action? Changed;

    private NodeRuntime(Context ctx)
    {
        this.ctx = ctx;
        Prefs = new Prefs(ctx);
        if (Prefs.Get("nodeId") == null) Prefs.Set("nodeId", NodeIdentity.NewId(Android.OS.Build.Model ?? "phone"));
        Host = new AndroidHost(ctx, Prefs, CreateTools, (t, j, p) => { State = t; Job = j; Progress = p; Changed?.Invoke(); });
        Directory.CreateDirectory(Host.FilesDir);
        Plugins = new PluginManager(Path.Combine(ctx.FilesDir!.AbsolutePath, "plugins"), LoadBundled, HostVersion);
    }

    private (Manifest, byte[])? LoadBundled()
    {
        try
        {
            using var m = ctx.Assets!.Open("plugin/manifest.json"); using var d = ctx.Assets.Open("plugin/IndexNode.Plugin.dll");
            using var ms = new MemoryStream(); d.CopyTo(ms);
            using var sr = new StreamReader(m);
            return (PluginLoader.ParseManifest(sr.ReadToEnd()), ms.ToArray());
        }
        catch (Exception e) { Host?.Log("no bundled plugin: " + e.Message); return null; }
    }

    // ---- Medusa ----
    private MedusaClient Medusa() => new(http, null, Prefs.MedusaToken);
    /// <summary>"v2" (paired, setting use_v2 not off) or "v1" (the plain-video fallback, behind the setting v1Fallback).</summary>
    public string MedusaMode() => Prefs.MedusaToken != null && Prefs.Get("medusaApi") != "v1" ? "v2" : "v1";
    private ITools CreateTools()
    {
        var sandbox = Path.Combine(ctx.CacheDir!.AbsolutePath, "tools"); Directory.CreateDirectory(sandbox);
        if (MedusaMode() == "v2") return new MedusaV2Tools(Medusa(), sandbox) { OnQueue = m => { if (m != null) Host.SetStatus(m, null, null); } };
        if (Prefs.Bool("v1Fallback", false)) return new MedusaV1Tools(http, sandbox);
        throw new InvalidOperationException("Medusa is not paired (/v2). Pair it, or switch on the /v1 fallback in the settings (plain videos only).");
    }

    public async Task<JsonNode> PairAsync()
    {
        var c = Medusa();
        if (PairRequest == null) { PairRequest = await c.StartPairingAsync(AppName, default); Host.Log("Medusa pairing requested: approve it in Medusa"); }
        var s = await c.PollPairingAsync(PairRequest, default);
        if (s.State == "approved" && s.Token != null) { Prefs.MedusaToken = s.Token; PairRequest = null; Host.Log("Medusa paired"); }
        else if (s.State == "denied") { PairRequest = null; Host.Log("Medusa pairing denied"); }
        return new JsonObject { ["state"] = s.State };
    }

    public async Task<JsonNode> MedusaStatusAsync()
    {
        var o = new JsonObject { ["mode"] = MedusaMode(), ["paired"] = Prefs.MedusaToken != null, ["pending"] = PairRequest != null };
        try { o["running"] = (await Medusa().V1StatusAsync(default))?["version"]?.ToString() ?? "yes"; } catch (Exception e) { o["running"] = false; o["error"] = HostRedact.Text(e.Message, Prefs.MedusaToken); }
        return o;
    }

    // ---- setup ----
    public void ImportSetup(string text)
    {
        var s = SetupSettings.Parse(text);
        if (s.Sas == null) throw new FormatException("this setup has no storage key (made with --no-sas)");
        Prefs.Sas = s.Sas;
        if (!string.IsNullOrWhiteSpace(s.NodeName)) Prefs.Set("nodeName", s.NodeName);
        Prefs.Set("wifiOnly", "true"); Prefs.Set("chargingOnly", s.Conditions == "wifi+charging" ? "true" : "false");
        Prefs.Set("dailyCap", s.DailyCap.ToString()); Prefs.Set("budget", s.Budget?.ToString()); Prefs.Set("maxHeight", s.MaxHeight.ToString());
        Prefs.Set("checkUrl", string.IsNullOrWhiteSpace(s.CheckUrl) ? null : s.CheckUrl.Trim()); Prefs.CheckToken = string.IsNullOrWhiteSpace(s.CheckToken) ? null : s.CheckToken;   // 0.1.4: a setup without a check server clears an earlier one
        Host.Log($"setup imported: store {Host.StoreUrl}, name {Host.NodeName}, conditions {s.Conditions}, daily cap {s.DailyCap}, check server {Prefs.Get("checkUrl") ?? "(none)"}");
    }

    // ---- plugin ----
    public async Task<UpdateResult> CheckPluginAsync()
    {
        await Plugins.EnsureLoadedAsync(Host);
        return await Plugins.CheckAndUpdateAsync(http, Prefs.Get("pluginUrl") ?? PluginManager.RollingManifestUrl, Host);
    }

    // ---- run ----
    public RunConditions Conditions => RunConditions.FromSettings(Prefs.Get);
    private readonly RunGuard guard = new();
    public bool Running => guard.Running;

    /// <summary>One node run. force = "Run now": the conditions are still checked, the daily cap too (the owner's rules hold).</summary>
    public async Task<JsonNode> RunAsync(bool force, CancellationToken outer)
    {
        CancellationTokenSource cts;
        // Run now during a run, or a WorkManager start while one is going, is not a failure: say so and do nothing
        if (!guard.TryEnter()) { Host.Log(RunGuard.Busy); return new JsonObject { ["skipped"] = RunGuard.Busy }; }
        lock (gate) runCts = cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        try
        {
            if (Prefs.Sas == null) throw new InvalidOperationException("not set up: import the node-setup QR or file first");
            var why = Conditions.WhyNot(Host.GetDeviceInfo(), Prefs.DoneToday);
            if (why != null) { State = "waiting: " + why; Host.Log("not running: " + why); Changed?.Invoke(); return new JsonObject { ["skipped"] = why }; }
            Host.SetStatus("checking the plugin", null, null);
            var upd = await CheckPluginAsync();   // before each run
            Host.Log($"plugin check: {upd.Outcome} {upd.Version} {upd.Detail}");
            var plugin = await Plugins.EnsureLoadedAsync(Host);
            int cap = Conditions.Remaining(Prefs.DoneToday);
            var args = new JsonObject
            {
                ["stages"] = new JsonArray("download"), ["once"] = true, ["maxJobs"] = Math.Min(cap, Prefs.Int("runMax", 5)),
                ["noRefresh"] = Prefs.Bool("noRefresh", false), ["maxHeight"] = Prefs.Int("maxHeight", 480), ["budget"] = Prefs.Get("budget") is { } b && int.TryParse(b, out var bn) ? bn : null,
            };
            Host.SetStatus("running", null, 0);
            var res = await plugin.Plugin!.RunAsync(Host, args.ToJsonString(), cts.Token);
            var started = (int?)JsonNode.Parse(res)?["jobsStarted"] ?? 0;
            Prefs.AddDone(started);
            LastResult = res; LastError = null;
            Host.SetStatus("idle", null, null);
            return JsonNode.Parse(res)!;
        }
        catch (OperationCanceledException) { Host.Log("run stopped"); Host.SetStatus("stopped", null, null); throw; }
        catch (Exception e) { LastError = HostRedact.Text(e.GetType().Name + ": " + e.Message, Prefs.Secrets); Host.Log("run failed: " + LastError); Host.SetStatus("failed", null, null); throw; }
        finally { lock (gate) { runCts = null; } cts.Dispose(); guard.Exit(); Changed?.Invoke(); }
    }

    public void Stop() { lock (gate) runCts?.Cancel(); }

    /// <summary>The diagnostics page: the last crash, the recent log (the rolling file, so it survives a restart) and the text behind "Copy diagnostics". The SAS and tokens never appear.</summary>
    public JsonObject DiagnosticsJson()
    {
        var s = StateJson();
        string? S(JsonNode? n) => n?.ToString();
        var settings = new Dictionary<string, string?>(); foreach (var (k, v) in s["settings"]!.AsObject()) settings[k] = S(v);
        var state = new Dictionary<string, string?>(); foreach (var (k, v) in s) if (k is not ("settings" or "device")) state[k] = S(v);
        foreach (var (k, v) in s["device"]!.AsObject()) state["device." + k] = S(v);
        settings["storeConfigured"] = (Prefs.Sas != null).ToString();
        var crash = CrashRecorder.LastCrash(); var log = CrashRecorder.LogFileLines(); var secrets = Prefs.Secrets;
        var device = (Android.OS.Build.Manufacturer + " " + Android.OS.Build.Model).Trim(); var os = Android.OS.Build.VERSION.Release + " (API " + (int)Android.OS.Build.VERSION.SdkInt + ")";
        var report = Diagnostics.Report(HostVersion, Plugins.Current?.Version, device, os, settings, state, crash, log, secrets);
        return new JsonObject { ["lastCrash"] = crash == null ? null : HostRedact.Text(crash, secrets), ["log"] = new JsonArray(Diagnostics.TrimLog(log, 100, 400, secrets).Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()), ["report"] = report };
    }

    public JsonObject StateJson()
    {
        var d = Host.GetDeviceInfo(); var c = Conditions;
        return new JsonObject
        {
            ["state"] = State, ["job"] = Job, ["progress"] = Progress, ["running"] = Running,
            ["hostVersion"] = HostVersion, ["pluginVersion"] = Plugins.Current?.Version, ["pluginSource"] = Plugins.Current?.Source, ["pluginError"] = Plugins.LastError,
            ["nodeId"] = Host.NodeId, ["nodeName"] = Host.NodeName, ["storeUrl"] = Host.StoreUrl, ["configured"] = Prefs.Sas != null,
            ["doneToday"] = Prefs.DoneToday, ["lastResult"] = LastResult, ["lastError"] = LastError,
            ["device"] = new JsonObject { ["transport"] = d.Transport, ["vpn"] = d.Vpn, ["via"] = d.Via, ["activeTransport"] = d.ActiveTransport, ["validated"] = d.Validated, ["metered"] = d.Metered, ["charging"] = d.Charging, ["battery"] = d.BatteryPercent, ["freeMb"] = d.FreeBytes / (1 << 20), ["model"] = d.Model },
            ["whyNot"] = c.WhyNot(d, Prefs.DoneToday),
            ["settings"] = new JsonObject
            {
                ["wifiOnly"] = c.WifiOnly, ["chargingOnly"] = c.ChargingOnly, ["dailyCap"] = c.DailyCap, ["budget"] = Prefs.Get("budget"), ["runMax"] = Prefs.Int("runMax", 5),
                ["maxHeight"] = Prefs.Int("maxHeight", 480), ["checkUrl"] = Prefs.Get("checkUrl"), ["checkToken"] = Prefs.CheckToken != null ? "set" : null, ["uiUrl"] = Prefs.Get("uiUrl"), ["v1Fallback"] = Prefs.Bool("v1Fallback", false), ["noRefresh"] = Prefs.Bool("noRefresh", false), ["medusaApi"] = Prefs.Get("medusaApi") ?? "auto",
                ["pluginUrl"] = Prefs.Get("pluginUrl") ?? PluginManager.RollingManifestUrl, ["periodic"] = Prefs.Bool("periodic", true), ["periodMinutes"] = Prefs.Int("periodMinutes", 60),
            },
            ["medusaMode"] = MedusaMode(),
        };
    }

    public void SaveSettings(JsonObject s)
    {
        foreach (var k in new[] { "wifiOnly", "chargingOnly", "v1Fallback", "periodic", "noRefresh" }) if (s[k] != null) Prefs.Set(k, (bool)s[k]! ? "true" : "false");
        foreach (var k in new[] { "dailyCap", "runMax", "maxHeight", "periodMinutes" }) if (s[k] != null) Prefs.Set(k, ((int)s[k]!).ToString());
        if (s.ContainsKey("budget")) Prefs.Set("budget", s["budget"]?.ToString() is { Length: > 0 } bs ? bs : null);
        if (s["medusaApi"] != null) Prefs.Set("medusaApi", (string)s["medusaApi"]! is "v1" ? "v1" : null);
        if (s["pluginUrl"] != null) { var u = (string)s["pluginUrl"]!; Prefs.Set("pluginUrl", u == PluginManager.RollingManifestUrl || u == "" ? null : u); }
        Host.Log("settings saved");
    }
}
