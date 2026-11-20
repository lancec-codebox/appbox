using System.Reflection;

namespace IndexNode;

/// <summary>
/// 0.12.0 AI-drama probe (model "ai-frames-v3"): a logistic regression on each L2-normalised SSCD frame vector,
/// p = sigmoid(((x - mu) / sd) . w + b); the drama score is the mean p over the sample frames. The weights are the embedded resource
/// Resources/ai-frames-v3.f32: little-endian f32, 1537 values in order mu[512], sd[512], w[512], b.
/// </summary>
public sealed class AiProbe
{
    public const string ModelId = "ai-frames-v3";
    public const int Dim = 512;
    public float[] Mu { get; }
    public float[] Sd { get; }
    public float[] W { get; }
    public float B { get; }

    public AiProbe(ReadOnlySpan<byte> f32le)
    {
        if (f32le.Length != (3 * Dim + 1) * 4) throw new InvalidDataException($"{ModelId}: {f32le.Length} bytes, expected {(3 * Dim + 1) * 4}");
        var v = new float[3 * Dim + 1]; for (int i = 0; i < v.Length; i++) v[i] = BitConverter.ToSingle(f32le.Slice(i * 4, 4));   // little-endian hosts only (x64, arm64)
        Mu = v[..Dim]; Sd = v[Dim..(2 * Dim)]; W = v[(2 * Dim)..(3 * Dim)]; B = v[3 * Dim];
    }

    static AiProbe? shared;
    public static AiProbe Default => shared ??= Load();
    static AiProbe Load()
    {
        using var s = typeof(AiProbe).Assembly.GetManifestResourceStream("ai-frames-v3.f32") ?? throw new InvalidOperationException("missing resource ai-frames-v3.f32");
        var b = new byte[s.Length]; s.ReadExactly(b); return new AiProbe(b);
    }

    /// <summary>p of one (normalised) vector.</summary>
    public double P(ReadOnlySpan<float> x)
    {
        double z = B;
        for (int d = 0; d < Dim; d++) z += ((double)x[d] - Mu[d]) / Sd[d] * W[d];
        return 1.0 / (1.0 + Math.Exp(-z));
    }

    /// <summary>The drama score: the mean p over the rows (n x 512, each row normalised by the caller or here).</summary>
    public double Score(IReadOnlyList<float[]> rows)
    {
        if (rows.Count == 0) return 0;
        double sum = 0; var t = new float[Dim];
        foreach (var r in rows) { r.CopyTo(t, 0); Sscd.L2Normalize(t); sum += P(t); }
        return sum / rows.Count;
    }

    /// <summary>Rows of f16 (as stored in sscd.zip): n x 512.</summary>
    public static float[][] RowsFromF16(byte[] f16)
    {
        int n = f16.Length / (Dim * 2); var rows = new float[n][];
        for (int i = 0; i < n; i++) { rows[i] = new float[Dim]; for (int d = 0; d < Dim; d++) rows[i][d] = (float)BitConverter.ToHalf(f16, (i * Dim + d) * 2); }
        return rows;
    }
}
