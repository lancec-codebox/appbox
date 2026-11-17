using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IndexNode;

public static partial class Protocol
{
    public const string ApiVersion = "2021-12-02";
    /// <summary>The processing steps, each its own job stage (jobs/&lt;step&gt;/&lt;id&gt;.json).</summary>
    public static readonly string[] StepStages = ["frames", "sscd", "dino", "thumbs", "clip", "transcribe", "ocrsubs", "cast", "sync", "describe", "translate"];
    /// <summary>The step jobs an upload gets by default (download, enqueue, sources): every step stage except the opt-in ones (dino: owner 2026-11-16, not part of ingest; clip: dropped 2026-11-14), which run only when asked for (process --job --steps).</summary>
    public static readonly string[] IngestStepStages = StepStages.Where(s => s is not ("dino" or "clip")).ToArray();
    /// <summary>"process" is the legacy all-in-one stage: its jobs fan out into step jobs.</summary>
    public static readonly string[] Stages = ["download", "process", .. StepStages, "comments", "watch", "classify", "availability"];
    /// <summary>Stages that decode or run models and so share the thread budget.</summary>
    public static readonly string[] HeavyStages = ["process", "frames", "sscd", "dino", "thumbs", "clip", "transcribe", "ocrsubs", "cast"];
    public static bool IsStep(string stage) => StepStages.Contains(stage);
    /// <summary>Files (relative to videos/&lt;id&gt;/) a step job waits for before a node claims it.</summary>
    public static string[] NeedsOf(string step) => step switch
    {
        "sscd" or "dino" or "clip" or "thumbs" => ["frames/manifest.json"],
        // 0.7: cast reads the stored frames (no keyframes), after the SSCD pass has rewritten their index; jobs created before keep their stored needs
        "cast" => ["frames/manifest.json", "sscd/manifest.json"],
        "sync" => ["asr/" + Transcribe.ModelName + ".zh.json", "ocr/lines.json"],
        "describe" => ["asr/" + Transcribe.ModelName + ".zh.json", "ocr/lines.json", "cast/clusters.json"],
        "translate" => ["describe.json"],
        _ => [],
    };
    public static readonly TimeSpan NeedsRetry = TimeSpan.FromMinutes(2);
    /// <summary>What a desktop node runs by default: downloads go to the phone, so download is opt-in (--stages).</summary>
    // owner 2026-11-14: only the yt-dlp nodes (residential downloaders) touch YouTube: download, watch and comments are opt-in there
    // (--stages download,watch,comments); processing nodes run the steps and classify (which needs an AI endpoint)
    public static readonly string[] DefaultNodeStages = ["process", "classify"]; // "process" stands for every step stage too (see ExpandStages)

    /// <summary>"process" means the legacy stage and all step stages.</summary>
    public static string[] ExpandStages(IEnumerable<string> stages) =>
        stages.SelectMany(s => s == "process" ? new[] { "process" }.Concat(StepStages) : [s]).Distinct().ToArray();
    // owner 2026-11-14: search vectors (clip) dropped from the default pipeline (weak matches; screenshot search is SSCD). The step still runs when asked for explicitly.
    public static readonly string[] DefaultSteps = ["frames", "sscd", "thumbs", "transcribe", "ocrsubs"];
    public const int LeaseSeconds = 60;
    public const int MaxAttempts = 3;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
    public static string NowUtc() => Utc(DateTimeOffset.UtcNow);

    [GeneratedRegex(@"(?:v=|youtu\.be/|/shorts/|/embed/|/live/)([A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])")]
    private static partial Regex YoutubeIdRegex();

    /// <summary>YouTube video id (11 chars), or a hash of the URL otherwise.</summary>
    public static string JobIdFor(string url)
    {
        var m = YoutubeIdRegex().Match(url);
        if (m.Success && Regex.IsMatch(url, @"(?i)(youtube\.com|youtu\.be|youtube-nocookie\.com)")) return m.Groups[1].Value;
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim()));
        return "u" + Convert.ToHexString(h)[..15].ToLowerInvariant();
    }
}

public sealed class Paths(string prefix)
{
    public string Prefix { get; } = prefix.Length == 0 || prefix.EndsWith('/') ? prefix : prefix + "/";
    public string Job(string stage, string id) => $"{Prefix}jobs/{stage}/{id}.json";
    public string JobsDir(string stage) => $"{Prefix}jobs/{stage}/";
    public string Video(string id, string file) => $"{Prefix}videos/{id}/{file}";
    public string VideoDir(string id) => $"{Prefix}videos/{id}/";
    public string Control => $"{Prefix}control.json";
    public string Node(string nodeId) => $"{Prefix}nodes/{nodeId}.json";
    public string Seed(string file) => $"{Prefix}seeds/{file}";
    public string NodesDir => $"{Prefix}nodes/";
}

public static class Tags
{
    public const string Stage = "idx_stage", State = "idx_state", Added = "idx_added", Due = "idx_due", Kind = "idx_kind", Priority = "idx_priority";

    public const int MinPriority = -1000, MaxPriority = 1000;

    /// <summary>Throws unless <paramref name="p"/> is within -1000..1000.</summary>
    public static int CheckPriority(int p) => p is >= MinPriority and <= MaxPriority ? p : throw new ArgumentOutOfRangeException(nameof(p), $"priority {p} is outside {MinPriority}..{MaxPriority}");

    /// <summary>idx_priority of a job blob: a plain integer string; missing, unparsable or out of range counts as 0.</summary>
    public static int PriorityOf(IReadOnlyDictionary<string, string> tags) =>
        tags.TryGetValue(Priority, out var s) && int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is >= MinPriority and <= MaxPriority ? p : 0;

    /// <summary>Claim order: priority descending, then idx_added ascending, then blob name.</summary>
    public static IEnumerable<BlobEntry> ClaimOrder(IEnumerable<BlobEntry> blobs) =>
        blobs.OrderByDescending(b => PriorityOf(b.Tags)).ThenBy(b => b.Tags.GetValueOrDefault(Added) ?? "", StringComparer.Ordinal).ThenBy(b => b.Name, StringComparer.Ordinal);

    /// <summary>The full idx_* set of a job blob; idx_due defaults to idx_added.</summary>
    public static Dictionary<string, string> Job(string stage, string state, string added, string? due = null, int priority = 0)
    {
        var t = new Dictionary<string, string> { [Stage] = stage, [State] = state, [Added] = added, [Due] = due ?? added };
        if (priority != 0) t[Priority] = CheckPriority(priority).ToString(System.Globalization.CultureInfo.InvariantCulture); // 0 is the default: no tag
        return t;
    }

    /// <summary>Pending jobs of a stage; with <paramref name="nowUtc"/> only those whose idx_due has arrived.</summary>
    public static string PendingWhere(string stage, string? nowUtc = null) =>
        $"\"{Stage}\"='{stage}' AND \"{State}\"='pending'" + (nowUtc == null ? "" : $" AND \"{Due}\" <= '{nowUtc}'");

    /// <summary>idx_due of a listed blob (idx_added when a legacy blob has none).</summary>
    public static string DueOf(IReadOnlyDictionary<string, string> tags) =>
        tags.GetValueOrDefault(Due) ?? tags.GetValueOrDefault(Added) ?? "";
    public static string StateWhere(string stage, string state) => $"\"{Stage}\"='{stage}' AND \"{State}\"='{state}'";
}

public sealed class JobDescriptor
{
    public int V { get; set; } = 1;
    public string Stage { get; set; } = "download";
    public string JobId { get; set; } = "";
    public string? Url { get; set; }
    public int? MaxHeight { get; set; }
    public string? Format { get; set; }
    public bool? Subs { get; set; }
    public string[]? Steps { get; set; }
    /// <summary>Step jobs: video files that must exist before a node claims the job.</summary>
    public string[]? Needs { get; set; }
    public CompressSettings? Compress { get; set; }
    public string AddedAt { get; set; } = "";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public bool? Permanent { get; set; }
    public int? MaxComments { get; set; }
    public string? Sort { get; set; }
    public int? Round { get; set; }
    /// <summary>Why a comments follow-up round exists ("lead-names").</summary>
    public string? Reason { get; set; }
    public string? FinishedAt { get; set; }
    public string? NodeId { get; set; }
    public double? ElapsedSeconds { get; set; }

    // ---- watch (a followed source) and the download jobs it creates
    /// <summary>"playlist" or "channel".</summary>
    public string? Kind { get; set; }
    /// <summary>Playlist id, or channel id (UC...).</summary>
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Every { get; set; }
    public string[]? Tabs { get; set; }
    public bool? FollowChannels { get; set; }
    public int? Backfill { get; set; }
    public string? LastPolled { get; set; }
    /// <summary>Video ids already seen in this source, newest first, capped at 500. Null until the first poll.</summary>
    public List<string>? Seen { get; set; }
    /// <summary>classify jobs: the channel uploads to decide on (listed by the downloader's watch job <see cref="Source"/>).</summary>
    public List<ClassifyEntry>? Entries { get; set; }
    /// <summary>How many new videos this source has produced so far.</summary>
    public int? Found { get; set; }
    /// <summary>On a download job: the watch job that found the video.</summary>
    public string? Source { get; set; }
    /// <summary>Claim priority (idx_priority): higher first, default 0. Jobs this job creates carry it; a source's priority goes to its downloads.</summary>
    public int Priority { get; set; }
    /// <summary>Every source (watch job id, e.g. "ch-UC..." / "pl-...") this video came from; <see cref="Source"/> is the first. A video in several sources gets the highest of their priorities.</summary>
    public List<string>? Sources { get; set; }
    /// <summary>A video added directly (enqueue --priority N, or sources priority &lt;video&gt; N): its own priority, which counts like one more source.</summary>
    public int? OwnPriority { get; set; }
    /// <summary>True when the priority was set by "priority set" on this job: it stays until a source priority of this video is changed again.</summary>
    public bool? Explicit { get; set; }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Protocol.Json);
    public static JobDescriptor Parse(byte[] b) => JsonSerializer.Deserialize<JobDescriptor>(b, Protocol.Json) ?? throw new InvalidDataException("empty job");

    public static JobDescriptor NewDownload(string url, int maxHeight, string? format) => new()
    {
        Stage = "download", JobId = Protocol.JobIdFor(url), Url = url, MaxHeight = maxHeight, Format = format, Subs = true, AddedAt = Protocol.NowUtc(),
    };

    public static JobDescriptor NewWatch(string kind, string id, string name, string every, string[] tabs, bool followChannels, int backfill, int maxHeight) => new()
    {
        Stage = "watch", JobId = (kind == "playlist" ? "pl-" : "ch-") + id, Kind = kind, Id = id, Name = name, Every = every, Tabs = tabs,
        FollowChannels = followChannels, Backfill = backfill, MaxHeight = maxHeight, AddedAt = Protocol.NowUtc(), Attempts = 0,
    };

    public static JobDescriptor NewComments(string jobId, string url) => new()
    {
        Stage = "comments", JobId = jobId, Url = url, MaxComments = 1000, Sort = "top", Round = 0, AddedAt = Protocol.NowUtc(),
    };

    public static JobDescriptor NewStep(string step, string jobId) => new()
    {
        Stage = step, JobId = jobId, Steps = [step], Needs = Protocol.NeedsOf(step) is { Length: > 0 } n ? n : null, AddedAt = Protocol.NowUtc(),
    };

    public static JobDescriptor NewClassify(JobDescriptor watch, List<ClassifyEntry> entries, string addedAt) => new()
    {
        Stage = "classify", JobId = $"{watch.JobId}-{addedAt.Replace(":", "").Replace("-", "")}", Kind = "channel", Id = watch.Id, Name = watch.Name, Source = watch.JobId,
        MaxHeight = watch.MaxHeight, Entries = entries, AddedAt = addedAt, Attempts = 0,
    };

    public static JobDescriptor NewProcess(string jobId) => new()
    {
        Stage = "process", JobId = jobId, Steps = Protocol.DefaultSteps, AddedAt = Protocol.NowUtc(),
    };
}

public sealed class ClassifyEntry
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public double? Duration { get; set; }
}

public sealed class CompressSettings
{
    public string Codec { get; set; } = "av1";
    public int Crf { get; set; } = 38;
    public int Preset { get; set; } = 8;
    public int AudioKbps { get; set; } = 48;
    public bool KeepOriginal { get; set; }
}

public enum Level { Run = 0, Drain = 1, Stop = 2 }

public sealed class ControlFile
{
    public int V { get; set; } = 1;
    public string Mode { get; set; } = "run";
    public Dictionary<string, string>? Stages { get; set; }
    public string? Message { get; set; }
    public string? ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
    /// <summary>Per-node run limits; they win over the node's own settings.</summary>
    public Dictionary<string, NodeLimits>? Nodes { get; set; }
    /// <summary>GPU routing (0.8.6): absent or no hold = off, CPU nodes claim sscd/clip/cast at once and no gpu-ready markers are written.</summary>
    public GpuControl? Gpu { get; set; }

    public static Level ParseLevel(string? s) => s?.ToLowerInvariant() switch
    {
        "drain" => Level.Drain, "stop" => Level.Stop, _ => Level.Run,
    };

    /// <summary>The stricter of the global mode and the per-stage setting.</summary>
    public Level Effective(string stage)
    {
        var g = ParseLevel(Mode);
        var s = Stages != null && Stages.TryGetValue(stage, out var v) ? ParseLevel(v)
            : Protocol.IsStep(stage) && Stages != null && Stages.TryGetValue("process", out var pv) ? ParseLevel(pv) : Level.Run; // a step follows "process" unless set itself
        return (Level)Math.Max((int)g, (int)s);
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Protocol.Json);
    public static ControlFile Parse(byte[]? b) => b == null || b.Length == 0 ? new() : JsonSerializer.Deserialize<ControlFile>(b, Protocol.Json) ?? new();
}

public sealed class GpuControl
{
    /// <summary>How long a CPU node holds a marker-stage job for the GPU node, e.g. "30m"; absent = no hold.</summary>
    public string? Hold { get; set; }
}

public sealed class NodeLimits
{
    public int? Budget { get; set; }
    public string? Schedule { get; set; }
    public string? Window { get; set; }
}

public sealed class RunningJob
{
    public string JobId { get; set; } = "";
    public string Stage { get; set; } = "";
    public string? Step { get; set; }
    public string? Progress { get; set; }
    public int? Worker { get; set; }
    /// <summary>When the job started (UTC), so the admin page can show how long a node has been on it; reset when the step changes.</summary>
    public string? StartedAt { get; set; }
}

public sealed class NodeInfo
{
    public int V { get; set; } = 1;
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "desktop";
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "";
    public string[] Stages { get; set; } = [];
    public Dictionary<string, int> Jobs { get; set; } = new();
    public int Threads { get; set; }
    public string? Conditions { get; set; }
    /// <summary>Process steps the node can run; a job needing another step is never claimed by it.</summary>
    public string[]? Steps { get; set; }
    public string State { get; set; } = "idle";
    public string Control { get; set; } = "run";
    public List<RunningJob> Running { get; set; } = new();
    /// <summary>State of the sources this node polls, e.g. { "watch-later": "ok" | "signed-out" }.</summary>
    public Dictionary<string, string>? Sources { get; set; }
    /// <summary>Bytes this node moved: totals since it started and the last 48 hourly buckets.</summary>
    public TrafficReport? Traffic { get; set; }
    /// <summary>Where the node runs, detected at start and refreshed daily.</summary>
    public Placement? Placement { get; set; }
    /// <summary>When this node first registered; kept across restarts.</summary>
    public string? FirstSeen { get; set; }
    /// <summary>A per-process GUID of the process that wrote the record last.</summary>
    public string? Instance { get; set; }
    /// <summary>Processes writing under this node id in the last 2 minutes: instance GUID to its lastSeen.</summary>
    public Dictionary<string, string>? Instances { get; set; }
    public string LastSeen { get; set; } = "";
    public string StartedAt { get; set; } = "";

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Protocol.Json);
    public static NodeInfo? Parse(byte[] b) { try { return JsonSerializer.Deserialize<NodeInfo>(b, Protocol.Json); } catch { return null; } }

    /// <summary>A node whose lastSeen is older than 5 minutes counts as offline.</summary>
    public string EffectiveState(DateTimeOffset now) =>
        State == "offline" || !DateTimeOffset.TryParse(LastSeen, out var seen) || now - seen > TimeSpan.FromMinutes(5) ? "offline" : State;
}

public static class PlatformInfo
{
    public static string Platform()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return $"{os}-{arch}";
    }
}
