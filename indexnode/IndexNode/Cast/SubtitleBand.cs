using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>The burned-in subtitles of a video from ocr/lines.json: when a cue is on screen and where (the union of its line boxes), in the OCR video's pixels (Width wide).</summary>
public sealed class SubtitleBand
{
    public double Width { get; }
    readonly List<(double S, double E, double X0, double Y0, double X1, double Y1)> cues = [];
    SubtitleBand(double width) => Width = width;

    public static SubtitleBand? Parse(byte[]? lines, string name = "ocr/lines.json")
    {
        if (lines == null || StoredJson.Parse(lines, name) is not JsonObject o) return null;
        var sb = new SubtitleBand(o["band"]?["width"]?.GetValue<double>() ?? 0); if (sb.Width <= 0) return null;
        foreach (var lang in new[] { "en", "zh" })
            foreach (var c in (o[lang] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var bs = (c["boxes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? []; if (bs.Count == 0) continue;
                sb.cues.Add((c["start"]!.GetValue<double>(), c["end"]!.GetValue<double>(), bs.Min(b => b["x0"]!.GetValue<double>()), bs.Min(b => b["y0"]!.GetValue<double>()), bs.Max(b => b["x1"]!.GetValue<double>()), bs.Max(b => b["y1"]!.GetValue<double>())));
            }
        return sb;
    }

    /// <summary>The subtitle on screen at time t (frames are 1 fps, cues 2 fps: half a second of slack) as [x, y, w, h] in a frame <paramref name="frameW"/> pixels wide (the OCR video has the same shape); null when none.</summary>
    public double[]? At(double t, double frameW)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue; bool any = false;
        foreach (var c in cues)
            if (c.S - 0.5 <= t && t < c.E + 0.5) { any = true; x0 = Math.Min(x0, c.X0); y0 = Math.Min(y0, c.Y0); x1 = Math.Max(x1, c.X1); y1 = Math.Max(y1, c.Y1); }
        if (!any) return null;
        double k = frameW / Width; return [x0 * k, y0 * k, (x1 - x0) * k, (y1 - y0) * k];
    }
}
