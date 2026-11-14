using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace IndexNode;

/// <summary>One stored frame: its video time, its number and pts in the chunk file, and the spans (seconds of video) it stands for after pruning.</summary>
public sealed record ChunkFrame(int I, double T, double Pts, List<double[]> Spans, int? Rep = null);

public sealed record ChunkInfo(string Chunk, double Start, List<ChunkFrame> Frames);

/// <summary>
/// Frames and keyframes stored in 10-minute chunks: &lt;prefix&gt;/NNNN.mp4 (AV1), &lt;prefix&gt;/NNNN.json (index) and &lt;prefix&gt;/manifest.json.
/// Vectors use the same chunk names: sscd/NNNN.f16, clip/NNNN.f16 (one row per frame of that chunk's index) plus their own manifest.json.
/// </summary>
public static class FrameChunks
{
    public const double ChunkSeconds = 600;
    public const int ShortSide = 360;
    public static string Name(int chunk) => chunk.ToString("D4", CultureInfo.InvariantCulture);
    public static int ChunkOf(double t) => Math.Max(0, (int)Math.Floor(t / ChunkSeconds));

    /// <summary>Encodes raw rgb frames into one AV1 mp4 (1 fps). Sampled frames: SVT-AV1 preset 8, crf, a key frame every 10; keyframes: all-intra.</summary>
    public sealed class Encoder : IDisposable
    {
        readonly Process p; readonly Task<string> err; readonly string file;
        Encoder(Process p, string file) { this.p = p; this.file = file; err = p.StandardError.ReadToEndAsync(); }
        public static Encoder Start(string ffmpeg, int w, int h, string file, int crf, bool intraOnly)
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{w}x{h}", "-framerate", "1", "-i", "-", "-c:v", "libsvtav1", "-preset", "8", "-crf", crf.ToString(CultureInfo.InvariantCulture) };
            if (intraOnly) args.AddRange(["-svtav1-params", "keyint=1"]); else args.AddRange(["-g", "10"]);
            args.AddRange(["-pix_fmt", "yuv420p", "-movflags", "+faststart", file]);
            foreach (var a in args) psi.ArgumentList.Add(a);
            return new Encoder(Process.Start(psi)!, file);
        }
        public async Task WriteAsync(byte[] rgb, CancellationToken ct) => await p.StandardInput.BaseStream.WriteAsync(rgb, ct);
        public async Task<byte[]> FinishAsync()
        {
            p.StandardInput.Close(); await p.WaitForExitAsync(CancellationToken.None);
            if (p.ExitCode != 0) throw new InvalidOperationException("AV1 encode failed: " + (await err).Trim());
            return await File.ReadAllBytesAsync(file);
        }
        public void Dispose() { try { if (!p.HasExited) p.Kill(true); } catch { } p.Dispose(); }
    }

    /// <summary>Throws unless every packet of the file is a key packet (SVT-AV1 may ignore -g 1).</summary>
    public static async Task VerifyAllIntraAsync(Tools tools, string file, int expected, CancellationToken ct)
    {
        var r = await Proc.RunAsync(await tools.FfprobeAsync(ct), ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=flags", "-of", "csv=p=0", file], ct);
        var flags = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (flags.Length != expected || flags.Any(f => !f.StartsWith('K'))) throw new InvalidOperationException($"{file}: not all-intra ({flags.Count(f => f.StartsWith('K'))} key packets of {flags.Length}, expected {expected})");
    }

    public static byte[] IndexJson(string chunk, double start, int w, int h, int crf, int gop, IReadOnlyList<ChunkFrame> frames, bool withSpans) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1, chunk, start, width = w, height = h, codec = "av1", encoder = "svt-av1", preset = 8, crf, gop, fps = 1,
        frames = frames.Select(f => withSpans ? (object)new { i = f.I, t = f.T, pts = f.Pts, spans = f.Spans, rep = f.Rep } : new { i = f.I, t = f.T, pts = f.Pts }),
    });

    public static byte[] ManifestJson(string kind, int w, int h, int crf, int count, IReadOnlyList<(string Chunk, double Start, int Frames, long Bytes)> chunks, object? extra = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1, kind, container = "mp4", codec = "av1", chunkSeconds = ChunkSeconds, width = w, height = h, crf, count, extra,
        chunks = chunks.Select(c => new { chunk = c.Chunk, start = c.Start, frames = c.Frames, bytes = c.Bytes }),
    });

    /// <summary>Reads a blob that may be a Nexis ghost: touched and hydrated first (RestStore only), then fetched.</summary>
    public static async Task<byte[]?> ReadAsync(IStore store, string name, CancellationToken ct, bool big = true)
    {
        if (big && store is RestStore rs) await NexisAccess.PrepareAsync(rs, name, ct);
        return (await store.GetAsync(name, ct))?.Data;
    }

    /// <summary>Copies &lt;prefix&gt;/ (manifest, indexes, mp4 chunks) of a job to a local folder, hydrating each chunk first; null when the job has no such chunk set.</summary>
    public static async Task<ChunkDir?> DownloadAsync(IStore store, Paths paths, string id, string prefix, string dir, CancellationToken ct)
    {
        var man = await store.GetAsync(paths.Video(id, prefix + "/manifest.json"), ct);
        if (man == null) return null;
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "manifest.json"), man.Data, ct);
        foreach (var c in JsonDocument.Parse(man.Data).RootElement.GetProperty("chunks").EnumerateArray())
        {
            var n = c.GetProperty("chunk").GetString()!;
            foreach (var ext in new[] { "json", "mp4" })
            {
                var b = await ReadAsync(store, paths.Video(id, $"{prefix}/{n}.{ext}"), ct, ext == "mp4") ?? throw new InvalidOperationException($"{id}: {prefix}/{n}.{ext} is missing");
                await File.WriteAllBytesAsync(Path.Combine(dir, $"{n}.{ext}"), b, ct);
            }
        }
        return ChunkDir.Open(dir);
    }

    /// <summary>Writes one vector chunk per frame chunk (rows are sliced from the all-frames <paramref name="rows"/>) and the manifest.</summary>
    public static async Task PutVectorsAsync(IStore store, Paths paths, string id, string step, ChunkDir frames, byte[] rows, int dim, object info, CancellationToken ct)
    {
        int rowBytes = dim * 2;
        if (rows.Length != frames.Count * rowBytes) throw new InvalidOperationException($"{step}: {rows.Length / rowBytes} rows for {frames.Count} frames");
        int pos = 0; var chunks = new List<object>();
        foreach (var c in frames.Chunks)
        {
            int n = c.Frames.Count;
            await store.PutAsync(paths.Video(id, $"{step}/{c.Chunk}.f16"), rows.AsSpan(pos * rowBytes, n * rowBytes).ToArray(), null, null, false, ct, "application/octet-stream");
            chunks.Add(new { chunk = c.Chunk, start = c.Start, rows = n }); pos += n;
        }
        var man = new Dictionary<string, object?> { ["v"] = 1, ["step"] = step, ["dim"] = dim, ["dtype"] = "float16", ["normalised"] = true, ["frames"] = frames.Count, ["info"] = info, ["chunks"] = chunks };
        await store.PutAsync(paths.Video(id, step + "/manifest.json"), JsonSerializer.SerializeToUtf8Bytes(man), null, null, false, ct);
    }

    /// <summary>Frame times of a job: from the chunks, or from the old frames.json when a job still has that.</summary>
    public static async Task<List<double>?> TimesAsync(IJobSource src, CancellationToken ct)
    {
        if (await src.ReadAsync("frames/manifest.json", ct) is { } man)
        {
            var times = new List<double>();
            foreach (var c in JsonDocument.Parse(man).RootElement.GetProperty("chunks").EnumerateArray())
                if (await src.ReadAsync($"frames/{c.GetProperty("chunk").GetString()}.json", ct) is { } j)
                    times.AddRange(JsonDocument.Parse(j).RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("t").GetDouble()));
            return times;
        }
        return await src.ReadAsync("frames.json", ct) is { } b
            ? JsonDocument.Parse(b).RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object ? e.GetProperty("t").GetDouble() : e.GetDouble()).ToList() : null;
    }

    public static async Task<byte[]?> VectorsAsync(IJobSource src, string step, CancellationToken ct)
    {
        if (await src.ReadAsync(step + "/manifest.json", ct) is { } man)
        {
            var ms = new MemoryStream();
            foreach (var c in JsonDocument.Parse(man).RootElement.GetProperty("chunks").EnumerateArray())
                ms.Write(await src.ReadAsync($"{step}/{c.GetProperty("chunk").GetString()}.f16", ct) ?? throw new InvalidOperationException($"{step} chunk missing"));
            return ms.ToArray();
        }
        return await src.ReadAsync(step + ".f16", ct);
    }

    /// <summary>The rows the SSCD pass kept (sscd/keep.json), or null when there is no such pass.</summary>
    public static async Task<HashSet<int>?> SscdKeepAsync(IJobSource src, CancellationToken ct) =>
        await src.ReadAsync("sscd/keep.json", ct) is { } b ? JsonDocument.Parse(b).RootElement.GetProperty("keep").EnumerateArray().Select(x => x.GetInt32()).ToHashSet() : null;

    /// <summary>Applies the SSCD pass to the stored indexes: a dropped frame points to the kept one (rep) and its spans move to it; writes sscd/keep.json and the rewritten frames/NNNN.json.</summary>
    public static async Task ApplySscdPassAsync(IStore store, Paths paths, string id, ChunkDir frames, int[] rep, List<int> keep, double cosine, double lookback, CancellationToken ct)
    {
        var all = frames.Chunks.SelectMany(c => c.Frames).ToList();
        all = ResetSscdSpans(all);
        for (int i = 0; i < all.Count; i++)
        {
            if (rep[i] == i) continue;
            foreach (var s in all[i].Spans) FramePruner.AddSpan(all[rep[i]].Spans, s[0], s[1]);
            all[i].Spans.Clear();
        }
        foreach (var f in all) f.Spans.Sort((a, b) => a[0].CompareTo(b[0]));
        for (int i = 0; i < all.Count; i++) if (rep[i] != i) all[i] = all[i] with { Rep = rep[i] };
        int pos = 0;
        foreach (var c in frames.Chunks)
        {
            var list = all.Skip(pos).Take(c.Frames.Count).ToList(); pos += list.Count;
            var j = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(frames.Dir, c.Chunk + ".json"), ct)).RootElement;
            await store.PutAsync(paths.Video(id, $"frames/{c.Chunk}.json"), IndexJson(c.Chunk, c.Start, frames.W, frames.H, j.GetProperty("crf").GetInt32(), j.GetProperty("gop").GetInt32(), list, true), null, null, false, ct);
        }
        await store.PutAsync(paths.Video(id, "sscd/keep.json"), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, cosine, lookback, rows = all.Count, kept = keep.Count, keep, rep }), null, null, false, ct);
    }

    /// <summary>A frame set that already went through an SSCD pass (a redo) gets its pre-pass spans back: each stored frame stands for [its time, the next stored frame's time), the last one up to the end of the covered time. Frames that were never merged are returned as they are.</summary>
    public static List<ChunkFrame> ResetSscdSpans(List<ChunkFrame> all)
    {
        if (!all.Any(f => f.Rep != null)) return all;
        double end = all.Max(f => f.Spans.Count == 0 ? f.T : f.Spans.Max(s => s[1]));
        var res = new List<ChunkFrame>(all.Count);
        for (int i = 0; i < all.Count; i++) res.Add(all[i] with { Rep = null, Spans = [[all[i].T, i + 1 < all.Count ? all[i + 1].T : Math.Max(end, all[i].T)]] });
        return res;
    }

    /// <summary>The second pruning pass: over the time-ordered rows, a row whose cosine to a kept row of the last <paramref name="lookback"/> seconds is at least <paramref name="cosine"/> is dropped. Returns the kept row numbers and, per row, the kept row it stands for.</summary>
    public static (List<int> Keep, int[] Rep) SscdPass(IReadOnlyList<double> times, byte[] f16, int dim, double cosine, double lookback)
    {
        int n = times.Count; var rows = new float[n][];
        for (int i = 0; i < n; i++) { rows[i] = new float[dim]; for (int d = 0; d < dim; d++) rows[i][d] = (float)BitConverter.ToHalf(f16, (i * dim + d) * 2); }
        var keep = new List<int>(); var rep = new int[n]; var window = new List<int>();
        for (int i = 0; i < n; i++)
        {
            window.RemoveAll(k => times[i] - times[k] > lookback);
            int best = -1; float bc = (float)cosine;
            foreach (var k in window) { float dot = 0; for (int d = 0; d < dim; d++) dot += rows[i][d] * rows[k][d]; if (dot >= bc) { bc = dot; best = k; } }
            if (best >= 0) rep[i] = best; else { rep[i] = i; keep.Add(i); window.Add(i); }
        }
        return (keep, rep);
    }
}

/// <summary>A chunk set on local disk.</summary>
public sealed class ChunkDir
{
    public string Dir { get; }
    public List<ChunkInfo> Chunks { get; }
    public int W { get; }
    public int H { get; }
    public int Count => Chunks.Sum(c => c.Frames.Count);
    private ChunkDir(string dir, List<ChunkInfo> chunks, int w, int h) { Dir = dir; Chunks = chunks; W = w; H = h; }

    public static ChunkDir Open(string dir)
    {
        var man = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "manifest.json"))).RootElement;
        var chunks = new List<ChunkInfo>();
        foreach (var c in man.GetProperty("chunks").EnumerateArray())
        {
            var n = c.GetProperty("chunk").GetString()!;
            var j = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, n + ".json"))).RootElement;
            chunks.Add(new ChunkInfo(n, j.GetProperty("start").GetDouble(), j.GetProperty("frames").EnumerateArray().Select(f => new ChunkFrame(
                f.GetProperty("i").GetInt32(), f.GetProperty("t").GetDouble(), f.GetProperty("pts").GetDouble(),
                f.TryGetProperty("spans", out var sp) ? sp.EnumerateArray().Select(x => new[] { x[0].GetDouble(), x[1].GetDouble() }).ToList() : [],
                f.TryGetProperty("rep", out var rp) && rp.ValueKind == JsonValueKind.Number ? rp.GetInt32() : null)).ToList()));
        }
        int w = man.TryGetProperty("width", out var we) ? we.GetInt32() : 0, h = man.TryGetProperty("height", out var he) ? he.GetInt32() : 0;
        return new ChunkDir(dir, chunks, w, h);
    }

    public List<double> Times => Chunks.SelectMany(c => c.Frames.Select(f => f.T)).ToList();

    /// <summary>Decodes the chunks (ffmpeg, in order) and passes (row, time, rgb) for every stored frame.</summary>
    public async Task DecodeAsync(Tools tools, Func<int, double, Rgb, Task> each, CancellationToken ct)
    {
        var ffmpeg = await tools.FfmpegAsync(ct); int n = 0; int fb = W * H * 3;
        foreach (var c in Chunks)
        {
            if (c.Frames.Count == 0) continue;
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", Path.Combine(Dir, c.Chunk + ".mp4"), "-f", "rawvideo", "-pix_fmt", "rgb24", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!; using var reg = ct.Register(() => Proc.Kill(p));
            var err = p.StandardError.ReadToEndAsync();
            var buf = new byte[fb]; var st = p.StandardOutput.BaseStream;
            for (int k = 0; k < c.Frames.Count; k++)
            {
                int off = 0; while (off < fb) { int r = await st.ReadAsync(buf.AsMemory(off, fb - off), ct); if (r == 0) break; off += r; }
                if (off < fb) throw new InvalidOperationException($"chunk {c.Chunk}: decoded {k} of {c.Frames.Count} frames ({(await err).Trim()})");
                await each(n++, c.Frames[k].T, new Rgb((byte[])buf.Clone(), W, H));
            }
            await p.WaitForExitAsync(CancellationToken.None);
        }
    }

    /// <summary>Writes every frame as 000000.jpg, 000001.jpg ... (in order, high quality) for the steps that read an image sequence: ffmpeg decodes each chunk once.</summary>
    public async Task ExtractAsync(Tools tools, string outDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir); int start = 0; var ffmpeg = await tools.FfmpegAsync(ct);
        foreach (var c in Chunks)
        {
            var r = await Proc.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", Path.Combine(Dir, c.Chunk + ".mp4"), "-q:v", "1", "-start_number", start.ToString(CultureInfo.InvariantCulture), Path.Combine(outDir, "%06d.jpg")], ct);
            if (r.ExitCode != 0) throw new InvalidOperationException($"decode {c.Chunk}: {r.Stderr.Trim()}");
            start += c.Frames.Count;
        }
    }
}
