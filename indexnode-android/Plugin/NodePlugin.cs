using System.Text.Json;
using System.Text.Json.Nodes;
using IndexNode.Hosting;

namespace IndexNode.Plugin;

/// <summary>Log lines of IndexNode.Core (already redacted by Log.Info) go to the host.</summary>
internal sealed class HostLogWriter(IHost host) : TextWriter
{
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void WriteLine(string? value) => host.Log(value ?? "");
    public override void Write(char value) { }
    public override void Write(string? value) { if (!string.IsNullOrEmpty(value)) host.Log(value.TrimEnd('\n', '\r')); }
}

/// <summary>
/// The node loop for phones. 0.1.2: it is the downloader node of the desktop (<see cref="DownloaderNode"/> in IndexNode.Core: the same stages, handlers, sample step and check-server calls);
/// only the tools (Medusa instead of local yt-dlp and ffmpeg), the store transport (the host adds the SAS) and the settings (the host's) differ.
/// </summary>
public sealed class NodePlugin : IPlugin
{
    public const string Version = "0.1.2";

    public Task<string> SmokeAsync(IHost host)
    {
        // no network: the embedded Core loads, the protocol round-trips, the store paths are what the desktop nodes use
        var paths = new Paths("index/");
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=abcdefghijk", 480, null);
        var back = JobDescriptor.Parse(job.ToBytes());
        if (back.Url != job.Url || paths.Job("download", "x") != "index/jobs/download/x.json") return Task.FromResult("protocol mismatch");
        if (!Protocol.Stages.Contains("download")) return Task.FromResult("no download stage");
        if (host.Kind != "android") return Task.FromResult("unexpected host kind " + host.Kind);
        return Task.FromResult("ok");
    }

    public async Task<string> RunAsync(IHost host, string args, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(host.StoreUrl)) throw new InvalidOperationException("no store configured (set up the node first)");
        Log.Out = new HostLogWriter(host);
        var http = new HttpClient(new HostStoreHandler(host)) { Timeout = TimeSpan.FromMinutes(30) };
        var restStore = new RestStore(http, host.StoreUrl + "?" + HostStoreHandler.Placeholder);
        restStore.GhostBlobFor = name => new NoGhostBlob(name);   // Nexis hydration needs the Azure SDK and a raw SAS: not on the phone (the download node never reads big blobs)
        return await RunWithStoreAsync(host, restStore, args, ct);
    }

    /// <summary>The node loop over any store (tests pass a FakeStore).</summary>
    public async Task<string> RunWithStoreAsync(IHost host, IStore baseStore, string args, CancellationToken ct)
    {
        var a = string.IsNullOrWhiteSpace(args) ? new JsonObject() : JsonNode.Parse(args)!.AsObject();
        var stages = (a["stages"] as JsonArray)?.Select(x => (string)x!).ToArray() ?? ["download"];
        stages = DownloaderNode.WithRefresh(stages, (bool?)a["noRefresh"] ?? false);   // like the desktop download node: it also lists sources unless "noRefresh" is set
        foreach (var s in stages) if (!Protocol.Stages.Contains(s)) throw new ArgumentException("unknown stage " + s);
        int? maxJobs = (int?)a["maxJobs"];
        bool once = (bool?)a["once"] ?? true;
        var prefix = (string?)a["prefix"] ?? "index/";
        int? budget = (int?)a["budget"];
        int maxHeight = (int?)a["maxHeight"] ?? 480;
        var zips = Path.Combine(host.FilesDir, "zips"); Directory.CreateDirectory(zips);
        var store = StageZipStore.Wrap(baseStore, zips);
        var paths = new Paths(prefix);
        var tools = host.Tools;
        var gate = new DownloadGate();
        // the check server (`indexnode serve`): from the setup (host settings checkUrl and checkToken); without it the node works from the work queue only and a sample's check runs in a processing node
        var settings = new DownloaderSettings { CheckUrl = host.GetSetting("checkUrl"), CheckToken = host.GetSetting("checkToken") };
        Redact.Register(settings.CheckToken);
        var http = new HttpClient(new HostHttpHandler(host)) { Timeout = TimeSpan.FromMinutes(5) };
        var all = DownloaderNode.Handlers(tools, store, gate, http, settings);
        foreach (var st in stages) if (!all.ContainsKey(st)) throw new ArgumentException("this node does not run stage " + st);
        var handlers = stages.Contains("download") ? all : all.Where(kv => stages.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);   // a node that downloads also takes samples (the engine's rule)
        var limits = new LimitState(host.NodeId, new NodeLimits { Budget = budget }, "host", Path.Combine(host.FilesDir, "state.json"));
        var opts = new EngineOptions
        {
            NodeId = host.NodeId, Name = host.NodeName, Kind = host.Kind, Version = Version, InstanceId = InstanceId(host),
            Stages = stages, Workers = 1, Lanes = 1, Threads = 1, Once = once, MaxJobs = maxJobs, Limits = limits,
            Conditions = host.GetSetting("conditions"),
            FetchNext = DownloaderNode.FetchNext(http, settings, host.NodeId),
            StorageHost = Uri.TryCreate(host.StoreUrl, UriKind.Absolute, out var su) ? su.Host : null, PollInterval = TimeSpan.FromSeconds(30),
            PlacementProbe = _ => Task.FromResult(new Placement { Provider = "phone", Reason = "phone", CheckedAt = Protocol.NowUtc() }),
            NetworkFetch = async (url, c) => { var r = await host.HttpAsync("GET", url, null, null, 65536, c); return r.Status == 200 ? r.Text : null; },
            NetworkExtra = n => { var d = host.GetDeviceInfo(); n.Transport = d.Transport; n.Vpn = d.Vpn; },
        };
        var engine = new NodeEngine(store, paths, opts, handlers);
        host.SetStatus("running", null, null);
        await engine.RunAsync(ct);
        host.SetStatus("idle", null, null);
        return new JsonObject { ["jobsStarted"] = engine.JobsStarted, ["version"] = Version, ["checkServer"] = settings.HasCheck, ["stages"] = new JsonArray(stages.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) }.ToJsonString();
    }

    /// <summary>One instance per phone: the id is made once and kept in the app's settings, so the app, WorkManager and a restart after an update all report as the same instance.</summary>
    public static string InstanceId(IHost host)
    {
        var id = host.GetSetting("instanceId");
        if (string.IsNullOrEmpty(id)) { id = Guid.NewGuid().ToString("N"); host.SetSetting("instanceId", id); }
        return id;
    }

    private sealed class NoGhostBlob(string name) : IGhostBlob
    {
        public string Name => name;
        public Task<GhostState?> ReadAsync(CancellationToken ct) => Task.FromResult<GhostState?>(null);
        public Task<long?> SnapshotLengthAsync(string snapshot, CancellationToken ct) => Task.FromResult<long?>(null);
        public Task CommitGhostAsync(GhostState state, CancellationToken ct) => throw new InvalidOperationException("ghost hydration is not available on the phone");
    }
}
