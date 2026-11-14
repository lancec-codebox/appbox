namespace IndexNode;

/// <summary>Frame pruning settings (all settable: frames.crf, prune.grid, prune.sscd, prune.lookback).</summary>
public sealed record FrameSettings(int Crf = 22, double GridThreshold = 0.08, double SscdCosine = 0.9, double LookbackSeconds = 30)
{
    public static FrameSettings From(NodeConfig c) => new(c.FrameCrf ?? 22, c.PruneGrid ?? 0.08, c.PruneSscd ?? 0.9, c.PruneLookback ?? 30);
}

/// <summary>
/// The colour-grid fingerprint of a frame: the frame at 36x64 (64x36 landscape) in Lab, an 8x4 grid of mean Lab (96 values), and per quadrant a 128-bin
/// hue(8) x chroma(4) x lightness(4) histogram (4 x 128 values). Distance is the mean absolute grid difference (in units of 20 Lab) averaged with the
/// mean quadrant histogram L1 distance (0..1); 0 = identical.
/// </summary>
public static class ColorGrid
{
    public const int GridValues = 96, HistBins = 128, Length = GridValues + 4 * HistBins;

    static float Lin(byte v) { float c = v / 255f; return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f); }
    static readonly float[] LinTable = Enumerable.Range(0, 256).Select(i => Lin((byte)i)).ToArray();
    static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;

    public static (float L, float A, float B) Lab(byte r, byte g, byte b)
    {
        float lr = LinTable[r], lg = LinTable[g], lb = LinTable[b];
        float x = (0.4124564f * lr + 0.3575761f * lg + 0.1804375f * lb) / 0.95047f, y = 0.2126729f * lr + 0.7151522f * lg + 0.0721750f * lb, z = (0.0193339f * lr + 0.1191920f * lg + 0.9503041f * lb) / 1.08883f;
        float fx = F(x), fy = F(y), fz = F(z);
        return (116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    public static float[] Compute(Rgb im)
    {
        bool land = im.W >= im.H; int tw = land ? 64 : 36, th = land ? 36 : 64;
        var lab = new (float L, float A, float B)[tw * th];
        for (int ty = 0; ty < th; ty++)
            for (int tx = 0; tx < tw; tx++)
            {
                int x0 = tx * im.W / tw, x1 = Math.Max(x0 + 1, (tx + 1) * im.W / tw), y0 = ty * im.H / th, y1 = Math.Max(y0 + 1, (ty + 1) * im.H / th);
                long r = 0, g = 0, b = 0; int n = 0;
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int o = (y * im.W + x) * 3; r += im.Data[o]; g += im.Data[o + 1]; b += im.Data[o + 2]; n++; }
                lab[ty * tw + tx] = Lab((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
        var f = new float[Length];
        int gc = land ? 8 : 4, gr = land ? 4 : 8;
        for (int cy = 0; cy < gr; cy++)
            for (int cx = 0; cx < gc; cx++)
            {
                double sl = 0, sa = 0, sb = 0; int n = 0;
                for (int y = cy * th / gr; y < (cy + 1) * th / gr; y++) for (int x = cx * tw / gc; x < (cx + 1) * tw / gc; x++) { var p = lab[y * tw + x]; sl += p.L; sa += p.A; sb += p.B; n++; }
                int k = (cy * gc + cx) * 3; f[k] = (float)(sl / n); f[k + 1] = (float)(sa / n); f[k + 2] = (float)(sb / n);
            }
        var cnt = new float[4];
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                var p = lab[y * tw + x]; int q = (y * 2 / th) * 2 + (x * 2 / tw);
                float hue = MathF.Atan2(p.B, p.A); int hb = Math.Clamp((int)((hue + MathF.PI) / (2 * MathF.PI) * 8), 0, 7);
                float ch = MathF.Sqrt(p.A * p.A + p.B * p.B); int cb = ch < 8 ? 0 : ch < 20 ? 1 : ch < 40 ? 2 : 3;
                int lb = p.L < 25 ? 0 : p.L < 50 ? 1 : p.L < 75 ? 2 : 3;
                f[GridValues + q * HistBins + (hb * 4 + cb) * 4 + lb] += 1; cnt[q]++;
            }
        for (int q = 0; q < 4; q++) if (cnt[q] > 0) for (int i = 0; i < HistBins; i++) f[GridValues + q * HistBins + i] /= cnt[q];
        return f;
    }

    public static double Distance(float[] a, float[] b)
    {
        double g = 0; for (int i = 0; i < GridValues; i++) g += Math.Abs(a[i] - b[i]);
        g = g / GridValues / 20.0;
        double h = 0; for (int i = GridValues; i < Length; i++) h += Math.Abs(a[i] - b[i]);
        h = h / 4 / 2.0;
        return (g + h) / 2;
    }
}

/// <summary>Colour-grid pruning of a time-ordered frame sequence: a frame within <c>threshold</c> of a kept frame from the last <c>lookback</c> seconds is pruned, and the kept frame's spans grow to cover it.</summary>
public sealed class FramePruner(FrameSettings st)
{
    readonly List<(int Index, double T, float[] Feat)> recent = [];
    public const double FrameSeconds = 1;

    /// <summary>The kept frame this one stands for, or -1 when it must be kept (the caller then numbers it).</summary>
    public int Offer(double t, float[] feat)
    {
        recent.RemoveAll(r => t - r.T > st.LookbackSeconds);
        int best = -1; double bd = st.GridThreshold;
        foreach (var r in recent) { var d = ColorGrid.Distance(r.Feat, feat); if (d < bd) { bd = d; best = r.Index; } }
        return best;
    }

    public void Keep(int index, double t, float[] feat) => recent.Add((index, t, feat));

    /// <summary>Adds [start, end) to a span list, merging with the last span when they touch.</summary>
    public static void AddSpan(List<double[]> spans, double start, double end)
    {
        if (spans.Count > 0 && start <= spans[^1][1] + 1e-6) spans[^1][1] = Math.Max(spans[^1][1], end);
        else spans.Add([start, end]);
    }
}
