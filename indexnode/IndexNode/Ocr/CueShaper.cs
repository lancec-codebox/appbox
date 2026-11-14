using System.Text;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>A subtitle cue on its way through <see cref="CueShaper"/>. <c>CharTimes</c>, when given, has one time per character of <c>Text</c>.</summary>
public sealed record ShapeCue(double Start, double End, string Text, double[]? CharTimes = null);

/// <summary>What every written track records in its sidecar; all but <c>Cues</c> are 0 after shaping, and a non-zero count is a bug.</summary>
public sealed record ShapeStats(int Cues, int Over6s, int OverChars, int Overlaps)
{
    public object ToJson() => new { cues = Cues, over6s = Over6s, overChars = OverChars, overlaps = Overlaps };
}

/// <summary>
/// The one shaping pass for every subtitle track IndexNode writes (protocol "Cue shaping"): at most 6 s and at least 0.7 s per cue, Chinese
/// 18 characters per line and Latin scripts 42, 2 lines, no overlaps; long cues split at sentence punctuation, then clause punctuation, then
/// the character timings (proportional time without them), never inside a glossary name; rolling auto-captions deduped; up to 0.5 s of
/// reading hold that never runs into the next cue.
/// </summary>
public static class CueShaper
{
    public const double MaxSeconds = 6, MinSeconds = 0.7, SoftSeconds = 4, HoldSeconds = 0.5, SilenceSeconds = 1.5, TailSeconds = 0.6;
    public const int ZhLine = 18, LatinLine = 42, MaxLines = 2;

    // ---- script and line wrapping ----

    public static bool IsZh(string s)
    {
        int cjk = 0, lat = 0;
        foreach (var ch in s)
        {
            if (ch is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff') cjk++;
            else if (char.IsLetter(ch)) lat++;
        }
        return cjk > 0 && cjk * 2 >= lat;
    }

    public static int LineLimit(string s) => IsZh(s) ? ZhLine : LatinLine;

    private static bool IsClause(char c) => c is '，' or '、' or '；' or '：' or ',' or ';' or ':';

    /// <summary>The cue text without leading or trailing clause punctuation (sentence marks stay).</summary>
    public static string StripEdges(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && (char.IsWhiteSpace(s[a]) || IsClause(s[a]))) a++;
        while (b > a && (char.IsWhiteSpace(s[b - 1]) || IsClause(s[b - 1]))) b--;
        return s[a..b];
    }

    /// <summary>The text on one line when it fits, else on two balanced lines; more lines only when it cannot fit in two (a count in the sidecar).</summary>
    public static List<string> Wrap(string text)
    {
        var s = text.Trim(); int lim = LineLimit(s);
        if (s.Length <= lim) return [s];
        bool zh = IsZh(s); int best = -1; double bd = double.MaxValue, half = s.Length / 2.0;
        var bounds = zh ? WordSeg.Boundaries(s) : null;
        // a balanced two-line split: the break nearest the middle, at a word boundary (a space for Latin text)
        for (int i = 1; i < s.Length; i++)
        {
            int left = i, right = s.Length - i;
            if (!zh) { if (s[i] != ' ') continue; right = s.Length - i - 1; }
            else if (!bounds![i]) continue;
            if (left > lim || right > lim) continue;
            double d = Math.Abs(i - half) + (zh && char.IsPunctuation(s[i - 1]) ? -3 : 0);
            if (d < bd) { bd = d; best = i; }
        }
        if (best > 0) return [s[..best].Trim(), s[best..].Trim()];
        var res = new List<string>();
        if (zh)
        {
            // no word boundary gives two lines that fit: lines of at most lim characters ending at a word boundary; inside a word only when it is longer than a line
            for (int a = 0; a < s.Length;)
            {
                int e = Math.Min(a + lim, s.Length);
                if (e < s.Length) { int k = e; while (k > a && !bounds![k]) k--; if (k > a) e = k; }
                res.Add(s[a..e].Trim()); a = e;
            }
            return res;
        }
        var cur = new StringBuilder();
        foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur.Length > 0 && cur.Length + 1 + w.Length > lim) { res.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(w);
        }
        if (cur.Length > 0) res.Add(cur.ToString());
        return res;
    }

    private static bool Fits(string text) { var l = Wrap(text); int lim = LineLimit(text); return l.Count <= MaxLines && l.All(x => x.Length <= lim); }

    // ---- timings ----

    /// <summary>One time per character of <paramref name="text"/> from recogniser units (tokens with times): letters and digits take the time of the matching unit character, punctuation the time of the character before it. Null when the units do not match the text.</summary>
    public static double[]? AlignTimes(string text, IReadOnlyList<(double T, string Text)> units)
    {
        var flat = new List<double>();
        foreach (var (t, u) in units) foreach (var ch in u) if (char.IsLetterOrDigit(ch)) flat.Add(t);
        if (text.Count(char.IsLetterOrDigit) != flat.Count || flat.Count == 0) return null;
        var times = new double[text.Length]; int k = 0;
        for (int i = 0; i < text.Length; i++)
        {
            double t = char.IsLetterOrDigit(text[i]) ? flat[k++] : k > 0 ? flat[k - 1] : flat[0];
            times[i] = i > 0 ? Math.Max(t, times[i - 1]) : t;
        }
        return times;
    }

    private static double[] Proportional(string text, double start, double end)
    {
        var t = new double[text.Length];
        for (int i = 0; i < t.Length; i++) t[i] = start + (end - start) * i / Math.Max(1, t.Length);
        return t;
    }

    // ---- splitting ----

    private sealed class Splitter(string text, double[] times, double cueEnd, bool zh, List<(int S, int E)> gloss, List<ShapeCue> sink)
    {
        private readonly bool[] bounds = zh ? WordSeg.Boundaries(text) : [];
        private readonly double per = times.Length > 1 ? Math.Max(0, (times[^1] - times[0]) / (times.Length - 1)) : 0.3;

        private double EndOf(int b)
        {
            int last = b - 1; double next = b < times.Length ? times[b] : cueEnd;
            return Math.Max(times[last] + 0.05, Math.Min(next, times[last] + Math.Max(TailSeconds, per * 1.5)));
        }

        public void Run(int a, int b)
        {
            while (a < b && char.IsWhiteSpace(text[a])) a++;
            while (b > a && char.IsWhiteSpace(text[b - 1])) b--;
            if (a >= b) return;
            double st = times[a], en = EndOf(b);
            bool hard = en - st > MaxSeconds || !Fits(StripEdges(text[a..b]));
            bool soft = !hard && en - st > SoftSeconds;
            if (hard || soft) { int p = Choose(a, b, soft, true); if (p < 0 && hard) p = Choose(a, b, false, false); if (p > 0) { Run(a, p); Run(p, b); return; } }
            var piece = StripEdges(text[a..b]);
            if (piece.Length > 0 && piece.Any(char.IsLetterOrDigit)) sink.Add(new ShapeCue(st, en, string.Join("\n", Wrap(piece))));
        }

        private bool InGloss(int p) { foreach (var (s, e) in gloss) if (p > s && p < e) return true; return false; }

        /// <summary>The best break in (a, b); <paramref name="atWords"/>: Chinese breaks without punctuation only at a word boundary (the caller relaxes this only for a word longer than a line).</summary>
        private int Choose(int a, int b, bool soft, bool atWords)
        {
            int best = -1; double bestScore = double.MaxValue, total = b - a;
            for (int p = a + 1; p < b; p++)
            {
                if (InGloss(p)) continue;
                int level; char c = text[p - 1];
                if (times[p] - times[p - 1] > SilenceSeconds) level = 0;
                else if (c is '。' or '！' or '？' or '…' or '‼' || (c is '.' or '!' or '?' && text[p] == ' ')) level = 1;
                else if (c is '，' or '、' or '；' or '：' || (c is ',' or ';' or ':' && text[p] == ' ')) level = 2;
                else if (zh)
                {
                    if (char.IsPunctuation(text[p])) continue;
                    if (atWords && !bounds[p]) continue;
                    if (c < 128 && char.IsLetterOrDigit(c) && text[p] < 128 && char.IsLetterOrDigit(text[p])) continue;
                    level = 3;
                }
                else if (text[p] == ' ') level = 3;
                else continue;
                if (text[a..p].All(char.IsWhiteSpace) || text[p..b].All(char.IsWhiteSpace)) continue;
                if (soft)
                {
                    if (level > 2 || times[p] - times[a] < 1.0 || EndOf(b) - times[p] < 1.0 || p - a < 4 || b - p < 4) continue;
                }
                double imbalance = Math.Abs((p - a) - (b - p)) / total;
                double score = level == 0 ? -1 + imbalance * 0.01 : imbalance + level * 0.15;
                if (score < bestScore) { bestScore = score; best = p; }
            }
            return best;
        }
    }

    // ---- rolling auto-captions ----

    /// <summary>Rolling auto-captions repeat the previous line (and often the whole previous text) in each cue: keep only the new words.</summary>
    public static List<ShapeCue> Dedupe(IEnumerable<ShapeCue> cues)
    {
        var res = new List<ShapeCue>(); string[] prev = [];
        foreach (var c in cues)
        {
            var lines = c.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            var fresh = new List<string>();
            foreach (var l in lines)
            {
                if (prev.Contains(l)) continue;
                var overlap = prev.Where(p => l.StartsWith(p + " ", StringComparison.Ordinal)).OrderByDescending(p => p.Length).FirstOrDefault();
                var rest = overlap != null ? l[(overlap.Length + 1)..].Trim() : l;
                if (rest.Length > 0) fresh.Add(rest);
            }
            prev = lines;
            if (fresh.Count == 0) continue;
            var joined = string.Join(IsZh(c.Text) ? "" : " ", fresh);
            res.Add(c with { Text = joined, CharTimes = null });
        }
        return res;
    }

    // ---- the pass ----

    public static List<ShapeCue> Shape(IEnumerable<ShapeCue> input, IReadOnlyCollection<string>? glossary = null, bool rolling = false)
    {
        var cues = input.Where(c => !string.IsNullOrWhiteSpace(c.Text)).OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        if (rolling) cues = Dedupe(cues);
        var pieces = new List<ShapeCue>();
        foreach (var c in cues)
        {
            bool zh = IsZh(c.Text);
            var text = c.CharTimes != null ? c.Text : Regex.Replace(c.Text.Trim(), @"\s*\r?\n\s*", zh ? "" : " ");
            var times = c.CharTimes is { } ct && ct.Length == text.Length ? ct : Proportional(text, c.Start, Math.Max(c.End, c.Start + 0.05));
            var gloss = new List<(int, int)>();
            foreach (var g in glossary ?? []) if (g.Length > 1) for (int i = text.IndexOf(g, StringComparison.Ordinal); i >= 0; i = text.IndexOf(g, i + 1, StringComparison.Ordinal)) gloss.Add((i, i + g.Length));
            new Splitter(text, times, c.End, zh, gloss, pieces).Run(0, text.Length);
        }
        pieces = pieces.OrderBy(p => p.Start).ThenBy(p => p.End).ToList();
        var res = new List<ShapeCue>();
        for (int i = 0; i < pieces.Count; i++)
        {
            double s = Math.Round(pieces[i].Start, 3), ns = i + 1 < pieces.Count ? Math.Round(pieces[i + 1].Start, 3) : double.MaxValue;
            double e = Math.Min(pieces[i].End, ns);
            if (e - s < MinSeconds) e = Math.Min(s + MinSeconds, ns);
            e = Math.Min(Math.Min(e + HoldSeconds, ns), s + MaxSeconds);
            e = Math.Round(e, 3);
            if (e <= s + 0.001) continue;
            res.Add(new ShapeCue(s, e, pieces[i].Text));
        }
        return res;
    }

    public static ShapeStats Stats(IReadOnlyList<ShapeCue> cues)
    {
        int over6 = 0, overChars = 0, overlaps = 0;
        var sorted = cues.OrderBy(c => c.Start).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var c = sorted[i];
            if (c.End - c.Start > MaxSeconds + 0.0005) over6++;
            var lines = c.Text.Split('\n'); int lim = LineLimit(c.Text);
            if (lines.Length > MaxLines || lines.Any(l => l.Length > lim)) overChars++;
            if (i + 1 < sorted.Count && c.End > sorted[i + 1].Start + 0.0005) overlaps++;
        }
        return new ShapeStats(sorted.Count, over6, overChars, overlaps);
    }

    // ---- VTT ----

    public static string ToVtt(IEnumerable<ShapeCue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues) sb.Append(OcrCues.Timestamp(c.Start)).Append(" --> ").Append(OcrCues.Timestamp(c.End)).Append('\n').Append(c.Text).Append("\n\n");
        return sb.ToString();
    }

    private static readonly Regex Time = new(@"(?:(\d+):)?(\d+):(\d+)[.,](\d+)", RegexOptions.Compiled);

    private static double? ParseTime(string s)
    {
        var m = Time.Match(s); if (!m.Success) return null;
        double h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
        return h * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) + double.Parse("0." + m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Cues of a WebVTT file with inline tags (&lt;c&gt;, word timestamps) removed.</summary>
    public static List<ShapeCue> ParseVtt(string vtt)
    {
        var res = new List<ShapeCue>();
        foreach (var block in Regex.Split(vtt.Replace("\r\n", "\n"), @"\n\s*\n"))
        {
            var lines = block.Split('\n'); int k = Array.FindIndex(lines, l => l.Contains("-->"));
            if (k < 0) continue;
            var parts = lines[k].Split("-->");
            if (parts.Length < 2 || ParseTime(parts[0]) is not { } s || ParseTime(parts[1].Trim().Split(' ')[0]) is not { } e) continue;
            var text = string.Join("\n", lines.Skip(k + 1).Select(l => Regex.Replace(l, "<[^>]*>", "")).Select(l => l.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&nbsp;", " ").Trim()).Where(l => l.Length > 0));
            if (text.Length > 0) res.Add(new ShapeCue(s, e, text));
        }
        return res;
    }
}
