using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace IndexNode.Tests;

/// <summary>
/// End to end with the real int8 SSCD model: the vector the node's ffmpeg path gives for an image against the one the service's in-process path gives, cosine &gt;= 0.999 for each image.
/// Opt-in (downloads the 25 MB model the way the node does, needs ffmpeg): set INDEXNODE_E2E=1.
/// </summary>
public class SscdParityTests(ITestOutputHelper output)
{
    static byte[] Ffmpeg(string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!; var ms = new MemoryStream(); var err = p.StandardError.ReadToEndAsync(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg: " + err.Result); return ms.ToArray();
    }

    static double Cos(float[] a, float[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += (double)a[i] * b[i]; return s; }

    [Fact]
    public async Task In_process_vectors_match_the_ffmpeg_path()
    {
        if (Environment.GetEnvironmentVariable("INDEXNODE_E2E") != "1") return;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var cfg = new NodeConfig(); var model = await Sscd.EnsureModelAsync(cfg, http, default);
        output.WriteLine($"model {model} {new FileInfo(model).Length} bytes");
        var tools = new Tools(http); tools.UseFfmpeg("ffmpeg", "ffprobe");
        var dir = Path.Combine(Path.GetTempPath(), "sscd-parity-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
        var cases = new List<(string Name, string File)>();
        // the stored-frame encoding: scale bicubic -> rgb24 -> yuvj444p mjpeg -q:v 2 (Frames.ProduceAsync); then also plain 4:2:0 JPEGs and a PNG (screenshot inputs)
        string[] srcs = ["testsrc2=s=1280x720:r=1", "testsrc=s=1280x720:r=1", "gradients=s=1280x720:seed=3,noise=alls=14:allf=t", "mandelbrot=s=1280x720", "life=s=640x360:mold=10:r=1:ratio=0.2:death_color=#C83232:life_color=#00ff00,scale=1280x720"];
        for (int i = 0; i < srcs.Length; i++)
        {
            var f444 = Path.Combine(dir, $"stored{i}.jpg"); File.WriteAllBytes(f444, Ffmpeg(["-f", "lavfi", "-i", srcs[i], "-frames:v", "1", "-vf", "scale=640:360:flags=bicubic,format=rgb24,scale=out_color_matrix=bt601:out_range=full,format=yuvj444p", "-c:v", "mjpeg", "-q:v", "2", "-f", "mjpeg", "-"])); cases.Add(($"stored q2 4:4:4 #{i}", f444));
            var f420 = Path.Combine(dir, $"shot{i}.jpg"); File.WriteAllBytes(f420, Ffmpeg(["-f", "lavfi", "-i", srcs[i], "-frames:v", "1", "-pix_fmt", "yuvj420p", "-q:v", "4", "-f", "mjpeg", "-"])); cases.Add(($"screenshot q4 4:2:0 1280x720 #{i}", f420));
            var png = Path.Combine(dir, $"shot{i}.png"); File.WriteAllBytes(png, Ffmpeg(["-f", "lavfi", "-i", srcs[i], "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "image2pipe", "-c:v", "png", "-"])); cases.Add(($"png 1280x720 #{i}", png));
        }
        // a photo-like image: smooth gradients + sensor noise + blur + vignette, odd size, portrait
        var photo = Path.Combine(dir, "photo.jpg"); File.WriteAllBytes(photo, Ffmpeg(["-f", "lavfi", "-i", "gradients=s=1080x1923:seed=11:c0=0x3b5d3a:c1=0xd8c3a5:c2=0x6f8fb5:c3=0x4a3f35:n=4:speed=0.0", "-frames:v", "1", "-vf", "noise=alls=9:allf=t,gblur=sigma=1.2,vignette=PI/4,format=yuvj420p", "-q:v", "3", "-f", "mjpeg", "-"])); cases.Add(("photo-like 1080x1923 q3 4:2:0", photo));
        var worst = 1.0; var fails = new List<string>();
        foreach (var (name, file) in cases)
        {
            var (viaFfmpeg, _) = await CentersCommands.EmbedImageDetailAsync(tools, cfg, http, file, default, false);
            var bytes = File.ReadAllBytes(file); var session = OrtOptions.Shared(model, 2);
            var (rgb, tw, th) = bytes[0] == 0xFF ? Sscd.PrepareJpeg(bytes, BarCrop.None) : Sscd.Prepare(InProcessMedia.DecodeImage(bytes), BarCrop.None);
            var inProc = Sscd.EmbedRgb(session, rgb, tw, th); var c = Cos(viaFfmpeg, inProc); worst = Math.Min(worst, c);
            var refRgb = Ffmpeg(["-i", file, "-frames:v", "1", "-vf", $"scale={tw}:{th}:flags=bicubic", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]);
            int mx = 0; double sm = 0; for (int k = 0; k < rgb.Length; k++) { int d = Math.Abs(rgb[k] - refRgb[k]); mx = Math.Max(mx, d); sm += d; }
            var viaRef = Sscd.EmbedRgb(session, refRgb, tw, th);
            output.WriteLine($"cosine {c:F6}  {name}  (model input {tw}x{th}; pixels vs ffmpeg max {mx} mean {sm / rgb.Length:F4}; ffmpeg rgb via this session vs node path {Cos(viaRef, viaFfmpeg):F6})");
            fails.Add(c < 0.999 ? name : "");
        }
        output.WriteLine($"worst cosine {worst:F6}");
        Assert.True(fails.All(x => x == ""), "below 0.999: " + string.Join("; ", fails.Where(x => x != "")));
    }
}
