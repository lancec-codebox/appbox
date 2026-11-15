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
    /// <summary>Nexis ghosts blobs over 2 MB: a thumbs chunk file must stay under it, so it is never ghosted.</summary>
    public const int MaxChunkBytes = 2_000_000;

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
        if (total >= MaxChunkBytes) throw new InvalidOperationException($"thumbs/{chunk}.mjpeg would be {total} bytes for {jpegs.Count} thumbnails, over the {MaxChunkBytes} bytes at which Nexis ghosts a blob; refusing to write it");
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

    public static byte[] ManifestJson(int w, int h, int count, IReadOnlyList<(string Chunk, double Start, int Frames, long Bytes)> chunks) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        v = 1, kind = "thumbs", container = "mjpeg", codec = "jpeg", filter = "box", quality = Quality, width = w, height = h, count, chunkSeconds = FrameChunks.ChunkSeconds,
        chunks = chunks.Select(c => new { chunk = c.Chunk, start = c.Start, frames = c.Frames, bytes = c.Bytes }),
    });

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
