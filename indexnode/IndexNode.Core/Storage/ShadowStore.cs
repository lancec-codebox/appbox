using System.Collections.Concurrent;
namespace IndexNode;

/// <summary>0.13.30 (`run --shadow --out-local DIR`): reads the real store, writes NOTHING to it. Every write (blobs, tags, metadata, leases, the queue log's appends, the registry) goes to an overlay: block blobs to a
/// <see cref="LocalStore"/> under DIR, append blobs (the queue log) to a copy-on-write in-memory log that is saved under DIR/append/ on <see cref="Flush"/>. A read prefers the overlay. Deletes are remembered, not sent.
/// The aggregate's block writes are dropped (a shadow run never changes videos.agg). For testing a node against the live queue with no effect on it.</summary>
public sealed class ShadowStore : IStore
{
    readonly IStore real; readonly LocalStore local; readonly string dir;
    readonly ConcurrentDictionary<string, Log0> appends = new(); readonly ConcurrentDictionary<string, bool> deleted = new();
    public long BlockedWrites; public string Dir => dir; public IStore Real => real;
    /// <summary>0.13.81: an append blob in the overlay = the real blob's first <see cref="Base"/> bytes (frozen at the first touch, read from the real store on demand with plain ranges) + the shadow's own <see cref="Tail"/>.
    /// A blob made in the shadow has Base 0. Never a copy of the real bytes: the frame codes log is binary and 1.5 GB.</summary>
    sealed class Log0 { public long Base; public byte[] Tail = []; public bool Sealed; public readonly object Gate = new(); public long Length => Base + Tail.Length; }
    public ShadowStore(IStore real, string dir) { this.real = real; this.dir = dir; Directory.CreateDirectory(dir); local = new LocalStore(Path.Combine(dir, "blobs")); }

    /// <summary>The shadow under these wrappers, if any (the aggregate's commit loop waits to see its own block, which a shadow never stores: it asks first).</summary>
    public static ShadowStore? Find(IStore? s) { for (int i = 0; i < 10 && s != null; i++) { if (s is ShadowStore sh) return sh; s = s switch { MeteredStore m => m.Inner, RecordingStore r => r.Inner, LocalCacheStore l => l.Inner, StageZipStore z => z.Inner, RedirectStore d => d.Inner, _ => null }; } return null; }
    public void NoteBlockedWrite() => Interlocked.Increment(ref BlockedWrites);

    bool Gone(string n) => deleted.ContainsKey(n);
    async Task<Log0?> LogAsync(string name, CancellationToken ct)
    {
        if (appends.TryGetValue(name, out var l)) return l;
        if (Gone(name)) return null;
        var info = await real.GetAppendInfoAsync(name, ct); if (info == null) return null;
        return appends.GetOrAdd(name, _ => new Log0 { Base = info.Length, Sealed = info.Sealed });
    }

    /// <summary>Bytes [off, off+len) of an overlay log: the base part from the real store (plain range: binary-safe), the rest from the shadow's tail.</summary>
    async Task<byte[]> ReadLogAsync(string name, Log0 l, long off, int len, CancellationToken ct)
    {
        long total; long bas; byte[] tail; lock (l.Gate) { total = l.Length; bas = l.Base; tail = l.Tail; }
        off = Math.Clamp(off, 0, total); len = (int)Math.Min(len, total - off); if (len <= 0) return [];
        var outp = new byte[len]; int got = 0;
        if (off < bas)
        {
            int n = (int)Math.Min(len, bas - off);
            var r = await real.GetRangeAsync(name, off, n, null, ct);
            var d = r.Data ?? throw new InvalidDataException($"{name}: the real log is shorter than the {bas} bytes the shadow saw");
            if (d.Length < n) throw new InvalidDataException($"{name}: the real log answered {d.Length} of {n} bytes at {off}");
            Buffer.BlockCopy(d, 0, outp, 0, n); got = n;
        }
        if (got < len) Buffer.BlockCopy(tail, (int)(off + got - bas), outp, got, len - got);
        return outp;
    }
    /// <summary>Saves the overlay's append logs under DIR/append/.</summary>
    /// <summary>0.13.81: the file holds the shadow's own appends only; "&lt;hex&gt;.base" says how many bytes of the real blob came before them.</summary>
    public void Flush() { var d = Path.Combine(dir, "append"); Directory.CreateDirectory(d); foreach (var kv in appends) lock (kv.Value.Gate) { var f = Path.Combine(d, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(kv.Key)).ToLowerInvariant()); File.WriteAllBytes(f + ".log", kv.Value.Tail); File.WriteAllText(f + ".base", kv.Value.Base.ToString(System.Globalization.CultureInfo.InvariantCulture)); } }

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
    public async Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct) =>
        appends.TryGetValue(name, out var al) ? new(await ReadLogAsync(name, al, off, len, ct), false)   // 0.13.81: an append blob the shadow has touched reads as the shadow sees it
        : await local.HeadAsync(name, ct) != null ? await local.GetRangeAsync(name, off, len, ifMatch, ct) : Gone(name) ? new(null, false) : await real.GetRangeAsync(name, off, len, ifMatch, ct);
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
    /// <summary>0.13.34: forgets (and deletes) the kept whole downloads of the blobs whose name contains <paramref name="part"/>: a claim that ended needs none of them.</summary>
    public void DropDownloads(string part)
    {
        foreach (var kv in dlProps.Where(k => k.Key.Contains(part, StringComparison.Ordinal)).ToList())
            if (dlProps.TryRemove(kv.Key, out var c)) try { File.Delete(c.Path); } catch { }
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
            if (l.Sealed) return new(AppendOutcome.Sealed); if (appendPos >= 0 && l.Length != appendPos) return new(AppendOutcome.PositionMismatch);
            l.Tail = [.. l.Tail, .. data]; return new(AppendOutcome.Ok, l.Length, 1);
        }
    }
    public async Task<AppendRead> ReadAppendAsync(string name, long offset, int maxLen, CancellationToken ct)
    {
        var l = await LogAsync(name, ct); if (l == null) return new([], 0, false, 0, true);
        long len; bool sealedNow; lock (l.Gate) { len = l.Length; sealedNow = l.Sealed; }
        return new(await ReadLogAsync(name, l, offset, maxLen, ct), len, sealedNow, 1);
    }
    public async Task<bool?> SealAppendBlobAsync(string name, long appendPos, CancellationToken ct) { var l = await LogAsync(name, ct); if (l == null) return null; lock (l.Gate) { if (l.Length != appendPos) return false; l.Sealed = true; return true; } }
    public async Task<AppendInfo?> GetAppendInfoAsync(string name, CancellationToken ct) { var l = await LogAsync(name, ct); if (l == null) return null; lock (l.Gate) return new(l.Length, l.Sealed, 1); }

    // ---- block blobs (the aggregate): reads pass through, writes are dropped
    public Task PutBlockAsync(string name, string blockId, byte[] data, CancellationToken ct) { Interlocked.Increment(ref BlockedWrites); return Task.CompletedTask; }
    public Task<BlockList?> GetBlockListAsync(string name, CancellationToken ct) => real.GetBlockListAsync(name, ct);
    public Task<bool> PutBlockListAsync(string name, IReadOnlyList<string> blockIds, string? etag, CancellationToken ct) { Interlocked.Increment(ref BlockedWrites); return Task.FromResult(true); }
}
