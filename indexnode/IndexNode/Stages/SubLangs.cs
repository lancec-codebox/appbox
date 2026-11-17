using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>The exact subtitle tracks to request for one video (0.8.7). Never a regex such as "en.*": that matches every auto-translated track (en-ar, en-ja, ...), and one timedtext request each gets the IP rate-limited.</summary>
public sealed record SubPlan(IReadOnlyList<string> Manual, IReadOnlyList<string> Auto)
{
    public static readonly SubPlan None = new([], []);
    public string ManualArg => string.Join(',', Manual);
    public string AutoArg => string.Join(',', Auto);
}

public static class SubLangs
{
    /// <summary>The uploaded Chinese tracks we keep, in this order.</summary>
    public static readonly string[] Chinese = ["zh", "zh-Hans", "zh-Hant", "zh-CN", "zh-TW", "zh-HK"];
    private static readonly Regex Orig = new("^(en|zh)(-[A-Za-z]+)*-orig$", RegexOptions.CultureInvariant);

    /// <summary>
    /// From the video's info JSON: uploaded subtitles are the keys of "subtitles" that are Chinese (the set above) or English (en, en-US, en-GB, any en-* upload: they are uploads because they are in "subtitles");
    /// automatic captions are only the original-language track(s), the "automatic_captions" keys ending in -orig (zh-orig, en-orig). Translations are never requested. No info JSON: no subtitles.
    /// </summary>
    public static SubPlan Plan(string? infoJson)
    {
        if (string.IsNullOrWhiteSpace(infoJson)) return SubPlan.None;
        JsonObject? o;
        try { o = JsonNode.Parse(infoJson) as JsonObject; } catch { return SubPlan.None; }
        if (o == null) return SubPlan.None;
        var subs = (o["subtitles"] as JsonObject)?.Select(p => p.Key).ToList() ?? [];
        var autos = (o["automatic_captions"] as JsonObject)?.Select(p => p.Key).ToList() ?? [];
        var manual = Chinese.Where(subs.Contains).Concat(subs.Where(k => k == "en" || k.StartsWith("en-", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal)).Distinct().ToList();
        var auto = autos.Where(k => Orig.IsMatch(k)).OrderBy(k => k.StartsWith("zh", StringComparison.Ordinal) ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).Distinct().ToList();
        return new SubPlan(manual, auto);
    }
}
