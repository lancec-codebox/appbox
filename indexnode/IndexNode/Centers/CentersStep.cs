using System.Diagnostics;

namespace IndexNode;

/// <summary>The centers step (0.11.0): per upload, spherical k-means over its stored SSCD vectors, written as centers.zip (centers/centers.bin + centers/manifest.json).</summary>
public static class CentersStep
{
    public const int K = 256, Iterations = 10;

    public sealed record Built(byte[] Bin, byte[] Manifest, CentersFile.Header Header, KMeansResult Result);

    /// <summary>Pure: the files of one upload from its SSCD rows (f16, 512 per row) and frame times. TODO: no featureless-frame filtering yet (every stored row is clustered).</summary>
    public static Built Build(string uploadId, byte[] f16, IReadOnlyList<double> times, int threads, string? sscdZipSha = null)
    {
        int n = f16.Length / (CentersFile.Dim * 2);
        if (n != times.Count) throw new InvalidOperationException($"{uploadId}: {n} sscd rows but {times.Count} frame times");
        var sw = Stopwatch.StartNew();
        var x = new float[(long)n * CentersFile.Dim]; Vec.FromHalf(f16, x);
        for (int i = 0; i < n; i++) Vec.Normalize(new Span<float>(x, i * CentersFile.Dim, CentersFile.Dim));   // f16 rows are unit length up to rounding
        var sum = new float[CentersFile.Dim]; for (int i = 0; i < n; i++) Vec.Add(sum, new ReadOnlySpan<float>(x, i * CentersFile.Dim, CentersFile.Dim));
        var r = KMeans.Run(x, n, CentersFile.Dim, K, Iterations, KMeans.SeedOf(uploadId), threads);
        var bin = CentersFile.Write(r, f16, times.Select(t => (float)t).ToArray(), Enumerable.Range(0, n).ToArray(), CentersFile.HashOf(f16), sum);
        var h = CentersFile.ParseHeader(bin);
        return new Built(bin, CentersFile.ManifestJson(h, sw.Elapsed.TotalSeconds, sscdZipSha), h, r);
    }

    /// <summary>Reads the upload's sscd vectors and times through the store (stage zips), builds and stages the two members. The caller commits the stage (or backfill writes the zip).</summary>
    public static async Task<string> RunAsync(IStore wst, Paths paths, string id, int threads, Action<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var src = new StoreJob(wst, paths, id);
        var f16 = await FrameChunks.VectorsAsync(src, "sscd", ct) ?? throw new InvalidOperationException($"{id} has no sscd vectors yet (run the sscd step)");
        var times = await FrameChunks.TimesAsync(src, ct) ?? throw new InvalidOperationException($"{id} has no frames");
        progress?.Invoke($"{times.Count} vectors read in {sw.Elapsed.TotalSeconds:F1}s");
        var sscdSha = (await wst.HeadAsync(paths.Video(id, "sscd.zip"), ct))?.Metadata.GetValueOrDefault("idx_sha256");
        var b = Build(id, f16, times, threads, sscdSha);
        await wst.PutAsync(paths.Video(id, CentersFile.Member), b.Bin, null, null, false, ct, "application/octet-stream");
        await wst.PutAsync(paths.Video(id, CentersFile.ManifestMember), b.Manifest, null, null, false, ct);
        await FramesFile.WriteAsync(wst, paths, id, b.Bin, 0, false, ct);   // 0.13.9: the lookup copy, created, or replaced under this claim's lease (a re-run writes a new one)
        return $"{b.Header.Frames} frames -> {b.Header.K} centers, {b.Bin.Length / 1024} KB, kmeans {b.Result.TotalSeconds:F2}s, total {sw.Elapsed.TotalSeconds:F1}s";
    }

    /// <summary>True when centers.zip exists but was built from another sscd.zip than the one there now (its idx_inputs differs).</summary>
    public static async Task<bool> StaleAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        var c = await store.HeadAsync(paths.Video(id, "centers.zip"), ct); if (c == null) return false;
        var s = await store.HeadAsync(paths.Video(id, "sscd.zip"), ct); if (s == null) return false;
        return c.Metadata.GetValueOrDefault("idx_inputs") != StageLayout.Inputs([s.Metadata.GetValueOrDefault("idx_sha256")]);
    }
}
