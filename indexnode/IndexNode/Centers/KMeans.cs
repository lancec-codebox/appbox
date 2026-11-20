using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace IndexNode;

/// <summary>Vector helpers for the centers code: 256-bit SIMD with fused multiply-add (FusedMultiplyAdd is correctly rounded on every machine, so a result does not depend on the CPU).</summary>
public static class Vec
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        ref float pa = ref MemoryMarshal.GetReference(a); ref float pb = ref MemoryMarshal.GetReference(b);
        var s0 = Vector256<float>.Zero; var s1 = s0; var s2 = s0; var s3 = s0;
        for (; i + 32 <= n; i += 32)
        {
            s0 = Vector256.FusedMultiplyAdd(Vector256.LoadUnsafe(ref pa, (nuint)i), Vector256.LoadUnsafe(ref pb, (nuint)i), s0);
            s1 = Vector256.FusedMultiplyAdd(Vector256.LoadUnsafe(ref pa, (nuint)(i + 8)), Vector256.LoadUnsafe(ref pb, (nuint)(i + 8)), s1);
            s2 = Vector256.FusedMultiplyAdd(Vector256.LoadUnsafe(ref pa, (nuint)(i + 16)), Vector256.LoadUnsafe(ref pb, (nuint)(i + 16)), s2);
            s3 = Vector256.FusedMultiplyAdd(Vector256.LoadUnsafe(ref pa, (nuint)(i + 24)), Vector256.LoadUnsafe(ref pb, (nuint)(i + 24)), s3);
        }
        for (; i + 8 <= n; i += 8) s0 = Vector256.FusedMultiplyAdd(Vector256.LoadUnsafe(ref pa, (nuint)i), Vector256.LoadUnsafe(ref pb, (nuint)i), s0);
        float r = Vector256.Sum((s0 + s1) + (s2 + s3));
        for (; i < n; i++) r = MathF.FusedMultiplyAdd(a[i], b[i], r);
        return r;
    }

    public static void Add(Span<float> dst, ReadOnlySpan<float> x)
    {
        int n = Math.Min(dst.Length, x.Length), i = 0;
        ref float pd = ref MemoryMarshal.GetReference(dst); ref float px = ref MemoryMarshal.GetReference(x);
        for (; i + 8 <= n; i += 8) (Vector256.LoadUnsafe(ref pd, (nuint)i) + Vector256.LoadUnsafe(ref px, (nuint)i)).StoreUnsafe(ref pd, (nuint)i);
        for (; i < n; i++) dst[i] += x[i];
    }

    /// <summary>Scales to unit length; false (vector untouched) when it is all zero.</summary>
    public static bool Normalize(Span<float> v)
    {
        double s = 0; foreach (var x in v) s += (double)x * x;
        if (s < 1e-24) return false;
        float n = (float)Math.Sqrt(s);
        for (int i = 0; i < v.Length; i++) v[i] /= n;
        return true;
    }

    public static void FromHalf(ReadOnlySpan<byte> f16, Span<float> dst)
    {
        for (int i = 0; i < dst.Length; i++) dst[i] = (float)System.Buffers.Binary.BinaryPrimitives.ReadHalfLittleEndian(f16.Slice(i * 2, 2));
    }
}

public sealed record KMeansResult(int K, int Dim, float[] Centers, int[] Assign, int[] Sizes, double SeedSeconds, double IterSeconds, double TotalSeconds);

/// <summary>Spherical k-means (cosine) over unit vectors: k-means++ seeding (D = 1 - max cosine, D^2 sampling), a fixed number of Lloyd iterations, then one final assignment; empty centers are dropped.
/// Deterministic for a given seed whatever the thread count: only the independent per-row assignments run in parallel, sums are added in row order.</summary>
public static class KMeans
{
    /// <summary>The fixed seed of an upload: the first 4 bytes of SHA-256 of its id.</summary>
    public static int SeedOf(string uploadId) => BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uploadId)), 0) & 0x7FFFFFFF;

    const int Block = 128;

    static void Rows(int n, ParallelOptions po, Action<int, int> body)
    {
        int blocks = (n + Block - 1) / Block;
        if (po.MaxDegreeOfParallelism == 1) { for (int b = 0; b < blocks; b++) body(b * Block, Math.Min(n, (b + 1) * Block)); return; }
        Parallel.For(0, blocks, po, b => body(b * Block, Math.Min(n, (b + 1) * Block)));
    }

    /// <param name="x">n rows of <paramref name="dim"/> floats, unit length.</param>
    public static KMeansResult Run(float[] x, int n, int dim, int k, int iters, int seed, int threads)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        k = Math.Min(k, n);
        if (n == 0 || k <= 0) return new KMeansResult(0, dim, [], [], [], 0, 0, 0);
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) };
        var rng = new Random(seed);
        var c = new float[(long)k * dim]; var best = new float[n]; Array.Fill(best, -2f); var d = new double[n]; var a = new int[n];
        ReadOnlySpan<float> X(int i) => new(x, i * dim, dim);
        Span<float> C(int j) => new(c, j * dim, dim);
        X(rng.Next(n)).CopyTo(C(0));
        for (int cc = 0; cc < k; cc++)
        {
            if (cc > 0)
            {
                double tot = 0; for (int i = 0; i < n; i++) { double v = Math.Max(0, 1 - best[i]); d[i] = v * v; tot += d[i]; }
                int pick = n - 1;
                if (tot <= 0) pick = rng.Next(n);
                else { double r = rng.NextDouble() * tot; for (int i = 0; i < n; i++) { r -= d[i]; if (r <= 0) { pick = i; break; } } }
                X(pick).CopyTo(C(cc));
            }
            int c2 = cc;
            Rows(n, po, (lo, hi) => { for (int i = lo; i < hi; i++) { float s = Vec.Dot(X(i), C(c2)); if (s > best[i]) best[i] = s; } });
        }
        double seedS = sw.Elapsed.TotalSeconds, iterS = 0;
        var sz = new int[k]; var sum = new float[(long)k * dim];
        void Assign() => Rows(n, po, (lo, hi) =>
        {
            for (int i = lo; i < hi; i++)
            {
                float b = -2; int bi = 0; var xi = X(i);
                for (int j = 0; j < k; j++) { float s = Vec.Dot(xi, C(j)); if (s > b) { b = s; bi = j; } }
                a[i] = bi; best[i] = b;
            }
        });
        for (int it = 0; it < iters; it++)
        {
            var t0 = sw.Elapsed.TotalSeconds;
            Assign();
            Array.Clear(sum); Array.Clear(sz);
            for (int i = 0; i < n; i++) { sz[a[i]]++; Vec.Add(new Span<float>(sum, a[i] * dim, dim), X(i)); }
            for (int j = 0; j < k; j++) if (sz[j] > 0) { var s = new Span<float>(sum, j * dim, dim); if (Vec.Normalize(s)) s.CopyTo(C(j)); }
            iterS += sw.Elapsed.TotalSeconds - t0;
        }
        Assign();   // final assignment against the final centers
        Array.Clear(sz); for (int i = 0; i < n; i++) sz[a[i]]++;
        // drop empty centers, renumber in the old order
        var map = new int[k]; int kept = 0; for (int j = 0; j < k; j++) map[j] = sz[j] > 0 ? kept++ : -1;
        var oc = new float[(long)kept * dim]; var os = new int[kept];
        for (int j = 0; j < k; j++) if (map[j] >= 0) { C(j).CopyTo(new Span<float>(oc, map[j] * dim, dim)); os[map[j]] = sz[j]; }
        for (int i = 0; i < n; i++) a[i] = map[a[i]];
        return new KMeansResult(kept, dim, oc, a, os, seedS, iters > 0 ? iterS / iters : 0, sw.Elapsed.TotalSeconds);
    }
}
