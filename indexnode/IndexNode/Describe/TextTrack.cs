namespace IndexNode;

/// <summary>The text track of a drama under the protocol's text priority: burned-in OCR first, our transcript only where no OCR cue exists.</summary>
public static class TextTrack
{
    /// <summary>OCR cues win; a transcript cue is kept (clipped) only for the time not covered by OCR cues. Result is sorted, non-overlapping and shaped.</summary>
    public static List<ShapeCue> Merge(IReadOnlyList<ShapeCue> ocr, IReadOnlyList<ShapeCue> transcript)
    {
        if (ocr.Count == 0) return CueShaper.Shape(transcript);
        var o = ocr.OrderBy(c => c.Start).ToList(); var all = new List<ShapeCue>(o);
        foreach (var t in transcript.OrderBy(c => c.Start))
        {
            double s = t.Start, e = t.End;
            // skip a transcript cue whose speech is mostly under OCR cues: the burned-in line is the text
            double covered = o.Sum(c => Math.Max(0, Math.Min(e, c.End + 0.3) - Math.Max(s, c.Start - 0.3)));
            if (covered >= 0.4 * Math.Max(0.1, e - s)) continue;
            // trim to the free gap
            foreach (var c in o) { if (c.Start < e && c.End > s) { if (c.Start <= s) s = Math.Max(s, c.End); else e = Math.Min(e, c.Start); } }
            if (e - s >= 0.5) all.Add(new ShapeCue(s, e, t.Text));
        }
        return CueShaper.Shape(all.OrderBy(c => c.Start).ToList());
    }

    /// <summary>Compact "[m:ss] text" lines for a prompt, at most <paramref name="maxChars"/> long: when over, every n-th line is dropped evenly.</summary>
    public static string Compress(IReadOnlyList<ShapeCue> cues, int maxChars = 60000)
    {
        string Line(ShapeCue c) { int t = (int)c.Start; return $"[{t / 60}:{t % 60:D2}] {c.Text.Replace('\n', ' ')}"; }
        var lines = cues.Select(Line).ToList(); long total = lines.Sum(l => l.Length + 1);
        if (total <= maxChars) return string.Join('\n', lines);
        double keep = maxChars / (double)total; var res = new List<string>(); double acc = 0;
        foreach (var l in lines) { acc += keep; if (acc >= 1) { acc -= 1; res.Add(l); } }
        return string.Join('\n', res);
    }
}
