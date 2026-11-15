namespace IndexNode;

/// <summary>Forwards to another store and remembers which blobs this run wrote, so a redo can tell its new outputs from the old ones.</summary>
public sealed class RecordingStore(IStore inner) : IStore
{
    readonly HashSet<string> written = new(StringComparer.Ordinal); readonly object gate = new();
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
    public Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct) => inner.AcquireLeaseAsync(name, seconds, ct);
    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.RenewLeaseAsync(name, leaseId, ct);
    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.ReleaseLeaseAsync(name, leaseId, ct);
    public Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct) => inner.SetMetadataAsync(name, metadata, leaseId, ct);
    public Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct) => inner.SetTagsAsync(name, tags, leaseId, ct);
    public Task DeleteAsync(string name, CancellationToken ct) => inner.DeleteAsync(name, ct);
}
