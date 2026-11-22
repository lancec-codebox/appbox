using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>0.13.9: the node's local copy of the frames files (config index.local-cache-gb, default 20). A frames file is fetched whole, once (one GET, not thousands of ranged reads), and kept across claims and runs in a size-bounded LRU on disk (least recently used file goes first).
/// A file is named by drama, content hash and size, so a replaced frames file is a new local file and the old one ages out.</summary>
public sealed class FramesCache(string dir, long maxBytes)
{
    /// <summary>Set once at start-up by a node; null = no local copy (a lookup fetches each file into memory for that check only).</summary>
    public static FramesCache? Current { get; set; }

    public string Dir { get; } = dir;
    public long MaxBytes { get; } = maxBytes;
    public int Downloads; public long DownloadedBytes;
    /// <summary>Files deleted by the LRU eviction; test hooks: called with the local path when a file is evicted / lands in the cache.</summary>
    /// <summary>Eviction never deletes a file used in the last this-many seconds (a check's own files are in use while it reads them).</summary>
    public int ProtectSeconds = 60;
    public int Evicted; public Action<string>? OnEvicted, OnAdded;
    public JsonObject Stats() => new() { ["files"] = Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.bin").Length : 0, ["maxMb"] = MaxBytes / (1024 * 1024), ["hits"] = Hits, ["downloads"] = Downloads, ["evicted"] = Evicted };
    readonly ConcurrentDictionary<string, Lazy<Task<string>>> inflight = new(StringComparer.Ordinal);
    readonly object evictGate = new();

    public string PathOf(string blob, string? sha, long size)
    {
        var id = Path.GetFileNameWithoutExtension(blob);
        return Path.Combine(Dir, $"{id}.{(string.IsNullOrEmpty(sha) ? "x" : sha[..Math.Min(12, sha.Length)])}.{size}.bin");
    }

    /// <summary>The local path of the whole file, downloading it first when this node has none. Concurrent callers share one download.</summary>
    public async Task<string> EnsureAsync(IStore store, string blob, string? sha, long size, CancellationToken ct)
    {
        var path = PathOf(blob, sha, size);
        if (File.Exists(path)) { Touch(path); return path; }
        var lazy = inflight.GetOrAdd(path, _ => new Lazy<Task<string>>(() => DownloadAsync(store, blob, path, size)));
        try { return await lazy.Value.WaitAsync(ct); }
        finally { if (lazy.Value.IsCompleted) inflight.TryRemove(path, out _); }
    }

    async Task<string> DownloadAsync(IStore store, string blob, string path, long size)
    {
        Directory.CreateDirectory(Dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".part";
        try
        {
            await FetchGate.WaitAsync();
            try { await store.DownloadToFileAsync(blob, tmp, CancellationToken.None); } finally { FetchGate.Release(); }
            var len = new FileInfo(tmp).Length;
            if (size > 0 && len != size) throw new StaleZipException(blob);
            File.Move(tmp, path, true);
            Interlocked.Increment(ref Downloads); Interlocked.Add(ref DownloadedBytes, len); OnAdded?.Invoke(path);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        Evict(path);
        return path;
    }

    /// <summary>0.13.21: at most this many whole-file GETs are in flight in the process (a vote-first check fetches up to 20 files at once).</summary>
    public const int FetchConcurrency = 20;
    public static readonly SemaphoreSlim FetchGate = new(FetchConcurrency, FetchConcurrency);
    public int Hits;

    /// <summary>The local path when the whole file is here (no fetch, no wait); otherwise null. For readers that only need a small part of a file and read it by range.</summary>
    public string? TryLocal(string blob, string? sha, long size)
    {
        var path = PathOf(blob, sha, size);
        if (File.Exists(path)) { Touch(path); Interlocked.Increment(ref Hits); return path; }
        return null;
    }

    /// <summary>0.13.21 (a lookup): the local path of the whole file, or ONE GET now (concurrent callers share it) and the file is kept in the LRU cache. Path null when the file is larger than the whole cache (the caller then holds it in memory).</summary>
    public async Task<(string? Path, bool Fetched)> LocalOrFetchAsync(IStore store, string blob, string? sha, long size, CancellationToken ct)
    {
        var path = PathOf(blob, sha, size);
        if (File.Exists(path)) { Touch(path); Interlocked.Increment(ref Hits); return (path, false); }
        if (size > MaxBytes) return (null, false);
        return (await EnsureAsync(store, blob, sha, size, ct), true);
    }

    static void Touch(string path) { try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { } }

    /// <summary>Deletes the least recently used files until the folder fits (the file just written stays).</summary>
    public void Evict(string keep)
    {
        lock (evictGate)
        {
            try
            {
                var files = new DirectoryInfo(Dir).GetFiles("*.bin").OrderBy(f => f.LastWriteTimeUtc).ToList(); long total = files.Sum(f => f.Length);
                foreach (var f in files) { if (total <= MaxBytes) break; if (f.FullName == keep || DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromSeconds(ProtectSeconds)) continue; { long len = f.Length; try { f.Delete(); total -= len; Interlocked.Increment(ref Evicted); OnEvicted?.Invoke(f.FullName); } catch { } } }
            }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void Configure(NodeConfig cfg)
    {
        var gb = cfg.Search?.LocalCacheGb ?? 20;
        Current = gb <= 0 ? null : new FramesCache(Path.Combine(NodeConfig.ConfigDir(), "frames-cache"), (long)(gb * (1L << 30)));
    }
}
