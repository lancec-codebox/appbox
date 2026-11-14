using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Nexis.Azure.Utilities;

namespace IndexNode;

/// <summary>What the hydrate logic needs to know about a blob: committed length, ETag and tags.</summary>
public sealed record GhostState(long Length, string ETag, IReadOnlyDictionary<string, string> Tags, IReadOnlyDictionary<string, string>? Metadata = null)
{
    public string State => Tags.GetValueOrDefault(DehydrateOperation.TagNames.State) ?? "(none)";
}

/// <summary>One blob as the Nexis hydrate logic sees it (the real one is a BlockBlobClient; the tests replay Nexis's sequence on a fake).</summary>
public interface IGhostBlob
{
    string Name { get; }
    /// <summary>Null when the blob does not exist.</summary>
    Task<GhostState?> ReadAsync(CancellationToken ct);
    /// <summary>Committed length of the snapshot, or null when the snapshot is gone.</summary>
    Task<long?> SnapshotLengthAsync(string snapshot, CancellationToken ct);
    /// <summary>Commits the uncommitted blocks with the ghost's block prefix back (Nexis's own hydrate), conditional on the ETag; throws when the blocks do not add up to the recorded size.</summary>
    Task CommitGhostAsync(GhostState state, CancellationToken ct);
}

/// <summary>Where to read a blob from: its base blob, or (Nexis caught it mid-dehydrate) the snapshot that still holds the content. The base blob is never written for a snapshot read.</summary>
public sealed record BlobRead(string? Snapshot, bool Repaired);

/// <summary>Big blobs (video.mp4, compact.mp4, the frames .mp4 chunks and the sscd/clip .f16 chunks) may be Nexis ghosts, or caught mid-dehydrate ("transitioning"): touch them, make them readable, keep touching.</summary>
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

    /// <summary>Touch, make the blob readable (ghost: commit its blocks; transitioning: read the snapshot, no write).</summary>
    public static async Task<BlobRead> PrepareAsync(RestStore store, string name, CancellationToken ct)
    {
        try { await TouchAsync(store, name, ct); }
        catch (StorageException e) { Log.Info($"nexis: could not touch {name} ({e.Message})"); }
        var read = await EnsureReadableAsync(new RestGhostBlob(store, name), ct);
        if (read.Snapshot != null) store.ReadSnapshots[name] = read.Snapshot; else store.ReadSnapshots.TryRemove(name, out _);
        return read;
    }

    /// <summary>
    /// Says where the blob can be read, or fails without a retry.
    /// - active (or untagged) with the recorded length: read the blob.
    /// - transitioning (Nexis committed an empty list; the content is only in its snapshot): read the snapshot (its length must be ghostd_size); the base blob is not written, Nexis finishes the ghosting on its next run. Snapshot gone: treat it as a ghost.
    /// - ghost (0 bytes, uncommitted blocks): commit the blocks (Nexis's hydrate), conditional on the ETag; a lost race re-reads the blob.
    /// - anything else with 0 bytes, or a length that is not the recorded size: PermanentFailure naming the blob and its state.
    /// </summary>
    public static async Task<BlobRead> EnsureReadableAsync(IGhostBlob blob, CancellationToken ct)
    {
        long? expected = null; bool repaired = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var s = await blob.ReadAsync(ct);
            if (s == null) return new(null, repaired);
            if (s.Tags.TryGetValue(Strings.size, out var sz) && long.TryParse(sz, out var z)) expected = z;
            var state = s.State;
            bool transitioning = state == nameof(BlobStateNames.transitioning), ghost = state == nameof(BlobStateNames.ghost);
            if (!transitioning && !ghost)
            {
                if (s.Length == 0) throw Unreadable(blob.Name, s, "0 bytes committed and not a ghost");
                if (expected is { } exp && s.Length != exp) throw Unreadable(blob.Name, s, $"{s.Length} bytes committed but {exp} expected");
                return new(null, repaired);      // active (or untagged): readable
            }
            if (!ghost && s.Length > 0) throw Unreadable(blob.Name, s, "transitioning but not empty");
            try
            {
                if (transitioning && s.Tags.TryGetValue(Strings.snapshot, out var snap) && snap.Length > 0)
                {
                    var len = await blob.SnapshotLengthAsync(snap, ct);
                    if (len != null)
                    {
                        if (expected is { } e2 && len != e2) throw Unreadable(blob.Name, s, $"snapshot {snap} has {len} bytes but {e2} expected");
                        Log.Info($"nexis: {blob.Name} is transitioning, reading its snapshot {snap}");
                        return new(snap, repaired);
                    }
                    Log.Info($"nexis: snapshot of {blob.Name} is gone, committing its uncommitted blocks instead");
                }
                try { await blob.CommitGhostAsync(s, ct); repaired = true; Log.Info($"nexis: hydrated {blob.Name}"); }
                catch (InvalidOperationException e) { throw Unreadable(blob.Name, s, e.Message); }
            }
            catch (Exception e) when (IsConditionNotMet(e)) { Log.Info($"nexis: {blob.Name} changed under us ({state}), looking again"); }
        }
        throw new PermanentFailure($"blob {blob.Name} kept changing state while it was being made readable; not retrying");
    }

    private enum BlobStateNames { pending, ghost, transitioning, active }

    static PermanentFailure Unreadable(string name, GhostState s, string why) =>
        new($"blob {name} cannot be read: state {s.State}, {s.Length} bytes committed ({why}); not retrying");

    public static bool IsConditionNotMet(Exception e) =>
        e is Azure.RequestFailedException { Status: 412 } || e is Azure.RequestFailedException { ErrorCode: "ConditionNotMet" }
        || e.Message.Contains("condition specified using HTTP conditional header(s) is not met", StringComparison.OrdinalIgnoreCase);

    /// <summary>Touches again every 5 minutes until disposed (used while a big blob is being downloaded).</summary>
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

/// <summary>The real blob: Azure Blob REST through the SAS in the store's URL.</summary>
public sealed class RestGhostBlob(RestStore store, string name) : IGhostBlob
{
    private readonly BlockBlobClient client = new(store.BlobUri(name));
    public string Name => name;

    public async Task<GhostState?> ReadAsync(CancellationToken ct)
    {
        try
        {
            var props = (await client.GetPropertiesAsync(cancellationToken: ct)).Value;
            var tags = (await client.GetTagsAsync(cancellationToken: ct)).Value.Tags;
            return new GhostState(props.ContentLength, props.ETag.ToString(), new Dictionary<string, string>(tags), new Dictionary<string, string>(props.Metadata));
        }
        catch (Azure.RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task<long?> SnapshotLengthAsync(string snapshot, CancellationToken ct)
    {
        try { return (await client.WithSnapshot(snapshot).GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength; }
        catch (Azure.RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task CommitGhostAsync(GhostState s, CancellationToken ct) =>
        await DehydrateOperation.HydrateBlobAsync(client, s.Tags, s.Metadata?.ToDictionary(x => x.Key, x => x.Value), new BlobRequestConditions { IfMatch = new ETag(s.ETag) }, ct);
}
