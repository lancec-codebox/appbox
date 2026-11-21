namespace IndexNode;

/// <summary>What a reader knows about a blob it reads by ranges: its name, the ETag it holds, the length and (when it has one) the idx_sha256 metadata of the content it opened.</summary>
public sealed class BlobPin(string name, string? etag, long length, string? sha = null)
{
    public string Name => name;
    public string? ETag { get; set; } = etag;
    public long Length => length;
    public string? Sha => sha;
}

/// <summary>
/// 0.13.8: no logic of its own any more. The ghost-aware reader is the store's ranged read (RestStore.GetRangeAsync): identity is content (length + idx_sha256), so an ETag changed by a Nexis transition
/// is never an error, and ghost / transitioning / vanished-snapshot states are settled inside it. This only carries the pin.
/// </summary>
public static class GhostSafeRead
{
    public static Task<RangeRead> RangeAsync(IStore store, BlobPin pin, long off, int len, CancellationToken ct) => store.GetRangeAsync(pin.Name, off, len, pin.ETag, ct);
}
