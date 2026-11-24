using System.Text.Json;
using System.Text.Json.Serialization;

namespace IndexNode;

public sealed class ServeConfig { public string? Token { get; set; } }
/// <summary>0.13.26 (config download.work-max, download.service-seconds): the download service's work queue size and loop interval.</summary>
public sealed class DownloadConfig { public int? WorkMax { get; set; } public int? ServiceSeconds { get; set; } }
public sealed class InlineCheckConfig
{
    public string? Url { get; set; } public string? Token { get; set; } public int? TimeoutSeconds { get; set; }
    public static InlineCheckClient? From(NodeConfig cfg, HttpClient http) => DownloaderNode.CheckClient(http, cfg.Downloader());
}

public sealed class NodeConfig
{
    /// <summary>0.13.42: the downloader settings of this config (the check server and its token, env INDEXNODE_CHECK_TOKEN when the config has none; the pre-check settings).</summary>
    public DownloaderSettings Downloader() => new() { CheckUrl = Check?.Url, CheckToken = Check?.Token ?? Environment.GetEnvironmentVariable("INDEXNODE_CHECK_TOKEN"), CheckTimeoutSeconds = Check?.TimeoutSeconds, PreCheck = PreCheck };
    public string? Sas { get; set; }
    /// <summary>0.13.37: the SAS URL of the Azure Table `media` (https://account.table.core.windows.net/media?...): per-upload facts. Env INDEXNODE_MEDIA_TABLE_SAS_URL overrides it; `config set-media-table-sas` stores it.</summary>
    public string? MediaTableSas { get; set; }
    public string? NodeId { get; set; }
    public string? NodeName { get; set; }
    public Dictionary<string, int>? Jobs { get; set; }
    public int? Threads { get; set; }
    /// <summary>Ignored since 0.9.0 (use Workers).</summary>
    public int? MaxHeavy { get; set; }
    /// <summary>Independent workers on this node (default 2).</summary>
    public int? Workers { get; set; }
    /// <summary>Threads per worker; null = Threads divided evenly by Workers.</summary>
    public int? WorkerThreads { get; set; }
    /// <summary>"cpu" (default) or "cuda".</summary>
    public string? Device { get; set; }

    /// <summary>Face models of the cast step: "buffalo_l" (default) or "yunet-sface".</summary>
    public string? CastModel { get; set; }
    /// <summary>Frame storage: AV1 CRF of the stored frames and keyframes (default 22); colour-grid prune distance (0.08), SSCD prune cosine (0.9), look-back seconds (30).</summary>
    public int? FrameCrf { get; set; }
    public double? PruneGrid { get; set; }
    public double? PruneSscd { get; set; }
    public double? PruneLookback { get; set; }
    /// <summary>The storage account's Azure region (default westus2): reads from a node in the same region are free.</summary>
    public string? StorageRegion { get; set; }
    public static StorageLimits TryLoadStorageLimits() { try { return Load().StorageBreaker ?? new(); } catch (Exception e) when (e is not OperationCanceledException) { return new(); } }
    /// <summary>0.13.18 (config storage.warn-per-min, max-per-min, max-per-step, max-failures-per-min, cooldown): the storage circuit breaker; null = the defaults (150 / 200 / 1,000 / 100 / 5 min).</summary>
    public StorageLimits? StorageBreaker { get; set; }
    /// <summary>0.13.21 (config serve.token): the bearer token POST /v1/check needs (the env INDEXNODE_SERVE_TOKEN overrides it).</summary>
    public ServeConfig? Serve { get; set; }
    /// <summary>0.13.21 (config check.url, check.token, check.timeout-seconds): the sample stage asks this serve instance for the check right after a sample download; null/empty url = the check entry as before.</summary>
    public InlineCheckConfig? Check { get; set; }
    public DownloadConfig? Download { get; set; }
    public string? ModelUrl { get; set; }
    public string? ModelPath { get; set; }
    public BrowserConfig? Browser { get; set; }
    public YtDlpConfig? Ytdlp { get; set; }
    /// <summary>0.12.0 download pre-check (config keys precheck.*): sample, duplicate lookup, AI flag. Null = defaults.</summary>
    public PreCheckSettings? PreCheck { get; set; }
    /// <summary>0.13.15 (config describe.main-min-screen / describe.main-max): the main-cast gate; null = 0.10 and 6 (the env INDEXNODE_DESCRIBE_MAIN_MIN_SCREEN / _MAIN_MAX override).</summary>
    public double? DescribeMainMinScreen { get; set; }
    public int? DescribeMainMax { get; set; }
    /// <summary>0.13.1 (config key search.keep-versions): how many search-index versions to keep behind the head. Null = 3.</summary>
    public SearchSettings? Search { get; set; }
    /// <summary>0.13.24 (config actors.*): when the actors-index singleton is ready.</summary>
    public ActorsSettings? Actors { get; set; }

    /// <summary>Run limits (see LimitState). BudgetSetAt restarts the countdown when the budget is set again.</summary>
    public int? Budget { get; set; }
    public string? BudgetSetAt { get; set; }
    /// <summary>0.13.16: the sample stage's own budget (config key sample-budget); null = unlimited.</summary>
    public int? SampleBudget { get; set; }
    public string? Schedule { get; set; }
    public string? Window { get; set; }

    /// <summary>An OpenAI-compatible endpoint for the steps that need a model. The key is a secret.</summary>
    public AiConfig? Ai { get; set; }

    /// <summary>When false the node never downloads models by itself: a step whose model files are missing is not advertised and its jobs are not claimed.</summary>
    public bool ModelsAutoDownload { get; set; } = true;

    [JsonIgnore] public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string ConfigDir()
    {
        var env = Environment.GetEnvironmentVariable("INDEXNODE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(env)) return env;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "indexnode");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var home = string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg;
        return Path.Combine(home, "indexnode");
    }

    public static string ConfigPath() => Path.Combine(ConfigDir(), "config.json");
    public static string ToolsDir() => Path.Combine(ConfigDir(), "tools");

    public static NodeConfig Load()
    {
        var p = ConfigPath();
        var c = File.Exists(p) ? JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(p), Json) ?? new() : new();
        var envSas = Environment.GetEnvironmentVariable("INDEXNODE_SAS_URL");
        if (!string.IsNullOrWhiteSpace(envSas)) c.Sas = envSas.Trim();
        var envTable = Environment.GetEnvironmentVariable("INDEXNODE_MEDIA_TABLE_SAS_URL");
        if (!string.IsNullOrWhiteSpace(envTable)) c.MediaTableSas = envTable.Trim();
        SearchData.Configure(c.Search?.ScanMode, c.Search?.ScanRerank);
        Describe.ConfigMainMinScreen = c.DescribeMainMinScreen; Describe.ConfigMainMax = c.DescribeMainMax;
        Redact.Register(c.Ai?.Key); Redact.Register(c.Serve?.Token); Redact.Register(c.Check?.Token); Redact.Register(Environment.GetEnvironmentVariable("INDEXNODE_SERVE_TOKEN"));
        Redact.Register(Environment.GetEnvironmentVariable("INDEXNODE_AI_KEY"));
        return c;
    }

    /// <summary>Writes the config user-only (mode 600 on Linux). Creates the node id on first save.</summary>
    public void Save()
    {
        var dir = ConfigDir();
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var p = ConfigPath();
        if (!File.Exists(p))
        {
            File.WriteAllText(p, "");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.WriteAllText(p, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public const string NodeNameEnv = "INDEXNODE_NODE_NAME";

    /// <summary>The name given for this run: the --node-name flag, then INDEXNODE_NODE_NAME. Null when neither is set (the config's node-name and the generated id apply).</summary>
    public static string? ExplicitName(string? flag, string? env) =>
        !string.IsNullOrWhiteSpace(flag) ? SanitizeName(flag) : !string.IsNullOrWhiteSpace(env) ? SanitizeName(env) : null;

    /// <summary>(nodeId, display name). A name given by flag or environment IS the node id (no suffix), so every run under it reports to one registry record; otherwise the stored id of this install (name plus random suffix).</summary>
    public (string NodeId, string Name) ResolveIdentity(string? flag, string? env)
    {
        if (ExplicitName(flag, env) is { } n) return (n, n);
        var id = EnsureNodeId();
        return (id, NodeName ?? SanitizeName(Environment.MachineName));
    }

    /// <summary>Stable per install: name plus 6 random hex characters, stored in the config.</summary>
    public string EnsureNodeId()
    {
        if (!string.IsNullOrEmpty(NodeId)) return NodeId;
        NodeName ??= SanitizeName(Environment.MachineName);
        NodeId = NodeName + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        // persist without the env-supplied SAS leaking into the file
        var onDisk = File.Exists(ConfigPath()) ? JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(ConfigPath()), Json) ?? new() : new();
        onDisk.NodeId = NodeId; onDisk.NodeName = NodeName;
        onDisk.Save();
        return NodeId;
    }

    public static string SanitizeName(string s)
    {
        var r = new string(s.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        return r.Length == 0 ? "node" : r;
    }

    public static int PhysicalCores()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                var set = new HashSet<string>();
                string phys = "0";
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("physical id")) phys = line.Split(':')[1].Trim();
                    else if (line.StartsWith("core id")) set.Add(phys + "/" + line.Split(':')[1].Trim());
                }
                if (set.Count > 0) return set.Count;
            }
            else if (OperatingSystem.IsWindows())
            {
                var r = Proc.RunQuick("powershell", ["-NoProfile", "-Command", "(Get-CimInstance Win32_Processor | Measure-Object NumberOfCores -Sum).Sum"]);
                if (int.TryParse(r.Trim(), out var n) && n > 0) return n;
            }
        }
        catch { }
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    /// <summary>"process=2,download=1" or a bare number for every stage.</summary>
    public static Dictionary<string, int> ParseJobs(string? spec)
    {
        var d = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(spec)) return d;
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=');
            if (kv.Length == 1 && int.TryParse(kv[0], out var all)) { d["download"] = all; foreach (var st in Protocol.Stages.Where(x => x == "process" || Protocol.IsStep(x))) d[st] = all; }
            else if (kv.Length == 2 && Protocol.Stages.Contains(kv[0]) && int.TryParse(kv[1], out var n) && n >= 0) d[kv[0]] = n;
            else throw new ArgumentException($"bad --jobs entry '{part}' (use e.g. process=2,download=1)");
        }
        return d;
    }

    public int JobsFor(string stage) => Jobs != null && Jobs.TryGetValue(stage, out var n) ? n : 1;
    public int EffectiveThreads => Threads is > 0 ? Threads.Value : PhysicalCores();
}

public sealed class AiConfig
{
    public string? BaseUrl { get; set; }
    public string? Key { get; set; }
    public string? TextModel { get; set; }
    public string? VisionModel { get; set; }
    /// <summary>"chat", "responses" or "auto" (the default: chat, and on unsupported_api_for_model one retry on Responses).</summary>
    public string? Api { get; set; }
    /// <summary>Sent as x-session-id, so a gateway that groups calls into sessions (hexad) keeps a whole run in one session instead of one per conversation.</summary>
    public string? SessionId { get; set; }

    public const string ApiChat = "chat", ApiResponses = "responses", ApiAuto = "auto";
    public const string DefaultModel = "wupeter/gpt-6-luna";
    public static bool ValidApi(string? v) => v is null or ApiChat or ApiResponses or ApiAuto;
    public string EffectiveApi => string.IsNullOrWhiteSpace(Api) ? ApiAuto : Api.Trim().ToLowerInvariant();

    public bool Configured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(TextModel);

    /// <summary>flag > environment (INDEXNODE_AI_BASE_URL, _KEY, _TEXT_MODEL, _VISION_MODEL, _API) > config file, field by field.</summary>
    public static AiConfig Resolve(AiConfig? file, Func<string, string?> flag, Func<string, string?> env)
    {
        static string? Pick(params string?[] v) => v.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
        return new AiConfig
        {
            BaseUrl = Pick(flag("ai-base-url"), env("INDEXNODE_AI_BASE_URL"), file?.BaseUrl),
            Key = Pick(flag("ai-key"), env("INDEXNODE_AI_KEY"), file?.Key),
            TextModel = Pick(flag("ai-text-model"), env("INDEXNODE_AI_TEXT_MODEL"), file?.TextModel, DefaultModel),
            VisionModel = Pick(flag("ai-vision-model"), env("INDEXNODE_AI_VISION_MODEL"), file?.VisionModel, DefaultModel),
            Api = Pick(flag("ai-api"), env("INDEXNODE_AI_API"), file?.Api)?.ToLowerInvariant(),
            SessionId = Pick(flag("ai-session-id"), env("INDEXNODE_AI_SESSION_ID"), file?.SessionId),
        };
    }

    /// <summary>One-line description that never contains the key.</summary>
    public string Describe() => $"base-url={BaseUrl ?? "(none)"} text-model={TextModel ?? "(none)"} vision-model={VisionModel ?? "(none)"} api={EffectiveApi} session-id={SessionId ?? "(none)"} key={(string.IsNullOrEmpty(Key) ? "(none)" : "set")}";
}

public sealed class YtDlpConfig
{
    /// <summary>A full command prefix, e.g. ["python", ".../yt-dlp.pyz"]. Null: the downloaded yt-dlp executable.</summary>
    public string[]? Command { get; set; }

    /// <summary>yt-dlp's --js-runtimes value ("node", "deno:/path/to/deno"). Null: node on PATH, else deno.</summary>
    public string? JsRuntime { get; set; }

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5), DefaultIdle = TimeSpan.FromMinutes(2);
    /// <summary>Wall-clock limit per yt-dlp/ffmpeg invocation in the download, comments, refresh and availability stages (default 5; 0: none). On expiry the whole process tree is killed and the job fails as transient ("timed out after N min").</summary>
    public double? TimeoutMinutes { get; set; }
    /// <summary>A yt-dlp/ffmpeg child that prints nothing for this long is hung: killed, transient failure (default 2; 0: none).</summary>
    public double? IdleMinutes { get; set; }
}

public sealed class BrowserConfig
{
    public string? Exe { get; set; }
    public string? ProfileDir { get; set; }
    public int? Port { get; set; }

    public const int DefaultPort = 9333;

    public static string DefaultProfileDir() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "indexnode", "brave-profile")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "indexnode", "brave-profile");

    public static string[] DefaultExeCandidates() => OperatingSystem.IsWindows()
        ?
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
        ]
        : ["brave-browser"];

    public string ResolveExe()
    {
        if (!string.IsNullOrWhiteSpace(Exe)) return Exe;
        foreach (var c in DefaultExeCandidates())
            if (!Path.IsPathRooted(c) || File.Exists(c)) return c;
        return DefaultExeCandidates()[0];
    }

    public string ResolveProfileDir() => string.IsNullOrWhiteSpace(ProfileDir) ? DefaultProfileDir() : ProfileDir;
    public int ResolvePort() => Port ?? DefaultPort;
}

public sealed class ActorsSettings
{
    /// <summary>actors.index-min-uploads: the actor index is built when this many finished actors uploads are not in it yet.</summary>
    public int IndexMinUploads { get; set; } = 25;
    /// <summary>actors.index-max-wait-minutes: ... or when the oldest of them has waited this long (a tail is never left).</summary>
    public int IndexMaxWaitMinutes { get; set; } = 10;
}

public sealed class SearchSettings
{
    public int KeepVersions { get; set; } = 3;
    /// <summary>0.13.9 (config index.local-cache-gb): the size bound of the node's local copy of the frames files; 0 turns the local copy off (every lookup is range requests).</summary>
    public double LocalCacheGb { get; set; } = 20;
    /// <summary>0.13.7 (config scan.mode): "fast" (default: popcount shortlist, then the exact LUT score on it) or "exact" (the 0.13.6 scan of every code with the LUT).</summary>
    public string ScanMode { get; set; } = "fast";
    /// <summary>0.13.7 (config scan.rerank): codes the fast scan's first stage keeps for the exact score (at least 20 x topC).</summary>
    public int ScanRerank { get; set; } = 10000;
}
