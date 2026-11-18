using System.Text.Json;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace IndexNode.Tests;

public class DinoTests
{
    static string? Ffmpeg() => new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" }.FirstOrDefault(File.Exists);

    static Rgb Frame(int w, int h, int seed)
    {
        var d = new byte[w * h * 3];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int o = (y * w + x) * 3; d[o] = (byte)((x * 255 / w + seed * 23) % 256); d[o + 1] = (byte)(y * 255 / h); d[o + 2] = (byte)(60 + seed * 11 % 150); }
        return new Rgb(d, w, h);
    }

    /// <summary>A stand-in for the model: row i = (mean of channel 0, 1, 2 of frame i, tensor height, tensor width, ...), so a test sees what reached the model.</summary>
    static Dino.Embed Fake(List<(int H, int W)> shapes) => t =>
    {
        int n = (int)t.Dimensions[0], h = (int)t.Dimensions[2], w = (int)t.Dimensions[3]; shapes.Add((h, w)); var rows = new float[n][];
        for (int i = 0; i < n; i++)
        {
            var r = new float[Dino.Dim]; for (int d = 0; d < Dino.Dim; d++) r[d] = 1f + d % 7;
            for (int c = 0; c < 3; c++) { double s = 0; for (int p = 0; p < h * w; p++) s += t.Buffer.Span[(i * 3 + c) * h * w + p]; r[c] = (float)(s / (h * w)) * 10; }
            r[3] = h; r[4] = w; rows[i] = r;
        }
        return rows;
    };

    static async Task<(FakeStore, Paths, List<(string, double, int, long)>)> Store(int w, int h, params (string n, int cnt, double st)[] chunks)
    {
        var store = new FakeStore(); var paths = new Paths("index/"); int seed = 0; var meta = new List<(string, double, int, long)>();
        foreach (var (nme, cnt, st) in chunks)
        {
            var ms = new MemoryStream(); var frames = new List<ChunkFrame>();
            for (int k = 0; k < cnt; k++) { var j = Imaging.EncodeJpeg(Frame(w, h, seed++), 85); frames.Add(new ChunkFrame(k, st + k, k, [], null, ms.Length, j.Length)); ms.Write(j); }
            await store.PutAsync(paths.Video("vid", $"frames/{nme}.mjpeg"), ms.ToArray(), null, null, false, default);
            await store.PutAsync(paths.Video("vid", $"frames/{nme}.json"), FrameChunks.JpegIndexJson(nme, st, w, h, 85, frames), null, null, false, default);
            meta.Add((nme, st, cnt, ms.Length));
        }
        await store.PutAsync(paths.Video("vid", "frames/manifest.json"), FrameChunks.ManifestJson("frames", w, h, 0, meta.Sum(m => m.Item3), meta, null, "mjpeg", "jpeg"), null, null, false, default);
        return (store, paths, meta);
    }

    [Fact]
    public void Portrait_frames_get_224_wide_by_392_tall_and_landscape_the_other_way()
    {
        Assert.Equal((224, 392), Dino.InputSize(360, 640)); Assert.Equal((392, 224), Dino.InputSize(640, 360));
        Assert.Equal((224, 392), Dino.InputSize(500, 500));   // square counts as portrait, as the thumbs step does
    }

    [Fact]
    public void The_tensor_is_scaled_to_0_1_then_imagenet_mean_and_std_per_channel()
    {
        var rgb = new byte[2 * 2 * 3]; for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = 255; rgb[i + 1] = 0; rgb[i + 2] = 128; }
        var t = Dino.ToTensor(rgb, 1, 2, 2);
        Assert.Equal([1, 3, 2, 2], t.Dimensions.ToArray());
        Assert.Equal((1f - 0.485f) / 0.229f, t[0, 0, 1, 1], 4); Assert.Equal((0f - 0.456f) / 0.224f, t[0, 1, 0, 0], 4); Assert.Equal((128 / 255f - 0.406f) / 0.225f, t[0, 2, 0, 1], 4);
    }

    [Fact]
    public async Task One_normalised_row_per_frame_per_chunk_and_the_manifest_is_written_last()
    {
        var ff = Ffmpeg(); if (ff == null) return;
        var (store, paths, meta) = await Store(360, 640, ("0000", 70, 0.0), ("0001", 5, 600.0));
        var shapes = new List<(int, int)>(); var tm = new Dino.Timing();
        Assert.Equal(75, await Dino.RunStoredAsync(ff, Fake(shapes), "dino-small", "abc", store, paths, "vid", 2, null, default, tm, 16));
        Assert.All(shapes, s => Assert.Equal((392, 224), s));
        var man = JsonDocument.Parse((await store.GetAsync(paths.Video("vid", "dino/manifest.json"), default))!.Data).RootElement;
        Assert.Equal(384, man.GetProperty("dim").GetInt32()); Assert.Equal(75, man.GetProperty("frames").GetInt32()); Assert.Equal("float16", man.GetProperty("dtype").GetString());
        Assert.Equal("abc", man.GetProperty("info").GetProperty("sha256").GetString()); Assert.Equal(224, man.GetProperty("info").GetProperty("inputSize").GetProperty("width").GetInt32());
        Assert.Equal(["0000", "0001"], man.GetProperty("chunks").EnumerateArray().Select(c => c.GetProperty("chunk").GetString()).ToArray());
        foreach (var (n, _, cnt, _) in meta)
        {
            var f16 = (await store.GetAsync(paths.Video("vid", $"dino/{n}.f16"), default))!.Data;
            Assert.Equal(cnt * Dino.Dim * 2, f16.Length);   // one row per frame
            for (int i = 0; i < cnt; i++)
            {
                double sq = 0; for (int d = 0; d < Dino.Dim; d++) { double v = (double)BitConverter.ToHalf(f16, (i * Dino.Dim + d) * 2); sq += v * v; }
                Assert.InRange(Math.Sqrt(sq), 0.998, 1.002);   // L2-normalised
            }
        }
        Assert.Equal(75, tm.Frames);
    }

    [Fact]
    public async Task Rows_stay_in_frame_order_across_parallel_slices()
    {
        var ff = Ffmpeg(); if (ff == null) return;
        var (store, paths, _) = await Store(360, 640, ("0000", 40, 0.0));
        await Dino.RunStoredAsync(ff, Fake([]), "m", "s", store, paths, "vid", 4, null, default, null, 8);   // 5 slices of 8 decoded at once
        var f16 = (await store.GetAsync(paths.Video("vid", "dino/0000.f16"), default))!.Data;
        var one = new Dino.Timing(); var (s2, p2, _) = await Store(360, 640, ("0000", 40, 0.0));
        await Dino.RunStoredAsync(ff, Fake([]), "m", "s", s2, p2, "vid", 1, null, default, one, 40);   // one slice
        var g = (await s2.GetAsync(p2.Video("vid", "dino/0000.f16"), default))!.Data;
        for (int i = 0; i < 40; i++) Assert.Equal((double)BitConverter.ToHalf(g, i * Dino.Dim * 2), (double)BitConverter.ToHalf(f16, i * Dino.Dim * 2), 3);
    }

    [Fact]
    public async Task A_landscape_frame_gets_392_wide_by_224_tall()
    {
        var ff = Ffmpeg(); if (ff == null) return;
        var (store, paths, _) = await Store(640, 360, ("0000", 6, 0.0)); var shapes = new List<(int, int)>();
        await Dino.RunStoredAsync(ff, Fake(shapes), "m", "s", store, paths, "vid", 1, null, default);
        Assert.All(shapes, s => Assert.Equal((224, 392), s));   // tensor [n,3,H=224,W=392]
        var man = JsonDocument.Parse((await store.GetAsync(paths.Video("vid", "dino/manifest.json"), default))!.Data).RootElement;
        Assert.Equal(392, man.GetProperty("info").GetProperty("inputSize").GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task A_decode_that_loses_a_frame_fails_and_no_manifest_is_written()
    {
        var ff = Ffmpeg(); if (ff == null) return;
        var (store, paths, _) = await Store(360, 640, ("0000", 10, 0.0));
        var idx = FrameChunks.ParseChunk("0000", (await store.GetAsync(paths.Video("vid", "frames/0000.json"), default))!.Data, "i");
        var data = (await store.GetAsync(paths.Video("vid", "frames/0000.mjpeg"), default))!.Data;
        var bad = idx with { Frames = idx.Frames.Take(4).Concat([idx.Frames[4] with { Len = 0 }]).ToList() };   // a row whose range holds no JPEG: 4 frames of 5
        await Assert.ThrowsAsync<InvalidOperationException>(() => Dino.ChunkAsync(ff, data, bad, 224, 392, 8, 1, Fake([]), "vid", new Dino.Timing(), default));
        Assert.Null(await store.GetAsync(paths.Video("vid", "dino/manifest.json"), default));
    }

    [Fact]
    public void The_step_is_known_opt_in_and_owns_dino_files()
    {
        Assert.Contains("dino", ProcessStage.KnownSteps); Assert.DoesNotContain("dino", Protocol.DefaultSteps); Assert.DoesNotContain("dino", ProcessStage.FullSteps);
        Assert.Contains("dino", Protocol.StepStages); Assert.Equal(["dino/"], ProcessStage.Owned["dino"]);
        Assert.Equal(["frames.zip"], Protocol.NeedsOf("dino"));
    }

    [Fact]
    public void Ingest_never_waits_for_an_opt_in_step_such_as_dino_or_clip()
    {
        // step-job mode decides "the last step" (and writes processed.json) from these outputs: an opt-in step in the list would keep a normally processed upload unfinished
        var steps = ProcessStage.Outputs.Select(o => o.Step).ToArray();
        Assert.DoesNotContain("dino", steps); Assert.DoesNotContain("clip", steps);
        Assert.All(steps, s => Assert.True(ProcessStage.FullSteps.Contains(s) || s == "sync", s));
        Assert.All(ProcessStage.FullSteps, s => Assert.Contains(s, steps));
        Assert.Equal(["frames", "sscd", "thumbs", "transcribe", "ocrsubs", "cast", "sync", "describe", "translate"], Protocol.IngestStepStages);
        Assert.DoesNotContain("dino", Protocol.DefaultSteps); Assert.DoesNotContain("dino", ProcessStage.FullSteps);
        Assert.DoesNotContain("dino", JobDescriptor.NewProcess("x").Steps!);
    }
}
