using System.Text;
using Microsoft.ML.OnnxRuntime;
namespace IndexNode.Tests;

/// <summary>0.10.8: `process --job X --steps sscd` on a store that holds only stage zips (no loose downloaded.json, frames/ ...).</summary>
[Collection("ort")]
public class ProcessZipStoreTests
{
    static void Varint(MemoryStream m, ulong v) { while (v >= 0x80) { m.WriteByte((byte)(v | 0x80)); v >>= 7; } m.WriteByte((byte)v); }
    static void Tag(MemoryStream m, int f, int w) => Varint(m, (ulong)((f << 3) | w));
    static void Int(MemoryStream m, int f, long v) { Tag(m, f, 0); Varint(m, (ulong)v); }
    static void Bytes(MemoryStream m, int f, byte[] b) { Tag(m, f, 2); Varint(m, (ulong)b.Length); m.Write(b); }
    static void Str(MemoryStream m, int f, string s) => Bytes(m, f, Encoding.UTF8.GetBytes(s));
    static byte[] Msg(Action<MemoryStream> f) { var m = new MemoryStream(); f(m); return m.ToArray(); }
    static byte[] VI(string name, int[]? dims, string[]? syms = null) => Msg(m => { Str(m, 1, name); Bytes(m, 2, Msg(t => Bytes(t, 1, Msg(tt => { Int(tt, 1, 1); Bytes(tt, 2, Msg(sh => { for (int i = 0; i < (dims?.Length ?? syms!.Length); i++) Bytes(sh, 1, Msg(dm => { if (syms != null) Str(dm, 2, syms[i]); else Int(dm, 1, dims![i]); })); })); })))); });

    /// <summary>A tiny float model with SSCD's shape: X[n,3,h,w] -> GlobalAveragePool -> Flatten -> MatMul(3x512) = Y[n,512].</summary>
    static byte[] TinyModel()
    {
        var w = new float[3 * 512]; for (int i = 0; i < w.Length; i++) w[i] = ((i * 7919) % 101 - 50) / 50f;
        var wb = new byte[w.Length * 4]; Buffer.BlockCopy(w, 0, wb, 0, wb.Length);
        var wt = Msg(t => { Int(t, 1, 3); Int(t, 1, 512); Int(t, 2, 1); Str(t, 8, "W"); Bytes(t, 9, wb); });
        byte[] Node(string op, string[] ins, string o, string name) => Msg(n => { foreach (var i in ins) Str(n, 1, i); Str(n, 2, o); Str(n, 3, name); Str(n, 4, op); });
        var flat = Msg(n => { Str(n, 1, "P"); Str(n, 2, "F"); Str(n, 3, "fl"); Str(n, 4, "Flatten"); Bytes(n, 5, Msg(a => { Str(a, 1, "axis"); Int(a, 3, 1); Int(a, 20, 2); })); });
        var graph = Msg(g => { Bytes(g, 1, Node("GlobalAveragePool", ["X"], "P", "gap")); Bytes(g, 1, flat); Bytes(g, 1, Node("MatMul", ["F", "W"], "Y", "mm")); Str(g, 2, "g"); Bytes(g, 5, wt);
            Bytes(g, 11, VI("X", null, ["n", "c", "h", "w"])); Bytes(g, 12, VI("Y", null, ["n", "d"])); });
        return Msg(m => { Int(m, 1, 8); Bytes(m, 8, Msg(o => { Str(o, 1, ""); Int(o, 2, 13); })); Bytes(m, 7, graph); });
    }

    static string? Real(string name)
    {
        var own = Path.Combine(NodeConfig.ToolsDir(), OperatingSystem.IsWindows() ? name + ".exe" : name); if (File.Exists(own)) return own;
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) { var f = Path.Combine(d, OperatingSystem.IsWindows() ? name + ".exe" : name); if (File.Exists(f)) return f; }
        return null;
    }

    [Fact]
    public async Task Process_job_steps_sscd_redoes_sscd_on_a_store_that_has_only_stage_zips()
    {
        var ff = Real("ffmpeg"); var pr = Real("ffprobe"); Assert.True(ff != null && pr != null, "ffmpeg and ffprobe are required for this test");
        var dir = Path.Combine(Path.GetTempPath(), "proc-zip-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var P = new Paths("index/"); var fs = new FakeStore();
            // build the drama the way nodes do: loose writes through the stage-zip wrapper, one commit per stage
            var build = StageZipStore.Wrap(fs, Path.Combine(dir, "build-cache"));
            var chunk = new MemoryStream(); var frames = new List<ChunkFrame>();
            for (int k = 0; k < 4; k++)
            {
                var px = new byte[360 * 640 * 3]; Array.Fill(px, (byte)(40 + k * 50));
                var j = Imaging.EncodeJpeg(new Rgb(px, 360, 640), 85); frames.Add(new ChunkFrame(k, k, k, [], null, chunk.Length, j.Length)); chunk.Write(j);
            }
            var meta = new List<(string, double, int, long)> { ("0000", 0, 4, chunk.Length) };
            await build.PutAsync(P.Video("vid", "frames/0000.mjpeg"), chunk.ToArray(), null, null, false, default);
            await build.PutAsync(P.Video("vid", "frames/0000.json"), FrameChunks.JpegIndexJson("0000", 0, 360, 640, 85, frames), null, null, false, default);
            await build.PutAsync(P.Video("vid", "frames/manifest.json"), FrameChunks.ManifestJson("frames", 360, 640, 0, 4, meta, null, "mjpeg", "jpeg"), null, null, false, default);
            await build.PutAsync(P.Video("vid", "downloaded.json"), Encoding.UTF8.GetBytes("{\"durationSeconds\":4}"), null, null, false, default);
            await VideoJsonSeed.PutAsync(build, P, "vid", 4);
            await build.PutAsync(P.Video("vid", "info.json"), Encoding.UTF8.GetBytes("{\"id\":\"vid\"}"), null, null, false, default);
            await build.PutAsync(P.Video("vid", "sscd/manifest.json"), Encoding.UTF8.GetBytes("{\"old\":true}"), null, null, false, default);
            foreach (var st in new[] { "frames", "download", "sscd" }) Assert.True(await StageZipStore.Stage(build)!.CommitStageAsync("vid", st, "n1", false, false, default), st);
            Assert.DoesNotContain(fs.Names("index/videos/vid/"), n => n.EndsWith(".json") && !n.EndsWith("/video.json", StringComparison.Ordinal));   // only stage zips are in the store, like the real one
            var oldSscd = fs[P.Video("vid", "sscd.zip")].Data.ToArray(); Assert.NotEmpty(oldSscd);

            var model = Path.Combine(dir, "tiny.onnx"); File.WriteAllBytes(model, TinyModel());
            var tools = new Tools(new HttpClient()); tools.UseFfmpeg(ff!, pr!);
            ProcessStage.Ai = new AiConfig();
            // what `process --job vid --steps sscd` builds: the raw store wrapped by SiteCommands.ProcessStore
            var store = SiteCommands.ProcessStore(fs);
            var ctx = new JobContext { Job = new JobDescriptor { Stage = "process", JobId = "vid", Steps = ["sscd"] }, Stage = "process", BlobName = "", LeaseId = "", Worker = 0, Store = store, Paths = P, NodeId = "t", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
            await new ProcessStage(tools, store, new NodeConfig { ModelPath = model }, new HttpClient()) { Redo = true }.RunAsync(ctx);

            var newSscd = fs[P.Video("vid", "sscd.zip")].Data.ToArray();
            Assert.NotEqual(oldSscd, newSscd);   // sscd.zip replaced
            var man = System.Text.Json.JsonDocument.Parse((await store.GetAsync(P.Video("vid", "sscd/manifest.json"), default))!.Data).RootElement;
            Assert.Equal(4, man.GetProperty("frames").GetInt32()); Assert.Equal("sscd", man.GetProperty("step").GetString());
            Assert.False(System.Text.Json.JsonDocument.Parse((await store.GetAsync(P.Video("vid", "sscd/manifest.json"), default))!.Data).RootElement.TryGetProperty("old", out _));
            Assert.False(fs.Exists(P.Video("vid", "processed.json")));   // a redo leaves it alone
            // and the raw, unwrapped store is what failed before 0.10.8
            var raw = new JobContext { Job = ctx.Job, Stage = "process", BlobName = "", LeaseId = "", Worker = 0, Store = fs, Paths = P, NodeId = "t", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProcessStage(tools, fs, new NodeConfig { ModelPath = model }, new HttpClient()) { Redo = true }.RunAsync(raw));
            Assert.Contains("no frames/ chunks", e.Message);   // 0.13.2: the duration is video.json's (loose, found on the raw store); what the raw store still cannot do is read the stage zips' members
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
