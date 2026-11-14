using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

public sealed record AudioTrack(string? Language, string? Note);

/// <summary>What downloaded.json records about the chosen formats, read from yt-dlp's info.json (protocol: Download stage).</summary>
public sealed record DownloadedFormats(
    string? VCodec, string? ACodec, string? FormatId, string? AudioLanguage, string? AudioNote,
    IReadOnlyList<AudioTrack> AudioTracks, bool Embeddable, int Height, double Duration)
{
    private static readonly Regex QualityTail = new(@",\s*(ultralow|low|medium|high)\s*$", RegexOptions.IgnoreCase);

    private static bool Real(string? codec) => !string.IsNullOrEmpty(codec) && codec != "none";
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The note of an audio format without its trailing quality word ("English (US), original (default), low" -> "English (US), original (default)").</summary>
    public static string? TrackNote(string? formatNote) => formatNote == null ? null : QualityTail.Replace(formatNote, "").Trim();

    public static DownloadedFormats FromInfo(JsonObject info)
    {
        JsonObject? video = null, audio = null;
        if (info["requested_formats"] is JsonArray rf)
        {
            foreach (var f in rf.OfType<JsonObject>())
            {
                if (video == null && Real(Str(f["vcodec"]))) video = f;
                if (audio == null && Real(Str(f["acodec"])) && !Real(Str(f["vcodec"]))) audio = f;
            }
        }
        var vcodec = Str(video?["vcodec"]) ?? (Real(Str(info["vcodec"])) ? Str(info["vcodec"]) : null);
        var acodec = Str(audio?["acodec"]) ?? (Real(Str(info["acodec"])) ? Str(info["acodec"]) : null);
        // a single muxed format carries its own audio language
        var langSource = audio ?? info;
        var tracks = new List<AudioTrack>();
        if (info["formats"] is JsonArray fs)
        {
            var seen = new HashSet<string>();
            foreach (var f in fs.OfType<JsonObject>())
            {
                if (Real(Str(f["vcodec"])) || !Real(Str(f["acodec"]))) continue;
                var t = new AudioTrack(Str(f["language"]), TrackNote(Str(f["format_note"])));
                if (t.Language == null && t.Note == null) continue;
                if (seen.Add((t.Language ?? "") + "|" + (t.Note ?? ""))) tracks.Add(t);
            }
        }
        return new DownloadedFormats(
            vcodec, acodec, Str(info["format_id"]), Str(langSource["language"]), audio != null ? TrackNote(Str(audio["format_note"])) : null,
            tracks, info["playable_in_embed"] is JsonValue e && e.TryGetValue<bool>(out var b) && b,
            (int?)info["height"] ?? 0, (double?)info["duration"] ?? 0);
    }
}
