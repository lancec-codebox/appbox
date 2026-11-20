using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IndexNode;

public static class Sscd
{
    public const string ModelName = "sscd_disc_mixup";
    /// <summary>The static QDQ int8 model (per-channel, calibrated at 288x512): used when present next to the fp32 one; never dynamic quantization (11x slower on CPU).</summary>
    public const string Int8Name = ModelName + "_int8_static";
    public static string ModelId(string modelPath) => Path.GetFileName(modelPath).Contains("int8", StringComparison.OrdinalIgnoreCase) ? Int8Name : ModelName;
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
        if (OrtOptions.Cuda) return await Models.EnsureAsync(Models.SscdFp32, http, ct); // the int8 model is for the CPU
        var int8 = Models.PathOf(Models.SscdInt8);
        if (File.Exists(int8)) return int8;
        var local = Path.Combine(NodeConfig.ToolsDir(), ModelName + ".onnx");
        if (File.Exists(local)) return local;
        if (string.IsNullOrEmpty(cfg.ModelUrl)) return await Models.EnsureAsync(Models.SscdInt8, http, ct); // the pinned static int8 model
        if (string.IsNullOrEmpty(cfg.ModelUrl)) throw new InvalidOperationException("sscd model missing: set modelUrl or modelPath in the config (see tools/export_sscd_onnx.py)");
        var gate = Models.LockOf(local); await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(local)) return local;   // another lane finished it while we waited
            Log.Info("sscd: downloading the ONNX model");
            Directory.CreateDirectory(NodeConfig.ToolsDir());
            var tmp = local + ".part-" + Guid.NewGuid().ToString("N")[..8];
            using (var r = await http.GetAsync(cfg.ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"model download failed: HTTP {(int)r.StatusCode}");
                await using var fs = File.Create(tmp);
                await r.Content.CopyToAsync(fs, ct);
                Traffic.Current.Add(Traffic.OtherIn, fs.Length);
            }
            File.Move(tmp, local, true);
            return local;
        }
        finally { gate.Release(); }
    }

    internal static void Fill(DenseTensor<float> t, int row, byte[] rgb, int plane)
    {
        var tb = t.Buffer.Span; int baseI = row * 3 * plane;
        for (int i = 0; i < plane; i++) for (int c = 0; c < 3; c++) tb[baseI + c * plane + i] = (rgb[i * 3 + c] / 255f - Mean[c]) / Std[c];
    }

    /// <summary>Descriptors for frames/000000.jpg ... : short side 288, ImageNet normalisation, L2-normalised, float16.</summary>
    /// <summary>The model input size for stored frames of w x h with the bar box cut first: the crop is applied to the stored pixels, then the result is resized to short side 288.</summary>
    public static (int W, int H) InputSize(int w, int h, BarCrop crop) => TargetSize(crop.Width(w), crop.Height(h));

    /// <summary>In-process preprocessing (no ffmpeg) of a decoded rgb24 image: crop by the bar box, then libswscale's default bicubic (<see cref="Swscale.ScaleRgb"/>) to the model input size, as the ffmpeg path's <c>scale=W:H:flags=bicubic</c> does for an rgb source (PNG). Returns rgb24 and the size.</summary>
    public static (byte[] Rgb, int W, int H) Prepare(Rgb im, BarCrop crop)
    {
        var (tw, th) = InputSize(im.W, im.H, crop);
        if (!crop.IsNone) { int cw = crop.Width(im.W), ch = crop.Height(im.H); var c = new byte[cw * ch * 3]; for (int y = 0; y < ch; y++) Buffer.BlockCopy(im.Data, ((y + crop.Top) * im.W + crop.Left) * 3, c, y * cw * 3, cw * 3); im = new Rgb(c, cw, ch); }
        return (Swscale.ScaleRgb(im, tw, th).Data, tw, th);
    }

    /// <summary>As <see cref="Prepare(Rgb, BarCrop)"/> for a JPEG file: the decoder's YCbCr planes (4:2:0, 4:2:2 or 4:4:4) go straight into the swscale port, so chroma is filtered exactly as ffmpeg does (decode -> crop -> scale -> rgb24 in one swscale pass); other JPEGs (gray, RGB, CMYK) go through rgb.</summary>
    public static (byte[] Rgb, int W, int H) PrepareJpeg(byte[] jpg, BarCrop crop)
    {
        var yuv = JpegYuv.Decode(jpg);
        if (yuv == null) return Prepare(Imaging.DecodeJpeg(jpg), crop);
        var (tw, th) = InputSize(yuv.W, yuv.H, crop);
        if (!crop.IsNone) yuv = yuv.Crop(crop.Left, crop.Top, crop.Width(yuv.W), crop.Height(yuv.H));
        return (Swscale.ScaleYuv(yuv, tw, th).Data, tw, th);
    }

    /// <summary>One image through the loaded session: L2-normalised 512-d descriptor.</summary>
    public static float[] EmbedRgb(Microsoft.ML.OnnxRuntime.InferenceSession session, byte[] rgb, int tw, int th)
    {
        var tensor = new DenseTensor<float>([1, 3, th, tw]); Fill(tensor, 0, rgb, th * tw);
        using var res = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor)]);
        var o = res.First().AsTensor<float>(); var v = new float[Dim]; for (int d = 0; d < v.Length; d++) v[d] = o[0, d];
        L2Normalize(v); return v;
    }

    /// <summary>As <see cref="DescribeAsync"/> without any child process: the stored JPEGs are decoded, cropped and resized in managed code, batches go through the loaded session.</summary>
    public static async Task<byte[]> DescribeInProcessAsync(string modelPath, string framesDir, int count, int threads, Action<string> progress, CancellationToken ct, BarCrop crop = default)
    {
        var session = OrtOptions.Shared(modelPath, threads); var inputName = session.InputMetadata.Keys.First();
        var rows = new List<float[]>(count); int batch = OrtOptions.Batch(32, 64);
        for (int s = 0; s < count; s += batch)
        {
            int n = Math.Min(batch, count - s); var prep = new (byte[] Rgb, int W, int H)[n];
            await Task.Run(() => Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct },
                i => prep[i] = PrepareJpeg(File.ReadAllBytes(Path.Combine(framesDir, $"{s + i:D6}.jpg")), crop)), ct);
            int tw = prep[0].W, th = prep[0].H; if (prep.Any(x => x.W != tw || x.H != th)) throw new InvalidOperationException("sscd: the frames differ in size");
            var tensor = new DenseTensor<float>([n, 3, th, tw]); for (int i = 0; i < n; i++) Fill(tensor, i, prep[i].Rgb, th * tw);
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
            var outT = results.First().AsTensor<float>();
            for (int i = 0; i < n; i++) { var row = new float[Dim]; for (int d = 0; d < Dim; d++) row[d] = outT[i, d]; L2Normalize(row); rows.Add(row); }
            progress($"{rows.Count}/{count} frames");
        }
        return ToF16Bytes(rows);
    }

    /// <summary>The ffmpeg filter that crops the stored frame by the box, then resizes to the model input size.</summary>
    public static string Filter(int w, int h, BarCrop crop) { var (tw, th) = InputSize(w, h, crop); return (crop.IsNone ? "" : crop.Filter(w, h) + ",") + $"scale={tw}:{th}:flags=bicubic"; }

    /// <summary>Descriptors for frames/000000.jpg ... : the black-bar box <paramref name="crop"/> is cut from each stored frame, then short side 288, ImageNet normalisation, L2-normalised, float16.</summary>
    public static async Task<byte[]> DescribeAsync(Tools tools, string modelPath, string framesDir, int count, int threads, Action<string> progress, CancellationToken ct, BarCrop crop = default)
    {
        var ffmpeg = await tools.FfmpegAsync(ct);
        var probe = await tools.FfprobeAsync(ct);
        var first = Path.Combine(framesDir, "000000.jpg");
        var pr = await Proc.RunAsync(probe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", first], ct);
        var wh = pr.Stdout.Trim().Split('x');
        int sw = int.Parse(wh[0]), sh = int.Parse(wh[1]); var (tw, th) = InputSize(sw, sh, crop); Log.Info($"sscd: stored {sw}x{sh}, bar box {crop}, model input {tw}x{th}");

        var session = OrtOptions.Shared(modelPath, threads);
        var inputName = session.InputMetadata.Keys.First();

        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-start_number", "0", "-framerate", "1", "-i", Path.Combine(framesDir, "%06d.jpg"),
            "-frames:v", count.ToString(), "-vf", Filter(sw, sh, crop), "-f", "rawvideo", "-pix_fmt", "rgb24", "-" }) psi.ArgumentList.Add(a);
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
