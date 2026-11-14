using System.Text.Json;

namespace IndexNode;

public sealed record SyncWindow(double Start, int N, double Median, double Iqr);
public sealed record SyncResult(double Offset, double Drift, bool Suspect, int Matches, IReadOnlyList<SyncWindow> Windows);

/// <summary>
/// Audio/video sync: burned-in subtitles are timed to the original audio, so the gap between an OCR cue's start and the transcript time
/// of its first matching character measures the audio shift (and its drift through the video).
/// </summary>
public static class AvSync
{
    public const int MinCueChars = 4, MinRun = 3, WindowSeconds = 300, MinWindowN = 20;
    public const double SearchSeconds = 4, SuspectOffset = 0.25, SuspectDrift = 0.3;

    /// <summary>Letters and digits only (punctuation and spaces differ between OCR and ASR).</summary>
    public static string Clean(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The transcript as one character stream with a time per character (multi-character tokens share their time).</summary>
    public static List<(char C, double T)> CharStream(IEnumerable<AsrSegment> segs)
    {
        var list = new List<(char, double)>();
        foreach (var s in segs)
        {
            if (s.Chars.Count > 0) { foreach (var c in s.Chars) foreach (var ch in Clean(c.Text)) list.Add((ch, c.T)); }
            else
            {
                var t = Clean(s.Text); // no per-character times: spread the characters over the segment
                for (int i = 0; i < t.Length; i++) list.Add((t[i], s.Start + (s.End - s.Start) * i / Math.Max(1, t.Length)));
            }
        }
        return list.OrderBy(x => x.Item2).ToList();
    }

    /// <summary>
    /// For one cue: the longest common run between the cue text and the transcript characters within ±4 s of the cue start. Returns the
    /// transcript time of the run's first character minus the cue start, only when the run begins at the cue's first character.
    /// </summary>
    public static double? CueOffset(string cueText, double cueStart, IReadOnlyList<(char C, double T)> stream, int lo, int hi)
    {
        var cue = Clean(cueText);
        if (cue.Length < MinCueChars) return null;
        int bestLen = 0, bestCueIdx = 0, bestStreamIdx = 0;
        for (int j = lo; j < hi; j++)
            for (int i = 0; i < cue.Length; i++)
            {
                int k = 0;
                while (i + k < cue.Length && j + k < hi && cue[i + k] == stream[j + k].C) k++;
                if (k > bestLen) { bestLen = k; bestCueIdx = i; bestStreamIdx = j; }
            }
        if (bestLen < MinRun || bestCueIdx != 0) return null;
        return stream[bestStreamIdx].T - cueStart;
    }

    public static double Median(IList<double> v)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    public static double Quantile(IList<double> v, double q)
    {
        var s = v.OrderBy(x => x).ToArray();
        if (s.Length == 0) return 0;
        double pos = (s.Length - 1) * q; int a = (int)Math.Floor(pos), b = (int)Math.Ceiling(pos);
        return s[a] + (s[b] - s[a]) * (pos - a);
    }

    public static SyncResult Measure(IReadOnlyList<(double Start, string Text)> cues, IEnumerable<AsrSegment> segments)
    {
        var stream = CharStream(segments);
        var times = stream.Select(x => x.T).ToArray();
        var offsets = new List<(double Start, double Off)>();
        foreach (var (start, text) in cues)
        {
            int lo = Array.BinarySearch(times, start - SearchSeconds); if (lo < 0) lo = ~lo;
            int hi = Array.BinarySearch(times, start + SearchSeconds); if (hi < 0) hi = ~hi;
            if (hi <= lo) continue;
            if (CueOffset(text, start, stream, lo, hi) is { } o) offsets.Add((start, o));
        }
        var windows = offsets.GroupBy(x => (int)(x.Start / WindowSeconds)).OrderBy(g => g.Key)
            .Select(g => { var v = g.Select(x => x.Off).ToList(); return new SyncWindow(g.Key * WindowSeconds, v.Count, Math.Round(Median(v), 3), Math.Round(Quantile(v, .75) - Quantile(v, .25), 3)); }).ToList();
        double offset = Math.Round(Median(offsets.Select(x => x.Off).ToList()), 3);
        var big = windows.Where(w => w.N >= MinWindowN).Select(w => w.Median).ToList();
        double drift = big.Count == 0 ? 0 : Math.Round(big.Max() - big.Min(), 3);
        return new SyncResult(offset, drift, Math.Abs(offset) > SuspectOffset || drift > SuspectDrift, offsets.Count, windows);
    }

    public static byte[] ToJson(SyncResult r) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1, offset = r.Offset, drift = r.Drift, suspect = r.Suspect, matches = r.Matches, windowSeconds = WindowSeconds,
        method = "OCR zh cues (4+ chars): longest common run with the transcript characters within +-4 s of the cue start, run beginning at the cue's first character; offset = transcript time - cue start",
        windows = r.Windows.Select(w => new { start = w.Start, n = w.N, median = w.Median, iqr = w.Iqr }),
    });

    /// <summary>Reads the stored outputs: ocr/lines.json (zh cues, simplified text) and asr/sensevoice.zh.json (segments with per-character times).</summary>
    public static SyncResult? FromFiles(byte[] linesJson, byte[] asrJson, double from = 0, double to = double.MaxValue)
    {
        using var lj = JsonDocument.Parse(linesJson); using var aj = JsonDocument.Parse(asrJson);
        var cues = lj.RootElement.GetProperty("zh").EnumerateArray().Select(c => (c.GetProperty("start").GetDouble(), (c.TryGetProperty("textHans", out var h) ? h.GetString() : c.TryGetProperty("t2s", out var t2) ? t2.GetString() : null) is { Length: > 0 } hs ? hs : c.GetProperty("text").GetString() ?? "")).ToList();
        var segs = aj.RootElement.GetProperty("segments").EnumerateArray().Select(s => new AsrSegment(s.GetProperty("start").GetDouble(), s.GetProperty("end").GetDouble(), s.GetProperty("text").GetString() ?? "",
            s.TryGetProperty("language", out var l) ? l.GetString() ?? "und" : "und",
            s.TryGetProperty("words", out var w) ? w.EnumerateArray().Select(x => new AsrChar(x[0].GetDouble(), x[x.GetArrayLength() - 1].GetString() ?? "")).ToList() : [])).ToList();
        cues = cues.Where(c => c.Item1 >= from && c.Item1 < to).ToList();
        if (cues.Count == 0 || segs.Count == 0) return null;
        return Measure(cues, segs);
    }
}
