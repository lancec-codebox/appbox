using System.Text.Json;

namespace IndexNode;

/// <summary>0.13.26: a refresh or classify run's result on the work queue's done line: inline when small, else a blob whose path is on the line.</summary>
public static class RunResults
{
    public static async Task<RunResult> SpillAsync(IStore store, Paths paths, string subject, RunResult r, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(r, Protocol.Json);
        if (bytes.Length <= RunResult.InlineMax) return r;
        var blob = paths.RunResult(subject, Protocol.NowUtc().Replace(":", "").Replace("-", ""));
        await store.PutAsync(blob, bytes, null, null, false, ct);
        return new RunResult { Blob = blob, Modified = r.Modified };
    }

    /// <summary>The full result of a line's: the blob read when there is one (null when it is gone).</summary>
    public static async Task<RunResult?> ResolveAsync(IStore? store, RunResult r, CancellationToken ct)
    {
        if (r.Blob == null) return r;
        if (store == null) return null;
        var b = await store.GetAsync(r.Blob, ct); if (b == null) return null;
        return JsonSerializer.Deserialize<RunResult>(b.Data, Protocol.Json);
    }
}
