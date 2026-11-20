using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace IndexNode.Tests;

/// <summary>The in-process resize must reproduce libswscale's default bicubic: pixels compared with what a real ffmpeg produces for the same input and scale.</summary>
[Collection("path-env")]
public class SwscaleTests(ITestOutputHelper output)
{
    static bool Have(string exe) { try { using var p = Process.Start(new ProcessStartInfo(exe, "-version") { RedirectStandardOutput = true, RedirectStandardError = true })!; p.WaitForExit(); return p.ExitCode == 0; } catch { return false; } }

    static byte[] Ffmpeg(string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!; var ms = new MemoryStream(); var err = p.StandardError.ReadToEndAsync(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg: " + err.Result); return ms.ToArray();
    }

    public static readonly string[] Sources = ["testsrc2=s=640x360:r=1", "gradients=s=640x360:seed=7,noise=alls=18:allf=t", "mandelbrot=s=640x360", "testsrc=s=1280x720:r=1"];

    static (int Max, double Mean, double P99) Diff(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length); int max = 0; double sum = 0; var hist = new int[256];
        for (int i = 0; i < a.Length; i++) { int d = Math.Abs(a[i] - b[i]); if (d > max) max = d; sum += d; hist[d]++; }
        long acc = 0, lim = (long)(a.Length * 0.99); int p99 = 0; for (int k = 0; k < 256; k++) { acc += hist[k]; if (acc >= lim) { p99 = k; break; } }
        return (max, sum / a.Length, p99);
    }

    [Theory]
    [InlineData(0, "yuvj420p", 512, 288)]
    [InlineData(1, "yuvj420p", 512, 288)]
    [InlineData(2, "yuvj420p", 512, 288)]
    [InlineData(3, "yuvj420p", 512, 288)]
    [InlineData(0, "yuvj444p", 512, 288)]
    [InlineData(1, "yuvj444p", 512, 288)]
    [InlineData(3, "yuvj444p", 512, 288)]
    [InlineData(1, "yuvj420p", 288, 512)]   // portrait target from a landscape source (any size must work)
    [InlineData(1, "yuvj420p", 777, 333)]   // odd width: swscale switches to full chroma
    public void Jpeg_resize_matches_ffmpeg(int src, string pix, int w, int h)
    {
        if (!Have("ffmpeg")) return;
        var jpg = Ffmpeg(["-f", "lavfi", "-i", Sources[src], "-frames:v", "1", "-pix_fmt", pix, "-q:v", "2", "-f", "mjpeg", "-"]);
        var reference = RunWithStdin(["-f", "mjpeg", "-i", "-", "-vf", $"scale={w}:{h}:flags=bicubic", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], jpg);
        var yuv = JpegYuv.Decode(jpg); Assert.NotNull(yuv);
        var mine = Swscale.ScaleYuv(yuv!, w, h);
        var (max, mean, p99) = Diff(mine.Data, reference);
        output.WriteLine($"jpeg {pix} src{src} -> {w}x{h}: max {max} mean {mean:F4} p99 {p99}");
        Assert.True(mean < 0.35, $"mean {mean}"); Assert.True(max <= 6, $"max {max}");
    }

    static byte[] RunWithStdin(string[] args, byte[] input)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(args.Where(x => x != "-nostdin"))) psi.ArgumentList.Add(a);
        psi.ArgumentList.Remove("-nostdin");
        using var p = Process.Start(psi)!; var ms = new MemoryStream(); var err = p.StandardError.ReadToEndAsync(); var outTask = p.StandardOutput.BaseStream.CopyToAsync(ms);
        p.StandardInput.BaseStream.Write(input); p.StandardInput.Close(); outTask.Wait(); p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg: " + err.Result); return ms.ToArray();
    }

    [Theory]
    [InlineData(1, 512, 288)]
    [InlineData(3, 512, 288)]
    [InlineData(0, 288, 512)]
    public void Rgb_resize_matches_ffmpeg(int src, int w, int h)
    {
        if (!Have("ffmpeg")) return;
        var (sw, sh) = src == 3 ? (1280, 720) : (640, 360);
        var raw = Ffmpeg(["-f", "lavfi", "-i", Sources[src], "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]);
        Assert.Equal(sw * sh * 3, raw.Length);
        var reference = RunWithStdin(["-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{sw}x{sh}", "-i", "-", "-vf", $"scale={w}:{h}:flags=bicubic", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], raw);
        var mine = Swscale.ScaleRgb(new Rgb(raw, sw, sh), w, h);
        var (max, mean, p99) = Diff(mine.Data, reference);
        output.WriteLine($"rgb src{src} {sw}x{sh} -> {w}x{h}: max {max} mean {mean:F4} p99 {p99}");
        Assert.True(mean < 0.35, $"mean {mean}"); Assert.True(max <= 6, $"max {max}");
    }

    [Fact]
    public void Decoded_planes_equal_ffmpegs()
    {
        if (!Have("ffmpeg")) return;
        var jpg = Ffmpeg(["-f", "lavfi", "-i", Sources[1], "-frames:v", "1", "-pix_fmt", "yuvj444p", "-q:v", "2", "-f", "mjpeg", "-"]);
        var refYuv = RunWithStdin(["-f", "mjpeg", "-i", "-", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuvj444p", "-"], jpg);
        var p = JpegYuv.Decode(jpg)!; int n = p.W * p.H; Assert.Equal(n * 3, refYuv.Length);
        var mine = p.Y.Concat(p.U).Concat(p.V).ToArray(); var d = Diff(mine, refYuv);
        output.WriteLine($"decoded planes vs ffmpeg decoder (4:4:4 q2 noisy): max {d.Max} mean {d.Mean:F4}");
        Assert.Equal(0, d.Max);
    }
}
