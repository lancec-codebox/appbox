using System.Globalization;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>0.13.16: the upload's publish time ("pub", UTC) that orders the sample, check, download and process stages newest first, and where it came from (pubSource: info | listing | estimate).</summary>
public static class PubTime
{
    public const string Info = "info", Listing = "listing", Estimate = "estimate";

    /// <summary>info.json: the epoch timestamp when it has one, else upload_date (a date only: taken at 12:00 UTC).</summary>
    public static string? FromInfo(JsonNode? info)
    {
        if (info == null) return null;
        try
        {
            if (info["timestamp"] is JsonValue tv && tv.TryGetValue<double>(out var ts) && ts > 0) return Protocol.Utc(DateTimeOffset.FromUnixTimeSeconds((long)ts));
            if (info["upload_date"] is JsonValue dv && dv.TryGetValue<string>(out var ud)) return FromDate(ud);
            if (info["uploadDate"] is JsonValue dv2 && dv2.TryGetValue<string>(out var ud2)) return FromDate(ud2);
        }
        catch (InvalidOperationException) { }
        return null;
    }

    public static string? FromDate(string? yyyymmdd) => DynamicSchedule.ParseUploadDate(yyyymmdd) is { } d ? Protocol.Utc(d.AddHours(12)) : null;

    /// <summary>The listing's own time: the flat listing's timestamp, else its upload_date; null when it has neither.</summary>
    public static string? FromListing(long? timestamp, string? uploadDate) =>
        timestamp is > 0 ? Protocol.Utc(DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)) : FromDate(uploadDate);

    /// <summary>
    /// A listing is newest first. Entries with a time keep it; the others get an estimate that keeps the listing order: one minute older per place than the nearest newer entry that has a time
    /// (else one minute newer per place than the nearest older one, else <paramref name="anchor"/> minus a minute per place). pubs = the known time per place (null = none).
    /// </summary>
    public static (string Pub, string Source)[] Fill(IReadOnlyList<(string? Pub, string? Source)> known, DateTimeOffset anchor)
    {
        var res = new (string, string)[known.Count];
        for (int i = 0; i < known.Count; i++)
        {
            if (known[i].Pub is { } p) { res[i] = (p, known[i].Source ?? Listing); continue; }
            int j = i - 1; while (j >= 0 && known[j].Pub == null) j--;
            if (j >= 0) { res[i] = (Protocol.Utc(Parse(known[j].Pub!).AddMinutes(-(i - j))), Estimate); continue; }
            int k = i + 1; while (k < known.Count && known[k].Pub == null) k++;
            res[i] = k < known.Count ? (Protocol.Utc(Parse(known[k].Pub!).AddMinutes(k - i)), Estimate) : (Protocol.Utc(anchor.AddMinutes(-i)), Estimate);
        }
        return res;
    }

    /// <summary>Stamps <paramref name="dst"/> with the publish time: info.json when the store has one for the video (sample/info.json, then info.json; pubSource info), else what the source job already carried.</summary>
    public static async Task StampAsync(IStore store, Paths paths, string id, JobDescriptor src, JobDescriptor dst, CancellationToken ct)
    {
        foreach (var name in new[] { "sample/info.json", "info.json" })
        {
            try
            {
                if (await store.GetAsync(paths.Video(id, name), ct) is { } b && FromInfo(JsonNode.Parse(b.Data)) is { } p) { dst.Pub = p; dst.PubSource = Info; return; }
            }
            catch (System.Text.Json.JsonException) { }
        }
        if (src.Pub != null) { dst.Pub = src.Pub; dst.PubSource = src.PubSource; }
    }

    static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
