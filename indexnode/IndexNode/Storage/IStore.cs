namespace IndexNode;

public sealed record BlobEntry(
    string Name,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyDictionary<string, string> Metadata,
    string? LeaseState = null,
    long Size = 0);

public sealed record BlobContent(byte[] Data, string? ETag);

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
    Task DeleteAsync(string name, CancellationToken ct);
}
