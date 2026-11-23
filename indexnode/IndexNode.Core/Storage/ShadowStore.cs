using System.Collections.Concurrent;
namespace IndexNode;

/// <summary>0.13.30 (`run --shadow --out-local DIR`): reads the real store, writes NOTHING to it. Every write (blobs, tags, metadata, leases, the queue log's appends, the registry) goes to an overlay: block blobs to a
/// <see cref="LocalStore"/> under DIR, append blobs (the queue log) to a copy-on-write in-memory log that is saved under DIR/append/ on <see cref="Flush"/>. A read prefers the overlay. Deletes are remembered, not sent.
/// The aggregate's block writes are dropped (a shadow run never changes videos.agg). For testing a node against the live queue with no effect on it.</summary>
public sealed class ShadowStore : IStore
{
    readonly IStore real; readonly LocalStore local; readonly string dir;
    readonly ConcurrentDictionary<string, Log0> appends = new(); readonly ConcurrentDictionary<string, bool> deleted = new();
    public long BlockedWrites; public string Dir => dir;
    sealed class Log0 { public byte[] Data = []; public bool Sealed; public readonly object Gate = new(); }
    public ShadowStore(IStore real, string dir) { this.real = real; this.dir = dir; Directory.CreateDirectory(dir); local = new LocalStore(Path.Combine(dir, "blobs")); }

    bool Gone(string n) => deleted.ContainsKey(n);
    async Task<Log0?> LogAsync(string name, CancellationToken ct)
    {
        if (appends.TryGetValue(name, out var l)) return l;
        if (Gone(name)) return null;
        var info = await real.GetAppendInfoAsync(name, ct); if (info == null) return null;
        var data = new List<byte>(); long off = 0;
        while (off < info.Length) { var r = await real.ReadAppendAsync(name, off, 4 * 1024 * 1024, ct); if (r.Data.Length == 0) break; data.AddRange(r.Data); off += r.Data.Length; }
        return appends.GetOrAdd(name, _ => new Log0 { Data = data.ToArray(), Sealed = info.Sealed });
    }
    /// <summary>Saves the overlay's append logs under DIR/append/.</summary>
    public void Flush() { var d = Path.Combine(dir, "append"); Directory.CreateDirectory(d); foreach (var kv in appends) lock (kv.Value.Gate) File.WriteAllBytes(Path.Combine(d, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(kv.Key)).ToLowerInvariant() + ".log"), kv.Value.Data); }

    // ---- reads: overlay first, then the real store
    public async Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct)
    {
        var r = (await real.ListAsync(prefix, withTags, withMetadata, ct)).Where(b => !Gone(b.Name)).ToDictionary(b => b.Name);
        foreach (var b in await local.ListAsync(prefix, withTags, withMetadata, ct)) r[b.Name] = b;
        return r.Values.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
    }
    public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct) => real.FindByTagsAsync(where, ct);
    public async Task<BlobContent?> GetAsync(string name, CancellationToken ct) => await local.GetAsync(name, ct) ?? (Gone(name) ? null : await real.GetAsync(name, ct));
    public async Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct) => await local.GetAsync(name, ct) != null ? await local.GetIfChangedAsync(name, etag, ct) : Gone(name) ? new(true, false, null, null) : await real.GetIfChangedAsync(name, etag, ct);
    public async Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct) => await local.GetTagsAsync(name, ct) ?? (Gone(name) ? null : await real.GetTagsAsync(name, ct));
    public async Task<BlobProps?> HeadAsync(string name, CancellationToken ct) => await local.HeadAsync(name, ct) ?? (Gone(name) ? null : await real.HeadAsync(name, ct));
    public async Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct) => await local.HeadAsync(name, ct) != null ? await local.GetRangeAsync(name, off, len, ifMatch, ct) : Gone(name) ? new(null, false) : await real.GetRangeAsync(name, off, len, ifMatch, ct);
    /// <summary>0.13.32: a download from the real store is kept under DIR/dl (keyed by name, size and ETag), so a repeat open is a local copy, as the node's zip and work caches make it; only writes are redirected.</summary>
    public async Task DownloadToFileAsync(string name, string path, CancellationToken ct)
    {
        if (await local.HeadAsync(name, ct) != null) { await local.DownloadToFileAsync(name, path, ct); return; }
        var h = Gone(name) ? null : await real.HeadAsync(name, ct);
        if (h == null) { await real.DownloadToFileAsync(name, path, ct); return; }
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(name + "|" + h.Size + "|" + (h.Metadata.GetValueOrDefault("idx_sha256") ?? h.ETag))))[..24];
        var cached = Path.Combine(dir, "dl", key); Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        var gate = dlGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(cached)) { var tmp = cached + "." + Guid.NewGuid().ToString("N")[..6]; await real.DownloadToFileAsync(name, tmp, ct); File.Move(tmp, cached, true); }
        }
        finally { gate.Release(); }
        File.Copy(cached, path, true);
    }
    readonly ConcurrentDictionary<string, SemaphoreSlim> dlGates = new();
    readonly ConcurrentDictionary<string, (string Path, BlobProps Props)> dlProps = new();
    /// <summary>The GET-first whole read of the real store, kept under DIR/dl: a repeat open of the same blob in this run is a local copy.</summary>
    public async Task<BlobProps?> DownloadToFileWithPropsAsync(string name, string path, CancellationToken ct)
    {
        if (await local.HeadAsync(name, ct) is { } lp) { await local.DownloadToFileAsync(name, path, ct); return lp; }
        if (Gone(name)) return null;
        if (dlProps.TryGetValue(name, out var c) && File.Exists(c.Path)) { File.Copy(c.Path, path, true); return c.Props; }
        var cached = Path.Combine(dir, "dl", Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes("w|" + name)))[..24]); Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        var gate = dlGates.GetOrAdd(cached, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(ct);
        try
        {
            if (dlProps.TryGetValue(name, out c) && File.Exists(c.Path)) { File.Copy(c.Path, path, true); return c.Props; }
            var tmp = cached + "." + Guid.NewGuid().ToString("N")[..6]; var p = await real.DownloadToFileWithPropsAsync(name, tmp, ct);
            if (p == null) return null;
            File.Move(tmp, cached, true); dlProps[name] = (cached, p); File.Copy(cached, path, true); return p;
        }
        finally { gate.Release(); }
    }

    // ---- writes: overlay only
    public Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json") => PutCore(name, data, tags, onlyIfNew, ct);
    async Task<bool> PutCore(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct)
    {
        if (onlyIfNew && await HeadAsync(name, ct) != null) return false;
        deleted.TryRemove(name, out _); return await local.PutAsync(name, data, tags, null, false, ct);
    }
    public async Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct) { deleted.TryRemove(name, out _); await local.PutFileAsync(name, path, contentType, progress, ct); }
    public async Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, CancellationToken ct)
    {
        var h = await HeadAsync(name, ct);
        if (etag == null ? h != null : h == null || h.ETag != etag) return false;
        return await PutCore(name, data, null, false, ct);
    }
    public async Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, IReadOnlyDictionary<string, string> meta, CancellationToken ct) { if (!await PutIfMatchAsync(name, data, etag, ct)) return false; await local.SetMetadataAsync(name, meta, null, ct); return true; }
    public async Task<(bool Ok, string? ETag)> PutIfMatchWithETagAsync(string name, byte[] data, string? etag, CancellationToken ct) { if (!await PutIfMatchAsync(name, data, etag, ct)) return (false, null); return (true, (await local.HeadAsync(name, ct))?.ETag); }
    public async Task<bool> PutZipAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct)
    {
        if (onlyIfNew && await HeadAsync(name, ct) != null) return false;
        await PutFileAsync(name, path, "application/zip", null, ct); await local.SetMetadataAsync(name, metadata, null, ct); if (tags is { Count: > 0 }) await local.SetTagsAsync(name, tags, null, ct); return true;
    }
    public async Task<bool> PutZipIfMatchAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, string etag, CancellationToken ct)
    {
        var h = await HeadAsync(name, ct); if (h == null || h.ETag != etag) return false; return await PutZipAsync(name, path, metadata, tags, false, ct);
    }
    public async Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct) { await Copy(name, ct); await local.SetMetadataAsync(name, metadata, null, ct); }
    public async Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct) { await Copy(name, ct); await local.SetTagsAsync(name, tags, null, ct); }
    async Task Copy(string name, CancellationToken ct) { if (await local.HeadAsync(name, ct) == null && !Gone(name) && await real.GetAsync(name, ct) is { } c) await local.PutAsync(name, c.Data, null, null, false, ct); }
    public Task DeleteAsync(string name, CancellationToken ct) { deleted[name] = true; return local.DeleteAsync(name, ct); }
    public Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct) => Task.FromResult<string?>(Guid.NewGuid().ToString());
    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct) => Task.CompletedTask;
    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct) => Task.CompletedTask;

    // ---- the queue log (append blobs): copy-on-write
    public async Task<bool> CreateAppendBlobAsync(string name, bool onlyIfNew, CancellationToken ct)
    {
        if (onlyIfNew && await LogAsync(name, ct) != null) return false;
        deleted.TryRemove(name, out _); appends[name] = new Log0(); return true;
    }
    public async Task<AppendResult> AppendBlockAsync(string name, byte[] data, long appendPos, CancellationToken ct)
    {
        var l = await LogAsync(name, ct); if (l == null) return new(AppendOutcome.NotFound);
        lock (l.Gate)
        {
            if (l.Sealed) return new(AppendOutcome.Sealed); if (l.Data.Length != appendPos) return new(AppendOutcome.PositionMismatch);
            l.Data = [.. l.Data, .. data]; return new(AppendOutcome.Ok, l.Data.Length, 1);
        }
    }
    public async Task<AppendRead> ReadAppendAsync(string name, long offset, int maxLen, CancellationToken ct)
    {
        var l = await LogAsync(name, ct); if (l == null) return new([], 0, false, 0, true);
        lock (l.Gate) { var from = (int)Math.Min(Math.Max(0, offset), l.Data.Length); var n = Math.Min(maxLen, l.Data.Length - from); return new(l.Data.AsSpan(from, n).ToArray(), l.Data.Length, l.Sealed, 1); }
    }
    public async Task<bool?> SealAppendBlobAsync(string name, long appendPos, CancellationToken ct) { var l = await LogAsync(name, ct); if (l == null) return null; lock (l.Gate) { if (l.Data.Length != appendPos) return false; l.Sealed = true; return true; } }
    public async Task<AppendInfo?> GetAppendInfoAsync(string name, CancellationToken ct) { var l = await LogAsync(name, ct); if (l == null) return null; lock (l.Gate) return new(l.Data.Length, l.Sealed, 1); }

    // ---- block blobs (the aggregate): reads pass through, writes are dropped
    public Task PutBlockAsync(string name, string blockId, byte[] data, CancellationToken ct) { Interlocked.Increment(ref BlockedWrites); return Task.CompletedTask; }
    public Task<BlockList?> GetBlockListAsync(string name, CancellationToken ct) => real.GetBlockListAsync(name, ct);
    public Task<bool> PutBlockListAsync(string name, IReadOnlyList<string> blockIds, string? etag, CancellationToken ct) { Interlocked.Increment(ref BlockedWrites); return Task.FromResult(true); }
}
