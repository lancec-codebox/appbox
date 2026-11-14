using Azure.Storage.Blobs.Specialized;
using Nexis.Azure.Utilities;

namespace IndexNode;

/// <summary>Big blobs (video.mp4, compact.mp4, the frames/keyframes .jpgs and the sscd/clip .f16 chunks) may be Nexis ghosts: touch them, hydrate them, keep touching.</summary>
public static class NexisAccess
{
    /// <summary>Sets the Nexis tag last_access to now (Nexis Timestamp format), keeping every other tag.</summary>
    public static async Task TouchAsync(IStore store, string name, CancellationToken ct)
    {
        var tags = await store.GetTagsAsync(name, ct);
        if (tags == null) return;
        var next = new Dictionary<string, string>(tags) { [DehydrateOperation.TagNames.LastAccess] = Timestamp.Now.ToString() };
        await store.SetTagsAsync(name, next, null, ct);
    }

    /// <summary>Touch, then hydrate through Nexis's own logic when the blob is a ghost.</summary>
    public static async Task PrepareAsync(RestStore store, string name, CancellationToken ct)
    {
        try { await TouchAsync(store, name, ct); }
        catch (StorageException e) { Log.Info($"nexis: could not touch {name} ({e.Message})"); }
        var client = new BlockBlobClient(store.BlobUri(name));
        await HydrateOrYieldAsync(name, () => DehydrateOperation.HydrateIfGhostAsync(client, ct), async () => (await client.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength == 0 && (await client.GetTagsAsync(cancellationToken: ct)).Value.Tags.TryGetValue(DehydrateOperation.TagNames.State, out var st) && st == "ghost");
    }

    /// <summary>
    /// Hydrates; when another step hydrated the same ghost first, the conditional commit fails (412 ConditionNotMet / "condition specified using HTTP conditional header(s) is not met").
    /// That means someone else did it: look again, and carry on when the blob is no longer a ghost. Any other failure, or a blob still a ghost, is thrown.
    /// </summary>
    public static async Task<bool> HydrateOrYieldAsync(string name, Func<Task<bool>> hydrate, Func<Task<bool>> stillGhost)
    {
        try
        {
            var done = await hydrate();
            if (done) Log.Info($"nexis: hydrated {name}");
            return done;
        }
        catch (Exception e) when (IsConditionNotMet(e))
        {
            if (await stillGhost()) throw;
            Log.Info($"nexis: {name} was hydrated by another step");
            return false;
        }
    }

    public static bool IsConditionNotMet(Exception e) =>
        e is Azure.RequestFailedException { Status: 412 } || e is Azure.RequestFailedException { ErrorCode: "ConditionNotMet" }
        || e.Message.Contains("condition specified using HTTP conditional header(s) is not met", StringComparison.OrdinalIgnoreCase);

    /// <summary>Touches again every 5 minutes until disposed.</summary>
    public static IDisposable KeepTouched(IStore store, string name, TimeSpan? every = null)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(every ?? TimeSpan.FromMinutes(5), cts.Token);
                    try { await TouchAsync(store, name, cts.Token); } catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"nexis: touch failed ({e.Message})"); }
                }
            }
            catch (OperationCanceledException) { }
        });
        return new Stop(cts);
    }

    private sealed class Stop(CancellationTokenSource cts) : IDisposable { public void Dispose() => cts.Cancel(); }
}
