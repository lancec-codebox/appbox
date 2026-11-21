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

    // 0.13.16: lookups never wait for a whole-file download. A miss is read by range and the file is queued here; two workers fill the queue in the background, pausing while any lookup reads.
    public const int FillConcurrency = 2;
    public int Hits, RangedMisses, FillsQueued, FillsDone, FillsFailed;
    long lastLookupTicks; int fillWorkers;
    readonly ConcurrentQueue<(IStore Store, string Blob, string? Sha, long Size)> fillQueue = new();
    readonly ConcurrentDictionary<string, byte> queued = new(StringComparer.Ordinal);
    /// <summary>Test hook: how long (ms) a lookup counts as active after its last read.</summary>
    public int PauseMs = 750;

    /// <summary>Marks a lookup read in progress (FramesReader opens and reads call it): background fills wait until none has happened for PauseMs.</summary>
    public void NoteLookup() => Interlocked.Exchange(ref lastLookupTicks, Environment.TickCount64);
    bool LookupActive => Environment.TickCount64 - Interlocked.Read(ref lastLookupTicks) < PauseMs;

    /// <summary>The local path when the whole file is here (a hit); otherwise null, the miss is counted and the file queued for a background fill.</summary>
    public string? TryLocalOrQueue(IStore store, string blob, string? sha, long size)
    {
        NoteLookup();
        var path = PathOf(blob, sha, size);
        if (File.Exists(path)) { Touch(path); Interlocked.Increment(ref Hits); return path; }
        Interlocked.Increment(ref RangedMisses);
        if (size > MaxBytes) return null;
        if (queued.TryAdd(path, 0)) { Interlocked.Increment(ref FillsQueued); fillQueue.Enqueue((store, blob, sha, size)); StartWorkers(); }
        return null;
    }

    void StartWorkers()
    {
        while (true)
        {
            int n = Volatile.Read(ref fillWorkers); if (n >= FillConcurrency) return;
            if (Interlocked.CompareExchange(ref fillWorkers, n + 1, n) == n) { _ = Task.Run(FillLoopAsync); }
        }
    }

    async Task FillLoopAsync()
    {
        try
        {
            while (fillQueue.TryDequeue(out var j))
            {
                var path = PathOf(j.Blob, j.Sha, j.Size);
                try
                {
                    while (LookupActive) await Task.Delay(50);
                    if (!File.Exists(path)) await EnsureAsync(j.Store, j.Blob, j.Sha, j.Size, CancellationToken.None);
                    Interlocked.Increment(ref FillsDone);
                }
                catch (Exception) { Interlocked.Increment(ref FillsFailed); }
                finally { queued.TryRemove(path, out _); }
            }
        }
        finally { Interlocked.Decrement(ref fillWorkers); if (!fillQueue.IsEmpty) StartWorkers(); }
    }

    /// <summary>Waits until the background fill queue is empty and idle (tests, shutdown).</summary>
    public async Task WaitFillsAsync(int timeoutMs = 60000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while ((!fillQueue.IsEmpty || Volatile.Read(ref fillWorkers) > 0 || !queued.IsEmpty) && Environment.TickCount64 < end) await Task.Delay(20);
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
