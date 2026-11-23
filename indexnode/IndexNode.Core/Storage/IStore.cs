namespace IndexNode;

public sealed record BlobEntry(
    string Name,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyDictionary<string, string> Metadata,
    string? LeaseState = null,
    long Size = 0,
    /// <summary>When the blob was created (List Blobs Creation-Time, else Last-Modified); null when the store does not say.</summary>
    DateTimeOffset? Created = null,
    /// <summary>0.13.18: the blob's ETag as the listing reports it (null when the store does not say). Lets a caller use a LIST for size + ETag instead of a HEAD per blob.</summary>
    string? ETag = null);

/// <summary>An append blob read (0.13.0 queue log): the bytes from the offset, the blob's length, whether it is sealed and how many blocks (appends) it holds.</summary>
public sealed record AppendRead(byte[] Data, long Length, bool Sealed, int Blocks, bool NotFound = false);

public enum AppendOutcome { Ok, PositionMismatch, Sealed, NotFound }

/// <summary>The answer to an Append Block: Ok (Length is the new length), PositionMismatch (412: someone appended first), Sealed (409: the segment is sealed) or NotFound.</summary>
public sealed record AppendResult(AppendOutcome Outcome, long Length = 0, int Blocks = 0);

public sealed record AppendInfo(long Length, bool Sealed, int Blocks);

public sealed record BlobContent(byte[] Data, string? ETag, IReadOnlyDictionary<string, string>? Meta = null);

/// <summary>A blob's size, ETag and metadata (Get Blob Properties).</summary>
public sealed record BlobProps(long Size, string? ETag, IReadOnlyDictionary<string, string> Metadata);

/// <summary>A range read pinned to an ETag: <see cref="Mismatch"/> when the blob changed since (412), <see cref="Data"/> null when it is gone.</summary>
public sealed record RangeRead(byte[]? Data, bool Mismatch);

public sealed record ConditionalResult(bool Changed, bool Exists, byte[]? Data, string? ETag, IReadOnlyDictionary<string, string>? Meta = null);

public class StorageException(int status, string? code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}

/// <summary>The lease is gone (expired or taken): the job is no longer ours.</summary>
public sealed class LeaseLostException(string message) : StorageException(409, "LeaseLost", message);

/// <summary>The SAS lacks a permission (e.g. 'f' for Find Blobs by Tags).</summary>
public sealed class ForbiddenException(string message) : StorageException(403, "AuthorizationPermissionMismatch", message);

/// <summary>One block of a block blob's list: its id (base64, as Azure names it) and size in bytes.</summary>
public sealed record BlockItem(string Id, long Size);

/// <summary>Get Block List (all): the blob's ETag (null while nothing is committed) and its committed and uncommitted blocks, in list order.</summary>
public sealed record BlockList(string? ETag, IReadOnlyList<BlockItem> Committed, IReadOnlyList<BlockItem> Uncommitted);

public interface IStore
{
    Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct);
    Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct);
    Task<BlobContent?> GetAsync(string name, CancellationToken ct);
    Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct);

    /// <summary>Put Blob. Returns false when <paramref name="onlyIfNew"/> is set and the blob exists.</summary>
    Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json");
    Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct);
    Task DownloadToFileAsync(string name, string path, CancellationToken ct);
    /// <summary>0.13.32: ONE GET of the whole blob into a file; the response carries what a HEAD would (length, ETag, metadata), so there is no HEAD first. Null when the blob does not exist. A 0-byte answer is a ghost: hydrated, then read again (RestStore). Other stores: download, then HEAD.</summary>
    async Task<BlobProps?> DownloadToFileWithPropsAsync(string name, string path, CancellationToken ct) { if (await HeadAsync(name, ct) == null) return null; await DownloadToFileAsync(name, path, ct); return await HeadAsync(name, ct); }

    /// <summary>Null when another node holds the lease (409).</summary>
    Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct);
    Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct);
    Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct);
    Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct);
    Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct);

    /// <summary>Set Blob Tags only while the blob's current tags equal <paramref name="expect"/> (x-ms-if-tags); false when they do not. Tag writes do not change the ETag, so this is how two nodes avoid racing on idx_state / idx_hold. The default is read-compare-write; RestStore sends the real condition.</summary>
    async Task<bool> SetTagsIfAsync(string name, IReadOnlyDictionary<string, string> tags, IReadOnlyDictionary<string, string> expect, CancellationToken ct)
    {
        var cur = await GetTagsAsync(name, ct);
        if (cur == null || expect.Any(e => cur.GetValueOrDefault(e.Key) != e.Value)) return false;
        await SetTagsAsync(name, tags, null, ct);
        return true;
    }
    /// <summary>Delete under a blob lease (a hold blob is deleted by the node that holds its lease). The default ignores the lease id; RestStore and FakeStore send/check it.</summary>
    Task DeleteAsync(string name, string? leaseId, CancellationToken ct) => DeleteAsync(name, ct);
    /// <summary>Size, ETag and metadata of one blob; null when it does not exist. The default lists the name (no ETag).</summary>
    async Task<BlobProps?> HeadAsync(string name, CancellationToken ct)
    {
        var e = (await ListAsync(name, false, true, ct)).FirstOrDefault(b => b.Name == name);
        return e == null ? null : new BlobProps(e.Size, null, e.Metadata);
    }
    /// <summary>Bytes off..off+len of a blob, only while its ETag still equals <paramref name="ifMatch"/> (null = any). The default reads the whole blob.</summary>
    async Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct)
    {
        var c = await GetAsync(name, ct);
        if (c == null) return new(null, false);
        if (ifMatch != null && c.ETag != null && c.ETag != ifMatch) return new(null, true);
        return new(c.Data.AsSpan((int)Math.Min(off, c.Data.Length), (int)Math.Max(0, Math.Min(len, c.Data.Length - off))).ToArray(), false);
    }
    /// <summary>Uploads a local file whole with its metadata (and tags) in one write; with <paramref name="onlyIfNew"/> false when the blob exists. A Put keeps neither metadata nor tags unless they are re-sent, so callers send all of both. The default is Put, then Set Metadata.</summary>
    async Task<bool> PutZipAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct)
    {
        if (onlyIfNew && await HeadAsync(name, ct) != null) return false;
        await PutFileAsync(name, path, "application/zip", null, ct);
        await SetMetadataAsync(name, metadata, null, ct);
        if (tags is { Count: > 0 }) await SetTagsAsync(name, tags, null, ct);
        return true;
    }
    /// <summary>Replaces an existing zip only while its ETag still equals <paramref name="etag"/> (If-Match); false when it changed or is gone (412/404), nothing written then. The default checks the ETag with a Head first (racy); RestStore and FakeStore make it one conditional write.</summary>
    async Task<bool> PutZipIfMatchAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, string etag, CancellationToken ct)
    {
        var h = await HeadAsync(name, ct);
        if (h == null || h.ETag != etag) return false;
        return await PutZipAsync(name, path, metadata, tags, false, ct);
    }
    Task DeleteAsync(string name, CancellationToken ct);
    /// <summary>Replaces a block blob only while its ETag still equals <paramref name="etag"/> (If-Match); with a null etag only when it does not exist yet (If-None-Match: *). False when the condition failed (412/409/404): nothing written. The default checks with a Head first (racy); RestStore and FakeStore make it atomic.</summary>
    async Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, CancellationToken ct)
    {
        if (etag == null) return await PutAsync(name, data, null, null, true, ct);
        var h = await HeadAsync(name, ct);
        if (h == null || h.ETag != etag) return false;
        return await PutAsync(name, data, null, null, false, ct);
    }

    /// <summary>PutIfMatchAsync that also sets the blob's metadata IN THE SAME REQUEST (x-ms-meta-*: no extra op). The default (a store with no single-request form) puts, then sets the metadata.</summary>
    async Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, IReadOnlyDictionary<string, string> meta, CancellationToken ct)
    {
        if (!await PutIfMatchAsync(name, data, etag, ct)) return false;
        await SetMetadataAsync(name, meta, null, ct); return true;
    }

    // ---- append blobs (0.13.0: the job queue log). Stores that cannot do them throw NotSupportedException.
    /// <summary>Creates an empty append blob; false when it exists (If-None-Match: *) and <paramref name="onlyIfNew"/> is set.</summary>
    Task<bool> CreateAppendBlobAsync(string name, bool onlyIfNew, CancellationToken ct) => throw new NotSupportedException("this store has no append blobs");
    /// <summary>Append Block with x-ms-blob-condition-appendpos = <paramref name="appendPos"/>: written only while the blob's length still equals it.</summary>
    Task<AppendResult> AppendBlockAsync(string name, byte[] data, long appendPos, CancellationToken ct) => throw new NotSupportedException("this store has no append blobs");
    /// <summary>Bytes from <paramref name="offset"/> (at most <paramref name="maxLen"/>) of an append blob, with its length and sealed flag, in one ranged read.</summary>
    Task<AppendRead> ReadAppendAsync(string name, long offset, int maxLen, CancellationToken ct) => throw new NotSupportedException("this store has no append blobs");
    /// <summary>Seals an append blob (later appends fail with Sealed) only while its length equals <paramref name="appendPos"/>; false on a length mismatch, true when sealed (or already). Null: no such blob.</summary>
    Task<bool?> SealAppendBlobAsync(string name, long appendPos, CancellationToken ct) => throw new NotSupportedException("this store has no append blobs");
    /// <summary>Length, sealed flag and block count of an append blob; null when it does not exist.</summary>
    Task<AppendInfo?> GetAppendInfoAsync(string name, CancellationToken ct) => throw new NotSupportedException("this store has no append blobs");

    // ---- block blobs (the aggregate video.json): Put Block never changes the blob's ETag; Put Block List does. Stores that cannot do them throw NotSupportedException.
    /// <summary>Put Block: stages <paramref name="data"/> as an uncommitted block of <paramref name="name"/> (the blob need not exist). No lease, no ETag condition. The block id must have the same length in every block of the blob.</summary>
    Task PutBlockAsync(string name, string blockId, byte[] data, CancellationToken ct) => throw new NotSupportedException("this store has no block blobs");
    /// <summary>Get Block List (all): ETag plus committed and uncommitted blocks; null when the blob has neither.</summary>
    Task<BlockList?> GetBlockListAsync(string name, CancellationToken ct) => throw new NotSupportedException("this store has no block blobs");
    /// <summary>Put Block List with every id as Latest (the uncommitted block of that id wins, else the committed one); uncommitted blocks not named are discarded. Conditional on <paramref name="etag"/> (If-Match; null: only while the blob has no committed content, If-None-Match: *). False on 412/409.</summary>
    Task<bool> PutBlockListAsync(string name, IReadOnlyList<string> blockIds, string? etag, CancellationToken ct) => throw new NotSupportedException("this store has no block blobs");
    /// <summary>PutIfMatchAsync that also says the ETag of the blob it wrote (null when it did not write). The default Puts, then Heads (racy); RestStore and FakeStore take it from the write itself.</summary>
    async Task<(bool Ok, string? ETag)> PutIfMatchWithETagAsync(string name, byte[] data, string? etag, CancellationToken ct)
    {
        if (!await PutIfMatchAsync(name, data, etag, ct)) return (false, null);
        return (true, (await HeadAsync(name, ct))?.ETag);
    }
}
