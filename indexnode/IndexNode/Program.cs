using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IndexNode;

public sealed class Args
{
    public List<string> Positional { get; } = new();
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "once", "help", "gpu", "follow-channels", "dry", "qr", "no-sas", "force", "json", "delete-loose" };

    public static Args Parse(IEnumerable<string> argv)
    {
        var a = new Args();
        var q = new Queue<string>(argv);
        bool rest = false;   // after a bare "--" everything is a positional argument (a video id may start with '-')
        while (q.Count > 0)
        {
            var t = q.Dequeue();
            if (!rest && t == "--") { rest = true; continue; }
            if (!rest && t.StartsWith("--"))
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

    /// <summary>--video=&lt;id&gt; is accepted wherever a video id is taken (--job, --videos): an id may start with '-', which no other spelling can carry.</summary>
    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : key is "job" or "videos" && Options.TryGetValue("video", out var vid) ? vid : null;
    public bool Has(string key) => Options.ContainsKey(key);
}

public static class Program
{
    public const string Usage = """
        indexnode run [--stages download,process,comments] [--no-refresh] [--gpu] [--device cpu|cuda] [--exit-when-idle 10m] [--batch 6] [--batch-hours 12] [--lanes 2] [--gpu-fallback 30m|off] [--once] [--max-jobs N] [--jobs process=2,download=1] [--threads N] [--node-name N]
                      [--status-out <file> [--status-every 60s]] [--budget N] [--schedule "<cron>" --window 3h] [--ai-base-url U --ai-key K --ai-text-model M --ai-vision-model M --ai-api chat|responses|auto --ai-session-id S]
        indexnode sources add <url> [--every 1d|6h|daily@03:00] [--backfill N] [--follow-channels] [--tabs videos,shorts] [--max-height 480] [--priority N]
        indexnode sources list | remove <id> | import <channels.json> [--every 1d] [--tabs videos,shorts] [--priority N]
        indexnode sources schedule <id> off|once|1d|6h|daily@03:00   (new sources are "once": looked up on add, never refreshed unless scheduled)
        indexnode sources lookup <id>   (full listing again: every member recorded, every unknown video queued)
        indexnode sources refresh <id>|--all-due   (incremental: only videos new since the last run; due now at priority 500)
        indexnode ai test [--ai-base-url ...]
        indexnode enqueue <url|playlist-url>... [--max-height 480] [--format F] [--priority N]
        indexnode priority set <N> --job <id>... | --from <file> | --channel <id|url> [--stage download] [--with-steps]
        indexnode priority list [--stage S] [--top 20]
        indexnode sources priority <source id|url|video url> N   (re-tags the source's pending jobs; lists the playlist/channel with yt-dlp to match older jobs: run it on a machine YouTube allows, e.g. a residential one)
        indexnode enqueue --from watch-later [--max-height 480]
        indexnode browser login | status
        indexnode tools install --ytdlp-zipapp
        indexnode gpu markers [--dry]
        indexnode status [--json]
        indexnode batch explain [--stages frames,sscd,...] [--batch 6] [--top 50]   (read-only: why each drama's steps are or are not eligible for the next batch)
        (a video id that starts with '-': --video=<id>, or put it after a bare --, e.g. indexnode classify set drama -- --QfpuGhs0U)
        indexnode repack [--videos <id>,<id>] [--dry] [--delete-loose] [--parallel <cores>]   one-off 0.9 -> 0.10.0 layout conversion (run on a node with all nodes stopped)
        indexnode process --job <id> [--steps cast,describe] [--threads N] [--ai-...]   (redo steps of one processed job in the store: real-data run; then 'publish --job <id>')
        indexnode describe --local <job folder> [--dry] [--out <file>]   (AI step; --dry writes the request it would send, no AI call)
        indexnode publish [--out <dir>] [--job <id>] [--since <time>] [--local <dir>] [--force]   (site packs per PACK.md; default out: %LOCALAPPDATA%\indexnode\site)
        indexnode medusa-setup [--qr] [--file <path>.medusa-node.json] [--name N] [--conditions wifi+charging|wifi] [--daily-cap N] [--budget N] [--schedule C] [--window W] [--no-sas]
        indexnode dev cast --video <file> --out <dir> [--model buffalo_l|yunet-sface] [--threshold D] [--merge D] [--border D]
        indexnode requeue [--stage S]
        indexnode classify set <videoId> drama|not [--reason R]   (owner override: writes the decision and sets the download job)
        indexnode classify list --rejected [--since 1d]
        indexnode classify requeue --source <id>   (sets the source's pending downloads back to unclassified)
        indexnode control run|drain|stop [--stage download|process|comments] [--message M]
        indexnode control gpu-hold <duration like 30m>|off
        indexnode control --max-per-ip N|off
        indexnode --version
        indexnode config set-sas <url>
        indexnode config set <budget|schedule|window|ai.base-url|ai.key|ai.text-model|ai.vision-model|ai.api|jobs|threads|model-url|model-path|node-name|ytdlp.command|ytdlp.js-runtime|browser.exe|browser.profile-dir|browser.port> <value>
        All commands take --prefix (default index/). INDEXNODE_SAS_URL overrides the stored SAS.
        """;

    public static async Task<int> Main(string[] argv)
    {
        try
        {
            var a = Args.Parse(argv);
            if (a.Has("version") && a.Positional.Count == 0) { Console.WriteLine(Commands.VersionString); return 0; }
            if (a.Positional.Count == 0 || a.Has("help")) { Console.WriteLine(Usage); return a.Has("help") ? 0 : 2; }
            var cmd = a.Positional[0].ToLowerInvariant();
            a.Positional.RemoveAt(0);
            return cmd switch
            {
                "run" => await Commands.RunAsync(a),
                "enqueue" => await Commands.EnqueueAsync(a),
                "download" => await Commands.DownloadAsync(a),
                "unprocessed" => await Commands.UnprocessedAsync(a),
                "seeds" => await Commands.SeedsAsync(a),
                "dev" => await DevCommands.RunAsync(a),
                "stages" => await StagesCommand.RunAsync(a),
                "ensure" => await EnsureCommand.RunAsync(a),
                "repack" => await Commands.RepackAsync(a),
                "batch" => await Commands.BatchAsync(a),
                "scripts" => await ScriptsCommand.RunAsync(a),
                "search" => await Commands.SearchAsync(a),
                "status" => await Commands.StatusAsync(a),
                "requeue" => await Commands.RequeueAsync(a),
                "classify" => await ClassifyCommand.RunAsync(a),
                "availability" => await Commands.AvailabilityAsync(a),
                "control" => await Commands.ControlAsync(a),
                "config" => Commands.Config(a),
                "browser" => await Commands.BrowserAsync(a),
                "gpu" => await Commands.GpuAsync(a),
                "tools" => await Commands.ToolsAsync(a),
                "sources" => await Commands.SourcesAsync(a),
                "priority" => await Commands.PriorityAsync(a),
                "ai" => await Commands.AiAsync(a),
                "publish" => await SiteCommands.PublishAsync(a),
                "cast-candidates" => await CastCandidates.RunAsync(a),
                "thumbs-bench" => await ThumbsBench.RunAsync(a),
                "dino-bench" => await DinoBench.RunAsync(a),
                "describe" => await SiteCommands.DescribeAsync(a),
                "process" => await SiteCommands.ProcessAsync(a),
                "medusa-setup" => SiteCommands.MedusaSetup(a),
                _ => Fail($"unknown command '{cmd}'"),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + Redact.Sas(e.Message));
            Console.Error.WriteLine(Redact.Trace(e));   // type and stack: the message alone ("'0' cannot be greater than ...") does not say where
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

    internal static (NodeConfig Cfg, IStore Store, Paths Paths, HttpClient Http) Open(Args a)
    {
        var cfg = NodeConfig.Load();
        if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or set INDEXNODE_SAS_URL");
        var http = NewHttp();
        return (cfg, StageZipStore.Wrap(new RestStore(http, cfg.Sas)), new Paths(a.Get("prefix") ?? "index/"), http);
    }

    public static string VersionString => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    public static async Task<int> RunAsync(Args a)
    {
        var (cfg, store, paths, http) = Open(a);
        // --gpu: a node that runs only the GPU stages (sscd, clip, cast, ocrsubs) on the CUDA provider and never falls back to the CPU
        bool gpu = a.Has("gpu");
        var device = OrtOptions.ParseDevice(gpu ? "cuda" : a.Get("device") ?? Environment.GetEnvironmentVariable("INDEXNODE_DEVICE") ?? cfg.Device);
        OrtOptions.Device = device;
        OrtOptions.DeviceId = int.TryParse(Environment.GetEnvironmentVariable("INDEXNODE_CUDA_DEVICE"), out var cudaDev) ? cudaDev : 0;
        if (gpu && OrtOptions.Device != "cuda") throw new ArgumentException("--gpu needs --device cuda");
        var requested = (a.Get("stages") ?? (gpu ? string.Join(',', GpuRouting.MarkerStages) : string.Join(',', Protocol.DefaultNodeStages))).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var stages = Protocol.ExpandStages(requested);
        foreach (var s in stages) if (!Protocol.Stages.Contains(s)) throw new ArgumentException($"unknown stage '{s}'");
        if (!gpu && stages.Contains("download") && !stages.Contains("refresh") && !a.Has("no-refresh")) stages = [.. stages, "refresh"]; // 0.10.3: a download node also lists sources (a lookup on add, scheduled refreshes), unless --no-refresh
        if (gpu)
        {
            stages = GpuRouting.GpuNodeStages(stages);
            if (stages.Length == 0) throw new ArgumentException("--gpu runs only sscd, clip, cast and ocrsubs; --stages names none of them");
        }
        TimeSpan? exitIdle = a.Get("exit-when-idle") is { } ew ? RefreshStage.ParseEvery(ew) : null;
        foreach (var old in new[] { "jobs", "max-heavy", "workers" }) if (a.Get(old) != null) Console.Error.WriteLine($"indexnode: --{old} is ignored since 0.9.0 (a node runs stage batches: --batch N, --batch-hours H, --lanes N)");
        if (cfg.MaxHeavy != null || cfg.Jobs != null || cfg.Workers != null) Console.Error.WriteLine("indexnode: config jobs / max-heavy / workers are ignored since 0.9.0 (use batch, batch-hours, lanes)");
        int lanes = a.Get("lanes") is { } ln ? int.Parse(ln) : 2;
        if (lanes < 1) throw new ArgumentException("--lanes needs at least 1");
        int batchN = a.Get("batch") is { } bn ? int.Parse(bn) : 6;
        double batchHours = a.Get("batch-hours") is { } bh ? double.Parse(bh, System.Globalization.CultureInfo.InvariantCulture) : 12;
        if (batchN < 1 || batchHours <= 0) throw new ArgumentException("--batch needs at least 1 and --batch-hours more than 0");
        int workers = gpu ? 1 : 2; // workers of the non-batch stages (download, comments, watch, ...)
        int? workerThreads = a.Get("worker-threads") is { } wt ? int.Parse(wt) : cfg.WorkerThreads;
        int threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads;
        var (nodeId, nodeName) = cfg.ResolveIdentity(a.Get("node-name"), Environment.GetEnvironmentVariable(NodeConfig.NodeNameEnv));
        try { var lf = RollingFile.FileFor(Environment.GetEnvironmentVariable("INDEXNODE_LOG_DIR") ?? RollingFile.DefaultDir(), nodeName); Log.UseFile(new RollingFile(lf)); Log.Info($"logging to {lf}"); } catch (Exception e) { Console.Error.WriteLine("indexnode: no log file (" + e.Message + ")"); }

        var tools = new Tools(http, cfg);
        try { await tools.CheckFfmpegAsync(stages, default); }
        catch (InvalidOperationException e) { Console.Error.WriteLine("indexnode: " + e.Message); return 2; }
        var gate = new DownloadGate();
        TimeSpan? gpuFlag = a.Get("gpu-fallback") is { } gfv ? GpuRouting.ParseFlag(gfv) : null; // null: follow control.json gpu.hold (absent = no hold)
        var handlers = new Dictionary<string, IStageHandler>
        {
            ["download"] = new DownloadStage(tools, store, gate),
            ["process"] = new ProcessStage(tools, store, cfg, http) { GpuFlag = gpuFlag },
            ["comments"] = new CommentsStage(tools, store, gate),
            ["refresh"] = new RefreshStage(tools, store),
            ["availability"] = new AvailabilityStage(tools, store),
            ["classify"] = new ClassifyStage(store) { Ai = () => ProcessStage.Ai is { Configured: true } ai0 ? (new AiClient(http, ai0), ai0.TextModel ?? ai0.VisionModel!) : null },
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
            Stages = stages, Workers = workers, Lanes = lanes, Batch = batchN, BatchHours = batchHours, WorkerThreads = workerThreads, Threads = threads, Once = a.Has("once"), Gpu = gpu, ExitWhenIdle = exitIdle,
            GpuFallback = gpuFlag,
            Kind = gpu ? "gpu" : "desktop",
            MaxJobs = a.Get("max-jobs") is { } mj ? int.Parse(mj) : null, Limits = limits,
            StorageHost = Uri.TryCreate(cfg.Sas, UriKind.Absolute, out var sasUri) ? sasUri.Host : null, StorageRegion = cfg.StorageRegion ?? "westus2",
        };
        var engine = new NodeEngine(store, paths, opts, handlers);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Log.Info("Ctrl+C: releasing current jobs"); cts.Cancel(); };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });

        var budgetText = limits.Budget is { } bd ? $" budget={limits.Remaining}/{bd}" : "";
        var schedText = (a.Get("schedule") ?? cfg.Schedule) is { } sc ? " schedule=" + sc.Replace(' ', '_') + " window=" + (a.Get("window") ?? cfg.Window ?? "1h") : "";
        Log.Info($"node {nodeId}: stages={string.Join(',', stages)}workers={workers} lanes={lanes} batch={batchN}/{batchHours}h threads={threads} worker-threads={opts.ThreadsPerWorker} prefix={paths.Prefix}{budgetText}{schedText} ai=[{ai.Describe()}]");
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
        if (local.Window != null) RefreshStage.ParseEvery(local.Window);
        var stamp = a.Has("budget") ? "flag" : cfg.BudgetSetAt ?? "";
        return new LimitState(nodeId, local, stamp, Path.Combine(NodeConfig.ConfigDir(), "state.json"));
    }

    public static async Task<int> PriorityAsync(Args a)
    {
        var (_, store, paths, _) = Open(a);
        return await PriorityTool.RunAsync(a, store, paths);
    }

    public static async Task<int> SourcesAsync(Args a)
    {
        int priority = PriorityTool.ParseOption(a.Get("priority"));
        var sub = a.Positional.FirstOrDefault();
        var (cfg, store, paths, http) = Open(a);
        int maxHeight = a.Get("max-height") is { } h ? int.Parse(h) : 480;
        var every = a.Get("every") ?? a.Get("schedule"); // null: "once" (a lookup on add, then nothing unless scheduled)
        switch (sub)
        {
            case "add":
            {
                var url = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources add <url>");
                var job = await Sources.ResolveAsync(new Tools(http, cfg), url, every ?? "once", Sources.ParseTabs(a.Get("tabs")), a.Has("follow-channels"), a.Get("backfill") is { } bf ? int.Parse(bf) : -1, maxHeight, default);
                if (a.Get("backfill") == null && job.Kind == "channel") job.Backfill = 0; // a channel's lookup takes its newest uploads as before (100); a playlist's takes every video
                job.Priority = priority;
                var created = await Sources.AddAsync(store, paths, job, default);
                Console.WriteLine($"{(created ? "added " : "exists")} {job.JobId}  {job.Kind}  {job.Name}  schedule={Schedule.Of(job).Describe()} backfill={(job.Backfill?.ToString() ?? "all")} followChannels={job.FollowChannels}{(created ? "  lookup due now (priority " + Protocol.RefreshNowPriority + ")" : "")}");
                return 0;
            }
            case "list":
            {
                var list = await Sources.ListAsync(store, paths, default);
                if (list.Count == 0) Console.WriteLine("(no followed sources)");
                var states = (await store.ListAsync(paths.JobsDir("refresh"), true, false, default)).ToDictionary(b => Path.GetFileNameWithoutExtension(b.Name), b => b.Tags.GetValueOrDefault(Tags.State));
                foreach (var j in list) Console.WriteLine(Sources.FormatRow(j, states.GetValueOrDefault(j.JobId)));
                return 0;
            }
            case "schedule":
            {
                var sid = PriorityTool.SourceId(a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources schedule <id> off|once|6h|1d|daily@03:00"));
                var j = await Sources.SetScheduleAsync(store, paths, sid, a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("sources schedule <id> off|once|6h|1d|daily@03:00"), default);
                Console.WriteLine($"{sid}: schedule {Schedule.Of(j).Describe()}{(j.NextRefresh != null ? ", next refresh " + j.NextRefresh : ", no further refresh unless triggered (sources refresh " + sid + ")")}");
                return 0;
            }
            case "lookup":
            case "refresh":
            {
                if (sub == "refresh" && a.Has("all-due"))
                {
                    var due = await Sources.DueAsync(store, paths, default);
                    int n = 0; foreach (var d in due) if (await Sources.TriggerAsync(store, paths, d, "refresh", default)) n++;
                    Console.WriteLine($"{n} scheduled refresh(es) made due now at priority {Protocol.RefreshNowPriority}");
                    return 0;
                }
                var sid = PriorityTool.SourceId(a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException($"sources {sub} <id>|--all-due"));
                Console.WriteLine(await Sources.TriggerAsync(store, paths, sid, sub, default) ? $"{sid}: {sub} due now (priority {Protocol.RefreshNowPriority}); the next free downloader runs it" : $"{sid}: a node is running it right now");
                return 0;
            }
            case "priority":
            {
                var what = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources priority <source id|url> N");
                int np = PriorityTool.ParseOption(a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("sources priority <source id|url> N"));
                var sid = PriorityTool.SourceOrVideoId(what);
                List<string>? listed = null;
                if (!sid.StartsWith("v-") && !a.Has("no-list"))
                {
                    var w = (await Sources.ListAsync(store, paths, default)).FirstOrDefault(x => x.JobId == sid) ?? throw new ArgumentException($"no source {sid}");
                    try { listed = await PriorityTool.ListIdsAsync(new Tools(http, cfg), w, default); }
                    catch (Exception e) { Console.Error.WriteLine($"could not list {sid} with yt-dlp ({e.Message}); only videos with a recorded source are re-tagged (run this on a machine YouTube allows, e.g. a residential one, to match older jobs)"); }
                }
                var r = await PriorityTool.SetSourceAsync(store, paths, sid, np, listed, default);
                Console.WriteLine($"{sid}: priority {np}; {r.Videos} video(s), {r.Changed} job(s) changed, skipped {r.Skipped.Values.Sum()}{(r.Skipped.Count > 0 ? " (" + string.Join(", ", r.Skipped.Select(k => $"{k.Value} {k.Key}")) + ")" : "")}{(r.Matched > 0 ? $"; {r.Matched} older job(s) matched by the listing" : "")}");
                return 0;
            }
            case "remove":
            {
                var id = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources remove <id>");
                await Sources.MigrateLegacyAsync(store, paths, default);
                var name = paths.Job("refresh", id);
                if (await store.GetTagsAsync(name, default) == null) { Console.Error.WriteLine($"no source {id}"); return 1; }
                await store.DeleteAsync(name, default);
                Console.WriteLine($"removed {id} (its downloaded videos stay)");
                return 0;
            }
            case "import":
            {
                var file = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources import <channels.json>");
                var r = await Sources.ImportAsync(store, paths, File.ReadAllBytes(file), every ?? "1d", Sources.ParseTabs(a.Get("tabs")), maxHeight, default, priority);
                Console.WriteLine($"{r.Added} added, {r.Existed} already existed{(r.Skipped > 0 ? $", {r.Skipped} skipped (no channelId)" : "")}");
                return 0;
            }
            default: throw new ArgumentException("sources add <url> | list | remove <id> | import <channels.json> | schedule <id> off|once|6h|1d|daily@03:00 | lookup <id> | refresh <id>|--all-due | priority <source id|url> N");
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

    /// <summary>
    /// `download <url|id>... [--no-process] [--max-height 480] [--format F]`: a one-off download on this node (a yt-dlp node), no queue. The video is
    /// stored like a queued download and recorded as a finished download job (so it is known and tracked); its processing is queued unless --no-process.
    /// </summary>
    public static async Task<int> DownloadAsync(Args a)
    {
        var (cfg, store, paths, http) = Open(a); using var _h = http;
        if (a.Positional.Count == 0) throw new ArgumentException("download <url|id>... [--no-process] [--max-height 480] [--format F]");
        var tools = new Tools(http, cfg); int maxHeight = a.Get("max-height") is { } h ? int.Parse(h) : 480; bool queue = !a.Has("no-process");
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        int failed = 0;
        foreach (var input in a.Positional)
        {
            var id = System.Text.RegularExpressions.Regex.IsMatch(input, @"^[A-Za-z0-9_-]{11}$") ? input : YtDlp.VideoId(input) ?? throw new ArgumentException($"not a video url or id: {input}");
            var job = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={id}", maxHeight, a.Get("format")); job.JobId = id; job.Source = "one-off";
            var ctx = new JobContext { Job = job, Stage = "download", BlobName = paths.Job("download", id), LeaseId = "", Worker = 0, Store = store, Paths = paths, NodeId = cfg.NodeId ?? "", Token = cts.Token, ThreadShare = () => 1, Report = (s, p) => Console.WriteLine($"  {s} {p}") };
            try
            {
                await new DownloadStage(tools, store, new DownloadGate()) { QueueProcessing = queue }.RunAsync(ctx);
                job.FinishedAt = Protocol.NowUtc();
                await store.PutAsync(paths.Job("download", id), job.ToBytes(), Tags.Job("download", "done", job.AddedAt), null, false, cts.Token);
                Console.WriteLine($"{id}: downloaded{(queue ? ", processing queued" : " (processing not queued: `indexnode process --job " + id + "` runs it)")}");
            }
            catch (Exception e) when (e is not OperationCanceledException) { failed++; Console.Error.WriteLine($"{id}: {Redact.Sas(e.Message)}"); Console.Error.WriteLine(Redact.Trace(e)); }
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>`unprocessed`: downloaded videos missing a step's output, with no processing job queued or running (e.g. `download --no-process`).</summary>
    public static async Task<int> UnprocessedAsync(Args a)
    {
        var (_, store, paths, http) = Open(a); using var _h = http; Console.OutputEncoding = System.Text.Encoding.UTF8;
        (string Step, string File)[] outputs = [("frames", "frames/manifest.json"), ("sscd", "sscd/manifest.json"), ("thumbs", "thumbs/manifest.json"), ("transcribe", "asr/" + Transcribe.ModelName + ".zh.json"), ("ocrsubs", "ocr/lines.json"), ("cast", "cast/clusters.json"), ("describe", "describe.json")];
        var ids = (await store.ListAsync(paths.JobsDir("download"), true, false, default)).Where(b => b.Tags.GetValueOrDefault(Tags.State) == "done").Select(b => Path.GetFileNameWithoutExtension(b.Name)).ToList();
        int n = 0;
        foreach (var id in ids)
        {
            var missing = new List<string>();
            foreach (var (step, file) in outputs) if (await store.GetTagsAsync(paths.Video(id, file), default) == null) missing.Add(step);
            if (missing.Count == 0) continue;
            bool queued = false;
            foreach (var st in Protocol.StepStages.Append("process"))
                if (await store.GetTagsAsync(paths.Job(st, id), default) is { } t && t.GetValueOrDefault(Tags.State) is "pending" or "leased") { queued = true; break; }
            if (queued) continue;
            var info = await store.GetAsync(paths.Video(id, "info.json"), default) is { } ib ? StoredJson.Parse(ib.Data, paths.Video(id, "info.json")) : null;
            var dl = await store.GetAsync(paths.Video(id, "downloaded.json"), default) is { } db ? StoredJson.Parse(db.Data, paths.Video(id, "downloaded.json")) : null;
            Console.WriteLine($"{id}  {(info?["duration"]?.GetValue<double>() is { } d ? $"{d / 60:F0} min" : "?"),7}  missing {string.Join(',', missing),-40}  {info?["title"]}");
            n++;
        }
        Console.WriteLine($"{n} unprocessed and unqueued of {ids.Count} downloaded (process one with: indexnode process --job <id>)");
        return 0;
    }

    public static async Task<int> EnqueueAsync(Args a)
    {
        var (cfg, store, paths, http) = Open(a);
        int maxHeight = a.Get("max-height") is { } h ? int.Parse(h) : 480;
        if (a.Has("from")) throw new ArgumentException("enqueue --from watch-later is gone: Watch Later only seeds channels now, use: indexnode seeds watch-later");
        if (a.Positional.Count == 0) throw new ArgumentException("enqueue needs at least one url");
        var format = a.Get("format"); int priority = PriorityTool.ParseOption(a.Get("priority"));
        var tools = new Tools(http, cfg);
        int added = 0, existed = 0;
        var meta = new Dictionary<string, RefreshStage.Entry>();
        foreach (var input in a.Positional)
        {
            var urls = new List<string>();
            if (YtDlp.IsSingleVideoUrl(input)) urls.Add(input);
            else
            {
                Log.Info($"expanding {Redact.Sas(input)}");
                var r = await tools.RunYtDlpAsync(["--flat-playlist", "--no-warnings", "--print", "%(webpage_url,url)s\t" + RefreshStage.ListPrint, input], default);
                if (r.ExitCode != 0) throw new InvalidOperationException(YtDlp.LastErrorLine(r.Stderr));
                foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var cut = line.IndexOf('\t'); var u = cut < 0 ? line : line[..cut];
                    if (!u.StartsWith("http")) continue;
                    urls.Add(u);
                    if (cut >= 0 && RefreshStage.ParseLine(line[(cut + 1)..]) is { } le) meta[u] = le;
                }
            }
            foreach (var url in urls.Distinct())
            {
                var job = JobDescriptor.NewDownload(url, maxHeight, format); job.Priority = priority; if (priority != 0) job.OwnPriority = priority;
                if (!meta.TryGetValue(url, out var m) && YtDlp.IsSingleVideoUrl(url))
                {   // a single video: one metadata call for the classifier (best effort: without it the AI is told the length is unknown)
                    try
                    {
                        var mr = await tools.RunYtDlpAsync(["--no-warnings", "--skip-download", "--print", RefreshStage.ListPrint, url], default);
                        if (mr.ExitCode == 0 && mr.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } ml) m = RefreshStage.ParseLine(ml);
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"enqueue: no metadata for {url}: {e.Message}"); }
                }
                if (m != null) { job.Title = m.Title; job.Duration = m.Duration; job.ChannelId = m.ChannelId; job.ChannelName = m.ChannelName; job.Availability = m.Availability; }
                var created = await ClassifyJobs.CreateUnclassifiedAsync(store, paths, job, default);
                if (created) added++; else existed++;
                Console.WriteLine($"{(created ? "added " : "exists")} {job.JobId} {url}");
            }
        }
        Console.WriteLine($"{added} added, {existed} already existed");
        return 0;
    }

    /// <summary>`availability start [--every 1d]`: creates the daily availability sweep (run by a node with --stages ...,availability; it needs yt-dlp).</summary>
    public static async Task<int> AvailabilityAsync(Args a)
    {
        if (a.Positional.FirstOrDefault() != "start") throw new ArgumentException("availability start [--every 1d]");
        var (_, store, paths, http) = Open(a); using var _h = http;
        var job = AvailabilityStage.NewSweep(a.Get("every") ?? "1d"); RefreshStage.ParseEvery(job.Every);
        bool made = await store.PutAsync(paths.Job("availability", job.JobId), job.ToBytes(), Tags.Job("availability", "pending", job.AddedAt), null, true, default);
        Console.WriteLine(made ? $"availability sweep created (every {job.Every}); a yt-dlp node runs it with --stages ...,availability" : "the availability sweep already exists");
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
            try { failed = (await Tags.FindFullAsync(store, Tags.StateWhere(stage, "failed"), default)).Where(b => b.Name.StartsWith(paths.JobsDir(stage), StringComparison.Ordinal)).ToList(); }
            catch (ForbiddenException)
            {
                failed = (await store.ListAsync(paths.JobsDir(stage), true, false, default)).Where(b => b.Tags.GetValueOrDefault(Tags.State) == "failed").ToList();
            }
            foreach (var b in failed)
            {
                var body = await store.GetAsync(b.Name, default);
                if (body == null) continue;
                var job = JobDescriptor.Parse(body.Data);
                if (job.Reason?.StartsWith("classify:", StringComparison.Ordinal) == true) continue; // a rejection: overrule it with "classify set", not requeue
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
        if (a.Get("max-per-ip") is { } mpi)
        {
            var mc = await ControlWatcher.SetMaxPerIpAsync(store, paths, mpi, cfg.NodeName ?? Environment.UserName, default);
            Console.WriteLine($"control: downloads per IP = {(mc.Downloads?.MaxPerIp is > 0 ? mc.Downloads.MaxPerIp.ToString() : "no cap")}");
            if (level == null) return 0;
        }
        if (level == "gpu-hold")
        {
            var v = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("control gpu-hold <duration like 30m>|off");
            var gc = await ControlWatcher.SetGpuHoldAsync(store, paths, v, cfg.NodeName ?? Environment.UserName, default);
            Console.WriteLine($"control: gpu hold = {gc.Gpu?.Hold ?? "off"} (CPU nodes {(gc.Gpu?.Hold == null ? "claim sscd/clip/cast at once and no gpu-ready markers are written" : "wait that long for a GPU node before taking sscd/clip/cast; markers are written")}; a node's --gpu-fallback overrides this)");
            return 0;
        }
        if (level is not ("run" or "drain" or "stop")) throw new ArgumentException("control needs run, drain, stop, gpu-hold or --max-per-ip N|off");
        var stage = a.Get("stage");
        if (stage != null && !Protocol.Stages.Contains(stage = Protocol.CanonicalStage(stage))) throw new ArgumentException("--stage must be download, process, comments or refresh (watch is the old name)");
        var c = await ControlWatcher.WriteAsync(store, paths, level, stage, a.Get("message"), cfg.NodeName ?? Environment.UserName, default);
        Console.WriteLine($"control: mode={c.Mode} download={c.Effective("download")} process={c.Effective("process")}{(c.Message != null ? " message=" + c.Message : "")}");
        return 0;
    }

    /// <summary>indexnode batch explain [--stages ...] [--batch N] [--top 50]: why the batch selection picks what it picks (read-only).</summary>
    public static async Task<int> BatchAsync(Args a)
    {
        // Main has already taken "batch" off the positionals: the subcommand is the first one left
        if (a.Positional.FirstOrDefault() != "explain") { Console.Error.WriteLine("usage: indexnode batch explain [--stages frames,sscd,...] [--batch 6] [--top 50]"); return 2; }
        var (cfg, store, paths, http) = Open(a); using var _h = http;
        return await ExplainAsync(a, cfg, store, paths, Console.WriteLine);
    }

    /// <summary>The body of `batch explain`, with the store and the output given (tests run it on a fake store).</summary>
    internal static async Task<int> ExplainAsync(Args a, NodeConfig cfg, IStore store, Paths paths, Action<string> say)
    {
        var requested = (a.Get("stages") ?? string.Join(',', StageOrder.PassOrder)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var stages = Protocol.ExpandStages(requested);
        TimeSpan? gpuFlag = a.Get("gpu-fallback") is { } gfv ? GpuRouting.ParseFlag(gfv) : null;
        var opts = new EngineOptions
        {
            NodeId = cfg.NodeId ?? "explain", Name = "explain", Steps = ProcessStage.SupportedSteps(cfg), CanRun = job => !(job.Stage == "process" || Protocol.IsStep(job.Stage)) || ProcessStage.CanRun(job, ProcessStage.SupportedSteps(cfg)),
            Stages = stages, Batch = a.Get("batch") is { } bn ? int.Parse(bn) : 6, Gpu = a.Has("gpu"), GpuFallback = gpuFlag,
        };
        var engine = new NodeEngine(store, paths, opts, new Dictionary<string, IStageHandler>());
        return await engine.ExplainAsync(a.Get("top") is { } tp ? int.Parse(tp) : 50, say, default);
    }

    public static async Task<int> RepackAsync(Args a)
    {
        var (cfg, store, paths, _) = Open(a);
        var only = a.Get("videos")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Concat(a.Positional.Skip(1)).ToList();
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return await Repack.RunAsync(store, paths, cfg.NodeId ?? "repack", only is { Count: > 0 } ? only : null, a.Has("dry"), a.Has("delete-loose"), int.TryParse(a.Get("parallel"), out var par) ? par : Environment.ProcessorCount, Console.WriteLine, cts.Token);
    }

    public static async Task<int> StatusAsync(Args a)
    {
        var (cfg, store, paths, _) = Open(a);
        var now = DateTimeOffset.UtcNow;
        if (a.Has("json")) { Console.WriteLine(StatusSnapshot.Serialize(await StatusSnapshot.BuildAsync(store, paths, now, default))); return 0; }
        Console.WriteLine($"prefix {paths.Prefix}   this node: {cfg.NodeId ?? "(none yet)"}  workers={cfg.Workers ?? 2}  threads={cfg.EffectiveThreads}");

        var layout = await Repack.CountAsync(Repack.Raw(store), paths, default);
        Console.WriteLine($"\nLayout: {layout.Repacked} repacked, {layout.Loose} loose, {layout.Mixed} mixed (0.10.0 nodes refuse loose and mixed dramas until `indexnode repack --delete-loose` has run){(layout.Loose + layout.Mixed > 0 ? "\n  " + string.Join(" ", layout.LooseIds.Concat(layout.MixedIds).Take(20)) : "")}");
        Console.WriteLine("\nNodes");
        var nodeBlobs = await store.ListAsync(paths.NodesDir, false, false, default);
        var nodes = new List<NodeInfo>();
        foreach (var b in nodeBlobs)
            if ((await store.GetAsync(b.Name, default)) is { } c && NodeInfo.Parse(c.Data) is { } n) nodes.Add(n);
        if (nodes.Count == 0) Console.WriteLine("  (none)");
        foreach (var n in nodes.OrderBy(n => n.NodeId))
        {
            Console.WriteLine($"  {n.NodeId,-24} {n.Kind,-8} stages={string.Join('+', n.Stages)} workers={(n.Workers > 0 ? n.Workers.ToString() : string.Join(',', n.Jobs.Select(j => j.Key + "=" + j.Value)))} threads={n.Threads} state={n.EffectiveState(now)} control={n.Control} lastSeen={n.LastSeen}");
            foreach (var r in n.Running)
                Console.WriteLine($"      worker {r.Worker}: {r.Stage}/{r.JobId} {r.Step} {r.Progress}");
        }

        var all = await store.ListAsync(paths.Prefix + "jobs/", true, true, default);
        Console.WriteLine("\nQueue");
        foreach (var stage in Protocol.Stages)
        {
            var inStage = all.Where(b => b.Tags.GetValueOrDefault(Tags.Stage) == stage).ToList();
            var counts = (stage == "download" ? new[] { Tags.Unclassified, "pending", "done", "failed" } : new[] { "pending", "done", "failed" }).Select(s => $"{s}={inStage.Count(b => b.Tags.GetValueOrDefault(Tags.State) == s)}");
            int prio = inStage.Count(b => b.Tags.GetValueOrDefault(Tags.State) == "pending" && Tags.PriorityOf(b.Tags) != 0);
            Console.WriteLine($"  {stage,-9} {string.Join("  ", counts)}  leased={inStage.Count(b => b.LeaseState == "leased")}{(prio > 0 ? $"  pending with priority={prio}" : "")}");
        }
        var (cDay, cDone, cRejected) = await ClassifyCommand.TodayAsync(store, paths, now, default);
        Console.WriteLine($"  classify  unclassified={all.Count(b => b.Tags.GetValueOrDefault(Tags.Stage) == "download" && b.Tags.GetValueOrDefault(Tags.State) == Tags.Unclassified)}  classified today={cDone}  rejected today={cRejected}  (UTC {cDay}; review: indexnode classify list --rejected)");
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

    /// <summary>`indexnode gpu markers [--dry]`: writes the marker of every pending sscd/clip/cast job whose frames are done and that has none (videos framed before 0.6.0), so the GPU container starts for them.</summary>
    public static async Task<int> GpuAsync(Args a)
    {
        var sub = a.Positional.FirstOrDefault();
        if (sub != "markers") throw new ArgumentException("gpu markers [--dry]");
        var (cfg, store, paths, http) = Open(a);
        using var _ = http;
        int made = 0, have = 0, waiting = 0;
        foreach (var stage in GpuRouting.MarkerStages)
        {
            var pending = (await Tags.FindFullAsync(store, Tags.StateWhere(stage, "pending"), default)).Where(b => b.Name.StartsWith(paths.JobsDir(stage), StringComparison.Ordinal)).ToList();
            foreach (var b in pending)
            {
                var id = Path.GetFileNameWithoutExtension(b.Name);
                bool ready = true;
                foreach (var need in Protocol.NeedsOf(stage)) if (await store.GetTagsAsync(paths.Video(id, need), default) == null) { ready = false; break; }
                if (!ready) { waiting++; continue; }
                if (await store.GetTagsAsync(GpuRouting.MarkerName(paths, stage, id), default) != null) { have++; continue; }
                if (!a.Has("dry")) await GpuRouting.WriteMarkerAsync(store, paths, stage, id, default);
                made++;
            }
        }
        Console.WriteLine($"gpu markers: {(a.Has("dry") ? "would write" : "wrote")} {made}, {have} already there, {waiting} jobs still wait for their frames");
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
                case "window": RefreshStage.ParseEvery(val); file.Window = val; break;
                case "ai.base-url": (file.Ai ??= new()).BaseUrl = val; break;
                case "ai.key": (file.Ai ??= new()).Key = val; break;
                case "ai.text-model": (file.Ai ??= new()).TextModel = val; break;
                case "ai.vision-model": (file.Ai ??= new()).VisionModel = val; break;
                case "ai.api": if (!AiConfig.ValidApi(val.ToLowerInvariant())) throw new ArgumentException("ai.api: chat, responses or auto"); (file.Ai ??= new()).Api = val.ToLowerInvariant(); break;
                case "jobs": file.Jobs = NodeConfig.ParseJobs(val); break;
                case "threads": file.Threads = int.Parse(val); break;
                case "max-heavy": file.MaxHeavy = int.Parse(val); break;
                case "workers": file.Workers = int.Parse(val); break;
                case "worker-threads": file.WorkerThreads = int.Parse(val); break;
                case "device": file.Device = OrtOptions.ParseDevice(val); break;
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
