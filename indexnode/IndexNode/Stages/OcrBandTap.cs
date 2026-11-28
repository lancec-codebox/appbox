using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>One part of the subtitle band, decoded by the frames step: [Start, End) of the video in a lossless file.</summary>
public sealed record BandPart(double Start, double End, string File);

/// <summary>
/// 0.13.94 (owner, 2026-11-28: "we can do one decode for both"): the frames step's decode also writes the subtitle band at the OCR rate, lossless (libx264rgb -qp 0: the
/// pixels the OCR step would decode itself, byte for byte), into a per-upload folder outside the step's work folder. The OCR step on the same node reads those small files
/// instead of decoding the whole video again (measured on a 480p AV1 upload: decode 12.3 s per 10 min, the band file 2.9 s at 4 fps). Any doubt (no file, other size,
/// a gap, another node) and OCR decodes the video as before. Env INDEXNODE_SHARED_DECODE=0 turns it off.
/// </summary>
public sealed class OcrBandTap
{
    public int Y { get; init; }
    public int H { get; init; }
    public int SrcW { get; init; }
    public int SrcH { get; init; }
    public double Duration { get; init; }
    /// <summary>The band search found no burned-in subtitles: the OCR step writes empty results without a decode.</summary>
    public bool NoBand { get; init; }
    public string Dir { get; init; } = "";
    public ConcurrentBag<BandPart> Parts { get; } = new();

    public static bool On => Environment.GetEnvironmentVariable("INDEXNODE_SHARED_DECODE") != "0";
    public static string DirFor(string id) => Path.Combine(Path.GetTempPath(), "indexnode", "band-" + id);
    /// <summary>The OCR step's own filter (OcrSubs.ReadAsync), so the pixels are the same.</summary>
    public string Filter => $"fps={OcrCues.Fps},crop={SrcW}:{H}:0:{Y}";
    public static readonly string[] EncodeArgs = ["-c:v", "libx264rgb", "-preset", "ultrafast", "-qp", "0", "-threads", "1", "-pix_fmt", "bgr24", "-f", "matroska"];

    static bool? encoder;
    /// <summary>Whether this ffmpeg has the lossless encoder (asked once per process).</summary>
    public static async Task<bool> EncoderAvailableAsync(string ffmpeg, CancellationToken ct)
    {
        if (encoder is { } e) return e;
        var r = await Proc.RunAsync(ffmpeg, ["-hide_banner", "-encoders"], ct);
        return (encoder = r.ExitCode == 0 && r.Stdout.Contains("libx264rgb", StringComparison.Ordinal)).Value;
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        var parts = new JsonArray(Parts.OrderBy(p => p.Start).Select(p => (JsonNode)new JsonObject { ["start"] = p.Start, ["end"] = p.End, ["file"] = Path.GetFileName(p.File) }).ToArray());
        var o = new JsonObject { ["y"] = Y, ["h"] = H, ["srcW"] = SrcW, ["srcH"] = SrcH, ["duration"] = Duration, ["noBand"] = NoBand, ["parts"] = parts };
        Directory.CreateDirectory(Dir);
        await File.WriteAllTextAsync(Path.Combine(Dir, "band.json"), o.ToJsonString(), ct);
    }

    /// <summary>The tap of <paramref name="id"/> when it matches this video and covers it with no gap; otherwise null (the OCR step decodes the video itself).</summary>
    public static OcrBandTap? Load(string id, int srcW, int srcH, double duration)
    {
        var dir = DirFor(id); var f = Path.Combine(dir, "band.json");
        try
        {
            if (!File.Exists(f) || JsonNode.Parse(File.ReadAllText(f)) is not JsonObject o) return null;
            if ((int?)o["srcW"] != srcW || (int?)o["srcH"] != srcH || Math.Abs(((double?)o["duration"] ?? -1) - duration) > 0.5) return null;
            var tap = new OcrBandTap { Y = (int?)o["y"] ?? 0, H = (int?)o["h"] ?? 0, SrcW = srcW, SrcH = srcH, Duration = duration, NoBand = (bool?)o["noBand"] == true, Dir = dir };
            if (tap.NoBand) return tap;
            if (tap.H <= 0) return null;
            double at = 0;
            foreach (var p in (o["parts"] as JsonArray ?? []).OfType<JsonObject>().Select(p => new BandPart((double?)p["start"] ?? -1, (double?)p["end"] ?? -1, Path.Combine(dir, (string?)p["file"] ?? ""))).OrderBy(p => p.Start))
            {
                if (Math.Abs(p.Start - at) > 0.01 || p.End <= p.Start || !File.Exists(p.File)) return null;   // a gap, an overlap or a missing file
                tap.Parts.Add(p); at = p.End;
            }
            return at >= Math.Floor(duration) ? tap : null;   // the frames step samples whole seconds: the last part may end at the last whole second
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException) { return null; }
    }

    public static void Delete(string id) { try { if (Directory.Exists(DirFor(id))) Directory.Delete(DirFor(id), true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

    /// <summary>Removes taps left behind (the OCR step ran on another node, or never ran) after 12 hours.</summary>
    public static void Sweep()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "indexnode"); if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root, "band-*"))
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > TimeSpan.FromHours(12)) try { Directory.Delete(d, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
