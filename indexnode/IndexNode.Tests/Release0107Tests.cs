using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.10.7: int8 models must not depend on the CPU; no empty frames chunk.</summary>
public class Release0107Tests
{
    // ---- 1. session.x64quantprecision

    [Fact]
    public void Every_session_option_set_carries_x64quantprecision_1()
    {
        Assert.Contains(("session.x64quantprecision", "1"), OrtOptions.ConfigEntries);
        Assert.Contains(("session.intra_op.allow_spinning", "0"), OrtOptions.ConfigEntries);
        // Make applies the whole list on the CPU path (and before the CUDA branch, so the GPU path gets it too); the entry is accepted by this ONNX Runtime
        using var so = OrtOptions.Make(2, true, "cpu");
        using var so2 = OrtOptions.Make(1, false);
        Assert.Throws<InvalidOperationException>(() => OrtOptions.Make(1, true, "cuda").Dispose()); // CUDA not available here: refused, never falls back (entry order is not the cause)
    }

    // minimal protobuf writer for a one-node ONNX model: Y(int32 [M,N]) = MatMulInteger(A(uint8 [M,K]), B(int8 [K,N] initializer))
    static void Varint(MemoryStream m, ulong v) { while (v >= 0x80) { m.WriteByte((byte)(v | 0x80)); v >>= 7; } m.WriteByte((byte)v); }
    static void Tag(MemoryStream m, int field, int wire) => Varint(m, (ulong)((field << 3) | wire));
    static void Int(MemoryStream m, int field, long v) { Tag(m, field, 0); Varint(m, (ulong)v); }
    static void Bytes(MemoryStream m, int field, byte[] b) { Tag(m, field, 2); Varint(m, (ulong)b.Length); m.Write(b); }
    static void Str(MemoryStream m, int field, string s) => Bytes(m, field, Encoding.UTF8.GetBytes(s));
    static byte[] Msg(Action<MemoryStream> f) { var m = new MemoryStream(); f(m); return m.ToArray(); }
    static byte[] ValueInfo(string name, int elem, int[] dims) => Msg(m => { Str(m, 1, name); Bytes(m, 2, Msg(t => Bytes(t, 1, Msg(tt => { Int(tt, 1, elem); Bytes(tt, 2, Msg(sh => { foreach (var d in dims) Bytes(sh, 1, Msg(dm => Int(dm, 1, d))); })); })))); });

    static byte[] MatMulIntegerModel(int M, int K, int N, sbyte bValue, bool withZp = true)
    {
        var b = new byte[K * N]; Array.Fill(b, (byte)bValue);
        var tensor = Msg(t => { Int(t, 1, K); Int(t, 1, N); Int(t, 2, 3 /*INT8*/); Str(t, 8, "B"); Bytes(t, 9, b); });
        var zp = Msg(t => { Int(t, 2, 3 /*INT8*/); Str(t, 8, "Bzp"); Bytes(t, 9, [0]); });   // scalar int8 zero point 0, as a QDQ model has (per-channel quantised weights always carry one)
        var node = Msg(n => { Str(n, 1, "A"); Str(n, 1, "B"); if (withZp) { Str(n, 1, ""); Str(n, 1, "Bzp"); } Str(n, 2, "Y"); Str(n, 3, "mm"); Str(n, 4, "MatMulInteger"); });
        var graph = Msg(g => { Bytes(g, 1, node); Str(g, 2, "g"); Bytes(g, 5, tensor); if (withZp) Bytes(g, 5, zp); Bytes(g, 11, ValueInfo("A", 2 /*UINT8*/, [M, K])); Bytes(g, 12, ValueInfo("Y", 6 /*INT32*/, [M, N])); });
        return Msg(m => { Int(m, 1, 8); Bytes(m, 8, Msg(o => { Str(o, 1, ""); Int(o, 2, 13); })); Bytes(m, 7, graph); });
    }

    [Fact]
    public void An_int8_u8s8_MatMul_whose_pair_sums_exceed_16_bits_gives_the_exact_integer_result_on_every_CPU()
    {
        // A = 255 everywhere, B = 127: each pair of u8*s8 products is 64770, which a saturating 16-bit AVX2 kernel (u8s8 without VNNI) clips to 32767.
        // With session.x64quantprecision=1 ORT takes the non-saturating path, so the result is the exact sum on any CPU (on VNNI/AVX512 CPUs it always was).
        const int M = 8, K = 64, N = 32;
        var model = MatMulIntegerModel(M, K, N, 127);
        var a = new byte[M * K]; Array.Fill(a, (byte)255);
        using var so = OrtOptions.Make(1, true, "cpu");
        using var s = new InferenceSession(model, so);
        using var r = s.Run([NamedOnnxValue.CreateFromTensor("A", new DenseTensor<byte>(a, [M, K]))]);
        var y = r.First().AsTensor<int>().ToArray();
        Assert.Equal(M * N, y.Length);
        int expected = K * 255 * 127;   // 2,072,640 (the saturated result would be K/2 * 32767)
        Assert.All(y, v => Assert.Equal(expected, v));
    }

    static byte[] ConvIntegerModel(int C, int O, int H, int W, int k, sbyte wv, bool zp)
    {
        var w = new byte[O * C * k * k]; Array.Fill(w, (byte)wv);
        var wt = Msg(t => { Int(t, 1, O); Int(t, 1, C); Int(t, 1, k); Int(t, 1, k); Int(t, 2, 3); Str(t, 8, "Wt"); Bytes(t, 9, w); });
        var z = Msg(t => { Int(t, 2, 3); Str(t, 8, "Wzp"); Bytes(t, 9, [0]); });
        int oh = H - k + 1, ow = W - k + 1;
        var node = Msg(n => { Str(n, 1, "X"); Str(n, 1, "Wt"); if (zp) { Str(n, 1, ""); Str(n, 1, "Wzp"); } Str(n, 2, "Y"); Str(n, 3, "cv"); Str(n, 4, "ConvInteger"); });
        var graph = Msg(g => { Bytes(g, 1, node); Str(g, 2, "g"); Bytes(g, 5, wt); if (zp) Bytes(g, 5, z); Bytes(g, 11, ValueInfo("X", 2, [1, C, H, W])); Bytes(g, 12, ValueInfo("Y", 6, [1, O, oh, ow])); });
        return Msg(m => { Int(m, 1, 8); Bytes(m, 8, Msg(o => { Str(o, 1, ""); Int(o, 2, 13); })); Bytes(m, 7, graph); });
    }


    [Fact]
    public void An_int8_ConvInteger_with_saturating_pair_sums_is_exact_too()
    {
        const int C = 64, O = 8;
        var a = new byte[C * 64]; Array.Fill(a, (byte)255);
        using var so = OrtOptions.Make(1, true, "cpu");
        using var s = new InferenceSession(ConvIntegerModel(C, O, 8, 8, 3, 127, true), so);
        using var r = s.Run([NamedOnnxValue.CreateFromTensor("X", new DenseTensor<byte>(a, [1, C, 8, 8]))]);
        Assert.All(r.First().AsTensor<int>().ToArray(), v => Assert.Equal(C * 9 * 255 * 127, v));
    }

    // ---- 2. no empty frames chunk

    static string? Real(string name)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = Path.Combine(d, OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    [Fact]
    public async Task The_frames_step_never_writes_a_chunk_with_no_frames()
    {
        var ff = Real("ffmpeg"); var pr = Real("ffprobe"); Assert.True(ff != null && pr != null, "ffmpeg and ffprobe are required for this test");
        var dir = Path.Combine(Path.GetTempPath(), "frames-empty-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var video = Path.Combine(dir, "v.mp4");
            var r = await Proc.RunAsync(ff!, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=160x90:r=5,format=yuv420p", "-t", "12", "-c:v", "mpeg4", "-q:v", "3", video], default);
            Assert.True(r.ExitCode == 0, r.Stderr);
            var tools = new Tools(new HttpClient()); tools.UseFfmpeg(ff!, pr!);
            var put = new Dictionary<string, byte[]>(); var work = Path.Combine(dir, "work"); Directory.CreateDirectory(work);
            // the stated duration runs past the content (as a video of exactly N x 600 s did): the chunk after the last frame is empty and must not be stored
            await Frames.ProduceAsync(tools, video, work, new FrameSettings(), 1, 1200, _ => { }, (n, b, _) => { lock (put) put[n] = b; return Task.CompletedTask; }, default);
            var man = System.Text.Json.JsonDocument.Parse(put["frames/manifest.json"]).RootElement;
            var chunks = man.GetProperty("chunks").EnumerateArray().ToList();
            Assert.Single(chunks); Assert.True(chunks[0].GetProperty("frames").GetInt32() > 0);
            Assert.DoesNotContain(put.Keys, k => k.StartsWith("frames/0001"));
            Assert.All(put.Where(p => p.Key.EndsWith(".mjpeg")), p => Assert.NotEmpty(p.Value));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static async Task PutChunk(FakeStore s, Paths p, string n, double start, int count, List<(string, double, int, long)> meta)
    {
        var ms = new MemoryStream(); var frames = new List<ChunkFrame>();
        for (int k = 0; k < count; k++)
        {
            var px = new byte[360 * 640 * 3]; Array.Fill(px, (byte)(40 + k * 30));
            var j = Imaging.EncodeJpeg(new Rgb(px, 360, 640), 85); frames.Add(new ChunkFrame(k, start + k, k, [], null, ms.Length, j.Length)); ms.Write(j);
        }
        await s.PutAsync(p.Video("vid", $"frames/{n}.mjpeg"), ms.ToArray(), null, null, false, default);
        await s.PutAsync(p.Video("vid", $"frames/{n}.json"), FrameChunks.JpegIndexJson(n, start, 360, 640, 85, frames), null, null, false, default);
        meta.Add((n, start, count, ms.Length));
    }

    [Fact]
    public async Task Thumbs_skips_a_stored_0_frame_chunk_and_writes_nothing_for_it()
    {
        var s = new FakeStore(); var p = new Paths("index/"); var meta = new List<(string, double, int, long)>();
        await PutChunk(s, p, "0000", 0, 3, meta); await PutChunk(s, p, "0001", 600, 0, meta);   // 0001: an index with no frames and a 0-byte-style chunk, as old zips hold
        await s.PutAsync(p.Video("vid", "frames/manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 0, 3, meta, null, "mjpeg", "jpeg"), null, null, false, default);
        Assert.Equal(3, await Thumbs.RunStoredAsync(null, s, p, "vid", 1, null, default));
        var man = System.Text.Json.JsonDocument.Parse((await s.GetAsync(p.Video("vid", "thumbs/manifest.json"), default))!.Data).RootElement;
        Assert.Equal(3, man.GetProperty("count").GetInt32()); Assert.Single(man.GetProperty("chunks").EnumerateArray());
        Assert.Null(await s.GetAsync(p.Video("vid", "thumbs/0001.mjpeg"), default));
        // the ffmpeg route too (an empty chunk never reaches ffmpeg)
        var ff = Real("ffmpeg"); Assert.NotNull(ff);
        var s2 = new FakeStore(); foreach (var b in s.Blobs) if (b.Key.Contains("/frames/")) await s2.PutAsync(b.Key, b.Value.Data, null, null, false, default);
        Assert.Equal(3, await Thumbs.RunStoredAsync(new Tools(new HttpClient()), s2, p, "vid", 1, null, default, null, false, ff));
        Assert.Null(await s2.GetAsync(p.Video("vid", "thumbs/0001.mjpeg"), default));
    }

    [Fact]
    public async Task ChunkDir_and_the_other_chunk_readers_skip_0_frame_chunks()
    {
        var s = new FakeStore(); var p = new Paths("index/"); var meta = new List<(string, double, int, long)>();
        await PutChunk(s, p, "0000", 0, 2, meta); await PutChunk(s, p, "0001", 600, 0, meta);
        await s.PutAsync(p.Video("vid", "frames/manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 0, 2, meta, null, "mjpeg", "jpeg"), null, null, false, default);
        var dir = Path.Combine(Path.GetTempPath(), "cd-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cd = (await FrameChunks.DownloadAsync(s, p, "vid", "frames", dir, default))!;
            Assert.Equal(["0000"], cd.Chunks.Select(c => c.Chunk).ToArray()); Assert.Equal(2, cd.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task The_ffmpeg_no_stream_failure_is_permanent_not_transient()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fakeff-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var fake = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.cmd" : "ffmpeg");
            if (OperatingSystem.IsWindows()) File.WriteAllText(fake, "@echo off\r\necho Output file does not contain any stream 1>&2\r\nexit /b 234\r\n");
            else { File.WriteAllText(fake, "#!/bin/sh\necho 'Output file does not contain any stream' >&2\nexit 234\n"); File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            var c = new ChunkInfo("0017", 10200, [new ChunkFrame(0, 10200, 0, [], null, 0, 3)]);
            var e = await Assert.ThrowsAsync<PermanentFailure>(() => Thumbs.ChunkFfmpegAsync(fake, [0xFF, 0xD8, 0xFF], c, 36, 64, 1, "vid", default));
            Assert.Contains("0017", e.Message);
            // another ffmpeg failure stays transient (a plain exception the engine retries)
            File.WriteAllText(fake, OperatingSystem.IsWindows() ? "@echo off\r\necho disk on fire 1>&2\r\nexit /b 1\r\n" : "#!/bin/sh\necho 'disk on fire' >&2\nexit 1\n");
            var e2 = await Assert.ThrowsAsync<InvalidOperationException>(() => Thumbs.ChunkFfmpegAsync(fake, [0xFF, 0xD8, 0xFF], c, 36, 64, 1, "vid", default));
            Assert.IsNotType<PermanentFailure>(e2);
            // an empty chunk never starts ffmpeg
            Assert.Empty(await Thumbs.ChunkFfmpegAsync(fake, [], new ChunkInfo("0018", 0, []), 36, 64, 1, "vid", default));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
