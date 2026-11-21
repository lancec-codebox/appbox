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
/// 0.13.4: a Nexis ghost transition (dehydrate or hydrate) changes a blob's ETag but not its bytes, so it must never fail a read. A ranged read pinned to an ETag that answers
/// "changed" (412) or "gone" is therefore checked: hydrate when the blob is a ghost, HEAD again, and when the blob still exists with the same length (and the same idx_sha256 when both
/// sides have one) read again with the new ETag, which the pin keeps. Only a real 404 or changed content is an error (Data null, or Mismatch).
/// </summary>
public static class GhostSafeRead
{
    public static async Task<RangeRead> RangeAsync(IStore store, BlobPin pin, long off, int len, CancellationToken ct)
    {
        var r = await store.GetRangeAsync(pin.Name, off, len, pin.ETag, ct);
        if (!r.Mismatch && r.Data != null) return r;
        // the read said changed or gone: is it the same content behind a ghost transition?
        var rest = LocalCacheStore.Unwrap(store) is var u && (u as RestStore ?? (u as RedirectStore)?.Inner as RestStore) is { } r0 ? r0 : null;
        var h = await store.HeadAsync(pin.Name, ct);
        if (h == null) return new(null, false);                                       // really deleted
        if (h.Size < pin.Length)
        {   // a ghost HEADs as 0 bytes: hydrate it (Nexis's own commit), then look again
            if (rest != null) await NexisAccess.PrepareAsync(rest, pin.Name, ct); else await store.GetAsync(pin.Name, ct);
            h = await store.HeadAsync(pin.Name, ct);
            if (h == null) return new(null, false);
        }
        if (h.Size != pin.Length) return new(null, true);                              // different bytes
        var sha = h.Metadata.GetValueOrDefault("idx_sha256");
        if (!string.IsNullOrEmpty(pin.Sha) && !string.IsNullOrEmpty(sha) && sha != pin.Sha) return new(null, true);
        Log.Info($"{pin.Name}: ETag changed without a change of content (ghost transition): reading on with the new ETag");
        pin.ETag = h.ETag;
        return await store.GetRangeAsync(pin.Name, off, len, h.ETag, ct);
    }
}
