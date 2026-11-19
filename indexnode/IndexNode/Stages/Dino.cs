using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IndexNode;

/// <summary>
/// The dino step: one DINOv2-small vector (CLS token, L2-normalised, float16) per stored frame: dino/NNNN.f16 (384 per row, same chunks and row order as frames/ and sscd/) and dino/manifest.json (written last).
/// Preprocessing as the reference (fixtures/dino_rank.py): RGB, bicubic resize to 224x392 (portrait) or 392x224 (landscape), /255, ImageNet mean and std. Frames are decoded in full by ffmpeg (no -lowres), in slices of one model batch, several slices at once.
/// </summary>
public static class Dino
{
    public const int Dim = 384, Long = 392, Short = 224;
    public static readonly float[] Mean = [0.485f, 0.456f, 0.406f], Std = [0.229f, 0.224f, 0.225f];

    /// <summary>Input (width, height) for frames of this shape: 224x392 for portrait, 392x224 for landscape.</summary>
    public static (int W, int H) InputSize(int frameW, int frameH) => frameH >= frameW ? (Short, Long) : (Long, Short);

    /// <summary>The model call: pixel_values [n,3,H,W] to n raw CLS rows (384 floats each, not yet normalised).</summary>
    public delegate float[][] Embed(DenseTensor<float> pixels);

    public sealed class Timing
    {
        public long DownloadMs, DecodeMs, ModelMs, UploadMs; public int Frames; public double WallMs;
        public override string ToString() => $"{Frames} frames, wall {WallMs:F0} ms ({(WallMs <= 0 ? 0 : Frames * 1000.0 / WallMs):F1} frames/s); model {ModelMs} ms (wall, waiting on it), decode {DecodeMs} ms (summed over parallel slices), download {DownloadMs} ms, upload {UploadMs} ms";
    }

    /// <summary>The ONNX model call on a session: output last_hidden_state, token 0.</summary>
    public static Embed OnnxEmbed(InferenceSession session)
    {
        var input = session.InputMetadata.Keys.First();
        return t =>
        {
            using var res = session.Run([NamedOnnxValue.CreateFromTensor(input, t)]);
            var o = res.First(r => r.Name == "last_hidden_state").AsTensor<float>(); int n = (int)o.Dimensions[0];
            var rows = new float[n][];
            for (int i = 0; i < n; i++) { var r = new float[Dim]; for (int d = 0; d < Dim; d++) r[d] = o[i, 0, d]; rows[i] = r; }
            return rows;
        };
    }

    /// <summary>rgb24 frames (n of w x h) to a normalised [n,3,h,w] tensor.</summary>
    public static DenseTensor<float> ToTensor(byte[] rgb, int n, int w, int h)
    {
        var t = new DenseTensor<float>([n, 3, h, w]); var tb = t.Buffer.Span; int plane = w * h;
        for (int k = 0; k < n; k++)
            for (int i = 0; i < plane; i++) { int p = (k * plane + i) * 3; for (int c = 0; c < 3; c++) tb[(k * 3 + c) * plane + i] = (rgb[p + c] / 255f - Mean[c]) / Std[c]; }
        return t;
    }

    /// <summary>ffmpeg: one slice of whole JPEGs (concatenated) to n rgb24 frames of w x h, full decode, bicubic. Exactly n frames or it throws.</summary>
    public static async Task<byte[]> DecodeSliceAsync(string ffmpeg, byte[] jpegs, int n, int w, int h, string what, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-threads", "1", "-f", "mjpeg", "-i", "pipe:0", "-vf", $"scale={w}:{h}:flags=bicubic", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" }) psi.ArgumentList.Add(x);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var errT = p.StandardError.ReadToEndAsync(ct);
        var outMs = new MemoryStream(n * w * h * 3); var outT = p.StandardOutput.BaseStream.CopyToAsync(outMs, ct);
        try { await p.StandardInput.BaseStream.WriteAsync(jpegs, ct); } catch (IOException) { }
        try { p.StandardInput.Close(); } catch (IOException) { }
        await outT; await p.WaitForExitAsync(ct); var err = await errT;
        if (p.ExitCode != 0) throw new InvalidOperationException($"{what}: ffmpeg failed (exit {p.ExitCode}): {err.Trim()[..Math.Min(300, err.Trim().Length)]}");
        if (outMs.Length != (long)n * w * h * 3) throw new InvalidOperationException($"{what}: ffmpeg decoded {outMs.Length / (w * h * 3.0):F2} frames, expected {n}. {err.Trim()[..Math.Min(200, err.Trim().Length)]}");
        return outMs.GetBuffer().AsSpan(0, (int)outMs.Length).ToArray();
    }

    /// <summary>One chunk to its vectors (float16 rows, in row order): slices of <paramref name="batch"/> frames decode in up to <paramref name="parallel"/> ffmpegs while the model takes the slices in order.</summary>
    public static async Task<byte[]> ChunkAsync(string ffmpeg, byte[] data, ChunkInfo c, int w, int h, int batch, int parallel, Embed embed, string id, Timing tm, CancellationToken ct)
    {
        int n = c.Frames.Count;
        foreach (var f in c.Frames) if (f.Off < 0 || f.Off + f.Len > data.Length) throw new InvalidOperationException($"{id}: frames/{c.Chunk}.json row {f.I} points outside frames/{c.Chunk}.mjpeg ({f.Off}+{f.Len} of {data.Length} bytes)");
        int slices = (n + batch - 1) / batch; var rows = new List<float[]>(n);
        Task<DenseTensor<float>> Start(int s)
        {
            int from = s * batch, to = Math.Min(n, from + batch); long o0 = c.Frames[from].Off, o1 = c.Frames[to - 1].Off + c.Frames[to - 1].Len;
            var bytes = data.AsMemory((int)o0, (int)(o1 - o0)).ToArray();
            return Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var rgb = await DecodeSliceAsync(ffmpeg, bytes, to - from, w, h, $"{id}: frames/{c.Chunk}.mjpeg rows {from}-{to - 1}", ct);
                var t = ToTensor(rgb, to - from, w, h); Interlocked.Add(ref tm.DecodeMs, sw.ElapsedMilliseconds); return t;
            }, ct);
        }
        int window = Math.Max(2, parallel); var pending = new Queue<Task<DenseTensor<float>>>(); int next = 0;
        while (next < Math.Min(slices, window)) pending.Enqueue(Start(next++));
        while (pending.Count > 0)
        {
            var t = await pending.Dequeue(); if (next < slices) pending.Enqueue(Start(next++));
            var sw = System.Diagnostics.Stopwatch.StartNew(); var r = embed(t); tm.ModelMs += sw.ElapsedMilliseconds;
            if (r.Length != t.Dimensions[0]) throw new InvalidOperationException($"{id}: the model returned {r.Length} rows for {t.Dimensions[0]} frames");
            foreach (var row in r) { Sscd.L2Normalize(row); rows.Add(row); }
        }
        if (rows.Count != n) throw new InvalidOperationException($"{id}: frames/{c.Chunk} has {n} rows but {rows.Count} vectors were made");
        var f16 = new byte[n * Dim * 2];
        for (int i = 0; i < n; i++) for (int d = 0; d < Dim; d++) BitConverter.TryWriteBytes(f16.AsSpan((i * Dim + d) * 2, 2), (Half)rows[i][d]);
        return f16;
    }

    public static byte[] ManifestJson(int w, int h, int count, string modelId, string sha, IReadOnlyList<(string Chunk, double Start, int Rows)> chunks) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1, step = "dino", dim = Dim, dtype = "float16", normalised = true, frames = count,
        info = new { model = modelId, sha256 = sha, output = "last_hidden_state token 0 (CLS)", inputSize = new { width = w, height = h }, resize = "bicubic (ffmpeg scale, full decode)", scale = "1/255", mean = Mean, std = Std },
        chunks = chunks.Select(c => new { chunk = c.Chunk, start = c.Start, rows = c.Rows }),
    });

    /// <summary>The standalone step: reads the stored frames/ chunks (hydrating ghosts), pipelined as thumbs: the next chunk downloads and the previous uploads while one is embedded.</summary>
    public static async Task<int> RunStoredAsync(string ffmpeg, Embed embed, string modelId, string sha, IStore store, Paths paths, string id, int threads, Action<string>? progress, CancellationToken ct, Timing? timing = null, int batch = 32)
    {
        var manName = paths.Video(id, "frames/manifest.json");
        var man = await store.GetAsync(manName, ct) ?? throw new InvalidOperationException($"{id} has no frames/ chunks yet (run the frames step)");
        var doc = StoredJson.Doc(man.Data, manName).RootElement;
        int fw = doc.TryGetProperty("width", out var we) ? we.GetInt32() : 0, fh = doc.TryGetProperty("height", out var he) ? he.GetInt32() : 0;
        if ((doc.TryGetProperty("container", out var co) ? co.GetString() : "mp4") != "mjpeg") throw new InvalidOperationException($"{id}: frames/ are AV1 chunks; the dino step reads mjpeg frame chunks (rerun the frames step)");
        var (w, h) = InputSize(fw, fh); timing ??= new Timing(); var wall = System.Diagnostics.Stopwatch.StartNew();
        var names = doc.GetProperty("chunks").EnumerateArray().Select(c => c.GetProperty("chunk").GetString()!).ToList();
        async Task<(ChunkInfo, byte[])> Fetch(string n)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var idx = await FrameChunks.ReadAsync(store, paths.Video(id, $"frames/{n}.json"), ct, false) ?? throw new InvalidOperationException($"{id}: frames/{n}.json is missing");
            var data = await FrameChunks.ReadAsync(store, paths.Video(id, $"frames/{n}.mjpeg"), ct) ?? throw new InvalidOperationException($"{id}: frames/{n}.mjpeg is missing");
            Interlocked.Add(ref timing.DownloadMs, sw.ElapsedMilliseconds);
            return (FrameChunks.ParseChunk(n, idx, paths.Video(id, $"frames/{n}.json")), data);
        }
        var chunks = new List<(string, double, int)>(); int done = 0, total = 0;
        Task<(ChunkInfo, byte[])>? nextT = names.Count > 0 ? Fetch(names[0]) : null; Task<(string, double, int)>? up = null;
        for (int i = 0; i < names.Count; i++)
        {
            var (c, data) = await nextT!; nextT = i + 1 < names.Count ? Fetch(names[i + 1]) : null;
            if (c.Frames.Count == 0) { done++; continue; }   // a 0-frame chunk (older zips keep theirs)
            var f16 = await ChunkAsync(ffmpeg, data, c, w, h, batch, threads, embed, id, timing, ct); timing.Frames += c.Frames.Count;
            if (up != null) chunks.Add(await up);
            var us = System.Diagnostics.Stopwatch.StartNew(); var cc = c;
            up = store.PutAsync(paths.Video(id, $"dino/{c.Chunk}.f16"), f16, null, null, false, ct, "application/octet-stream").ContinueWith(t => { t.GetAwaiter().GetResult(); Interlocked.Add(ref timing.UploadMs, us.ElapsedMilliseconds); return (cc.Chunk, cc.Start, cc.Frames.Count); }, TaskScheduler.Default);
            total += c.Frames.Count; progress?.Invoke($"{++done}/{names.Count} chunks");
        }
        if (up != null) chunks.Add(await up);
        await store.PutAsync(paths.Video(id, "dino/manifest.json"), ManifestJson(w, h, total, modelId, sha, chunks), null, null, false, ct);   // last: its presence means the step finished
        timing.WallMs = wall.Elapsed.TotalMilliseconds;
        return total;
    }

    /// <summary>Fetches the model (pinned sha256), opens a session with <paramref name="threads"/> intra-op threads and runs the step.</summary>
    public static async Task<int> RunAsync(Tools tools, HttpClient http, IStore store, Paths paths, string id, int threads, Action<string>? progress, CancellationToken ct, Timing? timing = null, bool int8 = false, string? modelPath = null)
    {
        var mf = int8 ? Models.DinoSmallInt8 : Models.DinoSmall; var path = modelPath ?? await Models.EnsureAsync(mf, http, ct);
        var ffmpeg = await tools.FfmpegAsync(ct);
        var session = OrtOptions.Shared(path, Math.Max(1, threads));
        return await RunStoredAsync(ffmpeg, OnnxEmbed(session), mf.Id, mf.Sha256, store, paths, id, threads, progress, ct, timing, OrtOptions.Batch(32, 64));
    }
}
