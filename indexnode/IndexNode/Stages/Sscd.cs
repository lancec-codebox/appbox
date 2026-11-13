using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IndexNode;

public static class Sscd
{
    public const string ModelName = "sscd_disc_mixup";
    public const int Dim = 512;
    public const int ShortSide = 288;
    public static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    public static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    public static void L2Normalize(Span<float> v)
    {
        double s = 0; foreach (var x in v) s += (double)x * x;
        var n = (float)Math.Sqrt(Math.Max(s, 1e-24));
        for (int i = 0; i < v.Length; i++) v[i] /= n;
    }

    /// <summary>Row-major little-endian float16: N rows of 512.</summary>
    public static byte[] ToF16Bytes(IReadOnlyList<float[]> rows)
    {
        var b = new byte[rows.Count * Dim * 2];
        int o = 0;
        foreach (var r in rows)
            for (int i = 0; i < Dim; i++) { BitConverter.TryWriteBytes(b.AsSpan(o, 2), (Half)r[i]); o += 2; }
        return b;
    }

    public static (int W, int H) TargetSize(int w, int h) => w >= h ? ((int)Math.Round(w * (double)ShortSide / h), ShortSide) : (ShortSide, (int)Math.Round(h * (double)ShortSide / w));

    public static async Task<string> EnsureModelAsync(NodeConfig cfg, HttpClient http, CancellationToken ct)
    {
        var path = cfg.ModelPath;
        if (!string.IsNullOrEmpty(path))
        {
            if (!File.Exists(path)) throw new InvalidOperationException($"sscd model not found at configured modelPath {path}");
            return path;
        }
        var local = Path.Combine(NodeConfig.ToolsDir(), ModelName + ".onnx");
        if (File.Exists(local)) return local;
        if (string.IsNullOrEmpty(cfg.ModelUrl)) throw new InvalidOperationException("sscd model missing: set modelUrl or modelPath in the config (see tools/export_sscd_onnx.py)");
        Log.Info("sscd: downloading the ONNX model");
        Directory.CreateDirectory(NodeConfig.ToolsDir());
        var tmp = local + ".part";
        using (var r = await http.GetAsync(cfg.ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"model download failed: HTTP {(int)r.StatusCode}");
            await using var fs = File.Create(tmp);
            await r.Content.CopyToAsync(fs, ct);
        }
        File.Move(tmp, local, true);
        return local;
    }

    /// <summary>Descriptors for frames/000000.jpg ... : short side 288, ImageNet normalisation, L2-normalised, float16.</summary>
    public static async Task<byte[]> DescribeAsync(Tools tools, string modelPath, string framesDir, int count, int threads, Action<string> progress, CancellationToken ct)
    {
        var ffmpeg = await tools.FfmpegAsync(ct);
        var probe = await tools.FfprobeAsync(ct);
        var first = Path.Combine(framesDir, "000000.jpg");
        var pr = await Proc.RunAsync(probe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", first], ct);
        var wh = pr.Stdout.Trim().Split('x');
        var (tw, th) = TargetSize(int.Parse(wh[0]), int.Parse(wh[1]));

        using var so = new SessionOptions { IntraOpNumThreads = Math.Max(1, threads), InterOpNumThreads = 1 };
        using var session = new InferenceSession(modelPath, so);
        var inputName = session.InputMetadata.Keys.First();

        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-start_number", "0", "-framerate", "1", "-i", Path.Combine(framesDir, "%06d.jpg"),
            "-frames:v", count.ToString(), "-vf", $"scale={tw}:{th}:flags=bicubic", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => Proc.Kill(p));
        var err = p.StandardError.ReadToEndAsync();
        var rows = new List<float[]>(count);
        int frameBytes = tw * th * 3, batch = 8;
        var buf = new byte[frameBytes];
        var stream = p.StandardOutput.BaseStream;
        int read = 0;
        while (read < count)
        {
            int n = Math.Min(batch, count - read);
            var tensor = new DenseTensor<float>([n, 3, th, tw]);
            int got = 0;
            for (; got < n; got++)
            {
                int off = 0;
                while (off < frameBytes) { int k = await stream.ReadAsync(buf.AsMemory(off, frameBytes - off), ct); if (k == 0) break; off += k; }
                if (off < frameBytes) break;
                for (int y = 0; y < th; y++)
                    for (int x = 0; x < tw; x++)
                        for (int c = 0; c < 3; c++)
                            tensor[got, c, y, x] = (buf[(y * tw + x) * 3 + c] / 255f - Mean[c]) / Std[c];
            }
            if (got == 0) break;
            if (got < n) tensor = new DenseTensor<float>(tensor.Buffer.Slice(0, got * 3 * th * tw), [got, 3, th, tw]);
            ct.ThrowIfCancellationRequested();
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
            var outT = results.First().AsTensor<float>();
            for (int i = 0; i < got; i++)
            {
                var row = new float[Dim];
                for (int d = 0; d < Dim; d++) row[d] = outT[i, d];
                L2Normalize(row);
                rows.Add(row);
            }
            read += got;
            progress($"{read}/{count} frames");
            if (got < n) break;
        }
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (rows.Count != count) throw new InvalidOperationException($"sscd: expected {count} frames, decoded {rows.Count}. {(await err).Trim()}");
        return ToF16Bytes(rows);
    }
}
