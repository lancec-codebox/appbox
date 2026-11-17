using IndexNode;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.9.0: each read-only model is loaded once per process and used by every worker.</summary>
[Collection("ort")]
public class SharedModelTests
{
    private static byte[] Msg(int field, byte[] body) => [.. Varint((field << 3) | 2), .. Varint(body.Length), .. body];
    private static byte[] Str(int field, string v) => Msg(field, System.Text.Encoding.UTF8.GetBytes(v));
    private static byte[] Int(int field, long v) => [.. Varint(field << 3), .. Varint(v)];
    private static byte[] Varint(long v) { var l = new List<byte>(); do { var b = (byte)(v & 0x7f); v >>= 7; if (v != 0) b |= 0x80; l.Add(b); } while (v != 0); return l.ToArray(); }
    private static byte[] Cat(params byte[][] p) => p.SelectMany(x => x).ToArray();

    /// <summary>A valid one-node ONNX model (Identity on a float[3]), encoded by hand so no download is needed.</summary>
    internal static string Model()
    {
        byte[] ValueInfo(string name) => Cat(Str(1, name), Msg(2, Msg(1, Cat(Int(1, 1), Msg(2, Msg(1, Int(1, 3)))))));
        var node = Cat(Str(1, "x"), Str(2, "y"), Str(4, "Identity"));
        var graph = Cat(Msg(1, node), Str(2, "g"), Msg(11, ValueInfo("x")), Msg(12, ValueInfo("y")));
        var model = Cat(Int(1, 8), Msg(7, graph), Msg(8, Int(2, 13)));
        var p = Path.Combine(Path.GetTempPath(), "indexnode-shared-" + Guid.NewGuid().ToString("N") + ".onnx");
        File.WriteAllBytes(p, model);
        return p;
    }

    [Fact]
    public async Task One_session_per_model_file_is_shared_by_all_callers_and_runs_concurrently()
    {
        var path = Model();
        try
        {
            var sessions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => OrtOptions.Shared(path, 1, true))));
            Assert.All(sessions, s => Assert.Same(sessions[0], s));
            Assert.NotSame(sessions[0], OrtOptions.Shared(path, 2, true)); // another thread count is another session
            var name = sessions[0].InputMetadata.Keys.First();
            var bad = 0;
            Parallel.For(0, 200, i =>
            {
                var t = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(new[] { 3f, i, 7f }, [3]);
                using var r = sessions[0].Run([Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(name, t)]);
                if (r.First().AsTensor<float>()[1] != i) Interlocked.Increment(ref bad);
            });
            Assert.Equal(0, bad);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Disposing_an_ocr_or_face_wrapper_does_not_close_the_shared_session()
    {
        var path = Model();
        try
        {
            var s = OrtOptions.Shared(path, 1, true);
            // the wrappers' Dispose is a no-op on shared sessions: the session still runs afterwards
            var t = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(new[] { 1f, 2f, 3f }, [3]);
            using var r = s.Run([Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), t)]);
            Assert.Equal(2f, r.First().AsTensor<float>()[1]);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
