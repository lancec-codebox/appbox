using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace IndexNode;

/// <summary>The clip step: SigLIP 2 base (patch16, 224 px) image embeddings for the sscd frames, and the text tower for `indexnode search`.</summary>
public static class Clip
{
    public const string ModelId = Models.ClipModelId;
    public const int Size = 224, Dim = 768, TextLength = 64;
    public const double ShotCosine = 0.92;
    public const string Preprocessing = "resize to 224x224 (no padding, as the checkpoint's SiglipImageProcessor), bilinear, rgb, (x/255-0.5)/0.5";

    public static byte[] InfoJson() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1, model = ModelId, vision = Models.ClipVision.File, inputSize = Size, dim = Dim, dtype = "float16", normalised = true, preprocessing = Preprocessing,
        rows = "row i matches frames.json[i]",
    });

    public static byte[] ToF16Bytes(IReadOnlyList<float[]> rows)
    {
        var b = new byte[rows.Count * Dim * 2]; int o = 0;
        foreach (var r in rows) for (int i = 0; i < Dim; i++) { BitConverter.TryWriteBytes(b.AsSpan(o, 2), (Half)r[i]); o += 2; }
        return b;
    }

    public static float[][] FromF16Bytes(byte[] b)
    {
        int n = b.Length / (Dim * 2);
        var rows = new float[n][];
        for (int r = 0; r < n; r++) { rows[r] = new float[Dim]; for (int i = 0; i < Dim; i++) rows[r][i] = (float)BitConverter.ToHalf(b, (r * Dim + i) * 2); }
        return rows;
    }

    public static float Dot(float[] a, float[] b) { float s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

    /// <summary>The video filter that resizes a frame to 224x224 (squashed, not padded: what SiglipImageProcessor does for this checkpoint).</summary>
    public static string Filter() => "scale=224:224:flags=bilinear";

    static void Fill(DenseTensor<float> t, int row, byte[] rgb)
    {
        var tb = t.Buffer.Span; int plane = Size * Size, baseI = row * 3 * plane;
        for (int i = 0; i < plane; i++) for (int c = 0; c < 3; c++) tb[baseI + c * plane + i] = rgb[i * 3 + c] / 127.5f - 1f;
    }

    /// <summary>Embeds the frames already extracted by the frames step (frames/000000.jpg ...): they are not decoded from the video again.</summary>
    public static async Task<byte[]> EmbedFramesAsync(Tools tools, string visionModel, string framesDir, int count, int threads, Action<string> progress, CancellationToken ct)
    {
        var ffmpeg = await tools.FfmpegAsync(ct);
        using var so = new SessionOptions { IntraOpNumThreads = Math.Max(1, threads), InterOpNumThreads = 1 };
        using var session = new InferenceSession(visionModel, so);
        var inputName = session.InputMetadata.Keys.First();
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-start_number", "0", "-framerate", "1", "-i", Path.Combine(framesDir, "%06d.jpg"),
            "-frames:v", count.ToString(), "-vf", Filter(), "-f", "rawvideo", "-pix_fmt", "rgb24", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => Proc.Kill(p));
        var err = p.StandardError.ReadToEndAsync();
        var rows = new List<float[]>(count);
        var stats = new StepStats("clip"); int fb = Size * Size * 3, batch = 32; var buf = new byte[fb];
        var stream = p.StandardOutput.BaseStream;
        while (rows.Count < count)
        {
            int n = Math.Min(batch, count - rows.Count), got = 0;
            var t = new DenseTensor<float>([n, 3, Size, Size]);
            using (stats.Phase("decode+prep"))
            for (; got < n; got++)
            {
                int off = 0;
                while (off < fb) { int k = await stream.ReadAsync(buf.AsMemory(off, fb - off), ct); if (k == 0) break; off += k; }
                if (off < fb) break;
                Fill(t, got, buf);
            }
            if (got == 0) break;
            if (got < n) t = new DenseTensor<float>(t.Buffer.Slice(0, got * 3 * Size * Size), [got, 3, Size, Size]);
            ct.ThrowIfCancellationRequested();
            using var res = stats.Time("model", () => session.Run([NamedOnnxValue.CreateFromTensor(inputName, t)]));
            var o = res.First(r => r.Name == "pooler_output").AsTensor<float>();
            for (int i = 0; i < got; i++) { var row = new float[Dim]; for (int d = 0; d < Dim; d++) row[d] = o[i, d]; Sscd.L2Normalize(row); rows.Add(row); }
            progress($"{rows.Count}/{count} frames");
            if (got < n) break;
        }
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        stats.Log(rows.Count, "frames", Math.Max(1, threads));
        if (rows.Count != count) throw new InvalidOperationException($"clip: expected {count} frames, decoded {rows.Count}. {(await err).Trim()}");
        return ToF16Bytes(rows);
    }

    /// <summary>Token ids for the SigLIP 2 text tower: lower-cased, sentencepiece (Gemma vocabulary), EOS, padded with 0 to 64.</summary>
    public static int[] Tokenize(SentencePieceTokenizer tok, string text)
    {
        var ids = tok.EncodeToIds(text.ToLowerInvariant().Trim(), addBeginningOfSentence: false, addEndOfSentence: true).ToList();
        if (ids.Count > TextLength) { ids = ids.Take(TextLength).ToList(); ids[^1] = 1; }
        while (ids.Count < TextLength) ids.Add(0);
        return [.. ids];
    }

    public static SentencePieceTokenizer LoadTokenizer(string modelPath)
    {
        using var fs = File.OpenRead(modelPath);
        return SentencePieceTokenizer.Create(fs, addBeginningOfSentence: false, addEndOfSentence: false);
    }

    public static float[] EmbedText(string textModel, SentencePieceTokenizer tok, string text, int threads = 0)
    {
        using var so = new SessionOptions(); if (threads > 0) so.IntraOpNumThreads = threads;
        using var s = new InferenceSession(textModel, so);
        var ids = Tokenize(tok, text);
        var t = new DenseTensor<long>(ids.Select(i => (long)i).ToArray(), [1, ids.Length]);
        using var r = s.Run([NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), t)]);
        var o = r.First(x => x.Name == "pooler_output").AsTensor<float>();
        var v = new float[Dim]; for (int i = 0; i < Dim; i++) v[i] = o[0, i];
        Sscd.L2Normalize(v);
        return v;
    }

    /// <summary>Shot id per row: a new shot starts where the cosine between neighbouring rows falls below <see cref="ShotCosine"/>.</summary>
    public static int[] ShotIds(IReadOnlyList<float[]> rows, double threshold = ShotCosine)
    {
        var ids = new int[rows.Count]; int shot = 0;
        for (int i = 1; i < rows.Count; i++) { if (Dot(rows[i - 1], rows[i]) < threshold) shot++; ids[i] = shot; }
        return ids;
    }

    public static string Stamp(double s) { var t = TimeSpan.FromSeconds(Math.Round(s)); return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"; }

    public sealed record Hit(int Row, double T, float Score, int Shot, int ShotFrames);

    /// <summary>Best frame per shot, by score: neighbouring frames of one shot count once.</summary>
    public static List<Hit> Search(IReadOnlyList<float[]> rows, IReadOnlyList<double> times, float[] query, int top)
    {
        var shots = ShotIds(rows);
        var best = new Dictionary<int, (int Row, float Score)>(); var size = new Dictionary<int, int>();
        for (int i = 0; i < rows.Count; i++)
        {
            var s = Dot(rows[i], query);
            size[shots[i]] = size.GetValueOrDefault(shots[i]) + 1;
            if (!best.TryGetValue(shots[i], out var b) || s > b.Score) best[shots[i]] = (i, s);
        }
        return best.OrderByDescending(kv => kv.Value.Score).Take(top).Select(kv => new Hit(kv.Value.Row, times[kv.Value.Row], kv.Value.Score, kv.Key, size[kv.Key])).ToList();
    }
}
