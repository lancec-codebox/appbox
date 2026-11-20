using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// The duration of a stored upload, for the pre-check's duration rule. Repacked (0.10.0+) layout: downloaded.json does not exist as a blob, it is download.zip's idx_summary metadata (durationSeconds):
/// one HEAD, never a hydrate. Older layouts: index/videos/&lt;id&gt;/downloaded.json. Null when neither says.
/// </summary>
public static class MatchedDuration
{
    public static async Task<double?> ReadAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        try
        {
            if ((await store.HeadAsync(paths.Video(id, "download.zip"), ct))?.Metadata.GetValueOrDefault("idx_summary") is { Length: > 2 } s && Parse(s) is { } d) return d;
            if (await store.GetAsync(paths.Video(id, "downloaded.json"), ct) is { } c) return Parse(System.Text.Encoding.UTF8.GetString(c.Data));
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        return null;
    }

    static double? Parse(string json) { try { return JsonNode.Parse(json) is JsonObject o && (double?)o["durationSeconds"] is > 0 and var v ? v : null; } catch (System.Text.Json.JsonException) { return null; } }
}
