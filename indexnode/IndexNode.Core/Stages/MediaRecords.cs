using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.37: the per-upload row of the Azure Table `media` (PartitionKey "upload", RowKey the upload id). Every top-level section of video.json is one JSON-string property of the row, named s_&lt;section&gt;; the flat query fields are
/// written by the section that owns them (grouping: seq, kind, joinUpload; ai: aiFlag, aiScore; youtube: title, channel, duration, the curated metadata; speech: speechLanguage). The stretches of a grouping record and any section over
/// <see cref="MediaTables.MaxStringChars"/> characters stay in the per-upload blobs (the row's inBlob lists them).
/// </summary>
public static class MediaRecords
{
    public const string InBlob = "inBlob";
    /// <summary>A section's property is named s_&lt;section&gt; (s_youtube, s_grouping, ...), as PROTOCOL decided: the plain names belong to the stage marks (the upload row's `download`, `cast`, `group` properties are the stages' stored-output objects), so a section and a stage of the same name (download, sample) never share a property.</summary>
    public const string SectionPrefix = "s_";
    public static string Prop(string section) => SectionPrefix + section;
    public const string DocRev = "docRev";
    /// <summary>A row readers may use: it mirrors a whole document (docRev) and keeps no section in the blob.</summary>
    public static bool IsFull(TableRow r) => r.Num(DocRev) != null && string.IsNullOrEmpty(r.Str(InBlob));
    static readonly HashSet<string> NotSections = new(StringComparer.Ordinal) { "v", "id", "rev" };

    /// <summary>The section's property text: grouping without its stretches; null when it is over the property limit (it stays in the blob).</summary>
    public static string? SectionText(string name, JsonNode? sec)
    {
        if (sec == null) return null;
        if (name == "grouping" && sec is JsonObject g) { var c = (JsonObject)g.DeepClone(); c.Remove("stretches"); sec = c; }
        var s = sec.ToJsonString(); return s.Length > MediaTables.MaxStringChars ? null : s;
    }

    /// <summary>The properties to merge for the named sections of a video.json: each section's JSON string and the flat fields its owner writes. Sections too big for a property are listed in inBlob.</summary>
    public static JsonObject Props(JsonObject root, IEnumerable<string> sectionNames)
    {
        var sections = sectionNames.ToList();
        var p = new JsonObject(); var big = new List<string>();
        foreach (var name in sections)
        {
            if (NotSections.Contains(name)) continue;
            var sec = root[name];
            if (sec == null) continue;
            var text = SectionText(name, sec);
            if (text == null) { big.Add(name); continue; }
            p[Prop(name)] = text;
            if (sec is JsonObject so) foreach (var kv in Flat(name, so)) p[kv.Key] = kv.Value?.DeepClone();
        }
        // the download and sample stages write the speech language they know from info.json (youtube.language); the detected one (the speech section) always wins
        if (p["speechLanguage"] == null && root["speech"] == null && sections.Contains("youtube") && (string?)(root["youtube"] as JsonObject)?["language"] is { Length: > 0 } yl) p["speechLanguage"] = yl;
        if (big.Count > 0) p[InBlob] = string.Join(',', big);
        return p;
    }

    static string? Join(JsonNode? n) => n is JsonArray a && a.Count > 0 ? string.Join(',', a.Select(x => (string?)x).Where(x => !string.IsNullOrEmpty(x))) : null;

    /// <summary>The flat query fields a section owns (null values left out: a merge never erases a field the section does not say).</summary>
    public static JsonObject Flat(string section, JsonObject sec)
    {
        var f = new JsonObject();
        void Put(string k, JsonNode? v) { if (v != null) f[k] = v.DeepClone(); }
        switch (section)
        {
            case "youtube":
                Put("title", sec["title"]); Put("description", sec["description"] is JsonValue dv && dv.TryGetValue<string>(out var d) ? JsonValue.Create(d.Length > MediaTables.MaxStringChars ? d[..MediaTables.MaxStringChars] : d) : null);
                Put("channel", sec["channel"]); Put("uploader", sec["uploader"]); Put("channelId", sec["channelId"]); Put("uploadDate", sec["uploadDate"]);
                Put("durationSeconds", sec["durationSeconds"]); Put("duration", sec["durationSeconds"]); Put("viewCount", sec["viewCount"]); Put("likeCount", sec["likeCount"]);
                Put("availability", sec["availability"]); Put("playableInEmbed", sec["playableInEmbed"]);
                Put("subtitleLanguages", Join(sec["subtitleLanguages"]) is { } a ? JsonValue.Create(a) : null); Put("autoCaptionLanguages", Join(sec["autoCaptionLanguages"]) is { } b ? JsonValue.Create(b) : null);
                Put("subtitleFormats", Join(sec["subtitleFormats"]) is { } c ? JsonValue.Create(c) : null); Put("audioLanguages", Join(sec["audioLanguages"]) is { } e ? JsonValue.Create(e) : null);
                Put("audioLanguage", sec["audioLanguage"]);
                break;
            case "grouping":
                Put("seq", sec["seq"]); var prop = sec["proposal"] as JsonObject; Put("kind", prop?["kind"]); Put("joinUpload", prop?["joinUpload"]);
                break;
            case "ai": Put("aiFlag", sec["flag"]); Put("aiScore", sec["score"]); break;
            case "speech": Put("speechLanguage", sec["language"]); break;
        }
        return f;
    }

    /// <summary>A video.json-shaped document from a table row (v, id and every section property parsed). A section the row keeps in the blob is simply absent.</summary>
    public static JsonObject DocOf(TableRow row)
    {
        var root = new JsonObject { ["v"] = VideoJson.Version, ["id"] = row.RowKey };
        foreach (var kv in row.Props)
        {
            if (!kv.Key.StartsWith(SectionPrefix, StringComparison.Ordinal) || kv.Key.Length == SectionPrefix.Length) continue;   // only the s_<section> properties are sections (a stage mark's property is not one)
            if (kv.Value is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length == 0 || s[0] != '{') continue;
            try { if (JsonNode.Parse(s) is JsonObject o) root[kv.Key[SectionPrefix.Length..]] = o; } catch (JsonException) { }
        }
        return root;
    }
    static readonly HashSet<string> Flats = new(StringComparer.Ordinal) { "title", "description", "channel", "uploader", "channelId", "uploadDate", "availability", "subtitleLanguages", "autoCaptionLanguages", "subtitleFormats", "audioLanguages", "audioLanguage", "kind", "joinUpload", "speechLanguage", "data", "seqBlob", InBlob, DocRev };

    /// <summary>The sections that differ between two documents (added, changed or removed), as top-level names.</summary>
    public static List<string> Changed(JsonObject before, JsonObject after)
    {
        var names = new List<string>();
        foreach (var kv in after) if (!NotSections.Contains(kv.Key) && before[kv.Key]?.ToJsonString() != kv.Value?.ToJsonString()) names.Add(kv.Key);
        return names;
    }
}
