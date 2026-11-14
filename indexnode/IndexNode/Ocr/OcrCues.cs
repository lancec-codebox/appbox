using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>An OCR'd line with its box, in full-frame coordinates.</summary>
public sealed record OcrBox(int X0, int Y0, int X1, int Y1, string Text, double Score);

/// <summary>What one sampled frame of the band read: Chinese lines and English lines, kept apart.</summary>
public sealed record FrameText(double T, IReadOnlyList<OcrBox> Lines);

public sealed class Cue
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public string TextHans { get; set; } = "";
    public double Conf { get; set; }
    public List<OcrBox> Boxes { get; set; } = [];
    public int Frames { get; set; }
}

/// <summary>Merging per-frame OCR into timed cues (port of the tested post.py).</summary>
public static partial class OcrCues
{
    public const double Fps = 4;
    public const int MaxEdit = 2;

    [GeneratedRegex(@"[\u3400-\u9fff]")] private static partial Regex Cjk();
    [GeneratedRegex(@"[A-Za-z]")] private static partial Regex Latin();

    public static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>A line is Chinese when it has a CJK character and fewer than 4 Latin letters; everything else is English.</summary>
    public static bool IsChinese(string t) => Cjk().IsMatch(t) && Latin().Matches(t).Count < 4;

    private sealed record Part(string Text, double Conf, List<OcrBox> Boxes);

    private static (Part? Zh, Part? En) Split(IReadOnlyList<OcrBox> lines)
    {
        var zh = new List<OcrBox>(); var en = new List<OcrBox>();
        foreach (var l in lines.OrderBy(l => l.Y0).ThenBy(l => l.Y1).ThenBy(l => l.X0).ThenBy(l => l.X1))
        {
            var t = l.Text.Trim();
            if (t.Length == 0) continue;
            (IsChinese(t) ? zh : en).Add(l with { Text = t });
        }
        Part? P(List<OcrBox> ls, string sep) => ls.Count == 0 ? null : new Part(string.Join(sep, ls.Select(l => l.Text)), ls.Average(l => l.Score), ls);
        return (P(zh, ""), P(en, " "));
    }

    public static List<Cue> Merge(IEnumerable<FrameText> frames, bool english)
    {
        var done = new List<List<(double T, Part P)>>();
        List<(double T, Part P)>? cur = null;
        int gap = 0;
        foreach (var f in frames.OrderBy(f => f.T))
        {
            var (zh, en) = Split(f.Lines);
            var v = english ? en : zh;
            if (v != null && cur != null && Levenshtein(v.Text, cur[^1].P.Text) <= MaxEdit && gap <= 1) { cur.Add((f.T, v)); gap = 0; continue; }
            if (v != null)
            {
                if (cur != null) done.Add(cur);
                cur = [(f.T, v)]; gap = 0;
            }
            else if (cur != null)
            {
                gap++;
                if (gap > 1) { done.Add(cur); cur = null; }
            }
        }
        if (cur != null) done.Add(cur);

        var cues = new List<Cue>();
        foreach (var c in done)
        {
            if (c.Count < 2) continue; // a single frame is flicker
            var groups = c.GroupBy(x => x.P.Text).Select(g => (Text: g.Key, Count: g.Count(), Conf: g.Sum(x => x.P.Conf), First: g.First().P)).ToList();
            var best = groups.OrderByDescending(g => g.Count).ThenByDescending(g => g.Conf).First();
            cues.Add(new Cue
            {
                Start = Math.Round(c[0].T, 2), End = Math.Round(c[^1].T + 1 / Fps, 2), Text = best.Text, Conf = Math.Round(c.Average(x => x.P.Conf), 3),
                Boxes = best.First.Boxes, Frames = c.Count,
            });
        }
        return cues;
    }

    public static string Timestamp(double s)
    {
        var ms = (long)Math.Round(s * 1000);
        return $"{ms / 3600000:D2}:{ms / 60000 % 60:D2}:{ms / 1000 % 60:D2}.{ms % 1000:D3}";
    }

    public static List<ShapeCue> Shaped(IEnumerable<Cue> cues, bool hans) =>
        CueShaper.Shape(cues.Select(c => new ShapeCue(c.Start, c.End, hans && c.TextHans.Length > 0 ? c.TextHans : c.Text)));

    public static string ToVtt(IEnumerable<Cue> cues, bool hans) => CueShaper.ToVtt(Shaped(cues, hans));

    public static void Simplify(IEnumerable<Cue> cues, OpenCc cc) { foreach (var c in cues) c.TextHans = cc.Convert(c.Text); }

    public static byte[] ToJson(IReadOnlyList<Cue> zh, IReadOnlyList<Cue> en, int bandY, int bandH, int width)
    {
        object Map(Cue c) => new
        {
            start = c.Start, end = c.End, text = c.Text, textHans = c.TextHans, conf = c.Conf,
            boxes = c.Boxes.Select(b => new { x0 = b.X0, y0 = b.Y0, x1 = b.X1, y1 = b.Y1, text = b.Text, conf = Math.Round(b.Score, 3) }),
        };
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1, fps = Fps, band = new { y = bandY, height = bandH, width }, converter = "OpenCC t2s", shape = new { zh = CueShaper.Stats(Shaped(zh, false)).ToJson(), zhHans = CueShaper.Stats(Shaped(zh, true)).ToJson(), en = CueShaper.Stats(Shaped(en, false)).ToJson() },
            zh = zh.Select(Map), en = en.Select(Map),
        });
    }
}
