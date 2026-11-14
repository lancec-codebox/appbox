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
