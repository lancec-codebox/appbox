namespace IndexNode;

public sealed record BlobEntry(
    string Name,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyDictionary<string, string> Metadata,
    string? LeaseState = null,
    long Size = 0);

public sealed record BlobContent(byte[] Data, string? ETag);

/// <summary>A blob's size, ETag and metadata (Get Blob Properties).</summary>
public sealed record BlobProps(long Size, string? ETag, IReadOnlyDictionary<string, string> Metadata);

/// <summary>A range read pinned to an ETag: <see cref="Mismatch"/> when the blob changed since (412), <see cref="Data"/> null when it is gone.</summary>
public sealed record RangeRead(byte[]? Data, bool Mismatch);

public sealed record ConditionalResult(bool Changed, bool Exists, byte[]? Data, string? ETag);

public class StorageException(int status, string? code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}

/// <summary>The lease is gone (expired or taken): the job is no longer ours.</summary>
public sealed class LeaseLostException(string message) : StorageException(409, "LeaseLost", message);

/// <summary>The SAS lacks a permission (e.g. 'f' for Find Blobs by Tags).</summary>
public sealed class ForbiddenException(string message) : StorageException(403, "AuthorizationPermissionMismatch", message);

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
    Task DeleteAsync(string name, CancellationToken ct);
}
