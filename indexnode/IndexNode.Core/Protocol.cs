using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IndexNode;

public static partial class Protocol
{
    public const string ApiVersion = "2021-12-02";
    /// <summary>Name of the speech model whose output the ASR step stores under asr/ (the cache key of the process zips).</summary>
    public const string AsrModelName = "sensevoice";
    /// <summary>The processing steps, each its own job stage (jobs/&lt;step&gt;/&lt;id&gt;.json).</summary>
    public static readonly string[] StepStages = ["frames", "sscd", "centers", "dino", "thumbs", "clip", "transcribe", "ocrsubs", "cast", "sync", "describe", "translate"];
    /// <summary>The step jobs an upload gets by default (download, enqueue, sources): every step stage except the opt-in ones (dino: owner 2026-11-16, not part of ingest; clip: dropped 2026-11-14), which run only when asked for (process --job --steps).</summary>
    public static readonly string[] IngestStepStages = StepStages.Where(s => s is not ("dino" or "clip")).ToArray();
    /// <summary>"process" is the legacy all-in-one stage: its jobs fan out into step jobs.</summary>
    public static readonly string[] Stages = ["download", "process", .. StepStages, "refresh", "classify", "availability", "search-index"];
    /// <summary>Stages that decode or run models and so share the thread budget.</summary>
    public static readonly string[] HeavyStages = ["process", "frames", "sscd", "centers", "dino", "thumbs", "clip", "transcribe", "ocrsubs", "cast"];
    public static bool IsStep(string stage) => StepStages.Contains(stage);
    /// <summary>Stage zips (relative to videos/&lt;id&gt;/) a step job waits for before a node claims it: a zip's existence is its stage's completion signal (0.10.0).</summary>
    public static string[] NeedsOf(string step) => step switch
    {
        "sscd" or "dino" or "clip" or "thumbs" => ["frames.zip"],
        // 0.11.0: the per-drama centers are built from the stored SSCD vectors
        "centers" => ["sscd.zip"],
        // 0.7: cast reads the stored frames (no keyframes), after the SSCD pass has rewritten their index; jobs created before keep their stored needs
        "cast" => ["frames.zip", "sscd.zip"],
        "sync" => ["transcribe.zip", "ocrsubs.zip"],
        "describe" => ["transcribe.zip", "ocrsubs.zip", "cast.zip"],
        "translate" => ["describe.zip"],
        _ => [],
    };
    /// <summary>A need as stored in a job written before 0.10.0 (a loose file) becomes the stage zip that holds it now: frames/manifest.json -> frames.zip, asr/... -> transcribe.zip, describe.json -> describe.zip. Zip names pass through; keyframes/ is part of frames.zip.</summary>
    public static string NormalizeNeed(string need)
    {
        if (need.EndsWith(".zip", StringComparison.Ordinal) && !need.Contains('/')) return need;
        return StageLayout.StageOf(need) is { } st && st != "download" || need is "info.json" or "downloaded.json" or "description.txt" or "thumb.jpg" ? (need is "downloaded.json" or "info.json" or "description.txt" or "thumb.jpg" ? "download.zip" : StageLayout.StageOf(need) + ".zip") : need;
    }
    public static string[] NormalizeNeeds(IEnumerable<string> needs) => needs.Select(NormalizeNeed).Distinct(StringComparer.Ordinal).ToArray();
    public static readonly TimeSpan NeedsRetry = TimeSpan.FromMinutes(2);
    /// <summary>What a desktop node runs by default: downloads go to the phone, so download is opt-in (--stages).</summary>
    // owner 2026-11-14: only the yt-dlp nodes (residential downloaders) touch YouTube: download, watch and comments are opt-in there
    // (--stages download,watch,comments); processing nodes run the steps and classify (which needs an AI endpoint)
    public static readonly string[] DefaultNodeStages = ["process", "classify"]; // "process" stands for every step stage too (see ExpandStages)

    /// <summary>"process" means the legacy stage and all step stages.</summary>
    public static string[] ExpandStages(IEnumerable<string> stages) =>
        stages.Select(CanonicalStage).SelectMany(s => s == "process" ? new[] { "process" }.Concat(StepStages) : [s]).Distinct().ToArray();

    /// <summary>0.10.3: the "watch" stage is now "refresh"; the old name is accepted everywhere (--stages, control.json, --stage).</summary>
    public static string CanonicalStage(string stage) => stage.Equals("watch", StringComparison.OrdinalIgnoreCase) ? "refresh" : stage;

    /// <summary>A source's lookup or refresh job is claimed at this priority when it is made due by hand or on add (the job body keeps the source's own priority, which its videos inherit).</summary>
    public const int RefreshNowPriority = 500;
    // owner 2026-11-14: search vectors (clip) dropped from the default pipeline (weak matches; screenshot search is SSCD). The step still runs when asked for explicitly.
    public static readonly string[] DefaultSteps = ["frames", "sscd", "centers", "thumbs", "transcribe", "ocrsubs"];
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
    /// <summary>0.13.0 queue: the first log segment, the later numbered segments, the snapshot and the per-subject reservations (all under jobs/, never ghosted).</summary>
    public string QueueLog => $"{Prefix}jobs/queue.log";
    public string QueueSegment(int n) => n <= 1 ? QueueLog : $"{Prefix}jobs/queue.{n}.log";
    /// <summary>The snapshot as 0.13.0 first wrote it (plain JSON, 25 MB on the real store); still read when the .gz does not exist yet.</summary>
    public string QueueSnapshot => $"{Prefix}jobs/queue.snapshot.json";
    public string QueueSnapshotGz => $"{Prefix}jobs/queue.snapshot.json.gz";
    public string ActiveDir => $"{Prefix}jobs/active/";
    public string Active(string subject) => $"{Prefix}jobs/active/{subject}.json";
    /// <summary>The ids a source has already listed (kept out of the log lines: up to 500 ids).</summary>
    public string SourceSeen(string sourceId) => $"{Prefix}sources/{sourceId}/seen.json";
    public string Job(string stage, string id) => $"{Prefix}jobs/{stage}/{id}.json";
    public string JobsDir(string stage) => $"{Prefix}jobs/{stage}/";
    public string Video(string id, string file) => $"{Prefix}videos/{id}/{file}";
    public string VideoDir(string id) => $"{Prefix}videos/{id}/";
    /// <summary>0.13.9: a drama's frames file, the lookup readers' only source of its frames (never centers.zip).</summary>
    public string FramesFile(string id) => $"{Prefix}search/frames/{id}.bin";
    /// <summary>0.13.10: a drama's retained frames (the ones no earlier drama covers), the same format as the frames file; the index is built from it when it exists.</summary>
    public string RetainedFile(string id) => $"{Prefix}search/retained/{id}.bin";
    public string RetainedDir => $"{Prefix}search/retained/";
    public string FramesDir => $"{Prefix}search/frames/";
    /// <summary>0.13.0: the control file lives with the queue (jobs/control.json). The old index/control.json stays mode=stop for ever (so old binaries never resume on the old store) and is never read.</summary>
    public string Control => $"{Prefix}jobs/control.json";
    public string LegacyControl => $"{Prefix}control.json";
    /// <summary>index/sources/&lt;id&gt;/members.json: every video the last lookup or refresh listed in the source (for the catalog).</summary>
    public string SourceMembers(string sourceId) => $"{Prefix}sources/{sourceId}/members.json";
    /// <summary>The pre-0.10.3 name of the refresh jobs folder (migrated on first touch).</summary>
    public string LegacyWatchDir => $"{Prefix}jobs/watch/";
    public string Node(string nodeId) => $"{Prefix}nodes/{nodeId}.json";
    public string Seed(string file) => $"{Prefix}seeds/{file}";
    public string NodesDir => $"{Prefix}nodes/";
    public string Hold(string videoId) => $"{Prefix}index/holds/{videoId}.json";
    public string HoldsDir => $"{Prefix}index/holds/";
}

public static class Tags
{
    public const string Stage = "idx_stage", State = "idx_state", Added = "idx_added", Due = "idx_due", Kind = "idx_kind", Priority = "idx_priority";
    /// <summary>When the classify job decided a download job (UTC, set on pending candidates and on rejections): "classify list --rejected --since" finds rejections by it.</summary>
    public const string Classified = "idx_classified";

    public const int MinPriority = -1000, MaxPriority = 1000;

    /// <summary>Throws unless <paramref name="p"/> is within -1000..1000.</summary>
    public static int CheckPriority(int p) => p is >= MinPriority and <= MaxPriority ? p : throw new ArgumentOutOfRangeException(nameof(p), $"priority {p} is outside {MinPriority}..{MaxPriority}");

    /// <summary>idx_priority of a job blob: a plain integer string; missing, unparsable or out of range counts as 0.</summary>
    public static int PriorityOf(IReadOnlyDictionary<string, string> tags) =>
        tags.TryGetValue(Priority, out var s) && int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is >= MinPriority and <= MaxPriority ? p : 0;

    /// <summary>Claim order: priority descending, then idx_added ascending, then blob name.</summary>
    public static IEnumerable<BlobEntry> ClaimOrder(IEnumerable<BlobEntry> blobs) =>
        blobs.OrderByDescending(b => PriorityOf(b.Tags)).ThenBy(b => b.Tags.GetValueOrDefault(Added) ?? "", StringComparer.Ordinal).ThenBy(b => b.Name, StringComparer.Ordinal);

    /// <summary>Find Blobs by Tags returns ONLY the tags the where expression names, so a result sorted by priority and date saw neither (every job looked like priority 0 with no date: claims came in blob-name order). This runs the query and its three variants and merges them by blob name: (a) + idx_added/idx_due >= '' (every job has both), (b) + idx_priority > '0', (c) + idx_priority < '0' (a priority of 0 has no tag; tag comparison is a string comparison, which is all (b) and (c) need: "100" > "0", "-10" < "0"). Order the result with <see cref="ClaimOrder"/>, which compares numbers.</summary>
    public static async Task<IReadOnlyList<BlobEntry>> FindFullAsync(IStore store, string where, CancellationToken ct)
    {
        var all = new[]
        {
            where + $" AND \"{Added}\" >= '' AND \"{Due}\" >= ''",
            where + $" AND \"{Priority}\" > '0'",
            where + $" AND \"{Priority}\" < '0'",
        };
        var merged = new Dictionary<string, BlobEntry>(StringComparer.Ordinal);
        foreach (var q in all)
            foreach (var b in await store.FindByTagsAsync(q, ct))
            {
                if (!merged.TryGetValue(b.Name, out var have)) { merged[b.Name] = b; continue; }
                var t = new Dictionary<string, string>(have.Tags); foreach (var (k, v) in b.Tags) t[k] = v;
                merged[b.Name] = have with { Tags = t };
            }
        return merged.Values.ToList();
    }

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
    /// <summary>Download jobs waiting for the classify job; no node claims them (claims match idx_state='pending' only).</summary>
    public const string Unclassified = "unclassified";
    /// <summary>0.12.0: terminal state of a download job whose pre-check sample matched a video we already have. Not failed, never retried, never claimed (claims match pending only), no follow-up jobs.</summary>
    public const string Duplicate = "duplicate";
    public static string UnclassifiedWhere => StateWhere("download", Unclassified);
    public static string StateWhere(string stage, string state) => $"\"{Stage}\"='{stage}' AND \"{State}\"='{state}'";
}

/// <summary>A drama's temporary hold (index/holds/&lt;videoId&gt;.json): one blob per held drama, created with If-None-Match *, renewed and taken over with If-Match, deleted when the drama's last batch stage is done or on a clean stop. The same four fields go in the body, in metadata (idx_node, idx_batch, idx_until, idx_since: one List Blobs call shows every hold) and, as hints only, node and until in tags (idx_hold_node, idx_hold_until).</summary>
public sealed record HoldInfo(string Node, string Batch, string Until, string Since)
{
    public const string TagNode = "idx_hold_node", TagUntil = "idx_hold_until";
    public static HoldInfo New(string node, string batch, string since) => new(node, batch, Protocol.NowUtc(), since);
    public Dictionary<string, string> Metadata() => new() { ["idx_node"] = Node, ["idx_batch"] = Batch, ["idx_until"] = Until, ["idx_since"] = Since };
    public Dictionary<string, string> HoldTags() => new() { [TagNode] = Node, [TagUntil] = Until };
    public byte[] Body() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { node = Node, batch = Batch, until = Until, since = Since });
    public static HoldInfo? FromMetadata(IReadOnlyDictionary<string, string> m)
    {
        string G(string k) => m.FirstOrDefault(e => string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase)).Value ?? "";
        return G("idx_until").Length == 0 ? null : new HoldInfo(G("idx_node"), G("idx_batch"), G("idx_until"), G("idx_since"));
    }
    public static HoldInfo? FromBody(byte[] data)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(data); var r = d.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            return S("until").Length == 0 ? null : new HoldInfo(S("node"), S("batch"), S("until"), S("since"));
        }
        catch { return null; }
    }
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
    /// <summary>The network the last download attempt started on (0.8.8): written next to lastError on a failure or bot block, and on success.</summary>
    public JobNetwork? Network { get; set; }
    public bool? Permanent { get; set; }
    public int? MaxComments { get; set; }
    public string? Sort { get; set; }
    public int? Round { get; set; }
    /// <summary>Why a comments follow-up round exists ("lead-names").</summary>
    public string? Reason { get; set; }
    public string? FinishedAt { get; set; }
    /// <summary>0.12.0, download jobs in state duplicate: the upload the pre-check sample matched, and the share of sample frames that matched it.</summary>
    public string? DuplicateOf { get; set; }
    public double? DuplicateShare { get; set; }
    /// <summary>Duplicate jobs also record what the site needs to point a user at the right YouTube audio: the sample's spoken language (SenseVoice), dubbed flag, where it came from, the metadata hint, and the YouTube url.</summary>
    public string? DuplicateSpeechLanguage { get; set; }
    public bool? DuplicateDubbed { get; set; }
    public string? DuplicateLanguageSource { get; set; }
    public string? DuplicateMetadataLanguage { get; set; }
    public string? DuplicateUrl { get; set; }
    public string? NodeId { get; set; }
    public double? ElapsedSeconds { get; set; }

    // ---- watch (a followed source) and the download jobs it creates
    /// <summary>"playlist" or "channel".</summary>
    public string? Kind { get; set; }
    /// <summary>Playlist id, or channel id (UC...).</summary>
    public string? Id { get; set; }
    public string? Name { get; set; }
    /// <summary>The interval of an "every" schedule (kept for jobs written before 0.10.3, whose only schedule is this); <see cref="Schedule"/> wins when set.</summary>
    public string? Every { get; set; }
    /// <summary>0.10.3: "once" (the default of a new source: a lookup on add, then never), "off", "6h"/"1d" or "daily@03:00" (UTC).</summary>
    public string? Schedule { get; set; }
    /// <summary>0.10.3: what the next claim of this source job does: "lookup" (full listing: every member recorded, every unknown video queued) or "refresh" (incremental: only videos new since the last run).</summary>
    public string? Run { get; set; }
    /// <summary>The kind of the last completed run ("lookup" or "refresh").</summary>
    public string? LastRun { get; set; }
    public string? LastLookup { get; set; }
    public string? LastRefresh { get; set; }
    /// <summary>When the schedule makes the job due next; null when nothing is scheduled.</summary>
    public string? NextRefresh { get; set; }
    /// <summary>How many videos members.json lists.</summary>
    public int? Members { get; set; }
    public string[]? Tabs { get; set; }
    public bool? FollowChannels { get; set; }
    public int? Backfill { get; set; }
    public string? LastPolled { get; set; }
    /// <summary>0.13.0 dynamic schedule: when the source was last modified (UTC): the last refresh that saw its set change. A channel's first poll uses the newest upload's approximate date, a playlist's first poll counts as now.</summary>
    public string? LastModified { get; set; }
    /// <summary>0.13.0: a dynamic source whose newest upload is over a year old; nothing is scheduled until a manual refresh or a new schedule.</summary>
    public bool? Inactive { get; set; }
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

    // ---- unclassified download jobs: what classification needs, from the listing that found the video (0.9.1)
    public string? Title { get; set; }
    public double? Duration { get; set; }
    public string? ChannelId { get; set; }
    public string? ChannelName { get; set; }
    /// <summary>yt-dlp's availability of the video in the listing (private, needs_auth, ...), when it had one.</summary>
    public string? Availability { get; set; }
    /// <summary>The singleton classify job (index/jobs/classify/all.json): what it decided today (UTC day in <see cref="CountDay"/>).</summary>
    public string? CountDay { get; set; }
    public int? Classified { get; set; }
    public int? Rejected { get; set; }
    /// <summary>0.10.1: how many times the AI left this upload out of its classify answer (given up at 3).</summary>
    public int? ClassifyMisses { get; set; }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Protocol.Json);
    public static JobDescriptor Parse(byte[] b) => JsonSerializer.Deserialize<JobDescriptor>(b, Protocol.Json) ?? throw new InvalidDataException("empty job");

    public static JobDescriptor NewDownload(string url, int maxHeight, string? format) => new()
    {
        Stage = "download", JobId = Protocol.JobIdFor(url), Url = url, MaxHeight = maxHeight, Format = format, Subs = true, AddedAt = Protocol.NowUtc(),
    };

    /// <summary>A followed source. <paramref name="schedule"/> is once | off | 6h | 1d | daily@03:00; a negative <paramref name="backfill"/> means no limit (every unknown video of a playlist is queued on the lookup).</summary>
    public static JobDescriptor NewSource(string kind, string id, string name, string schedule, string[] tabs, bool followChannels, int backfill, int maxHeight)
    {
        var sched = IndexNode.Schedule.Parse(schedule);
        return new()
        {
            Stage = "refresh", JobId = (kind == "playlist" ? "pl-" : "ch-") + id, Kind = kind, Id = id, Name = name, Schedule = sched.Text, Every = sched.Kind == "every" ? sched.Text : null, Tabs = tabs,
            FollowChannels = followChannels, Backfill = backfill < 0 ? null : backfill, MaxHeight = maxHeight, AddedAt = Protocol.NowUtc(), Attempts = 0, Run = "lookup",
        };
    }

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
    public string? ChannelName { get; set; }
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
    /// <summary>Download limits (0.8.8): absent, null or maxPerIp 0 = no cap (the default).</summary>
    public DownloadsControl? Downloads { get; set; }
    /// <summary>0.13.1: "on" lets nodes claim an AI-held drama (the pre-check flagged it AI); "off" or absent: they stay pending. Default off.</summary>
    public string? AiDramas { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool AiDramasOn => string.Equals(AiDramas, "on", StringComparison.OrdinalIgnoreCase);

    public static Level ParseLevel(string? s) => s?.ToLowerInvariant() switch
    {
        "drain" => Level.Drain, "stop" => Level.Stop, _ => Level.Run,
    };

    /// <summary>The stricter of the global mode and the per-stage setting.</summary>
    public Level Effective(string stage)
    {
        var g = ParseLevel(Mode);
        var s = Stages != null && Stages.TryGetValue(stage, out var v) ? ParseLevel(v)
            : stage == "refresh" && Stages != null && Stages.TryGetValue("watch", out var wv) ? ParseLevel(wv) // the control file's pre-0.10.3 "watch" key is the refresh stage
            : Protocol.IsStep(stage) && Stages != null && Stages.TryGetValue("process", out var pv) ? ParseLevel(pv) : Level.Run; // a step follows "process" unless set itself
        return (Level)Math.Max((int)g, (int)s);
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Protocol.Json);
    public static ControlFile Parse(byte[]? b) => b == null || b.Length == 0 ? new() : JsonSerializer.Deserialize<ControlFile>(b, Protocol.Json) ?? new();
}

public sealed class DownloadsControl
{
    /// <summary>At most this many downloads at once across the nodes that share one public address (same IPv4 or IPv6 /64). Best effort: two nodes may start at the same moment.</summary>
    public int? MaxPerIp { get; set; }
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
    public Dictionary<string, int> Jobs { get; set; } = new(); // old nodes (before 0.8.9) report their per-stage jobs; 0.8.9 reports Workers
    public int Workers { get; set; }
    public int WorkerThreads { get; set; }
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
    /// <summary>0.13.0: this node's storage operations per minute (reads, writes, lists) over the last five minutes, and totals since start.</summary>
    public OpsReport? Ops { get; set; }
    /// <summary>Where the node runs, detected at start and refreshed daily.</summary>
    public Placement? Placement { get; set; }
    /// <summary>The node's public network (0.8.8), re-checked every 30 minutes.</summary>
    public NetworkInfo? Network { get; set; }
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
