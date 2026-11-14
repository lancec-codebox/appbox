using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IndexNode;

public sealed class Args
{
    public List<string> Positional { get; } = new();
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "once", "help", "follow-channels", "dry", "qr", "no-sas", "force", "json" };

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
        indexnode run [--stages watch,process,comments] [--once] [--max-jobs N] [--jobs process=2,download=1] [--threads N] [--node-name N]
                      [--status-out <file> [--status-every 60s]] [--budget N] [--schedule "<cron>" --window 3h] [--ai-base-url U --ai-key K --ai-text-model M --ai-vision-model M --ai-api chat|responses|auto]
        indexnode sources add <url> [--every 1h] [--backfill N] [--follow-channels] [--tabs videos,shorts] [--max-height 480]
        indexnode sources list | remove <id> | import <channels.json> [--every 1d] [--tabs videos,shorts]
        indexnode ai test [--ai-base-url ...]
        indexnode enqueue <url|playlist-url>... [--max-height 480] [--format F]
        indexnode enqueue --from watch-later [--max-height 480]
        indexnode browser login | status
        indexnode tools install --ytdlp-zipapp
        indexnode status [--json]
        indexnode process --job <id> [--steps cast,describe] [--threads N] [--ai-...]   (redo steps of one processed job in the store: real-data run; then 'publish --job <id>')
        indexnode describe --local <job folder> [--dry] [--out <file>]   (AI step; --dry writes the request it would send, no AI call)
        indexnode publish [--out <dir>] [--job <id>] [--since <time>] [--local <dir>] [--force]   (site packs per PACK.md; default out: %LOCALAPPDATA%\indexnode\site)
        indexnode medusa-setup [--qr] [--file <path>.medusa-node.json] [--name N] [--conditions wifi+charging|wifi] [--daily-cap N] [--budget N] [--schedule C] [--window W] [--no-sas]
        indexnode dev cast --video <file> --out <dir> [--model buffalo_l|yunet-sface] [--threshold D] [--merge D] [--border D]
        indexnode requeue [--stage S]
        indexnode control run|drain|stop [--stage download|process|comments] [--message M]
        indexnode config set-sas <url>
        indexnode config set <budget|schedule|window|ai.base-url|ai.key|ai.text-model|ai.vision-model|ai.api|jobs|threads|model-url|model-path|node-name|ytdlp.command|ytdlp.js-runtime|browser.exe|browser.profile-dir|browser.port> <value>
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
                "seeds" => await Commands.SeedsAsync(a),
                "dev" => await DevCommands.RunAsync(a),
                "search" => await Commands.SearchAsync(a),
                "status" => await Commands.StatusAsync(a),
                "requeue" => await Commands.RequeueAsync(a),
                "control" => await Commands.ControlAsync(a),
                "config" => Commands.Config(a),
                "browser" => await Commands.BrowserAsync(a),
                "tools" => await Commands.ToolsAsync(a),
                "sources" => await Commands.SourcesAsync(a),
                "ai" => await Commands.AiAsync(a),
                "publish" => await SiteCommands.PublishAsync(a),
                "describe" => await SiteCommands.DescribeAsync(a),
                "process" => await SiteCommands.ProcessAsync(a),
                "medusa-setup" => SiteCommands.MedusaSetup(a),
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
        var stages = Protocol.ExpandStages((a.Get("stages") ?? string.Join(',', Protocol.DefaultNodeStages)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var s in stages) if (!Protocol.Stages.Contains(s)) throw new ArgumentException($"unknown stage '{s}'");
        var jobs = new Dictionary<string, int> { ["download"] = cfg.JobsFor("download"), ["process"] = cfg.JobsFor("process"), ["comments"] = cfg.JobsFor("comments"), ["watch"] = cfg.JobsFor("watch") };
        foreach (var st in Protocol.StepStages) jobs[st] = cfg.Jobs != null && cfg.Jobs.TryGetValue(st, out var sn) ? sn : cfg.JobsFor("process");
        foreach (var kv in NodeConfig.ParseJobs(a.Get("jobs"))) jobs[kv.Key] = kv.Value;
        int threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads;
        var (nodeId, nodeName) = cfg.ResolveIdentity(a.Get("node-name"), Environment.GetEnvironmentVariable(NodeConfig.NodeNameEnv));

        var tools = new Tools(http, cfg);
        try { await tools.CheckFfmpegAsync(stages, default); }
        catch (InvalidOperationException e) { Console.Error.WriteLine("indexnode: " + e.Message); return 2; }
        var gate = new DownloadGate();
        var handlers = new Dictionary<string, IStageHandler>
        {
            ["download"] = new DownloadStage(tools, store, gate),
            ["process"] = new ProcessStage(tools, store, cfg, http),
            ["comments"] = new CommentsStage(tools, store, gate),
            ["watch"] = new WatchStage(tools, store),
        };
        foreach (var st in Protocol.StepStages) handlers[st] = handlers["process"]; // a step job is a process job with one step
        var limits = BuildLimits(a, cfg, nodeId);
        var ai = AiConfig.Resolve(cfg.Ai, k => a.Get(k), Environment.GetEnvironmentVariable);
        Redact.Register(ai.Key);
        ProcessStage.Ai = ai;
        var opts = new EngineOptions
        {
            NodeId = nodeId, Name = nodeName,
            Version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0",
            Steps = ProcessStage.SupportedSteps(cfg), CanRun = job => !(job.Stage == "process" || Protocol.IsStep(job.Stage)) || ProcessStage.CanRun(job, ProcessStage.SupportedSteps(cfg)),
            Stages = stages, Jobs = jobs, Threads = threads, Once = a.Has("once"),
            MaxJobs = a.Get("max-jobs") is { } mj ? int.Parse(mj) : null, Limits = limits,
            StorageHost = Uri.TryCreate(cfg.Sas, UriKind.Absolute, out var sasUri) ? sasUri.Host : null, StorageRegion = cfg.StorageRegion ?? "westus2",
        };
        var engine = new NodeEngine(store, paths, opts, handlers);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Log.Info("Ctrl+C: releasing current jobs"); cts.Cancel(); };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });

        var budgetText = limits.Budget is { } bd ? $" budget={limits.Remaining}/{bd}" : "";
        var schedText = (a.Get("schedule") ?? cfg.Schedule) is { } sc ? " schedule=" + sc.Replace(' ', '_') + " window=" + (a.Get("window") ?? cfg.Window ?? "1h") : "";
        Log.Info($"node {nodeId}: stages={string.Join(',', stages)} jobs={string.Join(',', jobs.Where(j => stages.Contains(j.Key)).Select(j => j.Key + "=" + j.Value))} threads={threads} prefix={paths.Prefix}{budgetText}{schedText} ai=[{ai.Describe()}]");
        Task? statusLoop = null;
        if (a.Get("status-out") is { } statusOut)
        {
            var every = StatusSnapshot.ParseEvery(a.Get("status-every"));
            statusLoop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try { StatusSnapshot.WriteAtomic(statusOut, StatusSnapshot.Serialize(await StatusSnapshot.BuildAsync(store, paths, DateTimeOffset.UtcNow, cts.Token))); }
                    catch (OperationCanceledException) { }
                    catch (Exception e) { Log.Info("status snapshot failed: " + Redact.Sas(e.Message)); }
                    try { await Task.Delay(every, cts.Token); } catch (OperationCanceledException) { }
                }
            });
        }
        await engine.RunAsync(cts.Token);
        cts.Cancel();
        if (statusLoop != null) await statusLoop;
        Log.Info("node stopped");
        return 0;
    }

    /// <summary>Flags win over the config file. A --budget flag counts from the first run with that value; a config budget from when it was set.</summary>
    public static LimitState BuildLimits(Args a, NodeConfig cfg, string nodeId)
    {
        var local = new NodeLimits
        {
            Budget = a.Get("budget") is { } b ? int.Parse(b) : cfg.Budget,
            Schedule = a.Get("schedule") ?? cfg.Schedule,
            Window = a.Get("window") ?? cfg.Window,
        };
        if (local.Schedule != null) CronExpr.Parse(local.Schedule);
        if (local.Window != null) WatchStage.ParseEvery(local.Window);
        var stamp = a.Has("budget") ? "flag" : cfg.BudgetSetAt ?? "";
        return new LimitState(nodeId, local, stamp, Path.Combine(NodeConfig.ConfigDir(), "state.json"));
    }

    public static async Task<int> SourcesAsync(Args a)
    {
        var sub = a.Positional.FirstOrDefault();
        var (cfg, store, paths, http) = Open(a);
        int maxHeight = a.Get("max-height") is { } h ? int.Parse(h) : 480;
        var every = a.Get("every");
        switch (sub)
        {
            case "add":
            {
                var url = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources add <url>");
                var job = await Sources.ResolveAsync(new Tools(http, cfg), url, every ?? "1h", Sources.ParseTabs(a.Get("tabs")), a.Has("follow-channels"), a.Get("backfill") is { } bf ? int.Parse(bf) : 0, maxHeight, default);
                var created = await Sources.AddAsync(store, paths, job, default);
                Console.WriteLine($"{(created ? "added " : "exists")} {job.JobId}  {job.Kind}  {job.Name}  every={job.Every} backfill={job.Backfill} followChannels={job.FollowChannels}");
                return 0;
            }
            case "list":
            {
                var list = await Sources.ListAsync(store, paths, default);
                if (list.Count == 0) Console.WriteLine("(no followed sources)");
                foreach (var j in list) Console.WriteLine(Sources.FormatRow(j));
                return 0;
            }
            case "remove":
            {
                var id = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources remove <id>");
                var name = paths.Job("watch", id);
                if (await store.GetTagsAsync(name, default) == null) { Console.Error.WriteLine($"no source {id}"); return 1; }
                await store.DeleteAsync(name, default);
                Console.WriteLine($"removed {id} (its downloaded videos stay)");
                return 0;
            }
            case "import":
            {
                var file = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources import <channels.json>");
                var r = await Sources.ImportAsync(store, paths, File.ReadAllBytes(file), every ?? "1d", Sources.ParseTabs(a.Get("tabs")), maxHeight, default);
                Console.WriteLine($"{r.Added} added, {r.Existed} already existed{(r.Skipped > 0 ? $", {r.Skipped} skipped (no channelId)" : "")}");
                return 0;
            }
            default: throw new ArgumentException("sources add <url> | list | remove <id> | import <channels.json>");
        }
    }

    public static async Task<int> AiAsync(Args a)
    {
        if (a.Positional.FirstOrDefault() != "test") throw new ArgumentException("ai test");
        var cfg = NodeConfig.Load();
        var ai = AiConfig.Resolve(cfg.Ai, k => a.Get(k), Environment.GetEnvironmentVariable);
        Redact.Register(ai.Key);
        if (!ai.Configured) throw new InvalidOperationException("no AI endpoint: set ai.base-url and ai.text-model (config set, --ai-* flags or INDEXNODE_AI_* variables)");
        if (!AiConfig.ValidApi(ai.EffectiveApi)) throw new ArgumentException("ai.api must be chat, responses or auto");
        Console.WriteLine(ai.Describe());
        var client = new AiClient(NewHttp(), ai);
        Console.WriteLine($"text   ({ai.TextModel}): {Redact.Sas(await client.TextAsync(default))}");
        if (string.IsNullOrEmpty(ai.VisionModel)) Console.WriteLine("vision: skipped (no ai.vision-model)");
        else Console.WriteLine($"vision ({ai.VisionModel}): {Redact.Sas(await client.VisionAsync(default))}");
        return 0;
    }

    public static async Task<int> EnqueueAsync(Args a)
    {
        var (cfg, store, paths, http) = Open(a);
        int maxHeight = a.Get("max-height") is { } h ? int.Parse(h) : 480;
        if (a.Has("from")) throw new ArgumentException("enqueue --from watch-later is gone: Watch Later only seeds channels now, use: indexnode seeds watch-later");
        if (a.Positional.Count == 0) throw new ArgumentException("enqueue needs at least one url");
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
        if (stage != null && !Protocol.Stages.Contains(stage)) throw new ArgumentException("--stage must be download, process, comments or watch");
        var c = await ControlWatcher.WriteAsync(store, paths, level, stage, a.Get("message"), cfg.NodeName ?? Environment.UserName, default);
        Console.WriteLine($"control: mode={c.Mode} download={c.Effective("download")} process={c.Effective("process")}{(c.Message != null ? " message=" + c.Message : "")}");
        return 0;
    }

    public static async Task<int> StatusAsync(Args a)
    {
        var (cfg, store, paths, _) = Open(a);
        var now = DateTimeOffset.UtcNow;
        if (a.Has("json")) { Console.WriteLine(StatusSnapshot.Serialize(await StatusSnapshot.BuildAsync(store, paths, now, default))); return 0; }
        Console.WriteLine($"prefix {paths.Prefix}   this node: {cfg.NodeId ?? "(none yet)"}  jobs download={cfg.JobsFor("download")} process={cfg.JobsFor("process")}  threads={cfg.EffectiveThreads}");

        Console.WriteLine("\nNodes");
        var nodeBlobs = await store.ListAsync(paths.NodesDir, false, false, default);
        var nodes = new List<NodeInfo>();
        foreach (var b in nodeBlobs)
            if ((await store.GetAsync(b.Name, default)) is { } c && NodeInfo.Parse(c.Data) is { } n) nodes.Add(n);
        if (nodes.Count == 0) Console.WriteLine("  (none)");
        foreach (var n in nodes.OrderBy(n => n.NodeId))
        {
            Console.WriteLine($"  {n.NodeId,-24} {n.Kind,-8} stages={string.Join('+', n.Stages)} jobs={string.Join(',', n.Jobs.Select(j => j.Key + "=" + j.Value))} threads={n.Threads} state={n.EffectiveState(now)} control={n.Control} lastSeen={n.LastSeen}");
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

    public static async Task<int> SearchAsync(Args a)
    {
        var text = string.Join(' ', a.Positional);
        var job = a.Get("job") ?? throw new ArgumentException("search \"<text>\" --job <id> [--top 8]");
        if (text.Length == 0) throw new ArgumentException("search needs a text");
        int top = a.Get("top") is { } tp ? int.Parse(tp) : 8;
        var (cfg, store, paths, http) = Open(a);
        var sj = new StoreJob(store, paths, job);
        var f16 = await FrameChunks.VectorsAsync(sj, "clip", default) ?? throw new InvalidOperationException($"{job} has no clip vectors yet (run the clip step)");
        var times = await FrameChunks.TimesAsync(sj, default) ?? throw new InvalidOperationException($"{job} has no frames");
        var rows = Clip.FromF16Bytes(f16);
        if (rows.Length != times.Count) throw new InvalidOperationException($"clip vectors have {rows.Length} rows but the frames list has {times.Count}");
        var textModel = await Models.EnsureAsync(Models.ClipText, http, default);
        var tok = Clip.LoadTokenizer(await Models.EnsureAsync(Models.ClipTokenizer, http, default));
        var q = Clip.EmbedText(textModel, tok, text);
        foreach (var h in Clip.Search(rows, times, q, top))
            Console.WriteLine($"{Clip.Stamp(h.T)}  {h.Score:F3}  (shot of {h.ShotFrames} frame(s))");
        return 0;
    }

    public static async Task<int> SeedsAsync(Args a)
    {
        if (a.Positional.FirstOrDefault() != "watch-later") throw new ArgumentException("seeds watch-later");
        var (cfg, store, paths, http) = Open(a);
        var r = await WatchLater.RunAsync(cfg.Browser ?? new BrowserConfig(), http, new Tools(http, cfg), store, paths, default);
        if (r.State == SourceState.SignedOut) { Console.Error.WriteLine(WatchLater.SignedOutMessage); return 3; }
        Console.WriteLine($"Watch Later: {r.Videos} video(s) from {r.Channels} channel(s) written to {paths.Seed("")}  (nothing enqueued)");
        return 0;
    }

    public static async Task<int> ToolsAsync(Args a)
    {
        var sub = a.Positional.FirstOrDefault();
        var cfg = NodeConfig.Load();
        if (sub == "status")
        {
            foreach (var l in new Tools(NewHttp(), cfg).Status()) Console.WriteLine(l);
            foreach (var l in Models.Status()) Console.WriteLine(l);
            return 0;
        }
        if (sub != "install") throw new ArgumentException("tools status | tools install [--all] [--ffmpeg] [--ytdlp] [--ytdlp-zipapp] [--models]");
        var http = NewHttp();
        var tools = new Tools(http, cfg);
        bool all = a.Has("all");
        bool any = all || a.Has("ffmpeg") || a.Has("ytdlp") || a.Has("ytdlp-zipapp") || a.Has("models");
        if (all || a.Has("ytdlp"))
        {
            var yd = await tools.InstallYtDlpPinnedAsync(default);
            Console.WriteLine($"yt-dlp {Tools.YtDlpVersion} ready: {yd}");
        }
        if (a.Has("ytdlp-zipapp"))
        {
            var file = LoadFileConfig();
            var cmd = await tools.InstallYtDlpZipappAsync(default);
            file.Ytdlp = new YtDlpConfig { Command = cmd, JsRuntime = file.Ytdlp?.JsRuntime };
            file.Save();
            Console.WriteLine("yt-dlp zipapp installed; ytdlp.command = " + JsonSerializer.Serialize(cmd));
        }
        if (all || a.Has("ffmpeg") || !any)
        {
            await tools.InstallFfmpegPinnedAsync(default);
            Console.WriteLine($"ffmpeg {Tools.FfmpegVersion} installed in {NodeConfig.ToolsDir()}");
        }
        if (all || a.Has("models"))
            foreach (var m in Models.All) { await Models.EnsureAsync(m, http, default); Console.WriteLine($"model {m.Id} ready"); }
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
                case "budget": file.Budget = int.Parse(val); file.BudgetSetAt = Protocol.NowUtc(); break;
                case "schedule": CronExpr.Parse(val); file.Schedule = val; break;
                case "window": WatchStage.ParseEvery(val); file.Window = val; break;
                case "ai.base-url": (file.Ai ??= new()).BaseUrl = val; break;
                case "ai.key": (file.Ai ??= new()).Key = val; break;
                case "ai.text-model": (file.Ai ??= new()).TextModel = val; break;
                case "ai.vision-model": (file.Ai ??= new()).VisionModel = val; break;
                case "ai.api": if (!AiConfig.ValidApi(val.ToLowerInvariant())) throw new ArgumentException("ai.api: chat, responses or auto"); (file.Ai ??= new()).Api = val.ToLowerInvariant(); break;
                case "jobs": file.Jobs = NodeConfig.ParseJobs(val); break;
                case "threads": file.Threads = int.Parse(val); break;
                case "cast.model": CastLogic.ModelFor(val); file.CastModel = val.ToLowerInvariant(); break;
                case "frames.crf": file.FrameCrf = int.Parse(val); break;
                case "prune.grid": file.PruneGrid = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "prune.sscd": file.PruneSscd = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "prune.lookback": file.PruneLookback = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "storage.region": file.StorageRegion = val.Trim(); break;
                case "model-url": file.ModelUrl = val; break;
                case "model-path": file.ModelPath = val; break;
                case "node-name": file.NodeName = NodeConfig.SanitizeName(val); break;
                case "ytdlp.command": file.Ytdlp = new YtDlpConfig { Command = ParseCommand(val) }; break;
                case "ytdlp.js-runtime": (file.Ytdlp ??= new()).JsRuntime = val; break;
                case "browser.exe": (file.Browser ??= new()).Exe = val; break;
                case "browser.profile-dir": (file.Browser ??= new()).ProfileDir = val; break;
                case "browser.port": (file.Browser ??= new()).Port = int.Parse(val); break;
                default: throw new ArgumentException("unknown config key (budget, schedule, window, ai.base-url, ai.key, ai.text-model, ai.vision-model, ai.api, jobs, threads, storage.region, model-url, model-path, node-name, ytdlp.command, ytdlp.js-runtime, browser.exe, browser.profile-dir, browser.port)");
            }
            file.Save();
            Console.WriteLine($"{key} saved{(key == "ai.key" ? " (hidden)" : "")}");
            return 0;
        }
        throw new ArgumentException("config set-sas <url> | config set <key> <value>");
    }
}
