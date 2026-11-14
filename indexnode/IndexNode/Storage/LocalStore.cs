using System.Collections.Concurrent;

namespace IndexNode;

/// <summary>
/// An <see cref="IStore"/> over a local folder: a blob name is a relative path under the root. For `stages run --local`, so a stage reads and
/// writes exactly what it would in blob storage, with no network. Tags and leases are kept in memory (one process, no other nodes).
/// </summary>
public sealed class LocalStore(string root) : IStore
{
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> tags = new();
    string PathOf(string name) => Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));

    public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct) => Task.FromResult<IReadOnlyList<BlobEntry>>([]);

    public Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct)
    {
        var list = new List<BlobEntry>();
        if (Directory.Exists(root))
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/');
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                    list.Add(new BlobEntry(name, tags.GetValueOrDefault(name) ?? new Dictionary<string, string>(), new Dictionary<string, string>(), null, new FileInfo(f).Length));
            }
        return Task.FromResult<IReadOnlyList<BlobEntry>>(list.OrderBy(e => e.Name, StringComparer.Ordinal).ToList());
    }

    public async Task<BlobContent?> GetAsync(string name, CancellationToken ct) =>
        File.Exists(PathOf(name)) ? new BlobContent(await File.ReadAllBytesAsync(PathOf(name), ct), null) : null;

    public async Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct)
    {
        var c = await GetAsync(name, ct);
        return new ConditionalResult(c != null, c != null, c?.Data, null);
    }

    public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct) =>
        Task.FromResult(File.Exists(PathOf(name)) ? tags.GetValueOrDefault(name) ?? new Dictionary<string, string>() : null);

    public async Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? t, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json")
    {
        var p = PathOf(name);
        if (onlyIfNew && File.Exists(p)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllBytesAsync(p, data, ct);
        if (t != null) tags[name] = t;
        return true;
    }

    public Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct)
    {
        var p = PathOf(name); Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.Copy(path, p, true); progress?.Report(new FileInfo(p).Length);
        return Task.CompletedTask;
    }

    public Task DownloadToFileAsync(string name, string path, CancellationToken ct)
    {
        var p = PathOf(name);
        if (!File.Exists(p)) throw new StorageException(404, "BlobNotFound", $"{name} is missing under {root}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) File.Copy(p, path, true);
        return Task.CompletedTask;
    }

    public Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct) => Task.FromResult<string?>(Guid.NewGuid().ToString());
    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct) => Task.CompletedTask;
    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct) => Task.CompletedTask;
    public Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct) => Task.CompletedTask;
    public Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> t, string? leaseId, CancellationToken ct) { tags[name] = t; return Task.CompletedTask; }
    public Task DeleteAsync(string name, CancellationToken ct) { if (File.Exists(PathOf(name))) File.Delete(PathOf(name)); tags.TryRemove(name, out _); return Task.CompletedTask; }
}

/// <summary>
/// Sends every write under <paramref name="from"/> to <paramref name="to"/> instead (e.g. index/ -> index-test/gpu/run1/), and reads the redirected name
/// first, then the original. `stages run --out-prefix` uses it so a test run reads production inputs but never writes over production outputs.
/// </summary>
public sealed class RedirectStore(IStore inner, string from, string to) : IStore
{
    public IStore Inner => inner;
    string W(string name) => name.StartsWith(from, StringComparison.Ordinal) ? to + name[from.Length..] : name;

    public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct) => inner.FindByTagsAsync(where, ct);
    public Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct) => inner.ListAsync(prefix, withTags, withMetadata, ct);
    public async Task<BlobContent?> GetAsync(string name, CancellationToken ct) => await inner.GetAsync(W(name), ct) ?? await inner.GetAsync(name, ct);
    public async Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct)
    {
        var r = await inner.GetIfChangedAsync(W(name), etag, ct);
        return r.Exists ? r : await inner.GetIfChangedAsync(name, etag, ct);
    }
    public async Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct) => await inner.GetTagsAsync(W(name), ct) ?? await inner.GetTagsAsync(name, ct);
    public Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json") => inner.PutAsync(W(name), data, tags, leaseId, onlyIfNew, ct, contentType);
    public Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct) => inner.PutFileAsync(W(name), path, contentType, progress, ct);
    public Task DownloadToFileAsync(string name, string path, CancellationToken ct) => inner.DownloadToFileAsync(name, path, ct); // inputs come from the source
    public Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct) => inner.AcquireLeaseAsync(W(name), seconds, ct);
    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.RenewLeaseAsync(W(name), leaseId, ct);
    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.ReleaseLeaseAsync(W(name), leaseId, ct);
    public Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct) => inner.SetMetadataAsync(W(name), metadata, leaseId, ct);
    public Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct) => inner.SetTagsAsync(W(name), tags, leaseId, ct);
    public Task DeleteAsync(string name, CancellationToken ct) => inner.DeleteAsync(W(name), ct);
}
