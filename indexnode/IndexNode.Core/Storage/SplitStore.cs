namespace IndexNode;

/// <summary>
/// Separate input and output (0.11): the uploads' stage zips under <c>videos/</c> are read from <paramref name="input"/> (the remote blob store, or a folder),
/// while <c>centers.zip</c> and everything else (the search index under index/search/) live in <paramref name="output"/> (a folder, or the blob store).
/// The input is read-only: any write that routes to it throws, so a run with --output-local never touches the remote store.
/// </summary>
public sealed class SplitStore(IStore input, IStore output, string videosPrefix) : IStore
{
    public Task<bool> CreateAppendBlobAsync(string name, bool onlyIfNew, CancellationToken ct) => output.CreateAppendBlobAsync(name, onlyIfNew, ct);
    public Task<AppendResult> AppendBlockAsync(string name, byte[] data, long appendPos, CancellationToken ct) => output.AppendBlockAsync(name, data, appendPos, ct);
    public Task<AppendRead> ReadAppendAsync(string name, long offset, int maxLen, CancellationToken ct) => output.ReadAppendAsync(name, offset, maxLen, ct);
    public Task<bool?> SealAppendBlobAsync(string name, long appendPos, CancellationToken ct) => output.SealAppendBlobAsync(name, appendPos, ct);
    public Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, CancellationToken ct) => output.PutIfMatchAsync(name, data, etag, ct);
    public Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, IReadOnlyDictionary<string, string> meta, CancellationToken ct) => output.PutIfMatchAsync(name, data, etag, meta, ct);
    public Task<AppendInfo?> GetAppendInfoAsync(string name, CancellationToken ct) => output.GetAppendInfoAsync(name, ct);
    public Task PutBlockAsync(string name, string blockId, byte[] data, CancellationToken ct) => output.PutBlockAsync(name, blockId, data, ct);
    public Task<BlockList?> GetBlockListAsync(string name, CancellationToken ct) => output.GetBlockListAsync(name, ct);
    public Task<bool> PutBlockListAsync(string name, IReadOnlyList<string> blockIds, string? etag, CancellationToken ct) => output.PutBlockListAsync(name, blockIds, etag, ct);
    public Task<(bool Ok, string? ETag)> PutIfMatchWithETagAsync(string name, byte[] data, string? etag, CancellationToken ct) => output.PutIfMatchWithETagAsync(name, data, etag, ct);
    /// <summary>True for a blob that belongs to the input side.</summary>
    bool In(string name) => name.StartsWith(videosPrefix, StringComparison.Ordinal) && !name.EndsWith("/centers.zip", StringComparison.Ordinal);
    IStore R(string name) => In(name) ? input : output;
    IStore W(string name) => In(name) ? throw new InvalidOperationException($"refusing to write {name}: it belongs to the read-only input store") : output;

    public async Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct)
    {
        var res = (await output.ListAsync(prefix, withTags, withMetadata, ct)).Where(e => !In(e.Name)).ToList();
        if (videosPrefix.StartsWith(prefix, StringComparison.Ordinal) || prefix.StartsWith(videosPrefix, StringComparison.Ordinal))
            res.AddRange((await input.ListAsync(prefix, withTags, withMetadata, ct)).Where(e => In(e.Name)));
        return res.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }
    public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct) => output.FindByTagsAsync(where, ct);
    public Task<BlobContent?> GetAsync(string name, CancellationToken ct) => R(name).GetAsync(name, ct);
    public Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct) => R(name).GetIfChangedAsync(name, etag, ct);
    public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct) => R(name).GetTagsAsync(name, ct);
    public Task<BlobProps?> HeadAsync(string name, CancellationToken ct) => R(name).HeadAsync(name, ct);
    public Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct) => R(name).GetRangeAsync(name, off, len, ifMatch, ct);
    public Task DownloadToFileAsync(string name, string path, CancellationToken ct) => R(name).DownloadToFileAsync(name, path, ct);
    public Task<BlobProps?> DownloadToFileWithPropsAsync(string name, string path, CancellationToken ct) => R(name).DownloadToFileWithPropsAsync(name, path, ct);

    public Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json") => W(name).PutAsync(name, data, tags, leaseId, onlyIfNew, ct, contentType);
    public Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct) => W(name).PutFileAsync(name, path, contentType, progress, ct);
    public Task<bool> PutZipAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct) => W(name).PutZipAsync(name, path, metadata, tags, onlyIfNew, ct);
    public Task<bool> PutZipIfMatchAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, string etag, CancellationToken ct) => W(name).PutZipIfMatchAsync(name, path, metadata, tags, etag, ct);
    public Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct) => W(name).AcquireLeaseAsync(name, seconds, ct);
    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct) => W(name).RenewLeaseAsync(name, leaseId, ct);
    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct) => W(name).ReleaseLeaseAsync(name, leaseId, ct);
    public Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct) => W(name).SetMetadataAsync(name, metadata, leaseId, ct);
    public Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct) => W(name).SetTagsAsync(name, tags, leaseId, ct);
    public Task DeleteAsync(string name, CancellationToken ct) => W(name).DeleteAsync(name, ct);
    public Task DeleteAsync(string name, string? leaseId, CancellationToken ct) => W(name).DeleteAsync(name, leaseId, ct);
}
