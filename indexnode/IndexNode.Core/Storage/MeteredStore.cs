namespace IndexNode;

/// <summary>0.13.18: counts every call of the wrapped store through a <see cref="StoreMeter"/> (per call site and op, failures apart, the breaker). RestStore meters itself at the HTTP layer; this wrapper is for stores that do not (FakeStore in tests, a local folder) so a one-item run reports the same table.</summary>
public sealed class MeteredStore(IStore inner, StoreMeter? meter = null) : IStore
{
    public IStore Inner => inner;
    public StoreMeter Meter { get; } = meter ?? StoreMeter.Current;
    string B(string op, string? name) => Meter.Before(op, name);
    T F<T>(string site, string op, string? name, T r, bool missing) { if (missing) Meter.Failed(site, op, name, 404); return r; }

    public async Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct) { B("list", where); return await inner.FindByTagsAsync(where, ct); }
    public async Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct) { B("list", prefix); return await inner.ListAsync(prefix, withTags, withMetadata, ct); }
    public async Task<BlobContent?> GetAsync(string name, CancellationToken ct) { var s = B("get", name); var r = await inner.GetAsync(name, ct); return F(s, "get", name, r, r == null); }
    public async Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct) { var s = B("get", name); var r = await inner.GetIfChangedAsync(name, etag, ct); if (!r.Exists) Meter.Failed(s, "get", name, 404); else if (!r.Changed) Meter.Failed(s, "get", name, 304); return r; }
    public async Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct) { var s = B("tags", name); var r = await inner.GetTagsAsync(name, ct); return F(s, "tags", name, r, r == null); }
    public async Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json") { var s = B("put", name); var r = await inner.PutAsync(name, data, tags, leaseId, onlyIfNew, ct, contentType); if (!r) Meter.Failed(s, "put", name, 412); return r; }
    public async Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct) { B("put", name); await inner.PutFileAsync(name, path, contentType, progress, ct); }
    public async Task DownloadToFileAsync(string name, string path, CancellationToken ct) { B("get", name); await inner.DownloadToFileAsync(name, path, ct); }
    public async Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct) { var s = B("lease", name); var r = await inner.AcquireLeaseAsync(name, seconds, ct); return F(s, "lease", name, r, r == null); }
    public async Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct) { B("lease", name); await inner.RenewLeaseAsync(name, leaseId, ct); }
    public async Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct) { B("lease", name); await inner.ReleaseLeaseAsync(name, leaseId, ct); }
    public async Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct) { B("meta", name); await inner.SetMetadataAsync(name, metadata, leaseId, ct); }
    public async Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct) { B("meta", name); await inner.SetTagsAsync(name, tags, leaseId, ct); }
    public async Task<bool> SetTagsIfAsync(string name, IReadOnlyDictionary<string, string> tags, IReadOnlyDictionary<string, string> expect, CancellationToken ct) { var s = B("meta", name); var r = await inner.SetTagsIfAsync(name, tags, expect, ct); if (!r) Meter.Failed(s, "meta", name, 412); return r; }
    public async Task DeleteAsync(string name, string? leaseId, CancellationToken ct) { B("delete", name); await inner.DeleteAsync(name, leaseId, ct); }
    public async Task DeleteAsync(string name, CancellationToken ct) { B("delete", name); await inner.DeleteAsync(name, ct); }
    public async Task<BlobProps?> HeadAsync(string name, CancellationToken ct) { var s = B("head", name); var r = await inner.HeadAsync(name, ct); return F(s, "head", name, r, r == null); }
    public async Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct) { var s = B("range", name); var r = await inner.GetRangeAsync(name, off, len, ifMatch, ct); if (r.Data == null) Meter.Failed(s, "range", name, r.Mismatch ? 412 : 404); return r; }
    public async Task<bool> PutZipAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct) { B("put", name); return await inner.PutZipAsync(name, path, metadata, tags, onlyIfNew, ct); }
    public async Task<bool> PutZipIfMatchAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, string etag, CancellationToken ct) { B("put", name); return await inner.PutZipIfMatchAsync(name, path, metadata, tags, etag, ct); }
    public async Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, CancellationToken ct) { var s = B("put", name); var r = await inner.PutIfMatchAsync(name, data, etag, ct); if (!r) Meter.Failed(s, "put", name, 412); return r; }
    public async Task<bool> CreateAppendBlobAsync(string name, bool onlyIfNew, CancellationToken ct) { B("append", name); return await inner.CreateAppendBlobAsync(name, onlyIfNew, ct); }
    public async Task<AppendResult> AppendBlockAsync(string name, byte[] data, long appendPos, CancellationToken ct) { var s = B("append", name); var r = await inner.AppendBlockAsync(name, data, appendPos, ct); if (r.Outcome != AppendOutcome.Ok) Meter.Failed(s, "append", name, r.Outcome == AppendOutcome.NotFound ? 404 : r.Outcome == AppendOutcome.Sealed ? 409 : 412); return r; }
    public async Task<AppendRead> ReadAppendAsync(string name, long offset, int maxLen, CancellationToken ct) { var s = B("range", name); var r = await inner.ReadAppendAsync(name, offset, maxLen, ct); if (r.NotFound) Meter.Failed(s, "range", name, 404); return r; }
    public async Task<bool?> SealAppendBlobAsync(string name, long appendPos, CancellationToken ct) { B("append", name); return await inner.SealAppendBlobAsync(name, appendPos, ct); }
    public async Task<AppendInfo?> GetAppendInfoAsync(string name, CancellationToken ct) { var s = B("head", name); var r = await inner.GetAppendInfoAsync(name, ct); return F(s, "head", name, r, r == null); }
}
