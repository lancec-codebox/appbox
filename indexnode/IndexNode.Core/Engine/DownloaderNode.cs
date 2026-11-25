namespace IndexNode;

/// <summary>What a downloader node is told besides its store and its tools: the check server (`indexnode serve`: the inline check of a sample, and the download service's hand-over) and the pre-check settings.</summary>
public sealed record DownloaderSettings
{
    public string? CheckUrl { get; init; }
    public string? CheckToken { get; init; }
    public int? CheckTimeoutSeconds { get; init; }
    public PreCheckSettings? PreCheck { get; init; }
    public bool HasCheck => !string.IsNullOrWhiteSpace(CheckUrl);
}

/// <summary>
/// 0.13.42: the downloader node every host runs (the desktop node and IndexNode for Android): the same stages, the same handlers and the same calls to the check server.
/// A host supplies only what differs: its tools (yt-dlp, ffmpeg and ffprobe as local programs on a desktop, through Medusa on a phone), its store transport, its HTTP client and where the settings come from.
/// </summary>
public static class DownloaderNode
{
    /// <summary>The stages a downloader has handlers for (`sample` is claimed by a node that runs `download`; `classify` is the desktop's AI stage and is not here).</summary>
    public static readonly string[] StageNames = ["download", "sample", "refresh", "availability"];

    /// <summary>0.10.3: a download node also lists sources (a lookup when one is added, then its schedule), unless told not to.</summary>
    public static string[] WithRefresh(string[] stages, bool noRefresh) =>
        stages.Contains("download") && !stages.Contains("refresh") && !noRefresh ? [.. stages, "refresh"] : stages;

    /// <summary>The client of the check server's /v1/check, or null when no check server is set.</summary>
    public static InlineCheckClient? CheckClient(HttpClient http, DownloaderSettings s) =>
        s.HasCheck ? new InlineCheckClient(http, s.CheckUrl!.Trim().TrimEnd('/'), s.CheckToken, TimeSpan.FromSeconds(Math.Max(1, s.CheckTimeoutSeconds ?? 90))) : null;

    public static Dictionary<string, IStageHandler> Handlers(ITools tools, IStore store, DownloadGate gate, HttpClient http, DownloaderSettings s) => new()
    {
        ["download"] = new DownloadStage(tools, store, gate),
        ["sample"] = new SampleStage(tools, store, gate, s.PreCheck ?? new()) { Inline = CheckClient(http, s) },
        ["refresh"] = new RefreshStage(tools, store),
        ["availability"] = new AvailabilityStage(tools, store),
    };

    /// <summary>EngineOptions.FetchNext: the download service's hand-over (POST /v1/download/next) for a node whose work queue has nothing ready; null when no check server is set.</summary>
    public static Func<string[], CancellationToken, Task<NextClaim?>>? FetchNext(HttpClient http, DownloaderSettings s, string nodeId) =>
        s.HasCheck ? (stages, ct) => DownloadServiceClient.NextAsync(http, s.CheckUrl!.Trim(), s.CheckToken, nodeId, stages, ct) : null;

    /// <summary>EngineOptions.FetchSampled (0.13.49): after a sample, the same upload's download from the download service (POST /v1/download/sampled; the check runs inside the call, so it gets the check's timeout); null when no check server is set.</summary>
    public static Func<string, CancellationToken, Task<(NextClaim? Claim, string Outcome)>>? FetchSampled(HttpClient http, DownloaderSettings s, string nodeId) =>
        s.HasCheck ? (id, ct) => DownloadServiceClient.SampledAsync(http, s.CheckUrl!.Trim(), s.CheckToken, nodeId, id, TimeSpan.FromSeconds(Math.Max(1, s.CheckTimeoutSeconds ?? 90)), ct) : null;
}
