using System.Collections.Concurrent;

namespace IndexNode;

/// <summary>0.13.9: the node's local copy of the frames files (config index.local-cache-gb, default 20). A frames file is fetched whole, once (one GET, not thousands of ranged reads), and kept across claims and runs in a size-bounded LRU on disk (least recently used file goes first).
/// A file is named by drama, content hash and size, so a replaced frames file is a new local file and the old one ages out.</summary>
public sealed class FramesCache(string dir, long maxBytes)
{
    /// <summary>Set once at start-up by a node; null = no local copy (every read is a range request).</summary>
    public static FramesCache? Current { get; set; }

    public string Dir { get; } = dir;
    public long MaxBytes { get; } = maxBytes;
    public int Downloads; public long DownloadedBytes;
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
            await store.DownloadToFileAsync(blob, tmp, CancellationToken.None);
            var len = new FileInfo(tmp).Length;
            if (size > 0 && len != size) throw new StaleZipException(blob);
            File.Move(tmp, path, true);
            Interlocked.Increment(ref Downloads); Interlocked.Add(ref DownloadedBytes, len);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        Evict(path);
        return path;
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
                foreach (var f in files) { if (total <= MaxBytes) break; if (f.FullName == keep) continue; { long len = f.Length; try { f.Delete(); total -= len; } catch { } } }
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
