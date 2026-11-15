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
    public const double Fps = 2; // owner 2026-11-14: every 0.5 s (every cue lasts >= 0.5 s: none shorter in 139,331 measured cues)
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

    /// <summary>A reading taken while the line faded in or out: the longer text starts or ends with the shorter one (anywhere inside would chain different lines through a shared watermark).</summary>
    static bool Holds(string longer, string shorter) => shorter.Length > 0 && longer.Length > shorter.Length && (longer.StartsWith(shorter, StringComparison.Ordinal) || longer.EndsWith(shorter, StringComparison.Ordinal));

    /// <summary>
    /// Watermarks: text that stays on screen beside the subtitles (a disclaimer, a channel name) is read in sample after sample across the video.
    /// A line of at least 4 characters seen in at least <paramref name="minSamples"/> samples spread over at least 3 five-minute windows, and once for 20 s without a break (<paramref name="minRun"/> samples), is a watermark;
    /// it and its misreadings (a line sharing 6 characters in a row with it) are dropped before cues are merged. Returns the frames and the watermarks found.
    /// </summary>
    public static (List<FrameText> Frames, List<string> Marks) StripWatermarks(IReadOnlyList<FrameText> frames, int minSamples = 120, int minRun = 40)
    {
        // a subtitle stays a few seconds; a watermark stays on screen without a break: it must also have a run of at least minRun consecutive samples
        var count = new Dictionary<string, int>(); var windows = new Dictionary<string, HashSet<int>>(); var run = new Dictionary<string, (int Last, int Len, int Max)>();
        for (int k = 0; k < frames.Count; k++)
            foreach (var t in frames[k].Lines.Select(l => l.Text.Trim()).Where(t => t.Length >= 4).Distinct())
            {
                count[t] = count.GetValueOrDefault(t) + 1; (windows.TryGetValue(t, out var w) ? w : windows[t] = []).Add((int)(frames[k].T / 300));
                var r = run.TryGetValue(t, out var rv) ? rv : (Last: -10, Len: 0, Max: 0); int len = k <= r.Last + 2 ? r.Len + (k - r.Last) : 1; // a missed read or two does not break a run
                run[t] = (k, len, Math.Max(r.Max, len));
            }
        var marks = count.Where(kv => kv.Value >= minSamples && windows[kv.Key].Count >= 3 && run[kv.Key].Max >= minRun).Select(kv => kv.Key).ToList();
        if (marks.Count == 0) return (frames.ToList(), marks);
        bool IsMark(string t) { t = t.Trim(); return marks.Any(m => t == m || SharesRun(t, m, Math.Min(6, m.Length))); }
        return (frames.Select(f => f with { Lines = f.Lines.Where(l => !IsMark(l.Text)).ToList() }).ToList(), marks);
    }

    static bool SharesRun(string a, string b, int n)
    {
        if (a.Length < n || b.Length < n) return false;
        for (int i = 0; i + n <= b.Length; i++) if (a.Contains(b.Substring(i, n), StringComparison.Ordinal)) return true;
        return false;
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
            // same cue when the text differs by at most 2 edits AND at most a quarter of its length: short lines ("什么" vs "你说什么") stay apart
            // or when one reading holds the other (a line read while fading in or out: "换" next to "换一个")
            if (v != null && cur != null && gap <= 1 && (Levenshtein(v.Text, cur[^1].P.Text) <= Math.Min(MaxEdit, Math.Max(v.Text.Length, cur[^1].P.Text.Length) / 4)
                || Holds(v.Text, cur[^1].P.Text) || Holds(cur[^1].P.Text, v.Text))) { cur.Add((f.T, v)); gap = 0; continue; }
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
            // at 0.5 s sampling a single sample is a real cue (cues last >= 0.5 s); flicker is what the reads disagree on, not one sample
            var groups = c.GroupBy(x => x.P.Text).Select(g => (Text: g.Key, Count: g.Count(), Conf: g.Sum(x => x.P.Conf), First: g.First().P)).ToList();
            var best = groups.OrderByDescending(g => g.Count).ThenByDescending(g => g.Text.Length).ThenByDescending(g => g.Conf).First();
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
