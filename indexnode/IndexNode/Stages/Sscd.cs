using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IndexNode;

public static class Sscd
{
    public const string ModelName = "sscd_disc_mixup";
    /// <summary>Only recognised, never used by the pipeline: manifests written by 0.5.x/0.6.0 name this model, and the redo command resets them.</summary>
    public const string Int8Name = ModelName + "_int8_static";
    public static string ModelId(string modelPath) => Path.GetFileName(modelPath).Contains("int8", StringComparison.OrdinalIgnoreCase) ? Int8Name : ModelName;
    /// <summary>True when an sscd/manifest.json was made by the int8 model (0.5.x and 0.6.0 did): its vectors do not match fp32, so the step is done again.</summary>
    public static bool IsInt8Manifest(byte[] manifest)
    {
        try { return System.Text.Json.JsonDocument.Parse(manifest).RootElement.TryGetProperty("info", out var i) && i.TryGetProperty("model", out var m) && m.GetString() == Int8Name; }
        catch (System.Text.Json.JsonException) { return false; }
    }
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

    /// <summary>SSCD is fp32 everywhere (CPU and GPU): the pinned fp32 model. The static int8 model gave fingerprints that do not match fp32 (mean cosine 0.98, p1 0.83, min 0.09 on an A100 check), and the browser screenshot search embeds with fp32. A configured model-path / --model is a dev override only.</summary>
    public static async Task<string> EnsureModelAsync(NodeConfig cfg, HttpClient http, CancellationToken ct)
    {
        var path = cfg.ModelPath;
        if (!string.IsNullOrEmpty(path))
        {
            if (!File.Exists(path)) throw new InvalidOperationException($"sscd model not found at configured modelPath {path}");
            if (Path.GetFileName(path).Contains("int8", StringComparison.OrdinalIgnoreCase)) Log.Info("WARNING: sscd runs on an int8 model (dev override): its vectors do not match the fp32 index or the screenshot search");
            return path;
        }
        return await Models.EnsureAsync(Models.SscdFp32, http, ct);
    }

    static void Fill(DenseTensor<float> t, int row, byte[] rgb, int plane)
    {
        var tb = t.Buffer.Span; int baseI = row * 3 * plane;
        for (int i = 0; i < plane; i++) for (int c = 0; c < 3; c++) tb[baseI + c * plane + i] = (rgb[i * 3 + c] / 255f - Mean[c]) / Std[c];
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

        using var session = OrtOptions.Session(modelPath, threads);
        var inputName = session.InputMetadata.Keys.First();

        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-start_number", "0", "-framerate", "1", "-i", Path.Combine(framesDir, "%06d.jpg"),
            "-frames:v", count.ToString(), "-vf", $"scale={tw}:{th}:flags=bicubic", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => Proc.Kill(p));
        var err = p.StandardError.ReadToEndAsync();
        var rows = new List<float[]>(count);
        var stats = new StepStats("sscd"); int frameBytes = tw * th * 3, batch = OrtOptions.Batch(32, 64);
        var buf = new byte[frameBytes];
        var stream = p.StandardOutput.BaseStream;
        int read = 0;
        while (read < count)
        {
            int n = Math.Min(batch, count - read);
            var tensor = new DenseTensor<float>([n, 3, th, tw]);
            int got = 0; int plane = th * tw;
            using (stats.Phase("decode+prep"))
            for (; got < n; got++)
            {
                int off = 0;
                while (off < frameBytes) { int k = await stream.ReadAsync(buf.AsMemory(off, frameBytes - off), ct); if (k == 0) break; off += k; }
                if (off < frameBytes) break;
                Fill(tensor, got, buf, plane);
            }
            if (got == 0) break;
            if (got < n) tensor = new DenseTensor<float>(tensor.Buffer.Slice(0, got * 3 * th * tw), [got, 3, th, tw]);
            ct.ThrowIfCancellationRequested();
            using var results = stats.Time("model", () => session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]));
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
        stats.Log(rows.Count, "frames", Math.Max(1, threads));
        return ToF16Bytes(rows);
    }
}
