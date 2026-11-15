using System.Diagnostics;

namespace IndexNode;

/// <summary>A packed rgb24 image.</summary>
public sealed record Rgb(byte[] Data, int W, int H)
{
    public static Rgb Blank(int w, int h, byte v = 0) { var d = new byte[w * h * 3]; if (v != 0) Array.Fill(d, v); return new Rgb(d, w, h); }
}

/// <summary>Small image helpers for the cast step: bilinear resize and warp, crops, a Laplacian blur measure, a grid, and JPEG encoding through ffmpeg.</summary>
public static class Imaging
{
    public static void Sample(Rgb im, double x, double y, Span<byte> o)
    {
        x = Math.Clamp(x, 0, im.W - 1.001); y = Math.Clamp(y, 0, im.H - 1.001);
        int x0 = (int)x, y0 = (int)y; double fx = x - x0, fy = y - y0;
        for (int c = 0; c < 3; c++)
        {
            double a = im.Data[(y0 * im.W + x0) * 3 + c], b = im.Data[(y0 * im.W + x0 + 1) * 3 + c], d = im.Data[((y0 + 1) * im.W + x0) * 3 + c], e = im.Data[((y0 + 1) * im.W + x0 + 1) * 3 + c];
            o[c] = (byte)Math.Clamp(Math.Round((a * (1 - fx) + b * fx) * (1 - fy) + (d * (1 - fx) + e * fx) * fy), 0, 255);
        }
    }

    /// <summary>The region (x, y, w, h; may lie partly outside the image: that part is grey) resized to ow x oh.</summary>
    public static Rgb CropResize(Rgb im, double x, double y, double w, double h, int ow, int oh)
    {
        var o = Rgb.Blank(ow, oh, 114); Span<byte> px = stackalloc byte[3];
        for (int j = 0; j < oh; j++)
            for (int i = 0; i < ow; i++)
            {
                double sx = x + (i + 0.5) * w / ow - 0.5, sy = y + (j + 0.5) * h / oh - 0.5;
                if (sx < -0.5 || sy < -0.5 || sx > im.W - 0.5 || sy > im.H - 0.5) continue;
                Sample(im, sx, sy, px); px.CopyTo(o.Data.AsSpan((j * ow + i) * 3, 3));
            }
        return o;
    }


    // ---- in-process JPEG (BitMiracle.LibJpeg.NET, BSD-3, managed) and a Lanczos resampler: no ffmpeg ----

    /// <summary>Decodes a JPEG (grayscale, YCbCr, CMYK all handled) to packed rgb24.</summary>
    public static Rgb DecodeJpeg(byte[] jpg)
    {
        using var img = new BitMiracle.LibJpeg.JpegImage(new MemoryStream(jpg));
        int w = img.Width, h = img.Height; var d = new byte[w * h * 3];
        var cs = img.Colorspace; int nc = img.ComponentsPerSample;
        for (int y = 0; y < h; y++)
        {
            var row = img.GetRow(y).ToBytes();
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                if (nc == 1) d[o] = d[o + 1] = d[o + 2] = row[x];
                else if (nc >= 3 && cs == BitMiracle.LibJpeg.Colorspace.RGB) { d[o] = row[x * nc]; d[o + 1] = row[x * nc + 1]; d[o + 2] = row[x * nc + 2]; }
                else if (nc >= 3) { double Y = row[x * nc], cb = row[x * nc + 1] - 128, cr = row[x * nc + 2] - 128; d[o] = Clamp8(Y + 1.402 * cr); d[o + 1] = Clamp8(Y - 0.344136 * cb - 0.714136 * cr); d[o + 2] = Clamp8(Y + 1.772 * cb); }
                else throw new InvalidDataException("jpeg: unsupported component count " + nc);
            }
        }
        return new Rgb(d, w, h);
    }
    static byte Clamp8(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>Encodes rgb24 as a JPEG. quality 1..100; 85 is about what ffmpeg's mjpeg -q:v 3 gives.</summary>
    public static byte[] EncodeJpeg(Rgb im, int quality = JpegQuality)
    {
        var rows = new BitMiracle.LibJpeg.SampleRow[im.H];
        for (int y = 0; y < im.H; y++) rows[y] = new BitMiracle.LibJpeg.SampleRow(im.Data.AsSpan(y * im.W * 3, im.W * 3).ToArray(), im.W, 8, 3);
        using var img = new BitMiracle.LibJpeg.JpegImage(rows, BitMiracle.LibJpeg.Colorspace.RGB);
        using var ms = new MemoryStream();
        img.WriteJpeg(ms, new BitMiracle.LibJpeg.CompressionParameters { Quality = quality });
        return ms.ToArray();
    }
    public const int JpegQuality = 85;

    static double Lanczos3(double x) { x = Math.Abs(x); if (x < 1e-9) return 1; if (x >= 3) return 0; var px = Math.PI * x; return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px); }

    /// <summary>Weights for one output axis: per output pixel the first source index and the normalised Lanczos-3 taps (the kernel widens when shrinking, so it filters instead of aliasing).</summary>
    static (int First, float[] W)[] Taps(double start, double len, int outN, int srcN)
    {
        var res = new (int, float[])[outN]; double scale = len / outN, support = 3 * Math.Max(1, scale);
        for (int i = 0; i < outN; i++)
        {
            double c = start + (i + 0.5) * scale; int lo = (int)Math.Floor(c - support + 0.5), hi = (int)Math.Floor(c + support + 0.5);
            var w = new float[hi - lo + 1]; double sum = 0;
            for (int k = 0; k < w.Length; k++) { double v = Lanczos3((lo + k + 0.5 - c) / Math.Max(1, scale)); w[k] = (float)v; sum += v; }
            for (int k = 0; k < w.Length; k++) w[k] = (float)(w[k] / sum);
            res[i] = (lo, w);
        }
        return res;
    }

    /// <summary>The region (x, y, w, h in source pixels, fractional ok) resampled to ow x oh with a Lanczos-3 filter (separable, edge pixels repeated).</summary>
    public static Rgb CropResizeLanczos(Rgb im, double x, double y, double w, double h, int ow, int oh)
    {
        var tx = Taps(x, w, ow, im.W); var ty = Taps(y, h, oh, im.H);
        // horizontal pass over only the source rows the vertical pass needs
        int y0 = Math.Max(0, ty.Min(t => t.First)), y1 = Math.Min(im.H - 1, ty.Max(t => t.First + t.W.Length - 1));
        var mid = new float[(y1 - y0 + 1) * ow * 3];
        for (int sy = y0; sy <= y1; sy++)
            for (int i = 0; i < ow; i++)
            {
                var (f, ws) = tx[i]; float r = 0, g = 0, b = 0;
                for (int k = 0; k < ws.Length; k++) { int sx = Math.Clamp(f + k, 0, im.W - 1); int o = (sy * im.W + sx) * 3; r += ws[k] * im.Data[o]; g += ws[k] * im.Data[o + 1]; b += ws[k] * im.Data[o + 2]; }
                int m = ((sy - y0) * ow + i) * 3; mid[m] = r; mid[m + 1] = g; mid[m + 2] = b;
            }
        var o2 = Rgb.Blank(ow, oh);
        for (int j = 0; j < oh; j++)
        {
            var (f, ws) = ty[j];
            for (int i = 0; i < ow; i++)
            {
                float r = 0, g = 0, b = 0;
                for (int k = 0; k < ws.Length; k++) { int sy = Math.Clamp(f + k, y0, y1); int m = ((sy - y0) * ow + i) * 3; r += ws[k] * mid[m]; g += ws[k] * mid[m + 1]; b += ws[k] * mid[m + 2]; }
                int o = (j * ow + i) * 3; o2.Data[o] = Clamp8(r); o2.Data[o + 1] = Clamp8(g); o2.Data[o + 2] = Clamp8(b);
            }
        }
        return o2;
    }

    public static Rgb Resize(Rgb im, int ow, int oh) => CropResize(im, 0, 0, im.W, im.H, ow, oh);

    /// <summary>The square crop around a face box: centred on it, <paramref name="scale"/> times its longer side, at size x size.</summary>
    public static Rgb SquareAround(Rgb im, double bx, double by, double bw, double bh, double scale, int size)
    {
        double s = Math.Max(bw, bh) * scale, cx = bx + bw / 2, cy = by + bh / 2;
        return CropResize(im, cx - s / 2, cy - s / 2, s, s, size, size);
    }

    /// <summary>Variance of the 3x3 Laplacian of the grey image: low for blurred faces.</summary>
    public static double LaplacianVariance(Rgb im)
    {
        var g = new double[im.W * im.H];
        for (int i = 0; i < g.Length; i++) g[i] = 0.299 * im.Data[i * 3] + 0.587 * im.Data[i * 3 + 1] + 0.114 * im.Data[i * 3 + 2];
        double sum = 0, sq = 0; int n = 0;
        for (int y = 1; y < im.H - 1; y++)
            for (int x = 1; x < im.W - 1; x++)
            {
                double l = g[(y - 1) * im.W + x] + g[(y + 1) * im.W + x] + g[y * im.W + x - 1] + g[y * im.W + x + 1] - 4 * g[y * im.W + x];
                sum += l; sq += l * l; n++;
            }
        if (n == 0) return 0; double m = sum / n; return sq / n - m * m;
    }

    /// <summary>The images tiled into a grid of <paramref name="cols"/> columns (all the same size).</summary>
    public static Rgb Grid(IReadOnlyList<Rgb> tiles, int cols, int gap = 4)
    {
        if (tiles.Count == 0) return Rgb.Blank(8, 8);
        int tw = tiles[0].W, th = tiles[0].H, rows = (tiles.Count + cols - 1) / cols;
        var g = Rgb.Blank(cols * tw + (cols + 1) * gap, rows * th + (rows + 1) * gap, 255);
        for (int k = 0; k < tiles.Count; k++)
        {
            int ox = gap + k % cols * (tw + gap), oy = gap + k / cols * (th + gap);
            for (int y = 0; y < th; y++) Buffer.BlockCopy(tiles[k].Data, y * tw * 3, g.Data, ((oy + y) * g.W + ox) * 3, tw * 3);
        }
        return g;
    }

    /// <summary>Writes the image as a JPEG (quality 2..31, lower is better) by piping raw pixels to ffmpeg.</summary>
    public static async Task WriteJpegAsync(Tools tools, Rgb im, string path, CancellationToken ct, int q = 3)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var psi = new ProcessStartInfo(await tools.FfmpegAsync(ct)) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{im.W}x{im.H}", "-i", "-", "-frames:v", "1", "-q:v", q.ToString(), path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        await p.StandardInput.BaseStream.WriteAsync(im.Data, ct); p.StandardInput.Close();
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg jpeg: " + (await err).Trim());
    }

    /// <summary>A similarity transform (scale, rotation, translation) mapping the source points onto the destination points, least squares: dst = [a -b; b a] src + t.</summary>
    public static (double A, double B, double Tx, double Ty) Similarity(IReadOnlyList<(double X, double Y)> src, IReadOnlyList<(double X, double Y)> dst)
    {
        int n = src.Count; double msx = src.Average(p => p.X), msy = src.Average(p => p.Y), mdx = dst.Average(p => p.X), mdy = dst.Average(p => p.Y);
        double num1 = 0, num2 = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            double sx = src[i].X - msx, sy = src[i].Y - msy, dx = dst[i].X - mdx, dy = dst[i].Y - mdy;
            num1 += sx * dx + sy * dy; num2 += sx * dy - sy * dx; den += sx * sx + sy * sy;
        }
        double a = num1 / den, b = num2 / den;
        return (a, b, mdx - (a * msx - b * msy), mdy - (b * msx + a * msy));
    }

    /// <summary>The face aligned to the ArcFace template at 112x112 from its 5 landmarks (right eye, left eye, nose, right and left mouth corner, in image order).</summary>
    public static Rgb AlignFace(Rgb im, float[] kps)
    {
        (double, double)[] tmpl = [(38.2946, 51.6963), (73.5318, 51.5014), (56.0252, 71.7366), (41.5493, 92.3655), (70.7299, 92.2041)];
        var src = Enumerable.Range(0, 5).Select(i => ((double)kps[2 * i], (double)kps[2 * i + 1])).ToList();
        var (a, b, tx, ty) = Similarity(src, tmpl); double d = a * a + b * b;
        var o = Rgb.Blank(112, 112); Span<byte> px = stackalloc byte[3];
        for (int y = 0; y < 112; y++)
            for (int x = 0; x < 112; x++)
            {
                double dx = x - tx, dy = y - ty;
                Sample(im, (a * dx + b * dy) / d, (-b * dx + a * dy) / d, px);
                px.CopyTo(o.Data.AsSpan((y * 112 + x) * 3, 3));
            }
        return o;
    }
}
