using System.Text.Json;
using Xunit;

namespace IndexNode.Tests;

public class FrameChunkTests
{
    [Fact]
    public void The_chunk_index_carries_pts_and_spans_and_reads_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        try
        {
            var frames = new List<ChunkFrame> { new(0, 0, 0, [[0, 1], [5, 7]]), new(1, 3, 1, [[3, 4]], 0) };
            File.WriteAllBytes(Path.Combine(dir, "0000.json"), FrameChunks.IndexJson("0000", 0, 360, 640, 22, 10, frames, true));
            File.WriteAllBytes(Path.Combine(dir, "manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 22, 2, [("0000", 0, 2, 100)]));
            var set = ChunkDir.Open(dir);
            Assert.Equal((360, 640), (set.W, set.H)); Assert.Equal([0.0, 3.0], set.Times);
            Assert.Equal([[0.0, 1.0], [5.0, 7.0]], set.Chunks[0].Frames[0].Spans);
            Assert.Equal(0, set.Chunks[0].Frames[1].Rep);
            var man = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "manifest.json"))).RootElement;
            Assert.Equal("av1", man.GetProperty("codec").GetString()); Assert.Equal(22, man.GetProperty("crf").GetInt32());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_stored_size_has_a_360_short_side_and_an_even_long_side()
    {
        Assert.Equal((360, 640), Frames.StoredSize(480, 854)); Assert.Equal((640, 360), Frames.StoredSize(1920, 1080)); Assert.Equal((360, 640), Frames.StoredSize(1080, 1920));
    }

    [Fact]
    public async Task Vector_chunks_match_the_frame_chunks_and_join_back()
    {
        var s = new FakeStore(); var P = new Paths("");
        var dir = Path.Combine(Path.GetTempPath(), "fv-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        try
        {
            var chunks = new List<(string, double, int, long)>();
            foreach (var (n, ts) in new[] { ("0000", new[] { 0.0, 1.0 }), ("0001", new[] { 700.0 }) })
            {
                var fr = ts.Select((t, i) => new ChunkFrame(i, t, i, [[t, t + 1]])).ToList();
                await s.PutAsync(P.Video("v", $"frames/{n}.json"), FrameChunks.IndexJson(n, n == "0000" ? 0 : 600, 8, 8, 22, 10, fr, true), null, null, false, default);
                await s.PutAsync(P.Video("v", $"frames/{n}.mp4"), [1, 2, 3], null, null, false, default);
                chunks.Add((n, n == "0000" ? 0 : 600, ts.Length, 3));
            }
            await s.PutAsync(P.Video("v", "frames/manifest.json"), FrameChunks.ManifestJson("frames", 8, 8, 22, 3, chunks), null, null, false, default);
            var set = await FrameChunks.DownloadAsync(s, P, "v", "frames", Path.Combine(dir, "dl"), default);
            Assert.NotNull(set);
            var rows = new byte[3 * 4 * 2]; for (int i = 0; i < rows.Length; i++) rows[i] = (byte)i;
            await FrameChunks.PutVectorsAsync(s, P, "v", "sscd", set!, rows, 4, new { model = "m" }, default);
            Assert.Equal(16, (await s.GetAsync(P.Video("v", "sscd/0000.f16"), default))!.Data.Length);
            Assert.Equal(8, (await s.GetAsync(P.Video("v", "sscd/0001.f16"), default))!.Data.Length);
            var job = new StoreJob(s, P, "v");
            Assert.Equal(rows, await FrameChunks.VectorsAsync(job, "sscd", default));
            Assert.Equal([0.0, 1.0, 700.0], await FrameChunks.TimesAsync(job, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => FrameChunks.PutVectorsAsync(s, P, "v", "clip", set!, rows, 8, new { }, default));
        }
        finally { Directory.Delete(dir, true); }
    }
}
