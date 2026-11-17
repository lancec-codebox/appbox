namespace IndexNode;

/// <summary>
/// The local work folder of one drama pass: what a step writes (or downloads, for big files) is kept on disk, so the next step reads it
/// from there instead of fetching it again. Writes still go to the store first; job blobs (tags or a lease) are never cached.
/// </summary>
public sealed class LocalCacheStore(IStore inner, string dir) : IStore, IDisposable
{
    public const long MaxFile = 4L << 30;
    private readonly Dictionary<string, string> files = new(StringComparer.Ordinal); private readonly object gate = new();
    public string Dir => dir;
    public IStore Inner => inner;
    /// <summary>The store under any local cache layers (what the Nexis hydration checks look at).</summary>
    public static IStore Unwrap(IStore s) { while (s is LocalCacheStore c) s = c.Inner; return s; }
    public int Hits { get; private set; }
    public bool Has(string name) { lock (gate) return files.ContainsKey(name); }

    private string PathFor(string name) => System.IO.Path.Combine(dir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(name))));
    private void Remember(string name, string path) { lock (gate) files[name] = path; }
    private string? Cached(string name) { lock (gate) return files.TryGetValue(name, out var p) && File.Exists(p) ? p : null; }

    public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct) => inner.FindByTagsAsync(where, ct);
    public Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct) => inner.ListAsync(prefix, withTags, withMetadata, ct);
    public async Task<BlobContent?> GetAsync(string name, CancellationToken ct)
    {
        if (Cached(name) is { } p) { lock (gate) Hits++; return new BlobContent(await File.ReadAllBytesAsync(p, ct), null); }
        return await inner.GetAsync(name, ct);
    }
    public Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct) => inner.GetIfChangedAsync(name, etag, ct);
    public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct) => inner.GetTagsAsync(name, ct);
    public async Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json")
    {
        var r = await inner.PutAsync(name, data, tags, leaseId, onlyIfNew, ct, contentType);
        if (r && tags == null && leaseId == null) { Directory.CreateDirectory(dir); var p = PathFor(name); await File.WriteAllBytesAsync(p, data, ct); Remember(name, p); }
        return r;
    }
    public async Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct)
    {
        await inner.PutFileAsync(name, path, contentType, progress, ct);
        if (new FileInfo(path).Length <= MaxFile) { Directory.CreateDirectory(dir); var p = PathFor(name); File.Copy(path, p, true); Remember(name, p); }
    }
    public async Task DownloadToFileAsync(string name, string path, CancellationToken ct)
    {
        if (Cached(name) is { } p) { lock (gate) Hits++; File.Copy(p, path, true); return; }
        await inner.DownloadToFileAsync(name, path, ct);
        if (new FileInfo(path).Length <= MaxFile) { Directory.CreateDirectory(dir); var c = PathFor(name); File.Copy(path, c, true); Remember(name, c); } // the next step of this pass reads it from here
    }
    public Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct) => inner.AcquireLeaseAsync(name, seconds, ct);
    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.RenewLeaseAsync(name, leaseId, ct);
    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.ReleaseLeaseAsync(name, leaseId, ct);
    public Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct) => inner.SetMetadataAsync(name, metadata, leaseId, ct);
    public Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct) => inner.SetTagsAsync(name, tags, leaseId, ct);
    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        lock (gate) { if (files.Remove(name, out var p)) try { File.Delete(p); } catch { } }
        await inner.DeleteAsync(name, ct);
    }

    public void Dispose()
    {
        lock (gate) files.Clear();
        if (Environment.GetEnvironmentVariable("INDEXNODE_KEEP_WORK") == null) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
    }
}
