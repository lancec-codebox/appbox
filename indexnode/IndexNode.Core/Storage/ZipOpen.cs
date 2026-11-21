namespace IndexNode;

/// <summary>
/// 0.13.5: the one way to HEAD a blob that is about to be opened as a zip. A Nexis ghost HEADs as 0 bytes (tag ghostd_state=ghost, ghostd_size = the real length), so a zip opened from that length
/// failed with "not a zip: the blob is 0 bytes". A 0-byte blob that is a ghost (or transitioning) is hydrated first (a whole read, which RestStore turns into the ghost commit) and HEADed again.
/// A 0-byte blob with no ghost tags is really not a zip.
/// </summary>
public static class ZipOpen
{
    public const int MinZip = 22;

    /// <summary>The blob's properties after a ghost has been hydrated; null when the blob does not exist. <paramref name="mustBeZip"/>: a blob still under 22 bytes then is an InvalidDataException naming it.</summary>
    public static async Task<BlobProps?> HeadReadableAsync(IStore store, string blob, CancellationToken ct, bool mustBeZip = true)
    {
        var h = await store.HeadAsync(blob, ct); if (h == null) return null;
        if (h.Size < MinZip && await IsGhostAsync(store, blob, ct))
        {
            if (LocalCacheStore.Unwrap(store) is RestStore rest) { try { await NexisAccess.PrepareAsync(rest, blob, ct); } catch (StorageException) { } }
            else await store.GetAsync(blob, ct);
            h = await store.HeadAsync(blob, ct); if (h == null) return null;
            if (h.Size < MinZip) { await store.GetAsync(blob, ct); h = await store.HeadAsync(blob, ct); if (h == null) return null; }   // once more, whole
        }
        if (mustBeZip && h.Size < MinZip) throw new InvalidDataException($"not a zip: {blob} is {h.Size} bytes (empty, not a Nexis ghost)");
        return h;
    }

    static async Task<bool> IsGhostAsync(IStore store, string blob, CancellationToken ct)
    {
        var t = await store.GetTagsAsync(blob, ct); if (t == null) return false;
        if (t.TryGetValue("ghostd_state", out var st) && st is "ghost" or "transitioning") return true;
        return t.TryGetValue("ghostd_size", out var sz) && long.TryParse(sz, out var n) && n > 0;
    }
}
