namespace IndexNode;

public readonly record struct Rect(int X0, int Y0, int X1, int Y1);

/// <summary>
/// Finds the subtitle band without any AI: from text boxes found on ~200 sampled frames, the rows where text lines keep appearing.
/// Boxes that sit at the same place in many frames (a watermark, a logo, a channel bug) are dropped first.
/// </summary>
public static class BandFinder
{
    public const int Samples = 200;
    public const double WatermarkShare = 0.25;   // a box seen at the same place in this share of the samples is static
    public const int WatermarkTolerance = 4;     // px
    public const double MinCoverage = 0.08;      // a band needs text rows in at least this share of the samples
    public const double EdgeShare = 0.2;         // rows with at least this share of the peak coverage belong to the band

    /// <summary>Boxes at (nearly) the same place in at least <see cref="WatermarkShare"/> of the sampled frames.</summary>
    public static List<Rect> StaticBoxes(IReadOnlyList<IReadOnlyList<Rect>> samples)
    {
        var clusters = new List<(Rect Rep, HashSet<int> Seen)>();
        for (int s = 0; s < samples.Count; s++)
            foreach (var b in samples[s])
            {
                int k = clusters.FindIndex(c => Near(c.Rep, b));
                if (k < 0) clusters.Add((b, [s])); else clusters[k].Seen.Add(s);
            }
        int need = Math.Max(3, (int)Math.Ceiling(samples.Count * WatermarkShare));
        return clusters.Where(c => c.Seen.Count >= need).Select(c => c.Rep).ToList();
    }

    private static bool Near(Rect a, Rect b) =>
        Math.Abs(a.X0 - b.X0) <= WatermarkTolerance && Math.Abs(a.X1 - b.X1) <= WatermarkTolerance
        && Math.Abs(a.Y0 - b.Y0) <= WatermarkTolerance && Math.Abs(a.Y1 - b.Y1) <= WatermarkTolerance;

    /// <summary>The band (y start, height) in a frame of the given height, or null when no row has recurring text.</summary>
    public static (int Y, int Height)? Find(IReadOnlyList<IReadOnlyList<Rect>> samples, int frameHeight)
    {
        if (samples.Count == 0) return null;
        var fixedBoxes = StaticBoxes(samples);
        var cover = new int[frameHeight];
        foreach (var boxes in samples)
        {
            var rows = new bool[frameHeight];
            foreach (var b in boxes)
            {
                if (fixedBoxes.Any(f => Near(f, b))) continue;
                for (int y = Math.Max(0, b.Y0); y < Math.Min(frameHeight, b.Y1); y++) rows[y] = true;
            }
            for (int y = 0; y < frameHeight; y++) if (rows[y]) cover[y]++;
        }
        int peak = cover.Max();
        if (Environment.GetEnvironmentVariable("INDEXNODE_DEBUG_BAND") != null)
            Console.Error.WriteLine("band cover (every 10 rows): " + string.Join(' ', Enumerable.Range(0, frameHeight / 10).Select(k => $"{k * 10}:{cover[k * 10]}")));
        if (peak < Math.Max(3, (int)Math.Ceiling(samples.Count * MinCoverage))) return null;

        // the strongest run of rows with at least EdgeShare of the peak, allowing gaps up to a line spacing
        int floor = Math.Max(1, (int)Math.Ceiling(peak * EdgeShare));
        int maxGap = Math.Max(12, frameHeight / 20); // two subtitle lines (Chinese above English) are one band
        (long Weight, int From, int To) best = (0, 0, 0);
        int i = 0;
        while (i < frameHeight)
        {
            if (cover[i] < floor) { i++; continue; }
            int from = i, to = i, gap = 0; long weight = 0;
            for (int y = i; y < frameHeight; y++)
            {
                if (cover[y] >= floor) { to = y; gap = 0; weight += cover[y]; }
                else if (++gap > maxGap) break;
            }
            if (weight > best.Weight) best = (weight, from, to);
            i = to + 1;
        }
        // a little room above and below, and an even height
        int margin = Math.Max(4, (best.To - best.From + 1) / 8);
        int y0 = Math.Max(0, best.From - margin), y1 = Math.Min(frameHeight, best.To + 1 + margin);
        y0 &= ~1; y1 = Math.Min(frameHeight, (y1 + 1) & ~1);
        return (y0, y1 - y0);
    }
}
