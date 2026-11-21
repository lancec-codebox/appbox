using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>0.10.0 layout: per drama video.mp4 (and compact.mp4) as blobs plus one &lt;stage&gt;.zip per stage. Which stage owns a member, the zip's metadata keys and the loose/repacked test.</summary>
public static class StageLayout
{
    /// <summary>Stage zips, in pipeline order.</summary>
    public static readonly string[] Stages = ["download", "frames", "sscd", "thumbs", "transcribe", "ocrsubs", "cast", "sync", "describe", "translate", "comments"];
    public const int FormatVersion = 1;
    public const int MaxMetadataBytes = 8000;   // Azure allows 8 KB of metadata per blob
    public const string CropsDir = "cast/crops/";

    public static string ZipName(string stage) => stage + ".zip";
    public static bool IsRaw(string member) => member is "video.mp4" or "compact.mp4" || member.StartsWith("sample/", StringComparison.Ordinal);   // 0.13.16: the sample download's files are loose blobs, never members of download.zip (whose existence means the full download is complete)
    /// <summary>Retired (the dino step): never repacked, deleted by the repack.</summary>
    public static bool IsRetired(string member) => member.StartsWith("dino/", StringComparison.Ordinal);
    /// <summary>The opt-in clip step (dropped 2026-11-14) keeps its loose files: not packed, not counted as loose.</summary>
    public static bool IsPassThrough(string member) => member.StartsWith("clip/", StringComparison.Ordinal) || member is "precheck.json" or "ai.json" or "video.json";   // 0.13.2: video.json (the per-video descriptor) is loose and never in a zip; 0.12.0: the pre-check record and the AI score stay loose (a duplicate has no download.zip)
    /// <summary>The marker files 0.10.0 no longer writes: downloaded.json is download.zip's summary, processed.json is not needed.</summary>
    public static bool IsLegacyMarker(string member) => member is "downloaded.json" or "processed.json";

    /// <summary>The stage zip a member belongs to; null for a raw blob or a retired file.</summary>
    public static string? StageOf(string member)
    {
        if (IsRaw(member) || IsRetired(member) || IsPassThrough(member)) return null;
        if (member.StartsWith("frames/") || member.StartsWith("keyframes/")) return "frames";
        if (member.StartsWith("sscd/")) return "sscd";   // incl. sscd/frames/NNNN.json: the frames indexes as the sscd pass rewrote them (frames.zip is never patched)
        if (member.StartsWith("thumbs/")) return "thumbs";
        if (member.StartsWith("centers/")) return "centers";   // 0.11.0: centers.zip = centers/centers.bin + centers/manifest.json (not in Stages: no loose layout ever existed, and the virtual processed.json hash stays as it was)
        if (member.StartsWith("asr/")) return "transcribe";
        if (member.StartsWith("ocr/")) return "ocrsubs";
        if (member.StartsWith("cast/")) return "cast";
        if (member.StartsWith("subs/ai/")) return "translate";
        if (member.StartsWith("comments/")) return "comments";
        if (member == "sync.json") return "sync";
        if (member == "describe.json") return "describe";
        return "download";   // info.json, description.txt, thumb.jpg, subs/, downloaded.json
    }

    /// <summary>The zip a step job's output goes to (steps are stage names, but a few have none of their own).</summary>
    public static string? ZipOfStep(string step) => Stages.Contains(step) || step == "centers" ? step : null;

    public static string Inputs(IEnumerable<string?> shas) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("|", shas.Select(s => s ?? "-"))))).ToLowerInvariant();

    public static readonly Dictionary<string, string[]> InputStages = new()
    {
        ["sscd"] = ["frames"], ["centers"] = ["sscd"], ["thumbs"] = ["frames"], ["cast"] = ["frames", "sscd"], ["sync"] = ["transcribe", "ocrsubs"],
        ["describe"] = ["transcribe", "ocrsubs", "cast"], ["translate"] = ["describe"],
    };

    /// <summary>The zip's metadata: idx_v, idx_sha256, idx_inputs, idx_node, idx_at and idx_summary (compact JSON, ASCII-escaped). Throws when it would not fit under 8 KB.</summary>
    public static Dictionary<string, string> Metadata(string sha256, string inputs, string nodeId, string summaryJson)
    {
        var ascii = new StringBuilder();
        foreach (var ch in summaryJson) { if (ch < 128) ascii.Append(ch); else ascii.Append("\\u").Append(((int)ch).ToString("x4")); }
        var m = new Dictionary<string, string> { ["idx_v"] = FormatVersion.ToString(), ["idx_sha256"] = sha256, ["idx_inputs"] = inputs, ["idx_node"] = Sanitize(nodeId), ["idx_at"] = Protocol.NowUtc(), ["idx_summary"] = ascii.ToString() };
        int bytes = m.Sum(kv => kv.Key.Length + kv.Value.Length);
        if (bytes > MaxMetadataBytes) { m["idx_summary"] = "{}"; bytes = m.Sum(kv => kv.Key.Length + kv.Value.Length); if (bytes > MaxMetadataBytes) throw new InvalidOperationException("metadata over 8 KB"); }
        return m;
    }
    static string Sanitize(string s) => new(s.Where(c => c is >= ' ' and < (char)127).ToArray());

    /// <summary>The small facts other stages and tools read without opening the zip.</summary>
    public static string SummaryOf(string stage, IReadOnlyDictionary<string, byte[]> small)
    {
        try
        {
            switch (stage)
            {
                case "download" when small.TryGetValue("downloaded.json", out var d): return Compact(d);
                case "describe" when small.TryGetValue("describe.json", out var d):
                    var n = JsonNode.Parse(d)?.AsObject(); var o = new JsonObject();
                    if (n?["title"]?["en"] is { } t) o["titleEn"] = t.DeepClone();
                    if (n?["cast"] is JsonArray cast) o["leads"] = new JsonArray(cast.Where(c => c?["role"]?.ToString().Contains("lead") == true).Select(c => (JsonNode?)(c!["name"]?.DeepClone() ?? c["character"]?["en"]?.DeepClone())).Where(x => x != null).Take(4).ToArray());
                    return o.ToJsonString();
                case "transcribe" when small.TryGetValue("asrModel", out var m): return Compact(m);
            }
        }
        catch { }
        return "{}";
    }
    static string Compact(byte[] json) => JsonNode.Parse(json)!.ToJsonString();
}
