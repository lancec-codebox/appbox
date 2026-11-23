namespace IndexNode;

/// <summary>Forwards to another store and remembers which blobs this run wrote, so a redo can tell its new outputs from the old ones.</summary>
public sealed class RecordingStore(IStore inner) : IStore
{
    public Task<bool> CreateAppendBlobAsync(string name, bool onlyIfNew, CancellationToken ct) => inner.CreateAppendBlobAsync(name, onlyIfNew, ct);
    public Task<AppendResult> AppendBlockAsync(string name, byte[] data, long appendPos, CancellationToken ct) => inner.AppendBlockAsync(name, data, appendPos, ct);
    public Task<AppendRead> ReadAppendAsync(string name, long offset, int maxLen, CancellationToken ct) => inner.ReadAppendAsync(name, offset, maxLen, ct);
    public Task<bool?> SealAppendBlobAsync(string name, long appendPos, CancellationToken ct) => inner.SealAppendBlobAsync(name, appendPos, ct);
    public Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, CancellationToken ct) => inner.PutIfMatchAsync(name, data, etag, ct);
    public Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, IReadOnlyDictionary<string, string> meta, CancellationToken ct) => inner.PutIfMatchAsync(name, data, etag, meta, ct);
    public Task<AppendInfo?> GetAppendInfoAsync(string name, CancellationToken ct) => inner.GetAppendInfoAsync(name, ct);
    public Task PutBlockAsync(string name, string blockId, byte[] data, CancellationToken ct) => inner.PutBlockAsync(name, blockId, data, ct);
    public Task<BlockList?> GetBlockListAsync(string name, CancellationToken ct) => inner.GetBlockListAsync(name, ct);
    public Task<bool> PutBlockListAsync(string name, IReadOnlyList<string> blockIds, string? etag, CancellationToken ct) => inner.PutBlockListAsync(name, blockIds, etag, ct);
    public Task<(bool Ok, string? ETag)> PutIfMatchWithETagAsync(string name, byte[] data, string? etag, CancellationToken ct) => inner.PutIfMatchWithETagAsync(name, data, etag, ct);
    public IStore Inner => inner;
    readonly HashSet<string> written = new(StringComparer.Ordinal); readonly object gate = new();
    public Task<BlobProps?> HeadAsync(string name, CancellationToken ct) => inner.HeadAsync(name, ct);
    public Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct) => inner.GetRangeAsync(name, off, len, ifMatch, ct);
    public Task<bool> PutZipIfMatchAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, string etag, CancellationToken ct) => inner.PutZipIfMatchAsync(name, path, metadata, tags, etag, ct);
    public Task<bool> PutZipAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct) => inner.PutZipAsync(name, path, metadata, tags, onlyIfNew, ct);
    public IReadOnlyCollection<string> Written { get { lock (gate) return written.ToArray(); } }
    public void Note(string name) { lock (gate) written.Add(name); }

    public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct) => inner.FindByTagsAsync(where, ct);
    public Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct) => inner.ListAsync(prefix, withTags, withMetadata, ct);
    public Task<BlobContent?> GetAsync(string name, CancellationToken ct) => inner.GetAsync(name, ct);
    public Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct) => inner.GetIfChangedAsync(name, etag, ct);
    public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct) => inner.GetTagsAsync(name, ct);
    public async Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json")
    { var r = await inner.PutAsync(name, data, tags, leaseId, onlyIfNew, ct, contentType); Note(name); return r; }
    public async Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct)
    { await inner.PutFileAsync(name, path, contentType, progress, ct); Note(name); }
    public Task DownloadToFileAsync(string name, string path, CancellationToken ct) => inner.DownloadToFileAsync(name, path, ct);
    public Task<BlobProps?> DownloadToFileWithPropsAsync(string name, string path, CancellationToken ct) => inner.DownloadToFileWithPropsAsync(name, path, ct);
    public Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct) => inner.AcquireLeaseAsync(name, seconds, ct);
    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.RenewLeaseAsync(name, leaseId, ct);
    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.ReleaseLeaseAsync(name, leaseId, ct);
    public Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct) => inner.SetMetadataAsync(name, metadata, leaseId, ct);
    public Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct) => inner.SetTagsAsync(name, tags, leaseId, ct);
    public Task DeleteAsync(string name, CancellationToken ct) => inner.DeleteAsync(name, ct);
}
