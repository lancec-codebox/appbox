using System.Text.RegularExpressions;

namespace IndexNode;

public enum FailureKind { None, Permanent, BotBlock, Transient }

public static class YtDlp
{
    public static string Selector(int maxHeight, string? format) =>
        !string.IsNullOrWhiteSpace(format) ? format : $"bv*[height<={maxHeight}][ext=mp4]+ba[ext=m4a]/b[height<={maxHeight}]/b";

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
