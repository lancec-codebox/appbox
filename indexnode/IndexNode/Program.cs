using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IndexNode;

public sealed class Args
{
    public List<string> Positional { get; } = new();
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "once", "help" };

    public static Args Parse(IEnumerable<string> argv)
    {
        var a = new Args();
        var q = new Queue<string>(argv);
        while (q.Count > 0)
        {
            var t = q.Dequeue();
            if (t.StartsWith("--"))
            {
                var key = t[2..]; string? val = null;
                var eq = key.IndexOf('=');
                if (eq >= 0) { val = key[(eq + 1)..]; key = key[..eq]; }
                else if (!Flags.Contains(key) && q.Count > 0) val = q.Dequeue();
                a.Options[key] = val ?? "true";
            }
            else a.Positional.Add(t);
        }
        return a;
    }

    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : null;
    public bool Has(string key) => Options.ContainsKey(key);
}

public static class Program
{
    public const string Usage = """
        indexnode run --stages download,process,comments [--once] [--max-jobs N] [--jobs process=2,download=1] [--threads N]
                      [--sources watch-later --source-every 6h] [--max-height 480]
        indexnode enqueue <url|playlist-url>... [--max-height 480] [--format F]
        indexnode enqueue --from watch-later [--max-height 480]
        indexnode browser login | status
        indexnode tools install --ytdlp-zipapp
        indexnode status
        indexnode requeue [--stage S]
        indexnode control run|drain|stop [--stage download|process|comments] [--message M]
        indexnode config set-sas <url>
        indexnode config set <jobs|threads|model-url|model-path|node-name|ytdlp.command|browser.exe|browser.profile-dir|browser.port> <value>
        All commands take --prefix (default index/). INDEXNODE_SAS_URL overrides the stored SAS.
        """;

    public static async Task<int> Main(string[] argv)
    {
        try
        {
            var a = Args.Parse(argv);
            if (a.Positional.Count == 0 || a.Has("help")) { Console.WriteLine(Usage); return a.Has("help") ? 0 : 2; }
            var cmd = a.Positional[0].ToLowerInvariant();
            a.Positional.RemoveAt(0);
            return cmd switch
            {
                "run" => await Commands.RunAsync(a),
                "enqueue" => await Commands.EnqueueAsync(a),
                "status" => await Commands.StatusAsync(a),
                "requeue" => await Commands.RequeueAsync(a),
                "control" => await Commands.ControlAsync(a),
                "config" => Commands.Config(a),
                "browser" => await Commands.BrowserAsync(a),
                "tools" => await Commands.ToolsAsync(a),
                _ => Fail($"unknown command '{cmd}'"),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + Redact.Sas(e.Message));
            return 1;
        }
    }

    private static int Fail(string m) { Console.Error.WriteLine(m + "\n" + Usage); return 2; }
}

public static class Commands
{
    public static HttpClient NewHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("indexnode", "0.1"));
        return h;
    }

    private static (NodeConfig Cfg, RestStore Store, Paths Paths, HttpClient Http) Open(Args a)
    {
        var cfg = NodeConfig.Load();
        if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or set INDEXNODE_SAS_URL");
        var http = NewHttp();
        return (cfg, new RestStore(http, cfg.Sas), new Paths(a.Get("prefix") ?? "index/"), http);
    }

    public static async Task<int> RunAsync(Args a)
    {
        var (cfg, store, paths, http) = Open(a);
        var stages = (a.Get("stages") ?? "download,process,comments").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        foreach (var s in stages) if (!Protocol.Stages.Contains(s)) throw new ArgumentException($"unknown stage '{s}'");
        var jobs = new Dictionary<string, int> { ["download"] = cfg.JobsFor("download"), ["process"] = cfg.JobsFor("process"), ["comments"] = cfg.JobsFor("comments") };
        foreach (var kv in NodeConfig.ParseJobs(a.Get("jobs"))) jobs[kv.Key] = kv.Value;
        int threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads;
        var nodeId = cfg.EnsureNodeId();

        var tools = new Tools(http, cfg);
        var gate = new DownloadGate();
        var handlers = new Dictionary<string, IStageHandler>
        {
            ["download"] = new DownloadStage(tools, store, gate),
            ["process"] = new ProcessStage(tools, store, cfg, http),
            ["comments"] = new CommentsStage(tools, store, gate),
        };
        var opts = new EngineOptions
        {
            NodeId = nodeId, Name = cfg.NodeName ?? NodeConfig.SanitizeName(Environment.MachineName),
            Version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0",
            Stages = stages, Jobs = jobs, Threads = threads, Once = a.Has("once"),
            MaxJobs = a.Get("max-jobs") is { } mj ? int.Parse(mj) : null,
        };
        var engine = new NodeEngine(store, paths, opts, handlers);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Log.Info("Ctrl+C: releasing current jobs"); cts.Cancel(); };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });

        var sourceNames = (a.Get("sources") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var sn in sourceNames) if (sn != "watch-later") throw new ArgumentException($"unknown source '{sn}' (watch-later)");
        var every = WatchLater.ParseInterval(a.Get("source-every") ?? "6h");
        int sourceHeight = a.Get("max-height") is { } sh ? int.Parse(sh) : 480;
        Task sourceTask = Task.CompletedTask;
        async Task PollWatchLater()
        {
            if (engine.Control.Effective("download") == Level.Stop) return;
            try
            {
                var (state, counts) = await WatchLater.PollAsync(cfg.Browser ?? new BrowserConfig(), http, store, paths, sourceHeight, cts.Token);
                if (state == SourceState.SignedOut) { Log.Info(WatchLater.SignedOutMessage); engine.SetSource("watch-later", "signed-out"); }
                else { Log.Info($"Watch Later: found {counts!.Found}, new {counts.New}, existing {counts.Existing}"); engine.SetSource("watch-later", "ok"); }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Info("Watch Later: poll failed (" + Redact.Sas(e.Message) + ")"); engine.SetSource("watch-later", "error"); }
        }
        if (sourceNames.Contains("watch-later"))
        {
            if (a.Has("once")) await PollWatchLater();
            else sourceTask = Task.Run(async () =>
            {
                try { while (!cts.IsCancellationRequested) { await PollWatchLater(); await Task.Delay(every, cts.Token); } }
                catch (OperationCanceledException) { }
            });
        }

        Log.Info($"node {nodeId}: stages={string.Join(',', stages)} jobs={string.Join(',', jobs.Where(j => stages.Contains(j.Key)).Select(j => j.Key + "=" + j.Value))} threads={threads} prefix={paths.Prefix}");
        await engine.RunAsync(cts.Token);
        cts.Cancel();
        await sourceTask;
        Log.Info("node stopped");
        return 0;
    }

    public static async Task<int> EnqueueAsync(Args a)
    {
        var (cfg, store, paths, http) = Open(a);
        int maxHeight = a.Get("max-height") is { } h ? int.Parse(h) : 480;
        if (a.Get("from") is { } from)
        {
            if (from != "watch-later") throw new ArgumentException($"unknown source '{from}' (watch-later)");
            var (state, counts) = await WatchLater.PollAsync(cfg.Browser ?? new BrowserConfig(), http, store, paths, maxHeight, default);
            if (state == SourceState.SignedOut) { Console.Error.WriteLine(WatchLater.SignedOutMessage); return 3; }
            Console.WriteLine($"Watch Later: found {counts!.Found}, new {counts.New}, existing {counts.Existing}");
            return 0;
        }
        if (a.Positional.Count == 0) throw new ArgumentException("enqueue needs at least one url (or --from watch-later)");
        var format = a.Get("format");
        var tools = new Tools(http, cfg);
        int added = 0, existed = 0;
        foreach (var input in a.Positional)
        {
            var urls = new List<string>();
            if (YtDlp.IsSingleVideoUrl(input)) urls.Add(input);
            else
            {
                Log.Info($"expanding {Redact.Sas(input)}");
                var r = await tools.RunYtDlpAsync(["--flat-playlist", "--no-warnings", "--print", "%(webpage_url,url)s", input], default);
                if (r.ExitCode != 0) throw new InvalidOperationException(YtDlp.LastErrorLine(r.Stderr));
                urls.AddRange(r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(u => u.StartsWith("http")));
            }
            foreach (var url in urls.Distinct())
            {
                var job = JobDescriptor.NewDownload(url, maxHeight, format);
                var created = await store.PutAsync(paths.Job("download", job.JobId), job.ToBytes(), Tags.Job("download", "pending", job.AddedAt), null, true, default);
                if (created) added++; else existed++;
                Console.WriteLine($"{(created ? "added " : "exists")} {job.JobId} {url}");
            }
        }
        Console.WriteLine($"{added} added, {existed} already existed");
        return 0;
    }

    public static async Task<int> RequeueAsync(Args a)
    {
        var (_, store, paths, _) = Open(a);
        var stages = a.Get("stage") is { } s ? new[] { s } : Protocol.Stages;
        int n = 0;
        foreach (var stage in stages)
        {
            IReadOnlyList<BlobEntry> failed;
            try { failed = (await store.FindByTagsAsync(Tags.StateWhere(stage, "failed"), default)).Where(b => b.Name.StartsWith(paths.JobsDir(stage), StringComparison.Ordinal)).ToList(); }
            catch (ForbiddenException)
            {
                failed = (await store.ListAsync(paths.JobsDir(stage), true, false, default)).Where(b => b.Tags.GetValueOrDefault(Tags.State) == "failed").ToList();
            }
            foreach (var b in failed)
            {
                var body = await store.GetAsync(b.Name, default);
                if (body == null) continue;
                var job = JobDescriptor.Parse(body.Data);
                job.Attempts = 0; job.LastError = null; job.Permanent = null;
                await store.PutAsync(b.Name, job.ToBytes(), Tags.Job(stage, "pending", b.Tags.GetValueOrDefault(Tags.Added) ?? job.AddedAt, b.Tags.GetValueOrDefault(Tags.Due)), null, false, default);
                Console.WriteLine($"requeued {stage}/{job.JobId}");
                n++;
            }
        }
        Console.WriteLine($"{n} job(s) moved back to pending");
        return 0;
    }

    public static async Task<int> ControlAsync(Args a)
    {
        var (cfg, store, paths, _) = Open(a);
        var level = a.Positional.FirstOrDefault()?.ToLowerInvariant();
        if (level is not ("run" or "drain" or "stop")) throw new ArgumentException("control needs run, drain or stop");
        var stage = a.Get("stage");
        if (stage != null && !Protocol.Stages.Contains(stage)) throw new ArgumentException("--stage must be download, process or comments");
        var c = await ControlWatcher.WriteAsync(store, paths, level, stage, a.Get("message"), cfg.NodeName ?? Environment.UserName, default);
        Console.WriteLine($"control: mode={c.Mode} download={c.Effective("download")} process={c.Effective("process")}{(c.Message != null ? " message=" + c.Message : "")}");
        return 0;
    }

    public static async Task<int> StatusAsync(Args a)
    {
        var (cfg, store, paths, _) = Open(a);
        var now = DateTimeOffset.UtcNow;
        Console.WriteLine($"prefix {paths.Prefix}   this node: {cfg.NodeId ?? "(none yet)"}  jobs download={cfg.JobsFor("download")} process={cfg.JobsFor("process")}  threads={cfg.EffectiveThreads}");

        Console.WriteLine("\nNodes");
        var nodeBlobs = await store.ListAsync(paths.NodesDir, false, false, default);
        var nodes = new List<NodeInfo>();
        foreach (var b in nodeBlobs)
            if ((await store.GetAsync(b.Name, default)) is { } c && NodeInfo.Parse(c.Data) is { } n) nodes.Add(n);
        if (nodes.Count == 0) Console.WriteLine("  (none)");
        foreach (var n in nodes.OrderBy(n => n.NodeId))
        {
            Console.WriteLine($"  {n.NodeId,-24} {n.Kind,-8} stages={string.Join('+', n.Stages)} jobs={string.Join(',', n.Jobs.Select(j => j.Key + "=" + j.Value))} threads={n.Threads} state={n.EffectiveState(now)} control={n.Control}{(n.Sources is { Count: > 0 } src ? " sources=" + string.Join(',', src.Select(s => s.Key + ":" + s.Value)) : "")} lastSeen={n.LastSeen}");
            foreach (var r in n.Running)
                Console.WriteLine($"      worker {r.Worker}: {r.Stage}/{r.JobId} {r.Step} {r.Progress}");
        }

        var all = await store.ListAsync(paths.Prefix + "jobs/", true, true, default);
        Console.WriteLine("\nQueue");
        foreach (var stage in Protocol.Stages)
        {
            var inStage = all.Where(b => b.Tags.GetValueOrDefault(Tags.Stage) == stage).ToList();
            var counts = new[] { "pending", "done", "failed" }.Select(s => $"{s}={inStage.Count(b => b.Tags.GetValueOrDefault(Tags.State) == s)}");
            Console.WriteLine($"  {stage,-9} {string.Join("  ", counts)}  leased={inStage.Count(b => b.LeaseState == "leased")}");
        }
        var leased = all.Where(b => b.LeaseState == "leased").ToList();
        if (leased.Count > 0)
        {
            Console.WriteLine("\nLeased jobs");
            foreach (var b in leased)
                Console.WriteLine($"  {b.Tags.GetValueOrDefault(Tags.Stage)}/{Path.GetFileNameWithoutExtension(b.Name)}  node={b.Metadata.GetValueOrDefault("node")} progress={b.Metadata.GetValueOrDefault("progress")} heartbeat={b.Metadata.GetValueOrDefault("heartbeat")}");
        }

        var ctl = ControlFile.Parse((await store.GetAsync(paths.Control, default))?.Data);
        Console.WriteLine($"\nControl: mode={ctl.Mode} download={ctl.Effective("download")} process={ctl.Effective("process")}  changed {ctl.ChangedAt} by {ctl.ChangedBy}{(ctl.Message != null ? "  \"" + ctl.Message + "\"" : "")}");
        return 0;
    }

    /// <summary>The user-only config file as stored (without the SAS that an environment variable may supply).</summary>
    private static NodeConfig LoadFileConfig() =>
        File.Exists(NodeConfig.ConfigPath()) ? JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(NodeConfig.ConfigPath()), NodeConfig.Json) ?? new() : new();

    /// <summary>A JSON array, or a single program path.</summary>
    public static string[] ParseCommand(string val) =>
        val.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<string[]>(val) ?? throw new ArgumentException("bad command") : [val];

    public static async Task<int> ToolsAsync(Args a)
    {
        if (a.Positional.FirstOrDefault() != "install" || !a.Has("ytdlp-zipapp")) throw new ArgumentException("tools install --ytdlp-zipapp");
        var file = LoadFileConfig();
        var cmd = await new Tools(NewHttp()).InstallYtDlpZipappAsync(default);
        file.Ytdlp = new YtDlpConfig { Command = cmd };
        file.Save();
        Console.WriteLine("yt-dlp zipapp installed; ytdlp.command = " + JsonSerializer.Serialize(cmd));
        return 0;
    }

    public static async Task<int> BrowserAsync(Args a)
    {
        var cfg = (LoadFileConfig().Browser ?? new BrowserConfig());
        var sub = a.Positional.FirstOrDefault();
        if (sub == "login")
        {
            BrowserLauncher.Start(cfg, false, "https://accounts.google.com", "https://www.youtube.com");
            Console.WriteLine("Sign in to Google/YouTube in the Brave window that opened (or turn on Brave Sync), then close it or leave it open.");
            return 0;
        }
        if (sub == "status")
        {
            var http = NewHttp();
            bool running = await CdpBrowser.TryGetEndpointAsync(http, cfg.ResolvePort(), default) != null;
            Console.WriteLine($"browser running with port {cfg.ResolvePort()}: {(running ? "yes" : "no")}");
            bool signedIn;
            try
            {
                await using var b = await CdpBrowser.OpenAsync(cfg, http, default);
                var page = await b.NewPageAsync(default);
                try { signedIn = await WatchLater.IsSignedInAsync(page, default); } finally { await page.CloseAsync(); }
            }
            catch (Exception e) { Console.Error.WriteLine("could not check YouTube: " + e.Message); signedIn = false; }
            Console.WriteLine($"YouTube signed in: {(signedIn ? "yes" : "no")}");
            return 0;
        }
        throw new ArgumentException("browser login | browser status");
    }

    public static int Config(Args a)
    {
        var sub = a.Positional.FirstOrDefault();
        var file = File.Exists(NodeConfig.ConfigPath()) ? JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(NodeConfig.ConfigPath()), NodeConfig.Json) ?? new() : new();
        if (sub == "set-sas")
        {
            var url = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("config set-sas needs the SAS URL");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Query.Contains("sig=")) throw new ArgumentException("that does not look like a container SAS URL (https://<account>.blob.core.windows.net/<container>?...&sig=...)");
            file.Sas = url;
            file.Save();
            Console.WriteLine($"SAS stored in {NodeConfig.ConfigPath()} (permissions: {RestStore.Permissions(url) ?? "unknown"})");
            return 0;
        }
        if (sub == "set")
        {
            var key = a.Positional.ElementAtOrDefault(1)?.ToLowerInvariant();
            var val = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("config set <key> <value>");
            switch (key)
            {
                case "jobs": file.Jobs = NodeConfig.ParseJobs(val); break;
                case "threads": file.Threads = int.Parse(val); break;
                case "model-url": file.ModelUrl = val; break;
                case "model-path": file.ModelPath = val; break;
                case "node-name": file.NodeName = NodeConfig.SanitizeName(val); break;
                case "ytdlp.command": file.Ytdlp = new YtDlpConfig { Command = ParseCommand(val) }; break;
                case "browser.exe": (file.Browser ??= new()).Exe = val; break;
                case "browser.profile-dir": (file.Browser ??= new()).ProfileDir = val; break;
                case "browser.port": (file.Browser ??= new()).Port = int.Parse(val); break;
                default: throw new ArgumentException("unknown config key (jobs, threads, model-url, model-path, node-name, ytdlp.command, browser.exe, browser.profile-dir, browser.port)");
            }
            file.Save();
            Console.WriteLine($"{key} saved");
            return 0;
        }
        throw new ArgumentException("config set-sas <url> | config set <key> <value>");
    }
}
