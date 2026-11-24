using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IndexNode;

public sealed class Args
{
    public List<string> Positional { get; } = new();
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>0.13.32: every value of a repeated option (--set a=1 --set b=2).</summary>
    public Dictionary<string, List<string>> Multi { get; } = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "ids", "once", "keep-sample", "exclude-self", "help", "gpu", "follow-channels", "dry", "qr", "no-sas", "local-analyzer", "force", "json", "delete-loose", "split", "fp32", "download", "shadow", "repair-only", "no-table", "warm", "download-service" };

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
                else if (!Flags.Contains(key) && q.Count > 0 && !LooksLikeOption(q.Peek())) val = q.Dequeue();   // 0.13.40: an option never takes the NEXT OPTION as its value (`--warm --download-service` lost the second one; `--repair-only --stages x` lost the stages). A value that starts with "--" (a video id) is written --key=value, or after a bare "--"
                a.Options[key] = val ?? "true"; (a.Multi.TryGetValue(key, out var ml) ? ml : a.Multi[key] = []).Add(val ?? "true");
            }
            else a.Positional.Add(t);
        }
        return a;
    }

    /// <summary>--video=&lt;id&gt; is accepted wherever a video id is taken (--job, --videos): an id may start with '-', which no other spelling can carry.</summary>
    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : key is "job" or "videos" && Options.TryGetValue("video", out var vid) ? vid : null;
    public bool Has(string key) => Options.ContainsKey(key);
    /// <summary>"--name" or "--name=value" where name starts with a lowercase letter and holds only lowercase letters, digits and '-': an option, not a value. A YouTube id that starts with "--" (11 characters, e.g. --QfpuGhs0U, --DL3_0DNjg) is taken as an option only when it looks exactly like one; such ids are written --video=&lt;id&gt; anyway.</summary>
    internal static bool LooksLikeOption(string t)
    {
        if (!t.StartsWith("--", StringComparison.Ordinal) || t.Length < 4) return false;
        var name = t[2..]; var eq = name.IndexOf('='); if (eq >= 0) name = name[..eq];
        return name.Length >= 2 && name[0] is >= 'a' and <= 'z' && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }
}

public static class Program
{
    public const string Usage = """
        indexnode run [--shadow --out-local <dir> [--set versions.cast=2]... [--with-import process,group]] [--no-precheck] [--stages download,process,comments] [--no-refresh] [--gpu] [--device cpu|cuda] [--exit-when-idle 10m] [--allow-out-of-region] [--lanes 1] [--ai-stages] [--gpu-fallback 30m|off] [--once] [--max-jobs N] [--jobs process=2,download=1] [--threads N] [--node-name N]
                      [--status-out <file> [--status-every 60s]] [--budget N] [--schedule "<cron>" --window 3h] [--ai-base-url U --ai-key K --ai-text-model M --ai-vision-model M --ai-api chat|responses|auto --ai-session-id S]
        indexnode cast recluster <id>|--ids <file> [--merge-at D] [--split [--split-sim S] [--split-min N]] [--threshold T] [--min-faces N] [--local <root>] [--out-local <dir>]   (0.13.23: re-clusters a drama's stored faces (cast.zip), read-only; old against new groups, merges with distances, proposed splits)
        indexnode cast reembed <id>|--ids <file> --rec w600k_mbf|w600k_r50|glintr100 [--rec-path <file.onnx>] [--decoder libjpeg|ffmpeg] [--fp32] [--out-local <dir>] [--threads N] [--local <root>]   (0.13.23, a dev tool; --fp32 also writes faces_<model>.f32, unrounded: re-embeds a drama's stored faces from the stored frames and landmarks with another recognizer; writes faces_<model>.f16 + json locally, read-only on the store)
        indexnode sources add <url> [--every 1d|6h|daily@03:00] [--backfill N] [--follow-channels] [--tabs videos|videos,shorts (default videos)] [--max-height 480] [--priority N]
        indexnode sources migrate-seen [--dry-run]   (0.13.2: seen.json ids become members of members.json, then seen.json goes; a refresh does it for its own source on first touch)
        indexnode sources set-tabs videos|videos,shorts --all [--dry-run]   (0.13.2: the tabs of every followed source; the default is videos only)
        indexnode sources list | remove <id> | import <channels.json> [--every 1d] [--tabs videos,shorts] [--priority N]
        indexnode sources schedule <id> dynamic|off|once|1d|6h|daily@03:00   (dynamic: the next check follows the age of the newest upload; new sources are "once" unless --every says otherwise)
        indexnode sources lookup <id>   (full listing again: every member recorded, every unknown video queued)
        indexnode sources refresh <id>|--all-due   (incremental: only videos new since the last run; due now at priority 500)
        indexnode ai test [--ai-base-url ...]
        indexnode ai set <videoId>... real|ai [--reason R] [--dry-run]   (0.13.44: the owner's verdict on an upload; it wins over the probe's flag. real releases its AI holds, ai holds its pending entries)
        indexnode enqueue <url|playlist-url>... [--max-height 480] [--format F] [--priority N]
        indexnode priority set <N> --job <id>... | --from <file> | --channel <id|url> [--stage download] [--with-steps]
        indexnode priority list [--stage S] [--top 20]
        indexnode sources priority <source id|url|video url> N   (re-tags the source's pending jobs; lists the playlist/channel with yt-dlp to match older jobs: run it on a machine YouTube allows, e.g. a residential one)
        indexnode enqueue --from watch-later [--max-height 480]
        indexnode browser login | status
        indexnode tools install --ytdlp-zipapp
        indexnode serve [--warm] [--port 8080] [--bind 0.0.0.0] [--threads N] [--block-cache-mb 512] [--concurrency 2] [--input-local <dir>]   (0.12.0: the query service, warm and read-only on the store: POST /v1/precheck (sampled frames + audio -> the pre-check record), POST /v1/search (screenshot image or vector -> best frames), GET /v1/health; SAS from INDEXNODE_SAS_URL)
        indexnode precheck <id|url>... [--ids <file>] [--output-local <dir>] [--window-position 0.5] [--order-tolerance-seconds 120] [--keep-sample] [--exclude-self] [--threads N] [--input-local <dir>]   (0.12.0: ONLY the pre-check, read-only on the store: sample, frames, SSCD, lookup, AI probe -> <dir>/<id>/precheck.json, default ./precheck-out)
        indexnode sample <id|url> --output-local <dir> [--timebox 120]   (0.13.16: ONLY the sample download (two 5-minute segments at 0.30/0.70, 360p, the first segment's audio, info, description, thumbnail) into <dir>/<id>/; writes no store or queue; prints the time split; run it on lancecl1, never the hexad host)
        indexnode check <id|url> --input-local <dir> [--exclude-self] [--output-local <dir>] [--timebox 120] [--threads N]   (0.13.16: ONLY the check on those local sample files (the search index is read from the store, read-only): record and time split; writes no store or queue; run it on the hexad host)
        indexnode jobs sample-first [--dry-run]   (0.13.16: pending downloads become sample entries, pending process entries get a check entry; priority and due kept; batched, idempotent; prints the counts)
        indexnode seq assign [--dry-run]   (0.13.10: give every existing drama its sequence number, by first pre-check)
        indexnode seq clear-ungrouped [--dry-run]   (0.13.43, once at the cutover with no node running: numbers given at a pre-check to dramas not yet grouped are cleared; a drama is numbered when its group step starts)
        indexnode groups step <id> [--dry-run] [--threads N]   (0.13.15: the group step of ONE drama exactly as the node runs it (queue state, FramesCache, dedup set); --dry-run writes nothing to the store or the queue and prints the record it would write and the time split: membership with join-source counts, search, dedup, write-free total)
        indexnode precheck whatif [--ids <file>] [--limit N] [--pairs <file: 'idA idB' lines>] [--threads N]   (0.13.15: the two-segment pre-check on each drama's stored frames (itself excluded) against the old single window: decisions, possible false skips, AI flag changes, run time; works with --limit 1)
        indexnode groups enqueue [--dry-run]   (0.13.10: add the group step for every processed drama, in sequence order; create-only)
        indexnode frames convert [--dry-run] [--threads N] [--limit N]   (0.13.9: write index/search/frames/<id>.bin for every drama from its centers.zip; create-only, skips dramas under a claim)
        indexnode centers backfill [--ids <file>] [--since <time|6h|2d>] [--threads N] [--dry] [--limit N] [--input-local <dir>] [--output-local <dir>] [--local <dir>]   (build centers.zip for uploads that have sscd.zip and no current centers.zip)
        indexnode groups build [--dry-run] [--threads N] [--check-pairs <file>]   (the one-time drama groups by the frame-level full check; --check-pairs: lines "idA idB", reports how many share a group; then the search-index job keeps them)
        indexnode search-index build [--new-mean] | update [--threads N] [--input-local <dir>] [--output-local <dir>] [--local <dir>]   (the global search table under index/search/vN/)
        indexnode search --vector <f32|f16 file of 512> | --image <file> [--trim] [--top-centers 300] [--top 20] [--threads N] [--input-local <dir>] [--output-local <dir>] [--local <dir>]   (screenshot search over the centers; "search <text> --job <id>" is the clip text search)
        indexnode ai score [--ids <file>] [--all-frames] [--threads N] [--input-local <dir>] [--output-local <dir>] [--local <dir>]   (0.12.0: the AI-drama probe on processed uploads' stored sscd.zip -> videos/<id>/ai.json)
        indexnode status [--json] [--download]   (0.13.26: --download counts the full download queue, which only the download service otherwise reads)
        indexnode jobs migrate --to log [--dry-run]   (0.13.0: builds the first queue snapshot from today's job blobs; refuses while a job is running)
        indexnode jobs repair-steps [--dry-run]   (0.13.2: steps in a done list whose output is not on the store are taken out; a finished subject missing an output is re-opened with only the missing steps)
        indexnode jobs list [--stage S] | priority <subject> N | retry [--stage S] [<subject>] | release <subject>   (the queue)
        indexnode video <id>   (what the queue knows about one video)
        indexnode videos aggregate [--dry-run] [--threads N]   (builds index/data/videos.agg, one block per upload, from the per-upload video.json files, and writes videos/<id>/stretches.json; per-upload files are never changed)
        indexnode videos backfill [--dry-run] [--threads N]   (0.13.2: create index/videos/<id>/video.json for every upload that has none, from idx_summary, info.json, precheck.json, transcribe.zip and the AI probe; create-only; a video under an active claim is skipped and reported. Run once, from a westus2 node, before the search-index job relies on it)
        (a video id that starts with '-': --video=<id>, or put it after a bare --, e.g. indexnode classify set drama -- --QfpuGhs0U)
        indexnode repack [--videos <id>,<id>] [--dry] [--delete-loose] [--parallel <cores>]   one-off 0.9 -> 0.10.0 layout conversion (run on a node with all nodes stopped)
        indexnode process --job <id> [--steps cast,describe] [--threads N] [--ai-...]   (redo steps of one processed job in the store: real-data run; then 'publish --job <id>')
        indexnode describe ending --backfill [--dry-run N] [--limit N]   (0.10.4: adds the story ending to existing describe.zip; several dramas per AI call; --dry-run N prints the first prompt of N dramas, no AI call, nothing written)
        indexnode describe --local <job folder> [--dry] [--out <file>]   (AI step; --dry writes the request it would send, no AI call)
        indexnode publish [--out <dir>] [--job <id>] [--since <time>] [--local <dir>] [--force]   (site packs per PACK.md; default out: %LOCALAPPDATA%\indexnode\site)
        indexnode medusa-setup [--qr] [--file <path>.medusa-node.json] [--name N] [--conditions wifi+charging|wifi] [--daily-cap N] [--budget N] [--schedule C] [--window W] [--no-sas] [--no-check]
        indexnode dev cast --video <file> --out <dir> [--model buffalo_l|yunet-sface] [--threshold D] [--merge D] [--border D]
        indexnode requeue [--stage S]
        indexnode classify set <videoId> drama|not [--reason R]   (owner override: writes the decision and sets the download job)
        indexnode classify list --rejected [--since 1d]
        indexnode classify requeue --source <id>   (sets the source's pending downloads back to unclassified)
        indexnode control run|drain|stop [--stage download|process|comments] [--message M] [--max-per-ip N|off] [gpu-hold <30m|off>]
        indexnode control gpu-hold <duration like 30m>|off
        indexnode control version <cast|actors> <n>|off   (0.13.32: the version a step must have finished at; raising it makes every done upload's step ready again in its repair mode, in seq order)
        indexnode run --repair-only   (0.13.37: claims only stale versioned-step repairs and actors / actors-index; never pending or missing process work)
        indexnode queue whatif --repair-only   (the same filter in the readiness check)
        indexnode queue whatif --stage <stage or step> [--set versions.cast=2] [--set stage.actors=run] [--top N] [--ids]   (0.13.32: read-only: what would be ready, and the blockers)
        indexnode queue rebuild-snapshot [--dry-run]   (0.13.35: replays the sealed log segments and puts back the done entries an older snapshot compaction dropped; writes only if the snapshot is unchanged)
        indexnode control media-table-read on|off   (0.13.37: default off; readers use video.json and the aggregate; writes stay dual when a table is configured)
        indexnode media selftest | media verify [--limit N]   (0.13.37: selftest: the table's calls against a scratch partition, then cleanup; verify: read-only, row against video.json section by section)
        indexnode control ai-dramas on|off
        indexnode region [--stages a,b]   (0.13.17: read-only: what this machine detects (IMDS, placement, env), control.json's region, and whether a node here could run the region-only stages)
        indexnode control region <name like westus2>|off   (0.13.17: check, process and its steps, group and search-index run only on nodes in this Azure region; run --allow-out-of-region for a one-item test)
        indexnode control --max-per-ip N|off
        indexnode --version
        indexnode config set-sas <url>
        indexnode config set-media-table-sas <url>   (0.13.37: the Azure Table `media`; or INDEXNODE_MEDIA_TABLE_SAS_URL, or `media-table-sas` in the config file)
        indexnode media import [--out-local DIR | --from-local DIR]   (0.13.37: the aggregate -> the table, once)
        indexnode seq renumber [--out-local DIR | --from-local DIR]
        indexnode config set <budget|schedule|window|ai.base-url|ai.key|ai.text-model|ai.vision-model|ai.api|jobs|threads|model-url|model-path|node-name|ytdlp.command|ytdlp.js-runtime|browser.exe|browser.profile-dir|browser.port|serve.token|check.url|check.token|check.timeout-seconds|precheck.enabled|precheck.tau|precheck.skip-share|precheck.partial-share|precheck.duration-percent|precheck.duration-min|precheck.top-centers|precheck.vote-uploads|precheck.window-seconds|precheck.language-seconds|precheck.language-min-seconds|precheck.window-position|precheck.ai-flag|sample-budget> <value>
        All commands take --prefix (default index/). INDEXNODE_SAS_URL overrides the stored SAS.
        """;

    public static async Task<int> Main(string[] argv)
    {
        try
        {
            // 0.13.34: a background task nobody awaited (an HTTP copy into a file that hits a full disk) must not take the node down: it is logged and observed
            TaskScheduler.UnobservedTaskException += (_, e) => { e.SetObserved(); Log.Info($"background task failed (ignored): {Redact.Sas(DiskGuard.Root(e.Exception).Message)}"); };
            var a = Args.Parse(argv);
            if (a.Has("version") && a.Positional.Count == 0) { Console.WriteLine(Commands.VersionString); return 0; }
            if (a.Positional.Count == 0 || a.Has("help")) { Console.WriteLine(Usage); return a.Has("help") ? 0 : 2; }
            var cmd = a.Positional[0].ToLowerInvariant();
            a.Positional.RemoveAt(0);
            try { FramesCache.Configure(NodeConfig.Load()); } catch (Exception e) when (e is not OperationCanceledException) { }
            StoreMeter.Current.Limits = NodeConfig.TryLoadStorageLimits();   // 0.13.18: the breaker settings (config storage.*)
            if (a.Get("storage-max") is { } smax) { var n = int.Parse(smax); StoreMeter.Current.Limits.MaxPerStep = n; StoreMeter.Current.Limits.MaxPerMin = Math.Max(StoreMeter.Current.Limits.MaxPerMin, n); StoreMeter.Current.Limits.WarnPerMin = Math.Max(StoreMeter.Current.Limits.WarnPerMin, n); StoreMeter.Current.CliMax = n; }   // 0.13.9: the node's local copy of the frames files (index.local-cache-gb)
            var rc = await Dispatch(cmd, a);
            // 0.13.18: a write-free command (check --input-local, groups step --dry-run, whatif, region, ...) prints its per-site table at the end
            if (cmd is not ("run" or "serve" or "precheck-serve" or "status" or "config" or "control") && StoreMeter.Current.Total > 0 && !a.Has("json")) Console.WriteLine("\n" + StoreMeter.Current.TableText());
            return rc;
        }
        catch (StorageBudgetExceeded e) { Console.Error.WriteLine("error: " + e.Message); if (StoreMeter.Current.Total > 0) Console.Error.WriteLine(StoreMeter.Current.TableText()); return 1; }
        catch (QueueNotMigratedException e) { Console.Error.WriteLine(e.Message); return 1; }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + Redact.Sas(e.Message));
            Console.Error.WriteLine(Redact.Trace(e));
            if (StoreMeter.Current.Total > 0 && e is StorageBudgetExceeded) Console.Error.WriteLine(StoreMeter.Current.TableText());
            return 1;
        }
    }

    private static async Task<int> Dispatch(string cmd, Args a)
    {
        {
            return cmd switch
            {
                "run" => await Commands.RunAsync(a),
                "enqueue" => await Commands.EnqueueAsync(a),
                "download" => await Commands.DownloadAsync(a),
                "precheck" => await PreCheckCommand.RunAsync(a),
                "sample" => await SampleCommands.SampleAsync(a),
                "check" => await SampleCommands.CheckAsync(a),
                "region" => await RegionCommand.RunAsync(a),   // 0.13.17: read-only diagnostic of the region rule
                "serve" or "precheck-serve" => await ServeCommand.RunAsync(a),   // precheck-serve: the old name, a hidden alias
                "unprocessed" => await Commands.UnprocessedAsync(a),
                "seeds" => await Commands.SeedsAsync(a),
                "dev" => await DevCommands.RunAsync(a),
                "stages" => await StagesCommand.RunAsync(a),
                "ensure" => await EnsureCommand.RunAsync(a),
                "repack" => await Commands.RepackAsync(a),
                "jobs" => await JobsCommand.RunAsync(a),
                "trash" => await JobsCommand.TrashAsync(a),
                "video" => await JobsCommand.VideoAsync(a),
                "videos" => await VideosBackfill.RunAsync(a),
                "scripts" => await ScriptsCommand.RunAsync(a),
                "search" => a.Has("vector") || a.Has("image") ? await CentersCommands.SearchAsync(a) : await Commands.SearchAsync(a),
                "centers" => await CentersCommands.CentersAsync(a),
                "search-index" => await CentersCommands.SearchIndexAsync(a),
                "groups" => await GroupsCommand.RunAsync(a),
                "seq" when a.Positional.FirstOrDefault() == "renumber" => await MediaCommand.RenumberAsync(a),
                "media" => await MediaCommand.RunAsync(a),
                "seq" => await SeqCommand.RunAsync(a),
                "queue" => await QueueCommand.RunAsync(a),
                "frames" => await FramesCommand.RunAsync(a),
                "status" => await Commands.StatusAsync(a),
                "requeue" => await JobsCommand.RunAsync(Args.Parse(["retry", .. (a.Get("stage") is { } rs ? new[] { "--stage", rs } : [])])),
                "classify" => await ClassifyCommand.RunAsync(a),
                "availability" => await Commands.AvailabilityAsync(a),
                "control" => await Commands.ControlAsync(a),
                "config" => Commands.Config(a),
                "browser" => await Commands.BrowserAsync(a),
                "tools" => await Commands.ToolsAsync(a),
                "sources" => await Commands.SourcesAsync(a),
                "priority" => await Commands.PriorityAsync(a),
                "ai" => a.Positional.FirstOrDefault() == "score" ? await CentersCommands.AiScoreAsync(a) : a.Positional.FirstOrDefault() == "set" ? await Commands.AiSetAsync(a) : await Commands.AiAsync(a),
                "publish" => await SiteCommands.PublishAsync(a),
                "cast-candidates" => await CastCandidates.RunAsync(a),
                "cast" => a.Positional.FirstOrDefault() == "recluster" ? await CastRecluster.RunAsync(a) : a.Positional.FirstOrDefault() == "reembed" ? await CastReembed.Main2(a) : Fail("cast: recluster <id>|--ids <file> [--merge-at D] [--split] [--out-local <dir>]"),
                "actors" => await ActorsCommand.RunAsync(a),
                "thumbs-bench" => await ThumbsBench.RunAsync(a),
                "dino-bench" => await DinoBench.RunAsync(a),
                "describe" => await SiteCommands.DescribeAsync(a),
                "process" => await SiteCommands.ProcessAsync(a),
                "medusa-setup" => SiteCommands.MedusaSetup(a),
                _ => Fail($"unknown command '{cmd}'"),
            };
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

    /// <summary>The shadow store of a --shadow run (its append overlay is saved at the end).</summary>
    internal static ShadowStore? Shadow;
    internal static (NodeConfig Cfg, IStore Store, Paths Paths, HttpClient Http) Open(Args a)
    {
        var cfg = NodeConfig.Load();
        if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or set INDEXNODE_SAS_URL");
        var http = NewHttp();
        IStore rest = new RestStore(http, cfg.Sas);
        if (a.Has("shadow")) { var od = a.Get("out-local") ?? throw new ArgumentException("--shadow needs --out-local <dir>: the run reads the real store and writes only there"); rest = Shadow = new ShadowStore(rest, od); Console.Error.WriteLine($"indexnode: SHADOW run: reads the real store, writes nothing to it; every write goes under {od}"); }
        var wrapped = StageZipStore.Wrap(rest, Shadow != null ? Path.Combine(Shadow.Dir, "zips") : null);
        if (!string.IsNullOrWhiteSpace(cfg.MediaTableSas) && !a.Has("no-table"))
        {
            IMediaTable table = new RestMediaTable(http, cfg.MediaTableSas);
            if (Shadow != null) { var overlay = new ShadowMediaTable(table, Shadow.Dir); MediaTables.Attach(Shadow, overlay); }   // the shadow's table: reads the real one, writes under <out-local>/table/
            else { if (a.Has("out-local")) table = new ReadOnlyMediaTable(table); MediaTables.Attach(rest, table); }          // --out-local without --shadow: local only, a table write throws
        }   // 0.13.37: per-upload facts live in the table `media`
        return (cfg, wrapped, new Paths(a.Get("prefix") ?? "index/"), http);
    }

    public static string VersionString => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    public static async Task<int> RunAsync(Args a)
    {
        var (cfg, store, paths, http) = Open(a);
        await JobQueue.RequireSnapshotAsync(store, paths, default);   // 0.13.0 cutover: no queue, no node (and never old job blobs)
        // --gpu: a node that runs only the GPU stages (sscd, clip, cast, ocrsubs) on the CUDA provider and never falls back to the CPU
        bool gpu = a.Has("gpu");
        var device = OrtOptions.ParseDevice(gpu ? "cuda" : a.Get("device") ?? Environment.GetEnvironmentVariable("INDEXNODE_DEVICE") ?? cfg.Device);
        OrtOptions.Device = device;
        OrtOptions.DeviceId = int.TryParse(Environment.GetEnvironmentVariable("INDEXNODE_CUDA_DEVICE"), out var cudaDev) ? cudaDev : 0;
        if (gpu && OrtOptions.Device != "cuda") throw new ArgumentException("--gpu needs --device cuda");
        var requested = (a.Get("stages") ?? (gpu ? string.Join(',', GpuRouting.MarkerStages) : string.Join(',', Protocol.DefaultNodeStages))).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var stages = Protocol.ExpandStages(requested);
        foreach (var s in stages) if (!Protocol.Stages.Contains(s)) throw new ArgumentException($"unknown stage '{s}'");
        if (!gpu) stages = DownloaderNode.WithRefresh(stages, a.Has("no-refresh")); // 0.10.3: a download node also lists sources (a lookup on add, scheduled refreshes), unless --no-refresh
        if (gpu)
        {
            stages = GpuRouting.GpuNodeStages(stages);
            if (stages.Length == 0) throw new ArgumentException("--gpu runs only sscd, clip, cast and ocrsubs; --stages names none of them");
        }
        // 0.13.17: the region rule, before anything is claimed (the write-free check/groups-step/whatif commands never come through here)
        if (stages.Any(s => RegionGate.OnlyStages.Contains(s)))
        {
            var ctlRegion = ControlFile.Parse((await store.GetAsync(paths.Control, default))?.Data).Region;
            var own = await RegionGate.DetectAsync(null, Environment.GetEnvironmentVariable("INDEXNODE_REGION"), null, cfg.StorageRegion ?? "westus2", default);
            Console.Error.WriteLine($"indexnode: region {own.Region ?? "(none)"} (source: {own.Source}), control region {ctlRegion ?? "(none)"}");
            var rv = RegionGate.Evaluate(stages, ctlRegion, own, a.Has("allow-out-of-region"));
            if (rv.Warning != null) Console.Error.WriteLine("indexnode: " + rv.Warning);
            if (!rv.Run) { Console.Error.WriteLine(rv.Error); return 4; }
        }
        TimeSpan? exitIdle = a.Get("exit-when-idle") is { } ew ? RefreshStage.ParseEvery(ew) : null;
        foreach (var old in new[] { "jobs", "max-heavy", "workers", "batch", "batch-hours" }) if (a.Get(old) != null) Console.Error.WriteLine($"indexnode: --{old} is ignored since 0.13.0 (one video per claim; --lanes N runs more claims at once)");
        int lanes = a.Get("lanes") is { } ln ? int.Parse(ln) : 1;
        if (lanes < 1) throw new ArgumentException("--lanes needs at least 1");
        int workers = lanes;
        int? workerThreads = a.Get("worker-threads") is { } wt ? int.Parse(wt) : cfg.WorkerThreads;
        int threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads;
        var (nodeId, nodeName) = cfg.ResolveIdentity(a.Get("node-name"), Environment.GetEnvironmentVariable(NodeConfig.NodeNameEnv));
        try { var lf = RollingFile.FileFor(Environment.GetEnvironmentVariable("INDEXNODE_LOG_DIR") ?? RollingFile.DefaultDir(), nodeName); Log.UseFile(new RollingFile(lf)); Log.Info($"logging to {lf}"); } catch (Exception e) { Console.Error.WriteLine("indexnode: no log file (" + e.Message + ")"); }

        var tools = new Tools(http, cfg);
        if (a.Get("child-timeout-minutes") is { } ctm) tools.Limits = tools.Limits with { Timeout = TimeSpan.FromMinutes(double.Parse(ctm, System.Globalization.CultureInfo.InvariantCulture)) };
        if (a.Get("child-idle-minutes") is { } cim) tools.Limits = tools.Limits with { Idle = TimeSpan.FromMinutes(double.Parse(cim, System.Globalization.CultureInfo.InvariantCulture)) };
        try { await tools.CheckFfmpegAsync(stages, default); }
        catch (InvalidOperationException e) { Console.Error.WriteLine("indexnode: " + e.Message); return 2; }
        var gate = new DownloadGate();
        NodeEngine? engineRef = null;
        var childProbe = await ActorsBuild.ResolveProbeAsync(store, paths, a.Get("child-probe"), default);
        if (childProbe == null) Log.Info($"actors: this node does not claim the actors stage: no child probe (no --child-probe and no {ActorsBuild.StoreProbe(paths)} in the store); without it no child unit would be left out");
        bool aiStages = a.Has("ai-stages");
        TimeSpan? gpuFlag = a.Get("gpu-fallback") is { } gfv ? GpuRouting.ParseFlag(gfv) : null; // null: follow control.json gpu.hold (absent = no hold)
        var preStage = new PreCheckStage(tools, MakePreCheck(tools, cfg, http, store, paths, b => FreshFromQueue(engineRef, b)), NoPre(a, cfg.PreCheck), Commands.VersionString);
        var downloader = cfg.Downloader();
        var handlers = new Dictionary<string, IStageHandler>(DownloaderNode.Handlers(tools, store, gate, http, downloader))   // 0.13.42: download, sample, refresh, availability: the handlers a phone node runs too
        {
            ["precheck"] = preStage,
            ["check"] = new CheckStage(preStage),
            ["process"] = new ProcessStage(tools, store, cfg, http) { GpuFlag = gpuFlag },
            ["group"] = new GroupStage(store, cfg.PreCheck),
            ["search-index"] = new SearchIndexStage(store, cfg.Search?.KeepVersions ?? 3, cfg.PreCheck),
            ["actors-index"] = new ActorsIndexStage(store),
            ["classify"] = new ClassifyStage(store) { Ai = () => ProcessStage.Ai is { Configured: true } ai0 ? (new AiClient(http, ai0), ai0.TextModel ?? ai0.VisionModel!) : null },
        };
        if (childProbe != null)
        {
            var actorsStage = ActorsCommand.NewStage(store, http, a, false, null, childProbe); handlers["actors"] = actorsStage;
            // 0.13.32: this node can make the repaired cast state (it has the r50 model and the child probe): the cast step's repair mode, and the actors job that reads it
            ((ProcessStage)handlers["process"]).CastRepair = async (c, t) => await ActorsCommand.CastRepairAsync(actorsStage, c, t);
            StepVersions.SetCode("cast", StepVersions.CastRepaired); StepVersions.SetCode("actors", StepVersions.ActorsRepaired);
        }   // no probe: no handler, so the node never claims actors
        foreach (var st in Protocol.StepStages) handlers[st] = handlers["process"]; // a step job is a process job with one step
        var limits = BuildLimits(a, cfg, nodeId);
        var ai = AiConfig.Resolve(cfg.Ai, k => a.Get(k), Environment.GetEnvironmentVariable);
        Redact.Register(ai.Key);
        ProcessStage.Ai = ai;
        var opts = new EngineOptions
        {
            Device = () => OrtOptions.Device,
            NodeId = nodeId, Name = nodeName, AiStages = aiStages && ai.Configured, Ready = new ReadyOptions(cfg.Actors?.IndexMinUploads ?? 25, cfg.Actors?.IndexMaxWaitMinutes ?? 10),
            Version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0",
            Steps = ProcessStage.SupportedSteps(cfg),
            Stages = stages, Workers = workers, WorkerThreads = workerThreads, Threads = threads, Once = a.Has("once"), Gpu = gpu, ExitWhenIdle = exitIdle,
            GpuFallback = gpuFlag,
            FetchNext = DownloaderNode.FetchNext(http, downloader, nodeId),
            HeadBuiltAt = async ct => (await SearchData.LoadAsync(store, paths, null, ct))?.BuiltAt,
            EnsureVideoJson = async (st, pa, vid, t) => { await VideosBackfill.EnsureAsync(st, pa, vid, new PreCheckSettings().AiFlag, t); },
            Kind = gpu ? "gpu" : "desktop",
            RepairOnly = a.Has("repair-only"), Shadow = a.Has("shadow"), ControlOverride = a.Has("shadow") && a.Multi.GetValueOrDefault("set") is { Count: > 0 } shadowSets ? (c => WhatIf.Apply(c, shadowSets)) : null, MaxJobs = a.Get("max-jobs") is { } mj ? int.Parse(mj) : null, Limits = limits,
            StorageHost = Uri.TryCreate(cfg.Sas, UriKind.Absolute, out var sasUri) ? sasUri.Host : null, StorageRegion = cfg.StorageRegion ?? "westus2",
        };
        if (aiStages && !ai.Configured) Console.Error.WriteLine("indexnode: --ai-stages needs an AI endpoint (--ai-base-url ... or the config); describe and translate stay off");
        if (a.Get("with-import") is { } imports)
        {   // 0.13.32: the lines the imports WOULD append go to the shadow overlay's queue log only (the real store is never written)
            if (Shadow == null) { Console.Error.WriteLine("indexnode: --with-import needs --shadow"); return 2; }
            await JobQueue.RequireSnapshotAsync(store, paths, default); var iq = new JobQueue(store, paths, "shadow-import"); await iq.LoadAsync(default);
            foreach (var what in imports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (what == "group") { var gr = await QueueCommand.ImportDoneGroupAsync(iq, store, paths, false, default); Console.Error.WriteLine($"indexnode: shadow import group: {gr.GroupDone} group done, {gr.Seqs} seq, {gr.Joins} join, {gr.Holds} holds (overlay only)"); }
                else if (what == "process") { var pr = await QueueCommand.ImportDoneProcessAsync(iq, store, paths, false, default); Console.Error.WriteLine($"indexnode: shadow import process: {pr.Imported} imported, {pr.Partial} lack a zip, {pr.NotFull} not full (overlay only)"); }
                else { Console.Error.WriteLine($"indexnode: --with-import {what}: process and group are known"); return 2; }
            }
        }
        var engine = new NodeEngine(store, paths, opts, handlers); engineRef = engine;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Log.Info("Ctrl+C: releasing current jobs"); cts.Cancel(); };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });

        var budgetText = limits.Budget is { } bd ? $" budget={limits.Remaining}/{bd}" : "";
        var schedText = (a.Get("schedule") ?? cfg.Schedule) is { } sc ? " schedule=" + sc.Replace(' ', '_') + " window=" + (a.Get("window") ?? cfg.Window ?? "1h") : "";
        Log.Info($"node {nodeId}: stages={string.Join(',', stages)} lanes={lanes} ai-stages={(aiStages && ai.Configured ? "on" : "off")} threads={threads} worker-threads={opts.ThreadsPerWorker} prefix={paths.Prefix}{budgetText}{schedText} ai=[{ai.Describe()}]");
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
        try { await engine.RunAsync(cts.Token); } finally { if (Shadow != null) { Shadow.Flush(); Console.Error.WriteLine($"indexnode: shadow run: {Shadow.BlockedWrites} aggregate writes dropped; the overlay is saved under {a.Get("out-local")}"); } }
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
            SampleBudget = a.Get("sample-budget") is { } sbf ? int.Parse(sbf) : cfg.SampleBudget,
            Schedule = a.Get("schedule") ?? cfg.Schedule,
            Window = a.Get("window") ?? cfg.Window,
        };
        if (local.Schedule != null) CronExpr.Parse(local.Schedule);
        if (local.Window != null) RefreshStage.ParseEvery(local.Window);
        var stamp = a.Has("budget") || a.Has("sample-budget") ? "flag" : cfg.BudgetSetAt ?? "";
        return new LimitState(nodeId, local, stamp, Path.Combine(NodeConfig.ConfigDir(), "state.json"));
    }

    public static async Task<int> PriorityAsync(Args a)
    {
        var (_, store, paths, _) = Open(a);
        var q = await JobQueue.OpenAsync(store, paths, "cli", true, default);   // 0.13.26: the full download queue
        return await PriorityTool.RunAsync(a, q);
    }

    public static async Task<int> SourcesAsync(Args a)
    {
        int priority = PriorityTool.ParseOption(a.Get("priority"));
        var sub = a.Positional.FirstOrDefault();
        var (cfg, store, paths, http) = Open(a);
        var queue = await JobQueue.OpenAsync(store, paths, "cli", true, default);   // 0.13.26: sources live in the full download queue
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
                var created = await Sources.AddAsync(queue, job, default);
                Console.WriteLine($"{(created ? "added " : "exists")} {job.JobId}  {job.Kind}  {job.Name}  schedule={Schedule.Of(job).Describe()} backfill={(job.Backfill?.ToString() ?? "all")} followChannels={job.FollowChannels}{(created ? "  lookup due now (priority " + Protocol.RefreshNowPriority + ")" : "")}");
                return 0;
            }
            case "migrate-seen":
            {
                bool dry = a.Has("dry-run"); int srcs = 0, ids = 0, files = 0; var at = Protocol.NowUtc();
                foreach (var j in Sources.List(queue))
                {
                    if (await SourceMembers.MigrateSeenAsync(store, paths, j.JobId, j.Seen, at, default, dry) is not { } n) continue;
                    if (n > 0 || await store.HeadAsync(paths.SourceSeen(j.JobId), default) != null) { srcs++; ids += n; }
                    if (dry && await store.HeadAsync(paths.SourceSeen(j.JobId), default) != null) files++;
                }
                Console.WriteLine($"{(dry ? "would migrate" : "migrated")} {srcs} source(s): {ids} id(s) {(dry ? "would become" : "became")} members{(dry ? $", {files} seen.json file(s) would be deleted (dry run: nothing written)" : "; seen.json deleted")}");
                return 0;
            }
            case "set-tabs":
            {
                var tabs = Sources.ParseTabs(a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources set-tabs videos|videos,shorts --all [--dry-run]"));
                if (!a.Has("all")) throw new ArgumentException("sources set-tabs <tabs> --all [--dry-run] (every followed source)");
                bool dry = a.Has("dry-run"); int n = await Sources.SetTabsAsync(queue, tabs, dry, default);
                Console.WriteLine($"{(dry ? "would set" : "set")} tabs={string.Join(',', tabs)} on {n} source(s){(dry ? " (dry run: nothing written)" : "")}");
                return 0;
            }
            case "list":
            {
                var list = Sources.List(queue);
                if (list.Count == 0) Console.WriteLine("(no followed sources)");
                var states = queue.Read(st => st.Subjects.Values.Where(s => s.Entries.ContainsKey("refresh")).ToDictionary(s => s.Id, s => s.Entries["refresh"].State));
                foreach (var j in list) Console.WriteLine(Sources.FormatRow(j, states.GetValueOrDefault(j.JobId)));
                return 0;
            }
            case "schedule":
            {
                var sid = PriorityTool.SourceId(a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources schedule <id> dynamic|off|once|6h|1d|daily@03:00"));
                var j = await Sources.SetScheduleAsync(queue, sid, a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("sources schedule <id> dynamic|off|once|6h|1d|daily@03:00"), default);
                Console.WriteLine($"{sid}: schedule {Schedule.Of(j).Describe()}{(j.NextRefresh != null ? ", next refresh " + j.NextRefresh : ", no further refresh unless triggered (sources refresh " + sid + ")")}");
                return 0;
            }
            case "lookup":
            case "refresh":
            {
                if (sub == "refresh" && a.Has("all-due"))
                {
                    var due = await Sources.DueAsync(queue);
                    int n = 0; foreach (var d in due) if (await Sources.TriggerAsync(queue, d, "refresh", default)) n++;
                    Console.WriteLine($"{n} scheduled refresh(es) made due now at priority {Protocol.RefreshNowPriority}");
                    return 0;
                }
                var sid = PriorityTool.SourceId(a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException($"sources {sub} <id>|--all-due"));
                Console.WriteLine(await Sources.TriggerAsync(queue, sid, sub, default) ? $"{sid}: {sub} due now (priority {Protocol.RefreshNowPriority}); the next free downloader runs it" : $"{sid}: a node is running it right now");
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
                    var w = Sources.List(queue).FirstOrDefault(x => x.JobId == sid) ?? throw new ArgumentException($"no source {sid}");
                    try { listed = await PriorityTool.ListIdsAsync(new Tools(http, cfg), w, default); }
                    catch (Exception e) { Console.Error.WriteLine($"could not list {sid} with yt-dlp ({e.Message}); only videos with a recorded source are re-tagged (run this on a machine YouTube allows, e.g. a residential one, to match older jobs)"); }
                }
                var r = await PriorityTool.SetSourceAsync(queue, sid, np, listed, default);
                Console.WriteLine($"{sid}: priority {np}; {r.Videos} video(s), {r.Changed} job(s) changed, skipped {r.Skipped.Values.Sum()}{(r.Skipped.Count > 0 ? " (" + string.Join(", ", r.Skipped.Select(k => $"{k.Value} {k.Key}")) + ")" : "")}{(r.Matched > 0 ? $"; {r.Matched} older job(s) matched by the listing" : "")}");
                return 0;
            }
            case "remove":
            {
                var id = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources remove <id>");
                if (queue.Read(st => st.Entry(id, "refresh")) == null) { Console.Error.WriteLine($"no source {id}"); return 1; }
                await queue.AppendLinesAsync([new QLine { T = "drop", S = id, Stage = "refresh" }], default);
                Console.WriteLine($"removed {id} (its downloaded videos stay)");
                return 0;
            }
            case "import":
            {
                var file = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("sources import <channels.json>");
                var r = await Sources.ImportAsync(queue, File.ReadAllBytes(file), every ?? "dynamic", Sources.ParseTabs(a.Get("tabs")), maxHeight, default, priority);
                Console.WriteLine($"{r.Added} added, {r.Existed} already existed{(r.Skipped > 0 ? $", {r.Skipped} skipped (no channelId)" : "")}");
                return 0;
            }
            default: throw new ArgumentException("sources add <url> | list | remove <id> | import <channels.json> | schedule <id> off|once|6h|1d|daily@03:00 | lookup <id> | refresh <id>|--all-due | priority <source id|url> N");
        }
    }

    public static async Task<int> AiSetAsync(Args a)
    {
        var rest = a.Positional.Skip(1).ToList();
        if (rest.Count < 2 || rest[^1] is not ("real" or "ai")) throw new ArgumentException("ai set <videoId>... real|ai [--reason R] [--dry-run]");
        var verdict = rest[^1]; var ids = rest.Take(rest.Count - 1).ToList(); if (a.Get("video") is { } one) ids.Add(one);
        bool dry = a.Has("dry-run");
        var (cfg, store, paths, http) = Open(a); using var _h = http;
        await JobQueue.RequireSnapshotAsync(store, paths, default);
        var processing = await JobQueue.OpenAsync(store, paths, "cli", false, default);
        var download = await JobQueue.OpenAsync(store, paths, "cli", true, default);
        var by = cfg.NodeName ?? Environment.UserName;
        foreach (var id in ids)
        {
            var r = await AiHold.SetVerdictAsync(processing, download, store, paths, id, verdict, a.Get("reason"), by, dry, default);
            Console.WriteLine($"{id}: {(dry ? "would be" : "is")} marked {verdict}{(r.Known ? "" : " (no video.json: nothing recorded)")}; released: {(r.Released.Count == 0 ? "none" : string.Join(", ", r.Released))}; held: {(r.Held.Count == 0 ? "none" : string.Join(", ", r.Held))}");
        }
        if (dry) Console.WriteLine("dry run: nothing written");
        return 0;
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
        var jq = new JobQueue(store, paths, cfg.NodeId ?? "cli"); await jq.LoadAsync(default);
        var tools = new Tools(http, cfg); int maxHeight = a.Get("max-height") is { } h ? int.Parse(h) : 480; bool queue = !a.Has("no-process");
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        int failed = 0;
        foreach (var input in a.Positional)
        {
            var id = System.Text.RegularExpressions.Regex.IsMatch(input, @"^[A-Za-z0-9_-]{11}$") ? input : YtDlp.VideoId(input) ?? throw new ArgumentException($"not a video url or id: {input}");
            var job = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={id}", maxHeight, a.Get("format")); job.JobId = id; job.Source = "one-off";
            var ctx = new JobContext { Job = job, Stage = "download", BlobName = id, LeaseId = "", Worker = 0, Queue = jq, Store = store, Paths = paths, NodeId = cfg.NodeId ?? "", Token = cts.Token, ThreadShare = () => 1, Report = (s, p) => Console.WriteLine($"  {s} {p}") };
            try
            {
                await new DownloadStage(tools, store, new DownloadGate()) { QueueProcessing = queue }.RunAsync(ctx);   // 0.13.0: no pre-check here; it is the first step of the process claim
                job.FinishedAt = Protocol.NowUtc();
                // a one-off: known and tracked as a finished download; the stage's follow-ups (process) go to the queue in the same append
                job.Stage = "download";
                await jq.AppendLinesAsync([QLine.Add(id, "download", job, job.AddedAt, null, null, "done", force: true), .. ctx.FollowUps], cts.Token);
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
        var uq = new JobQueue(store, paths, "cli"); await uq.LoadAsync(default);
        var ids = uq.Read(st => st.Subjects.Values.Where(s => s.Entries.TryGetValue("download", out var e) && e.State == "done").Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());
        int n = 0;
        foreach (var id in ids)
        {
            var missing = new List<string>();
            foreach (var (step, file) in outputs) if (await store.GetTagsAsync(paths.Video(id, file), default) == null) missing.Add(step);
            if (missing.Count == 0) continue;
            bool queued = uq.Read(st => st.Get(id) is { } s && (s.ClaimBy != null || s.Entries.Values.Any(e => e.Stage is "process" or "describe" or "translate" && e.State == "pending")));
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
        var eq = await JobQueue.OpenAsync(store, paths, "cli", true, default);   // 0.13.26: new downloads go to the full download queue
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
                var created = await ClassifyJobs.CreateUnclassifiedAsync(eq, job, default);
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
        var aq = await JobQueue.OpenAsync(store, paths, "cli", true, default);
        var job = AvailabilityStage.NewSweep(a.Get("every") ?? "1d"); RefreshStage.ParseEvery(job.Every);
        bool made = false;
        await aq.AppendLinesAsync(st => { made = false; if (st.Entry(AvailabilityStage.SweepId, "availability") != null) return null; made = true; return [QLine.Add(AvailabilityStage.SweepId, "availability", job, job.AddedAt)]; }, default);
        Console.WriteLine(made ? $"availability sweep created (every {job.Every}); a yt-dlp node runs it with --stages ...,availability" : "the availability sweep already exists");
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
        if (level == "storage")
        {
            var v = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("control storage stop|run");
            var sc = await ControlWatcher.SetStorageAsync(store, paths, v, cfg.NodeName ?? Environment.UserName, default);
            Console.WriteLine($"control: storage = {(sc.StorageStopped ? "stop (no node claims; running steps finish)" : "run")}");
            return 0;
        }
        if (level == "region")
        {
            var v = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("control region <name like westus2>|off");
            var rc = await ControlWatcher.SetRegionAsync(store, paths, v, cfg.NodeName ?? Environment.UserName, default);
            Console.WriteLine($"control: region = {rc.Region ?? "off (no region rule)"} (region-only stages: {string.Join(",", RegionGate.OnlyStages)})");
            return 0;
        }
        if (level == "ai-dramas")
        {
            var v = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("control ai-dramas on|off");
            var ac = await ControlWatcher.SetAiDramasAsync(store, paths, v, cfg.NodeName ?? Environment.UserName, default);
            Console.WriteLine($"control: aiDramas = {(ac.AiDramasOn ? "on (AI-held dramas are claimable and resume from frames)" : "off (AI-held dramas stay pending)")}");
            return 0;
        }
        if (level == "version")
        {   // 0.13.32: indexnode control version <step> <n>|off
            var vs = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("control version <cast|actors> <n>|off"); var vn = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("control version <cast|actors> <n>|off");
            var vc = await ControlWatcher.SetVersionAsync(store, paths, vs, vn, cfg.NodeName ?? Environment.UserName, default);
            Console.WriteLine($"control: versions = {(vc.Versions == null ? "none" : string.Join(", ", vc.Versions.Select(kv => kv.Key + " >= v" + kv.Value)))} (a done step below it is ready again in its repair mode; try 'indexnode queue whatif --stage {vs}' first)");
            return 0;
        }
        if (level == "classify")
        {
            var v = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("control classify on|off");
            var cc = await ControlWatcher.SetClassifyAsync(store, paths, v, cfg.NodeName ?? Environment.UserName, default);
            Console.WriteLine($"control: classify = {(cc.ClassifyOn ? "on (classify is seeded and claimed)" : "off (classify is paused: not seeded, not claimed)")}");
            return 0;
        }
        if (level == "media-table-read")
        {
            var v = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("control media-table-read on|off");
            var mc = await ControlWatcher.SetMediaTableReadAsync(store, paths, v, cfg.NodeName ?? Environment.UserName, default);
            Console.WriteLine($"control: media-table-read = {(mc.MediaTableReadOn ? "on (readers use the media table)" : "off (readers use video.json and the aggregate, as in 0.13.36)")}");
            return 0;
        }
        if (level == "dynamic-refresh")
        {
            var v = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("control dynamic-refresh on|off");
            var dc = await ControlWatcher.SetDynamicRefreshAsync(store, paths, v, cfg.NodeName ?? Environment.UserName, default);
            Console.WriteLine($"control: dynamicRefresh = {(dc.DynamicRefreshOn ? "on (sources with a dynamic schedule are refreshed)" : "off (no refresh is seeded for a dynamic-schedule source; explicit schedules, manual refreshes and first listings still run)")}");
            return 0;
        }
        if (level == "gpu-hold")
        {
            var v = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("control gpu-hold <duration like 30m>|off");
            var gc = await ControlWatcher.SetGpuHoldAsync(store, paths, v, cfg.NodeName ?? Environment.UserName, default);
            Console.WriteLine($"control: gpu hold = {gc.Gpu?.Hold ?? "off"} (CPU nodes {(gc.Gpu?.Hold == null ? "claim sscd/clip/cast at once and no gpu-ready markers are written" : "wait that long for a GPU node before taking sscd/clip/cast; markers are written")}; a node's --gpu-fallback overrides this)");
            return 0;
        }
        if (level is not ("run" or "drain" or "stop")) throw new ArgumentException("control needs run, drain, stop, gpu-hold, ai-dramas or --max-per-ip N|off");
        var stage = a.Get("stage");
        if (stage != null && !Protocol.Stages.Contains(stage = Protocol.CanonicalStage(stage))) throw new ArgumentException("--stage must be download, process, comments or refresh (watch is the old name)");
        var c = await ControlWatcher.WriteAsync(store, paths, level, stage, a.Get("message"), cfg.NodeName ?? Environment.UserName, default);
        Console.WriteLine($"control: mode={c.Mode} download={c.Effective("download")} process={c.Effective("process")}{(c.Message != null ? " message=" + c.Message : "")}");
        return 0;
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
        Console.WriteLine($"prefix {paths.Prefix}   this node: {cfg.NodeId ?? "(none yet)"}  threads={cfg.EffectiveThreads}");
        Console.WriteLine(StatusLines.MediaTable(cfg.MediaTableSas, now));

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
            Console.WriteLine($"  {n.NodeId,-24} {n.Kind,-8} stages={string.Join('+', n.Stages)} lanes={n.Workers} threads={n.Threads} state={n.EffectiveState(now)} control={n.Control} lastSeen={n.LastSeen}{(n.Ops != null ? "  storage ops: " + n.Ops + (n.Ops.Failed > 0 ? $", {n.Ops.Failed} failed" : "") + (n.Ops.Tripped ? ", BREAKER OPEN" : "") + (!a.Has("ops") && n.Ops.TopSites is { Count: > 0 } ts0 ? "; top: " + string.Join("; ", ts0.Take(5)) : "") : "")}");
            if (a.Has("ops") && n.Ops?.TopSites is { } ts) { foreach (var s in ts) Console.WriteLine("      ops  " + s); }
            foreach (var r in n.Running)
                Console.WriteLine($"      lane {r.Worker}: {r.Stage}/{r.JobId} {r.Step} {r.Progress}");
        }

        bool fullDownload = a.Has("download") && !(await JobQueue.ResolveAsync(store, paths, default)).IsLegacyLayout;   // 0.13.26: status --download counts the FULL download queue (a host one-off: it reads the whole of it)
        var queue = await JobQueue.OpenAsync(store, paths, "status", fullDownload, default);
        if (fullDownload) Console.WriteLine($"\nFULL DOWNLOAD queue ({queue.Paths.QueueDir}): every sample / download / refresh / classify entry; processing and the work queue are not counted here");
        Console.WriteLine($"\nQueue (segment {queue.Segment}, {queue.Blocks} appends, {queue.Offset} bytes read)");
        var subjects = queue.Read(st => st.Subjects.Values.SelectMany(s => s.Entries.Values.Select(e => (Subject: s, Entry: e))).ToList());
        foreach (var stage in Protocol.Stages)
        {
            var inStage = subjects.Where(x => x.Entry.Stage == stage).ToList();
            var counts = (stage == "download" ? new[] { Tags.Unclassified, "pending", "done", Tags.Duplicate, "failed" } : new[] { "pending", "done", "failed" }).Select(s => $"{s}={inStage.Count(x => x.Entry.State == s)}");
            int prio = inStage.Count(x => x.Entry.State == "pending" && x.Entry.Priority != 0);
            Console.WriteLine($"  {stage,-9} {string.Join("  ", counts)}  claimed={inStage.Count(x => x.Subject.ClaimBy != null && x.Subject.ClaimStages?.Contains(stage) == true)}{(prio > 0 ? $"  pending with priority={prio}" : "")}");
        }
        if (!queue.Paths.IsLegacyLayout && !fullDownload)
        {   // 0.13.26: the split layout: this is the PROCESSING queue; the downloaders' work queue is small and shown too; the full download queue belongs to `indexnode serve --download-service` and is never read here
            var wq = new JobQueue(store, paths.DownloadWork, "status"); await wq.LoadAsync(default);
            var wc = wq.Read(st => st.Subjects.Values.SelectMany(x => x.Entries.Values).GroupBy(e => e.Stage + ":" + e.State).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}").ToList());
            Console.WriteLine($"\nQueue split: the numbers above are the processing queue ({queue.Paths.QueueDir}).\nDownload work queue ({wq.Paths.QueueDir}): {(wc.Count == 0 ? "empty" : string.Join("  ", wc))}\n  (the full download queue is the download service's; see its log)");
        }
        var (cDay, cDone, cRejected) = ClassifyCommand.Today(queue, now);
        Console.WriteLine($"  classify  unclassified={subjects.Count(x => x.Entry.Stage == "download" && x.Entry.State == Tags.Unclassified)}  classified today={cDone}  rejected today={cRejected}  (UTC {cDay}; review: indexnode classify list --rejected)");
        var claimed = queue.Read(st => st.Subjects.Values.Where(s => s.ClaimBy != null).ToList());
        if (claimed.Count > 0)
        {
            Console.WriteLine("\nClaimed subjects");
            foreach (var s in claimed.OrderBy(s => s.Id, StringComparer.Ordinal))
                Console.WriteLine($"  {string.Join('+', s.ClaimStages ?? [])}/{s.Id}  node={s.ClaimBy} epoch={s.Epoch} since={s.ClaimAt}");
        }

        {   // 0.13.32: per versioned step, how many uploads are at each version (a finished process entry: its recorded step version, v1 when none)
            var vers = queue.Read(st => st.Subjects.Values.Where(s => s.Entries.GetValueOrDefault("process") is { State: "done" }).ToList());
            var ctl0 = ControlFile.Parse((await store.GetAsync(paths.Control, default))?.Data);
            foreach (var step in StepVersions.Steps) { var g = vers.GroupBy(s => s.VerOf(step)).OrderBy(x => x.Key).Select(x => $"v{x.Key}={x.Count()}"); Console.WriteLine($"Step version {step}: {string.Join("  ", g)}  (required: {(ctl0.RequiredVersion(step) is > 0 and var rq ? "v" + rq : "none")})"); }
            var acts = queue.Read(st => st.Subjects.Values.Select(s => s.Entries.GetValueOrDefault("actors")).Where(e => e is { State: "done" }).ToList());
            Console.WriteLine($"Stage version actors: {string.Join("  ", acts.GroupBy(e => e!.Ver ?? 1).OrderBy(x => x.Key).Select(x => $"v{x.Key}={x.Count()}"))}  (required: {(ctl0.RequiredVersion("actors") is > 0 and var ra ? "v" + ra : "none")})");
        }
        var ctl = ControlFile.Parse((await store.GetAsync(paths.Control, default))?.Data);
        Console.WriteLine($"\nAI dramas: {(ctl.AiDramasOn ? "on" : "off")}  held (pre-check flagged AI): {queue.Read(st => st.Subjects.Values.Count(s => s.Entries.Values.Any(e => e.Hold == "ai" && !e.Final)))}");
        Console.WriteLine($"Classify: {(ctl.ClassifyOn ? "on" : "off (paused)")}");
        Console.WriteLine($"Dynamic refresh: {(ctl.DynamicRefreshOn ? "on" : "off (dynamic-schedule sources are not refreshed)")}");
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
        if (sub != "install") throw new ArgumentException("tools status | tools install [--all] [--ffmpeg] [--ytdlp] [--ytdlp-zipapp] [--models [serve]]");
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
        {
            var which = a.Get("models");   // --models (default pipeline, no SigLIP), --models serve (only what serve loads), --all (everything, SigLIP too)
            var set = all ? Models.All : which == "serve" ? Models.Serve : which is null or "true" or "default" ? Models.Default : throw new ArgumentException("--models takes no value (the default pipeline, without SigLIP) or 'serve'; --all installs every model");
            foreach (var m in set) { await Models.EnsureAsync(m, http, default); Console.WriteLine($"model {m.Id} ready"); }
        }
        return 0;
    }

    /// <summary>`indexnode gpu markers [--dry]`: writes the marker of every pending sscd/clip/cast job whose frames are done and that has none (videos framed before 0.6.0), so the GPU container starts for them.</summary>
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

    /// <summary>The pre-check analyzer of this node: the search index is cached under the config folder (search-cache/), re-checked at most every precheck.IndexRecheckMinutes.</summary>
    public static IPreCheckAnalyzer MakePreCheck(Tools tools, NodeConfig cfg, HttpClient http, IStore store, Paths paths, Func<DateTimeOffset?, IReadOnlyList<string>>? fresh = null) =>
        new PreCheckAnalyzer(tools, cfg, http, store, paths, new SearchIndexCache(Path.Combine(NodeConfig.ConfigDir(), "search-cache"), TimeSpan.FromMinutes((cfg.PreCheck ?? new()).IndexRecheckMinutes))) { FreshUploads = fresh };

    /// <summary>The uploads whose process `centers` step finished (queue log) after the index build time.</summary>
    static IReadOnlyList<string> FreshFromQueue(NodeEngine? e, DateTimeOffset? builtAt)
    {
        try { return e?.Queue is { } q ? SearchIndexJobs.FreshUploads(q, builtAt) : []; } catch (NullReferenceException) { return []; }
    }

    static PreCheckSettings NoPre(Args a, PreCheckSettings? s) { s ??= new(); if (a.Has("no-precheck")) s.Enabled = false; return s; }

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
        if (sub == "set-media-table-sas")
        {
            var url = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("config set-media-table-sas needs the table SAS URL");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Query.Contains("sig=") || !u.Host.Contains(".table.")) throw new ArgumentException("that does not look like a table SAS URL (https://<account>.table.core.windows.net/media?...&sig=...)");
            file.MediaTableSas = url; file.Save();
            Console.WriteLine($"table SAS stored in {NodeConfig.ConfigPath()} (expires {RestMediaTable.Expiry(url)?.ToString("u") ?? "unknown"})");
            return 0;
        }
        if (sub == "set")
        {
            var key = a.Positional.ElementAtOrDefault(1)?.ToLowerInvariant();
            var val = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("config set <key> <value>");
            switch (key)
            {
                case "budget": file.Budget = int.Parse(val); file.BudgetSetAt = Protocol.NowUtc(); break;
                case "sample-budget": file.SampleBudget = val.Equals("off", StringComparison.OrdinalIgnoreCase) || val.Equals("unlimited", StringComparison.OrdinalIgnoreCase) ? null : int.Parse(val); file.BudgetSetAt = Protocol.NowUtc(); break;
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
                case "serve.token": (file.Serve ??= new()).Token = val.Trim(); break;
                case "check.url": (file.Check ??= new()).Url = val.Trim().Length == 0 || val.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : val.Trim().TrimEnd('/'); break;
                case "download.work-max": { var n = int.Parse(val); if (n < 2) throw new ArgumentException("download.work-max must be 2 or more"); (file.Download ??= new()).WorkMax = n; break; }
                case "download.service-seconds": { var n = int.Parse(val); if (n < 5) throw new ArgumentException("download.service-seconds must be 5 or more"); (file.Download ??= new()).ServiceSeconds = n; break; }
                case "check.token": (file.Check ??= new()).Token = val.Trim(); break;
                case "check.timeout-seconds": { var n = int.Parse(val); if (n < 1) throw new ArgumentException("check.timeout-seconds must be 1 or more"); (file.Check ??= new()).TimeoutSeconds = n; break; }
                case "storage.warn-per-min": { var n = int.Parse(val); if (n < 1) throw new ArgumentException("storage.warn-per-min must be 1 or more"); (file.StorageBreaker ??= new()).WarnPerMin = n; break; }
                case "storage.max-per-min-per-lane": { var n = int.Parse(val); if (n < 1) throw new ArgumentException("storage.max-per-min-per-lane must be 1 or more"); (file.StorageBreaker ??= new()).MaxPerMinPerLane = n; break; }
                case "storage.max-per-min": { var n = int.Parse(val); if (n < 1) throw new ArgumentException("storage.max-per-min must be 1 or more"); (file.StorageBreaker ??= new()).MaxPerMin = n; break; }
                case "storage.max-per-step": { var n = int.Parse(val); if (n < 1) throw new ArgumentException("storage.max-per-step must be 1 or more"); (file.StorageBreaker ??= new()).MaxPerStep = n; break; }
                case "storage.max-failures-per-min": { var n = int.Parse(val); if (n < 1) throw new ArgumentException("storage.max-failures-per-min must be 1 or more"); (file.StorageBreaker ??= new()).MaxFailuresPerMin = n; break; }
                case "storage.cooldown": { var d = RefreshStage.ParseEvery(val); if (d <= TimeSpan.Zero) throw new ArgumentException("storage.cooldown must be a positive duration like 5m"); (file.StorageBreaker ??= new()).Cooldown = d; break; }
                case "model-url": file.ModelUrl = val; break;
                case "model-path": file.ModelPath = val; break;
                case "actors.index-min-uploads": { var n = int.Parse(val); if (n < 1) throw new ArgumentException("actors.index-min-uploads must be 1 or more"); (file.Actors ??= new()).IndexMinUploads = n; break; }
                case "actors.index-max-wait-minutes": { var n = int.Parse(val); if (n < 0) throw new ArgumentException("actors.index-max-wait-minutes must be 0 or more"); (file.Actors ??= new()).IndexMaxWaitMinutes = n; break; }
                case "search.keep-versions": case "search.keepversions": { var kv = int.Parse(val); if (kv < 0) throw new ArgumentException("search.keep-versions must be 0 or more"); (file.Search ??= new()).KeepVersions = kv; break; }
                case "index.local-cache-gb": { var gb = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); if (gb < 0) throw new ArgumentException("index.local-cache-gb must be 0 or more"); (file.Search ??= new()).LocalCacheGb = gb; break; }
                case "scan.mode": { var m = val.Trim().ToLowerInvariant(); if (m is not ("fast" or "exact")) throw new ArgumentException("scan.mode must be fast or exact"); (file.Search ??= new()).ScanMode = m; break; }
                case "scan.rerank": { var rr = int.Parse(val); if (rr < 1) throw new ArgumentException("scan.rerank must be 1 or more"); (file.Search ??= new()).ScanRerank = rr; break; }
                case "retain.enabled": (file.PreCheck ??= new()).RetainEnabled = bool.Parse(val); break;
                case "retain.t": (file.PreCheck ??= new()).RetainT = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "retain.f": (file.PreCheck ??= new()).RetainF = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.enabled": (file.PreCheck ??= new()).Enabled = bool.Parse(val); break;
                case "precheck.tau": (file.PreCheck ??= new()).Tau = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.skip-share": (file.PreCheck ??= new()).SkipShare = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.partial-share": (file.PreCheck ??= new()).PartialShare = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.duration-percent": (file.PreCheck ??= new()).DurationPercent = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.duration-slack-percent": (file.PreCheck ??= new()).DurationSlackPercent = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.duration-slack-seconds": (file.PreCheck ??= new()).DurationSlackSeconds = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.duration-min": (file.PreCheck ??= new()).DurationMinSeconds = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.vote-uploads": { var vu = int.Parse(val); if (vu < 0) throw new ArgumentException("precheck.vote-uploads must be 0 (read every upload with a top row) or more"); (file.PreCheck ??= new()).VoteUploads = vu; break; }
                case "precheck.top-centers": (file.PreCheck ??= new()).TopCenters = int.Parse(val); break;
                case "precheck.window-seconds": (file.PreCheck ??= new()).WindowSeconds = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.language-seconds": (file.PreCheck ??= new()).LanguageSeconds = PreCheckSettings.ParseLanguageSeconds(val); break;
                case "precheck.language-min-seconds": (file.PreCheck ??= new()).LanguageMinSeconds = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.frames": (file.PreCheck ??= new()).Frames = PreCheckSettings.ParseFrames(val); break;
                case "precheck.video-height": (file.PreCheck ??= new()).VideoHeight = int.Parse(val); break;
                case "precheck.audio-format": (file.PreCheck ??= new()).AudioFormat = val.Trim(); break;
                case "precheck.window-positions": (file.PreCheck ??= new()).WindowPositions = val.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); break;
                case "precheck.order-tolerance-seconds": (file.PreCheck ??= new()).OrderToleranceSeconds = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "describe.main-min-screen": { var mv = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); if (mv < 0) throw new ArgumentException("describe.main-min-screen must be 0 or more"); file.DescribeMainMinScreen = mv; break; }
                case "describe.main-max": { var mx = int.Parse(val); if (mx < 0) throw new ArgumentException("describe.main-max must be 0 or more"); file.DescribeMainMax = mx; break; }
                case "precheck.window-position": (file.PreCheck ??= new()).WindowPosition = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "precheck.ai-flag": (file.PreCheck ??= new()).AiFlag = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture); break;
                case "groups.check-frames": (file.PreCheck ??= new()).GroupCheckFrames = PreCheckSettings.ParseFrames(val); break;
                case "groups.cache-mb": (file.PreCheck ??= new()).GroupCacheMb = Math.Max(16, int.Parse(val)); break;
                case "groups.max-new-per-build": (file.PreCheck ??= new()).GroupMaxNewPerBuild = Math.Max(0, int.Parse(val)); break;
                case "precheck.group-min-hits": (file.PreCheck ??= new()).GroupMinHits = int.Parse(val); break;
                case "precheck.index-recheck-minutes": (file.PreCheck ??= new()).IndexRecheckMinutes = int.Parse(val); break;
                case "node-name": file.NodeName = NodeConfig.SanitizeName(val); break;
                case "ytdlp.command": file.Ytdlp = new YtDlpConfig { Command = ParseCommand(val) }; break;
                case "ytdlp.js-runtime": (file.Ytdlp ??= new()).JsRuntime = val; break;
                case "browser.exe": (file.Browser ??= new()).Exe = val; break;
                case "browser.profile-dir": (file.Browser ??= new()).ProfileDir = val; break;
                case "browser.port": (file.Browser ??= new()).Port = int.Parse(val); break;
                default: throw new ArgumentException("unknown config key (budget, schedule, window, ai.base-url, ai.key, ai.text-model, ai.vision-model, ai.api, jobs, threads, storage.region, model-url, model-path, node-name, ytdlp.command, ytdlp.js-runtime, browser.exe, browser.profile-dir, browser.port)");
            }
            file.Save();
            Console.WriteLine($"{key} saved{(key is "ai.key" or "serve.token" or "check.token" ? " (hidden)" : "")}");
            return 0;
        }
        throw new ArgumentException("config set-sas <url> | config set <key> <value>");
    }
}
