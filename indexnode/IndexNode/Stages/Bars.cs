using System.Globalization;

namespace IndexNode;

/// <summary>Rows/columns removed from each side of a stored frame, in stored-frame pixels.</summary>
public readonly record struct BarCrop(int Top, int Bottom, int Left, int Right)
{
    public static readonly BarCrop None = new(0, 0, 0, 0);
    public bool IsNone => Top == 0 && Bottom == 0 && Left == 0 && Right == 0;
    public int Width(int w) => w - Left - Right;
    public int Height(int h) => h - Top - Bottom;
    /// <summary>ffmpeg crop filter for a frame of w x h.</summary>
    public string Filter(int w, int h) => $"crop={Width(w)}:{Height(h)}:{Left}:{Top}";
}

/// <summary>
/// Fixed black bars baked into a video (letterbox / pillarbox). The rule (owner 2026-11-16, tested on real uploads): on 24 frames sampled evenly between 5% and 95% of the
/// duration (at the stored frame size), an edge row (column) is "bar" when at least 70% of its pixels have luminance (0-255) below 20, in at least 80% of the sampled frames; a side's thickness is the run of
/// bar rows from that edge inward. A side pair (top+bottom, left+right) is cropped only when it covers at least 2% of that dimension, and never when the remaining
/// picture would be under 20% of the dimension. The cropped size is kept even. Pure arithmetic on plain 8-bit luminance arrays, so screenshot search can use it as is.
/// </summary>
public static class Bars
{
    /// <summary>Version of this rule, recorded in frames/manifest.json (cropRule). 3: a row is bar when at least 70% of its pixels are dark, in at least 80% of the samples (2: mean below 20 in 80% of samples; 1: every sample).</summary>
    public const int RuleVersion = 3;
    public const double MinDarkFraction = 0.8, MinDarkPixels = 0.7;
    public const int Samples = 24;
    public const double From = 0.05, To = 0.95;
    public const double MaxMeanLuma = 20;
    public const double MinPairFraction = 0.02;
    public const double MinKeepFraction = 0.2;

    /// <summary>The sample times: <see cref="Samples"/> instants evenly from 5% to 95% of the duration.</summary>
    public static double[] SampleTimes(double duration) =>
        Enumerable.Range(0, Samples).Select(i => duration * (From + (To - From) * i / (Samples - 1))).ToArray();

    /// <summary>Crop for frames given as w*h luminance bytes (row-major, 0-255). Fewer than 2 frames: no crop (a single frame cannot show that a bar is fixed).</summary>
    public static BarCrop Detect(IReadOnlyList<byte[]> frames, int w, int h)
    {
        if (frames.Count < 2 || w <= 0 || h <= 0) return BarCrop.None;
        // a row/column is bar in a frame when at least 70% of its pixels are below the limit (subtitle strokes cover a minority of a row); it is bar overall when that holds in at least 80% of the frames
        var rowDark = new int[h]; var colDark = new int[w];
        foreach (var f in frames)
        {
            if (f.Length < w * h) return BarCrop.None;
            var rows = new int[h]; var cols = new int[w];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (f[y * w + x] < MaxMeanLuma) { rows[y]++; cols[x]++; }
            for (int y = 0; y < h; y++) if (rows[y] >= MinDarkPixels * w - 1e-9) rowDark[y]++;
            for (int x = 0; x < w; x++) if (cols[x] >= MinDarkPixels * h - 1e-9) colDark[x]++;
        }
        double need = MinDarkFraction * frames.Count - 1e-9;
        var rowMax = rowDark.Select(c => c >= need ? 0.0 : 255.0).ToArray(); var colMax = colDark.Select(c => c >= need ? 0.0 : 255.0).ToArray();
        var (top, bottom) = Pair(rowMax, h); var (left, right) = Pair(colMax, w);
        return new BarCrop(top, bottom, left, right);
    }

    static (int Lo, int Hi) Pair(double[] maxMean, int n)
    {
        int lo = 0; while (lo < n && maxMean[lo] < MaxMeanLuma) lo++;
        if (lo == n) return (0, 0); // all dark
        int hi = 0; while (hi < n && maxMean[n - 1 - hi] < MaxMeanLuma) hi++;
        if (lo + hi < MinPairFraction * n) return (0, 0);
        if (n - lo - hi < MinKeepFraction * n) return (0, 0);
        if ((n - lo - hi) % 2 != 0) { if (hi > 0) hi--; else lo--; } // keep the cropped size even: give one bar row back
        return (lo, hi);
    }

    static async Task<byte[]?> OneFrameAsync(string ffmpeg, string[] args, int size, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        using var reg = ct.Register(() => Proc.Kill(p));
        _ = p.StandardError.ReadToEndAsync();
        var buf = new byte[size]; int off = 0;
        while (off < size) { int k = await p.StandardOutput.BaseStream.ReadAsync(buf.AsMemory(off), ct); if (k == 0) break; off += k; }
        await p.WaitForExitAsync(CancellationToken.None);
        return off == size ? buf : null;
    }

    /// <summary>Reads 24 sampled frames (scaled to w x h, 8-bit gray) from the video and returns the crop. A frame that cannot be read is left out; under 12 readable: no crop.</summary>
    public static async Task<BarCrop> DetectAsync(string ffmpeg, string video, double duration, int w, int h, CancellationToken ct)
    {
        if (duration <= 0) return BarCrop.None;
        var times = SampleTimes(duration); var frames = new byte[times.Length][];
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(times.Select(async (t, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await OneFrameAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-ss", t.ToString("F3", CultureInfo.InvariantCulture), "-i", video, "-an", "-sn", "-frames:v", "1",
                    "-vf", $"scale={w}:{h}:flags=bicubic,format=gray", "-f", "rawvideo", "-"], w * h, ct);
                if (r != null) frames[i] = r;
            }
            finally { gate.Release(); }
        }));
        var ok = frames.Where(f => f != null).ToList();
        return ok.Count < Samples / 2 ? BarCrop.None : Detect(ok, w, h);
    }
}
