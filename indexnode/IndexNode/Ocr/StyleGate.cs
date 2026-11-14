namespace IndexNode;

/// <summary>
/// Reads each subtitle line once. A drama's burned-in subtitles keep one style (fill colour, usually white, with a dark outline or shadow), so after
/// learning it from the first lines the normal OCR reads, a sample's text pixels are found with plain pixel arithmetic (no model):
///   mask = pixels near a learned fill colour that have a dark (outline) pixel within 2 px.
/// Then: mask nearly empty -> no subtitle; mask the same as at the last read, inside each read line's box and nothing new outside them -> reuse that
/// reading; otherwise read again. A periodic full read is the safety net for styles the mask misses.
/// Measured on the 10-min test window (Python prototype): 1188/1196 samples read the same text as reading every sample; ~half the samples need no read.
/// </summary>
public sealed class StyleGate(int w, int h)
{
    public const int MinPx = 40, Pad = 3, LearnLines = 12;
    public const double SameIou = 0.85;
    /// <summary>A reused reading must not have grown: more mask pixels than this times the anchor's means the line was still fading in when it was read ("换" read, "换一个" shown).</summary>
    public const double MaxGrowth = 1.15;
    readonly object gate = new();
    readonly List<float[]> fills = [];
    readonly List<float> darks = [];
    float[][] centers = [];
    float threshold;
    volatile bool learned;
    public bool Learned => learned;

    static float Lum(byte b, byte g, byte r) => 0.114f * b + 0.587f * g + 0.299f * r;

    /// <summary>Collects fill/outline colours from the lines a normal read found (bgr band, line boxes in band coordinates). Shared by all segments: thread-safe, learned once.</summary>
    public void Learn(byte[] bgr, IReadOnlyList<OcrLine> lines)
    {
        lock (gate) LearnLocked(bgr, lines);
    }

    void LearnLocked(byte[] bgr, IReadOnlyList<OcrLine> lines)
    {
        if (learned) return;
        foreach (var l in lines)
        {
            int x0 = Math.Max(0, l.X0), y0 = Math.Max(0, l.Y0), x1 = Math.Min(w, l.X1), y1 = Math.Min(h, l.Y1);
            int n = (x1 - x0) * (y1 - y0); if (n < 50) continue;
            var lum = new float[n]; var idx = new int[n]; int k = 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int p = (y * w + x) * 3; lum[k] = Lum(bgr[p], bgr[p + 1], bgr[p + 2]); idx[k++] = p; }
            Array.Sort(lum, idx);
            fills.Add(MedianColour(bgr, idx, (int)(n * 0.92), n));
            darks.Add(lum[(int)(n * 0.04)]);
        }
        if (fills.Count < LearnLines) return;
        centers = TwoMeans(fills);
        float fillLum = centers.Min(c => Lum((byte)c[0], (byte)c[1], (byte)c[2]));
        var ds = darks.OrderBy(x => x).ToList(); float darkLum = ds[ds.Count / 2];
        threshold = (fillLum + darkLum) / 2;
        learned = true;
    }

    static float[] MedianColour(byte[] bgr, int[] idx, int from, int to)
    {
        var res = new float[3];
        for (int c = 0; c < 3; c++) { var v = new List<byte>(to - from); for (int i = from; i < to; i++) v.Add(bgr[idx[i] + c]); v.Sort(); res[c] = v[v.Count / 2]; }
        return res;
    }

    /// <summary>One or two fill colours (e.g. white Chinese + yellow English): 2-means, merged when the two centres are within 40.</summary>
    static float[][] TwoMeans(List<float[]> pts)
    {
        float D(float[] a, float[] b) => MathF.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
        var c0 = (float[])pts.OrderByDescending(p => p.Sum()).First().Clone(); var c1 = (float[])pts.OrderByDescending(p => D(p, c0)).First().Clone();
        for (int it = 0; it < 10; it++)
        {
            var a = pts.Where(p => D(p, c0) <= D(p, c1)).ToList(); var b = pts.Where(p => D(p, c0) > D(p, c1)).ToList();
            if (a.Count > 0) c0 = [(float)a.Average(p => p[0]), (float)a.Average(p => p[1]), (float)a.Average(p => p[2])];
            if (b.Count > 0) c1 = [(float)b.Average(p => p[0]), (float)b.Average(p => p[1]), (float)b.Average(p => p[2])];
        }
        return D(c0, c1) > 40 ? [c0, c1] : [[(c0[0] + c1[0]) / 2, (c0[1] + c1[1]) / 2, (c0[2] + c1[2]) / 2]];
    }

    /// <summary>The text mask (1 = text pixel) and its pixel count: near a fill colour (every channel within 45) with a dark pixel within 2 px.</summary>
    public byte[] Mask(byte[] bgr, out int count)
    {
        int n = w * h; var dark = new byte[n]; var tmp = new byte[n]; var m = new byte[n];
        for (int i = 0, p = 0; i < n; i++, p += 3) dark[i] = Lum(bgr[p], bgr[p + 1], bgr[p + 2]) < threshold ? (byte)1 : (byte)0;
        for (int y = 0; y < h; y++)   // dilate 5x5: horizontal pass
        {
            int row = y * w;
            for (int x = 0; x < w; x++) { byte v = 0; for (int d = Math.Max(0, x - 2); d <= Math.Min(w - 1, x + 2) && v == 0; d++) v = dark[row + d]; tmp[row + x] = v; }
        }
        count = 0;
        for (int y = 0; y < h; y++)   // vertical pass, then AND with "near a fill colour"
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; byte v = 0;
                for (int d = Math.Max(0, y - 2); d <= Math.Min(h - 1, y + 2) && v == 0; d++) v = tmp[d * w + x];
                if (v == 0) continue;
                int p = i * 3; byte b = bgr[p], g = bgr[p + 1], r = bgr[p + 2];
                foreach (var c in centers)
                    if (Math.Abs(b - c[0]) < 45 && Math.Abs(g - c[1]) < 45 && Math.Abs(r - c[2]) < 45) { m[i] = 1; count++; break; }
            }
        return m;
    }

    /// <summary>The box around the mask's text pixels, padded by <paramref name="pad"/> and clipped to the band; null when the mask is empty.</summary>
    public (int X0, int Y0, int X1, int Y1)? Bounds(byte[] m, int pad)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++) { int row = y * w; for (int x = 0; x < w; x++) if (m[row + x] != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; y1 = y; } }
        if (x1 < 0) return null;
        return (Math.Max(0, x0 - pad), Math.Max(0, y0 - pad), Math.Min(w, x1 + 1 + pad), Math.Min(h, y1 + 1 + pad));
    }

    /// <summary>Same text as at the last read: inside every box read then (padded), the masks overlap by >= 85% (IoU), and no new text pixels outside them.</summary>
    public bool SameText(byte[] m, int count, byte[] anchor, int anchorCount, IReadOnlyList<OcrLine> anchorLines)
    {
        if (anchorLines.Count == 0 || count > anchorCount * MaxGrowth) return false;
        var inBox = new bool[w * h];
        foreach (var l in anchorLines)
        {
            int x0 = Math.Max(0, l.X0 - Pad), y0 = Math.Max(0, l.Y0 - Pad), x1 = Math.Min(w, l.X1 + Pad), y1 = Math.Min(h, l.Y1 + Pad);
            int inter = 0, uni = 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = y * w + x; inBox[i] = true; int a = m[i], b = anchor[i]; inter += a & b; uni += a | b; }
            if (uni > 0 && (double)inter / uni < SameIou) return false;
        }
        int outside = 0;
        for (int i = 0; i < m.Length; i++) if (m[i] != 0 && !inBox[i] && ++outside > MinPx) return false;
        return true;
    }
}
