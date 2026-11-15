using System.Text.Json;
using Xunit;

namespace IndexNode.Tests;

public class ThumbsTests
{
    /// <summary>A frame with some structure (gradient plus a bright block) so the thumbnail is not a flat colour.</summary>
    static Rgb Frame(int w, int h, int seed)
    {
        var d = new byte[w * h * 3];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int o = (y * w + x) * 3; bool box = x > w / 3 && x < w / 2 && y > h / 3 && y < h / 2; d[o] = (byte)(box ? 250 : (x * 255 / w + seed * 17) % 256); d[o + 1] = (byte)(y * 255 / h); d[o + 2] = (byte)(box ? 30 : 120); }
        return new Rgb(d, w, h);
    }

    [Fact]
    public void A_thumbnail_is_36x64_and_decodes_on_its_own_with_its_own_tables()
    {
        var t = Thumbs.Thumb(Frame(360, 640, 1));
        var back = Imaging.DecodeJpeg(t);
        Assert.Equal((36, 64), (back.W, back.H));
        Assert.True(t.Length is > 300 and < 3000, "size " + t.Length);
        Assert.Equal((byte)0xFF, t[0]); Assert.Equal((byte)0xD8, t[1]);
        // full tables in each: a DQT (FF DB) and a DHT (FF C4) segment before the scan
        Assert.True(Find(t, 0xFF, 0xDB) > 0); Assert.True(Find(t, 0xFF, 0xC4) > 0);
        var land = Imaging.DecodeJpeg(Thumbs.Thumb(Frame(640, 360, 1))); Assert.Equal((64, 36), (land.W, land.H));
    }

    static int Find(byte[] d, byte a, byte b) { for (int i = 0; i + 1 < d.Length; i++) if (d[i] == a && d[i + 1] == b) return i; return -1; }

    [Fact]
    public void Box_resize_averages_the_source_area()
    {
        var im = new Rgb([0, 0, 0, 100, 100, 100, 200, 200, 200, 40, 40, 40], 2, 2);   // 2x2 -> 1x1: the mean
        var o = Thumbs.BoxResize(im, 1, 1);
        Assert.Equal(85, o.Data[0]);
        var flat = Thumbs.BoxResize(Rgb.Blank(360, 640, 77), 36, 64); Assert.All(flat.Data, b => Assert.Equal(77, b));
    }

    [Fact]
    public void Offsets_and_lengths_round_trip_through_the_index_json()
    {
        var jpegs = Enumerable.Range(0, 5).Select(i => Thumbs.Thumb(Frame(360, 640, i))).ToList();
        var (data, rows) = Thumbs.Pack("0000", jpegs);
        Assert.Equal(jpegs.Sum(j => j.Length), data.Length);
        var parsed = Thumbs.ParseIndex(Thumbs.IndexJson(rows), "thumbs/0000.json");
        Assert.Equal(rows, parsed);
        Assert.Equal(5, parsed.Count);
        for (int i = 0; i < 5; i++) Assert.Equal(jpegs[i], data.AsSpan(parsed[i].Off, parsed[i].Len).ToArray());
        // the JSON is plain [[off,len],...]
        var doc = JsonDocument.Parse(Thumbs.IndexJson(rows)).RootElement; Assert.Equal(2, doc[0].GetArrayLength());
        // and the whole file splits back into the same ranges at each SOI
        Assert.Equal(rows, FrameChunks.SplitJpegs(data));
    }

    [Fact]
    public void A_chunk_over_the_ghosting_size_is_refused_with_a_clear_error()
    {
        var big = new byte[Thumbs.MaxChunkBytes / 2 + 1];
        var e = Assert.Throws<InvalidOperationException>(() => Thumbs.Pack("0003", [big, big]));
        Assert.Contains("thumbs/0003.mjpeg", e.Message); Assert.Contains("Nexis ghosts", e.Message);
        var ok = new byte[Thumbs.MaxChunkBytes / 2 - 1]; Thumbs.Pack("0003", [ok, ok]);   // just under: fine
    }

    [Fact]
    public async Task The_stage_writes_the_same_chunks_and_rows_as_frames_and_each_row_decodes()
    {
        // two mjpeg frame chunks of 3 and 2 frames, as the frames step stores them
        var dir = Path.Combine(Path.GetTempPath(), "thumbs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var chunks = new[] { (n: "0000", count: 3, start: 0.0), (n: "0001", count: 2, start: 600.0) }; int seed = 0; var chunkMeta = new List<object>();
            foreach (var c in chunks)
            {
                var ms = new MemoryStream(); var frames = new List<ChunkFrame>();
                for (int k = 0; k < c.count; k++) { var j = Imaging.EncodeJpeg(Frame(360, 640, seed++), 85); frames.Add(new ChunkFrame(k, c.start + k, k, [], null, ms.Length, j.Length)); ms.Write(j); }
                File.WriteAllBytes(Path.Combine(dir, c.n + ".mjpeg"), ms.ToArray());
                File.WriteAllBytes(Path.Combine(dir, c.n + ".json"), FrameChunks.JpegIndexJson(c.n, c.start, 360, 640, 85, frames));
                chunkMeta.Add((c.n, c.start, c.count, ms.Length));
            }
            File.WriteAllBytes(Path.Combine(dir, "manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 0, 5, chunks.Select(c => (c.n, c.start, c.count, 0L)).ToList(), null, "mjpeg", "jpeg"));
            var fs = ChunkDir.Open(dir);
            var store = new FakeStore(); var paths = new Paths("index/");
            Assert.Equal(5, await Thumbs.RunAsync(null!, store, paths, "vid", fs, null, default));
            var man = JsonDocument.Parse((await store.GetAsync(paths.Video("vid", "thumbs/manifest.json"), default))!.Data).RootElement;
            Assert.Equal(36, man.GetProperty("width").GetInt32()); Assert.Equal(64, man.GetProperty("height").GetInt32()); Assert.Equal(75, man.GetProperty("quality").GetInt32()); Assert.Equal(5, man.GetProperty("count").GetInt32());
            Assert.Equal(["0000", "0001"], man.GetProperty("chunks").EnumerateArray().Select(c => c.GetProperty("chunk").GetString()).ToArray());
            foreach (var c in fs.Chunks)
            {
                var rows = Thumbs.ParseIndex((await store.GetAsync(paths.Video("vid", $"thumbs/{c.Chunk}.json"), default))!.Data, "idx");
                Assert.Equal(c.Frames.Count, rows.Count);   // one row per frame, same order
                var data = (await store.GetAsync(paths.Video("vid", $"thumbs/{c.Chunk}.mjpeg"), default))!.Data;
                Assert.True(data.Length < Thumbs.MaxChunkBytes);
                foreach (var (off, len) in rows) { var im = Imaging.DecodeJpeg(data.AsSpan(off, len).ToArray()); Assert.Equal((36, 64), (im.W, im.H)); }
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
