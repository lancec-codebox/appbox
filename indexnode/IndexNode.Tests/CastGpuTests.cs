namespace IndexNode.Tests;

public class CastGpuTests
{
    private static Rgb Face(int seed)
    {
        var r = new Random(seed); var d = new byte[112 * 112 * 3]; r.NextBytes(d); return new Rgb(d, 112, 112);
    }

    /// <summary>Needs the buffalo_l recogniser: INDEXNODE_FACE_REC=/path/w600k_r50.onnx (the test passes trivially without it).</summary>
    [Fact]
    public void Batched_arcface_rows_equal_the_single_face_rows()
    {
        var path = Environment.GetEnvironmentVariable("INDEXNODE_FACE_REC");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        using var e = new ArcFace(path, 2);
        var faces = Enumerable.Range(1, 5).Select(Face).ToList();
        var batch = ((IFaceEmbedder)e).EmbedBatch(faces);
        Assert.Equal(5, batch.Length);
        for (int i = 0; i < 5; i++)
        {
            var one = e.Embed(faces[i]);
            Assert.True(CastLogic.Cosine(one, batch[i]) > 0.9999, $"face {i}");
        }
    }

    [Fact]
    public void The_default_batch_embedding_equals_calling_Embed_in_order()
    {
        IFaceEmbedder e = new Fake();
        var rows = e.EmbedBatch([Face(1), Face(2)]);
        Assert.Equal(2, rows.Length); Assert.Equal(1f, rows[0][0]); Assert.Equal(2f, rows[1][0]);
    }

    [Fact]
    public void Credits_sample_every_second_second_on_the_gpu_only()
    {
        var old = OrtOptions.Device;
        try
        {
            OrtOptions.Device = "cpu"; Assert.Equal(1, CastStage.CreditsStep);
            OrtOptions.Device = "cuda"; Assert.Equal(2, CastStage.CreditsStep);
        }
        finally { OrtOptions.Device = old; }
    }

    private sealed class Fake : IFaceEmbedder
    {
        public int Dim => 1; int n;
        public float[] Embed(Rgb f) => [++n];
        public void Dispose() { }
    }
}
