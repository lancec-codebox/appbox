using System.Text.RegularExpressions;

namespace IndexNode;

public enum FailureKind { None, Permanent, BotBlock, Transient }

public static class YtDlp
{
    /// <summary>The -f expression: a job's explicit format wins, else the protocol's default.</summary>
    public static string Selector(int maxHeight, string? format) =>
        !string.IsNullOrWhiteSpace(format) ? format : DefaultSelector;

    /// <summary>
    /// The protocol's rule: resolution first, then codec. <c>-S res:H</c> sorts by resolution closest to (and not above) H, then yt-dlp's
    /// default order (codec: AV1, VP9, H.264...), so 480p VP9 beats 240p AV1; audio is compact Opus when there is one.
    /// </summary>
    public const string DefaultSelector = "bv*+ba[acodec=opus][abr<=80]/bv*+ba/b";

    /// <summary>The format arguments for yt-dlp: <c>-S res:H[,vcodec:C] -f ...</c>, or just <c>-f format</c> when the job sets one.
    /// 0.13.79: a preferred codec (control downloads.videoCodec) comes after the resolution, so it only breaks the tie among formats of that resolution.</summary>
    public static string[] FormatArgs(int maxHeight, string? format, string? codec = null) =>
        !string.IsNullOrWhiteSpace(format) ? ["-f", format] : ["-S", NormalizeCodec(codec) is { } c ? $"res:{maxHeight},vcodec:{c}" : $"res:{maxHeight}", "-f", DefaultSelector];

    /// <summary>0.13.79: the yt-dlp sort name of a codec setting (h264/avc/avc1 -> h264, vp9 -> vp9, av1/av01 -> av01); null for unset, "default" or "off". Anything else is refused.</summary>
    public static string? NormalizeCodec(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" or "default" or "off" => null,
        "h264" or "avc" or "avc1" or "h.264" => "h264",
        "vp9" => "vp9",
        "av1" or "av01" => "av01",
        var x => throw new ArgumentException($"video codec must be h264, vp9, av01 or default (got {x})"),
    };

    private static readonly Regex[] Bot =
    [
        new(@"(?i)confirm you.{0,3}re not a bot"),
        new(@"(?i)HTTP Error 429"),
        new(@"(?i)Too Many Requests"),
        new(@"(?i)rate.?limit"),
    ];

    private static readonly Regex[] Perm =
    [
        new(@"(?i)Video unavailable"),
        new(@"(?i)Private video"),
        new(@"(?i)members-only|members only|Join this channel"),
        new(@"(?i)confirm your age|age-restricted|age restricted|inappropriate for some users"),
        new(@"(?i)not available in your country|not made this video available in your country|blocked it in your country|blocked in your country|geo.?restrict"),
        new(@"(?i)has been removed|been terminated|no longer available|account associated with this video"),
        new(@"(?i)Unsupported URL"),
        new(@"(?i)This video is not available"),
        new(@"(?i)Video has been removed|copyright claim"),
    ];

    /// <summary>Classifies yt-dlp's stderr: bot blocks win over permanent over transient.</summary>
    public static FailureKind Classify(string stderr)
    {
        if (Bot.Any(r => r.IsMatch(stderr))) return FailureKind.BotBlock;
        if (Perm.Any(r => r.IsMatch(stderr))) return FailureKind.Permanent;
        return FailureKind.Transient;
    }

    public static string LastErrorLine(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault() ?? "yt-dlp failed";
    }

    /// <summary>True for a plain video URL that needs no playlist expansion.</summary>
    public static bool IsSingleVideoUrl(string url) =>
        Regex.IsMatch(url, @"(?i)^https?://(www\.|m\.)?(youtube\.com/(watch\?|shorts/)|youtu\.be/)") && !Regex.IsMatch(url, @"[?&]list=");

    /// <summary>The 11-character video id of a single-video URL (watch?v=, youtu.be/, shorts/), else null.</summary>
    public static string? VideoId(string url)
    {
        var m = Regex.Match(url, @"(?:[?&]v=|youtu\.be/|/shorts/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static void Throw(string stderr)
    {
        var msg = Redact.Sas(LastErrorLine(stderr));
        switch (Classify(stderr))
        {
            case FailureKind.BotBlock: throw new BotBlocked(msg);
            case FailureKind.Permanent: throw new PermanentFailure(msg);
            default: throw new InvalidOperationException(msg);
        }
    }
}
