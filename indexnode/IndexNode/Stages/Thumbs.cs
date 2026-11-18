using System.Text.Json;

namespace IndexNode;

/// <summary>
/// The thumbs step: one tiny JPEG per stored frame (36x64, quality 75, a box/area downscale), packed like frames/*.mjpeg:
/// thumbs/NNNN.mjpeg (whole standalone JPEGs back to back, same chunks and row order as frames/ and sscd/),
/// thumbs/NNNN.json ([[off, len], ...], one pair per row; its own index) and thumbs/manifest.json.
/// Screenshot search re-ranks with them, and the UI shows frame previews from them without rehydrating Nexis-ghosted frames.
/// </summary>
public static class Thumbs
{
    public const int Width = 36, Height = 64, Quality = 75;

    /// <summary>Box (area) downscale: each output pixel is the mean of the source area it covers, partial pixels weighted by their overlap.</summary>
    public static Rgb BoxResize(Rgb im, int ow, int oh)
    {
        var o = new byte[ow * oh * 3];
        double sx = (double)im.W / ow, sy = (double)im.H / oh;
        for (int y = 0; y < oh; y++)
        {
            double y0 = y * sy, y1 = y0 + sy; int iy0 = (int)Math.Floor(y0), iy1 = Math.Min(im.H, (int)Math.Ceiling(y1));
            for (int x = 0; x < ow; x++)
            {
                double x0 = x * sx, x1 = x0 + sx; int ix0 = (int)Math.Floor(x0), ix1 = Math.Min(im.W, (int)Math.Ceiling(x1));
                double r = 0, g = 0, b = 0, wsum = 0;
                for (int yy = iy0; yy < iy1; yy++)
                {
                    double wy = Math.Min(y1, yy + 1) - Math.Max(y0, yy);
                    for (int xx = ix0; xx < ix1; xx++)
                    {
                        double w = wy * (Math.Min(x1, xx + 1) - Math.Max(x0, xx)); int p = (yy * im.W + xx) * 3;
                        r += im.Data[p] * w; g += im.Data[p + 1] * w; b += im.Data[p + 2] * w; wsum += w;
                    }
                }
                int q = (y * ow + x) * 3;
                o[q] = (byte)Math.Clamp(Math.Round(r / wsum), 0, 255); o[q + 1] = (byte)Math.Clamp(Math.Round(g / wsum), 0, 255); o[q + 2] = (byte)Math.Clamp(Math.Round(b / wsum), 0, 255);
            }
        }
        return new Rgb(o, ow, oh);
    }

    /// <summary>The thumbnail size for frames of this shape: 36x64 for portrait (9:16) frames, 64x36 for landscape ones.</summary>
    public static (int W, int H) SizeFor(int frameW, int frameH) => frameH >= frameW ? (Width, Height) : (Height, Width);

    /// <summary>One frame to its thumbnail: a whole standalone JPEG (full tables), quality 75.</summary>
    public static byte[] Thumb(Rgb frame) { var (w, h) = SizeFor(frame.W, frame.H); return Imaging.EncodeJpeg(BoxResize(frame, w, h), Quality); }

    /// <summary>Concatenates thumbnails into a chunk file and returns each one's byte range. Throws when the chunk file would reach Nexis's ghosting size.</summary>
    public static (byte[] Data, List<(int Off, int Len)> Rows) Pack(string chunk, IReadOnlyList<byte[]> jpegs)
    {
        long total = jpegs.Sum(j => (long)j.Length);
        var data = new byte[total]; var rows = new List<(int, int)>(jpegs.Count); int pos = 0;
        foreach (var j in jpegs) { Buffer.BlockCopy(j, 0, data, pos, j.Length); rows.Add((pos, j.Length)); pos += j.Length; }
        return (data, rows);
    }

    /// <summary>thumbs/NNNN.json: [[off, len], ...], one pair per row, in the row order of frames/NNNN.json.</summary>
    public static byte[] IndexJson(IReadOnlyList<(int Off, int Len)> rows) => JsonSerializer.SerializeToUtf8Bytes(rows.Select(r => new[] { r.Off, r.Len }));

    public static List<(int Off, int Len)> ParseIndex(byte[] json, string name)
    {
        var l = new List<(int, int)>();
        foreach (var e in StoredJson.Doc(json, name).RootElement.EnumerateArray()) l.Add((e[0].GetInt32(), e[1].GetInt32()));
        return l;
    }

    public static byte[] ManifestJson(int w, int h, int count, IReadOnlyList<(string Chunk, double Start, int Frames, long Bytes)> chunks, string decode = "full") => JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1, kind = "thumbs", container = "mjpeg", codec = "jpeg", filter = "box", decode = decode, quality = Quality, width = w, height = h, count, chunkSeconds = FrameChunks.ChunkSeconds,
        chunks = chunks.Select(c => new { chunk = c.Chunk, start = c.Start, frames = c.Frames, bytes = c.Bytes }),
    });

    /// <summary>Fallback without ffmpeg: full decode (BitMiracle) then box-resize, frames in parallel. (The classic API's scaled decode of this LibJpeg.NET port returns blank images, so there is no managed 1/8 route.)</summary>
    public static byte[] ThumbFull(byte[] jpg, int tw, int th) => Imaging.EncodeJpeg(BoxResize(Imaging.DecodeJpeg(jpg), tw, th), Quality);

    /// <summary>The fallback for a whole chunk file, frames in parallel (the chunk's rows say where each JPEG is).</summary>
    public static List<byte[]> ChunkManaged(byte[] data, ChunkInfo c, int tw, int th, int threads, string id)
    {
        var res = new byte[c.Frames.Count][];
        Parallel.For(0, c.Frames.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, k =>
        {
            var f = c.Frames[k];
            if (f.Off < 0 || f.Off + f.Len > data.Length) throw new InvalidOperationException($"{id}: frames/{c.Chunk}.json row {f.I} points outside frames/{c.Chunk}.mjpeg ({f.Off}+{f.Len} of {data.Length} bytes)");
            res[k] = ThumbFull(data.AsMemory((int)f.Off, f.Len).ToArray(), tw, th);
        });
        return res.ToList();
    }

    /// <summary>ffmpeg route: one process per chunk reads the concatenated JPEGs (-f mjpeg, -lowres 3 = 1/8 DCT-scaled decode), scales with flags=area and writes mjpeg; the output is split on SOI. Exactly one JPEG per input frame, else it throws.</summary>
    public static async Task<List<byte[]>> ChunkFfmpegAsync(string ffmpeg, byte[] data, ChunkInfo c, int tw, int th, int threads, string id, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in new[] { "-v", "error", "-nostdin", "-threads", Math.Max(1, threads).ToString(), "-lowres", "3", "-f", "mjpeg", "-i", "pipe:0", "-vf", $"scale={tw}:{th}:flags=area", "-fps_mode", "passthrough", "-c:v", "mjpeg", "-q:v", "3", "-f", "mjpeg", "pipe:1" }) psi.ArgumentList.Add(x);
        psi.ArgumentList.Remove("-nostdin");
        using var p = System.Diagnostics.Process.Start(psi)!;
        var errT = p.StandardError.ReadToEndAsync(ct);
        var outMs = new MemoryStream(); var outT = p.StandardOutput.BaseStream.CopyToAsync(outMs, ct);
        try { await p.StandardInput.BaseStream.WriteAsync(data, ct); } catch (IOException) { }   // ffmpeg quit early: its error is below
        try { p.StandardInput.Close(); } catch (IOException) { }
        await outT; await p.WaitForExitAsync(ct); var err = await errT;
        if (p.ExitCode != 0) throw new InvalidOperationException($"{id}: ffmpeg failed on frames/{c.Chunk}.mjpeg (exit {p.ExitCode}): {err.Trim()[..Math.Min(300, err.Trim().Length)]}");
        var all = outMs.ToArray(); var rows = FrameChunks.SplitJpegs(all);
        if (rows.Count != c.Frames.Count) throw new InvalidOperationException($"{id}: ffmpeg made {rows.Count} thumbnails for {c.Frames.Count} frames of frames/{c.Chunk}.mjpeg");
        return rows.Select(r => all.AsSpan(r.Off, r.Len).ToArray()).ToList();
    }

    /// <summary>The chunk's frames split into up to <paramref name="threads"/> runs of whole frames, one ffmpeg each at the same time; results joined in order.</summary>
    public static async Task<List<byte[]>> ChunkFfmpegParallelAsync(string ffmpeg, byte[] data, ChunkInfo c, int tw, int th, int threads, string id, CancellationToken ct)
    {
        int n = c.Frames.Count, parts = Math.Max(1, Math.Min(threads, n / 24));
        if (parts == 1) return await ChunkFfmpegAsync(ffmpeg, data, c, tw, th, 1, id, ct);
        foreach (var f in c.Frames) if (f.Off < 0 || f.Off + f.Len > data.Length) throw new InvalidOperationException($"{id}: frames/{c.Chunk}.json row {f.I} points outside frames/{c.Chunk}.mjpeg ({f.Off}+{f.Len} of {data.Length} bytes)");
        var tasks = Enumerable.Range(0, parts).Select(p =>
        {
            int from = n * p / parts, to = n * (p + 1) / parts; long o0 = c.Frames[from].Off, o1 = c.Frames[to - 1].Off + c.Frames[to - 1].Len;
            var sub = new ChunkInfo(c.Chunk, c.Start, c.Frames.GetRange(from, to - from));
            return ChunkFfmpegAsync(ffmpeg, data.AsMemory((int)o0, (int)(o1 - o0)).ToArray(), sub, tw, th, 1, id, ct);
        }).ToList();
        return (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();
    }

    /// <summary>Last: pack, store and describe one chunk.</summary>
    static async Task<(string, double, int, long)> StoreChunkAsync(IStore store, Paths paths, string id, ChunkInfo c, List<byte[]> jpegs, CancellationToken ct)
    {
        if (jpegs.Count != c.Frames.Count) throw new InvalidOperationException($"{id}: frames/{c.Chunk} has {c.Frames.Count} rows but {jpegs.Count} thumbnails were made");
        var (bytes, rows) = Pack(c.Chunk, jpegs);
        await Task.WhenAll(store.PutAsync(paths.Video(id, $"thumbs/{c.Chunk}.mjpeg"), bytes, null, null, false, ct, "image/jpeg"), store.PutAsync(paths.Video(id, $"thumbs/{c.Chunk}.json"), IndexJson(rows), null, null, false, ct));
        return (c.Chunk, c.Start, c.Frames.Count, bytes.Length);
    }

    /// <summary>Timings of a run (milliseconds, summed over chunks): downloading, decoding/encoding, uploading.</summary>
    public sealed class Timing { public long DownloadMs, ProcessMs, UploadMs; public int Frames; public override string ToString() => $"download {DownloadMs} ms, thumbnailing {ProcessMs} ms ({(Frames == 0 ? 0 : ProcessMs * 1000.0 / Frames):F0} us/frame), upload {UploadMs} ms"; }

    /// <summary>Standalone path: reads the stored frames/ chunks itself (hydrating ghosts) and pipelines: chunk n+1 downloads and chunk n-1 uploads while chunk n is processed.</summary>
    public static async Task<int> RunStoredAsync(Tools? tools, IStore store, Paths paths, string id, int threads, Action<string>? progress, CancellationToken ct, Timing? timing = null, bool managed = false, string? ffmpegPath = null)
    {
        var manName = paths.Video(id, "frames/manifest.json");
        var man = await store.GetAsync(manName, ct) ?? throw new InvalidOperationException($"{id} has no frames/ chunks yet (run the frames step)");
        var doc = StoredJson.Doc(man.Data, manName).RootElement;
        int w = doc.TryGetProperty("width", out var we) ? we.GetInt32() : 0, h = doc.TryGetProperty("height", out var he) ? he.GetInt32() : 0;
        var container = doc.TryGetProperty("container", out var co) ? co.GetString() ?? "mp4" : "mp4";
        var names = doc.GetProperty("chunks").EnumerateArray().Select(c => c.GetProperty("chunk").GetString()!).ToList();
        if (container != "mjpeg")
        {   // older AV1 frame chunks: the all-in-memory path through ffmpeg decoding
            var dir = Path.Combine(Path.GetTempPath(), "indexnode", "thumbs-" + Guid.NewGuid().ToString("N"));
            try { var fs = await FrameChunks.DownloadAsync(store, paths, id, "frames", dir, ct) ?? throw new InvalidOperationException($"{id} has no frames/ chunks"); return await RunAsync(tools!, store, paths, id, fs, progress, ct); }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        var (tw, th) = SizeFor(w, h); timing ??= new Timing();
        string? ffmpeg = managed ? null : ffmpegPath ?? (tools == null ? null : await tools.FfmpegAsync(ct));
        async Task<(ChunkInfo, byte[])> Fetch(string n)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var idx = await FrameChunks.ReadAsync(store, paths.Video(id, $"frames/{n}.json"), ct, false) ?? throw new InvalidOperationException($"{id}: frames/{n}.json is missing");
            var data = await FrameChunks.ReadAsync(store, paths.Video(id, $"frames/{n}.mjpeg"), ct) ?? throw new InvalidOperationException($"{id}: frames/{n}.mjpeg is missing");
            Interlocked.Add(ref timing.DownloadMs, sw.ElapsedMilliseconds);
            return (FrameChunks.ParseChunk(n, idx, paths.Video(id, $"frames/{n}.json")), data);
        }
        var chunks = new List<(string, double, int, long)>(); int done = 0, total = 0;
        Task<(ChunkInfo, byte[])>? next = names.Count > 0 ? Fetch(names[0]) : null; Task<(string, double, int, long)>? up = null;
        for (int i = 0; i < names.Count; i++)
        {
            var (c, data) = await next!;
            next = i + 1 < names.Count ? Fetch(names[i + 1]) : null;   // chunk n+1 downloads while n is processed
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var jpegs = ffmpeg != null ? await ChunkFfmpegParallelAsync(ffmpeg, data, c, tw, th, threads, id, ct) : ChunkManaged(data, c, tw, th, threads, id);
            timing.ProcessMs += sw.ElapsedMilliseconds; timing.Frames += c.Frames.Count;
            if (up != null) chunks.Add(await up);      // chunk n-1 finished uploading before n's upload starts
            var us = System.Diagnostics.Stopwatch.StartNew();
            up = StoreChunkAsync(store, paths, id, c, jpegs, ct).ContinueWith(t => { Interlocked.Add(ref timing.UploadMs, us.ElapsedMilliseconds); return t.GetAwaiter().GetResult(); }, TaskScheduler.Default);
            total += c.Frames.Count; progress?.Invoke($"{++done}/{names.Count} chunks");
        }
        if (up != null) chunks.Add(await up);
        await store.PutAsync(paths.Video(id, "thumbs/manifest.json"), ManifestJson(tw, th, total, chunks, ffmpeg != null ? "scaled-1/8" : "full"), null, null, false, ct);   // last: its presence means the step finished
        return total;
    }

    /// <summary>Builds and stores the thumbnails of every stored frame chunk (same chunks and rows as <paramref name="frames"/>). Returns the number of thumbnails.</summary>
    public static async Task<int> RunAsync(Tools tools, IStore store, Paths paths, string id, ChunkDir frames, Action<string>? progress, CancellationToken ct)
    {
        var (tw, th) = SizeFor(frames.W, frames.H); var chunks = new List<(string, double, int, long)>(); int done = 0, total = frames.Count;
        Dictionary<string, List<byte[]>>? fromVideo = null;
        if (frames.Container != "mjpeg")
        {   // an older job with AV1 frame chunks: decode them (ffmpeg), same row order
            fromVideo = frames.Chunks.ToDictionary(c => c.Chunk, _ => new List<byte[]>()); int row = 0;
            await frames.DecodeAsync(tools, (n, t, rgb) =>
            {
                int k = 0, acc = 0; while (k < frames.Chunks.Count && n >= acc + frames.Chunks[k].Frames.Count) acc += frames.Chunks[k++].Frames.Count;
                fromVideo[frames.Chunks[k].Chunk].Add(Thumb(rgb)); row++; return Task.CompletedTask;
            }, ct);
        }
        foreach (var c in frames.Chunks)
        {
            List<byte[]> jpegs;
            if (fromVideo != null) jpegs = fromVideo[c.Chunk];
            else
            {
                var data = await File.ReadAllBytesAsync(frames.ChunkFile(c), ct);
                jpegs = new List<byte[]>(c.Frames.Count);
                foreach (var f in c.Frames)
                {
                    if (f.Off < 0 || f.Off + f.Len > data.Length) throw new InvalidOperationException($"{id}: frames/{c.Chunk}.json row {f.I} points outside frames/{c.Chunk}.mjpeg ({f.Off}+{f.Len} of {data.Length} bytes)");
                    jpegs.Add(Thumb(Imaging.DecodeJpeg(data.AsMemory((int)f.Off, f.Len).ToArray())));
                    if (++done % 500 == 0) progress?.Invoke($"{done}/{total}");
                }
            }
            if (jpegs.Count != c.Frames.Count) throw new InvalidOperationException($"{id}: frames/{c.Chunk} has {c.Frames.Count} rows but {jpegs.Count} thumbnails were made");
            var (bytes, rows) = Pack(c.Chunk, jpegs);
            await store.PutAsync(paths.Video(id, $"thumbs/{c.Chunk}.mjpeg"), bytes, null, null, false, ct, "image/jpeg");
            await store.PutAsync(paths.Video(id, $"thumbs/{c.Chunk}.json"), IndexJson(rows), null, null, false, ct);
            chunks.Add((c.Chunk, c.Start, c.Frames.Count, bytes.Length));
        }
        await store.PutAsync(paths.Video(id, "thumbs/manifest.json"), ManifestJson(tw, th, frames.Count, chunks), null, null, false, ct);   // last: its presence means the step finished
        return frames.Count;
    }
}
