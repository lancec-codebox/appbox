using System.Text.RegularExpressions;

namespace IndexNode.Tests;

/// <summary>In-memory store with lease semantics, for the job state machine tests.</summary>
public sealed class FakeStore : IStore
{
    public sealed class Blob
    {
        public byte[] Data = [];
        public Dictionary<string, string> Tags = new();
        public Dictionary<string, string> Meta = new();
        public string? Lease;
        public DateTimeOffset LeaseUntil;
        public int Version;
        public bool Append, Sealed;
        public int Blocks;
        public DateTimeOffset Created = DateTimeOffset.UtcNow;
    }

    /// <summary>The store's clock. When set, a lease lapses once it passes (acquire time + seconds); null: leases never lapse (the old behaviour).</summary>
    public Func<DateTimeOffset>? Clock;
    private DateTimeOffset Now => Clock?.Invoke() ?? DateTimeOffset.UtcNow;
    private void Lapse(Blob b) { if (Clock != null && b.Lease != null && b.LeaseUntil <= Now) b.Lease = null; }
    /// <summary>Counts of every call by kind, for the op-count tests.</summary>
    public readonly Dictionary<string, int> Ops = new();
    private void Op(string k) { Ops[k] = Ops.GetValueOrDefault(k) + 1; }
    /// <summary>Test hook: runs right before the next Append Block is checked (a racing writer gets in first).</summary>
    public Action<string>? BeforeAppend;

    public readonly Dictionary<string, Blob> Blobs = new();
    public readonly Dictionary<string, int> Renews = new();
    public readonly Dictionary<string, int> Puts = new();
    public readonly List<string> Calls = new();
    public readonly Dictionary<string, int> LeaseSeconds = new();
    public readonly List<(string Name, int Seconds)> LeaseLog = new();
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Trace = new();
    private void T(string what, string name, string? extra = null) => Trace.Enqueue($"{DateTime.UtcNow:ss.fff} t{Environment.CurrentManagedThreadId} {what} {name} {extra}");
    public bool ForbidFind;
    public IReadOnlyList<BlobEntry>? StaleFindResult; // Find Blobs by Tags answers with this fixed (old) result
    public bool StaleFind; // Find Blobs by Tags answers empty, as when the tag index lags
    private readonly object gate = new();

    public Blob this[string name] { get { lock (gate) return Blobs[name]; } }
    public List<string> Names(string prefix) { lock (gate) return Blobs.Keys.Where(k => k.StartsWith(prefix)).ToList(); }
    public bool Exists(string name) { lock (gate) return Blobs.ContainsKey(name); }
    public string? LeaseOf(string name)
    {
        lock (gate)
        {
            if (Blobs.TryGetValue(name, out var b)) return b.Lease;
            if (ParseJobName(name) is { } n && Blobs.TryGetValue(n.Prefix + "jobs/active/" + n.Id + ".json", out var a)) return a.Lease;
            return null;
        }
    }
    public int RenewCount(string name) { lock (gate) return Renews.GetValueOrDefault(name); }
    public int PutCount(string name) { lock (gate) return Puts.GetValueOrDefault(name); }
    // ---- 0.13.0 queue shims: a job "blob name" (jobs/<stage>/<id>.json) that no longer exists as a blob is looked up in the queue log, so the old assertions keep working on the new state
    private static (string Prefix, string Stage, string Id)? ParseJobName(string name)
    {
        var i = name.IndexOf("jobs/", StringComparison.Ordinal); if (i < 0) return null;
        var rest = name[(i + 5)..]; var slash = rest.IndexOf('/'); if (slash < 0) return null;
        return (name[..i], rest[..slash], Path.GetFileNameWithoutExtension(rest[(slash + 1)..]));
    }
    /// <summary>The queue as the log says it is now (every segment replayed).</summary>
    public QueueState Replay(string prefix)
    {
        var st = new QueueState();
        lock (gate)
            for (int n = 1; Blobs.TryGetValue(n == 1 ? prefix + "jobs/queue.log" : $"{prefix}jobs/queue.{n}.log", out var b); n++)
                foreach (var l in System.Text.Encoding.UTF8.GetString(b.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries)) if (QLine.Parse(l) is { } q) st.Apply(q);
        return st;
    }
    public QEntry? QEntry(Paths p, string subject, string stage) => Replay(p.Prefix).Entry(subject, stage);
    public JobDescriptor Job(string name)
    {
        lock (gate) { if (Blobs.TryGetValue(name, out var b)) return JobDescriptor.Parse(b.Data); }
        if (ParseJobName(name) is { } n && Replay(n.Prefix).Entry(n.Id, n.Stage) is { } e) { var j = e.Job ?? new JobDescriptor { JobId = n.Id, Stage = n.Stage }; j.Attempts = e.Attempts; j.LastError = e.LastError; j.Permanent = e.Permanent; j.FinishedAt ??= e.FinishedAt; j.NodeId ??= e.Node; return j; }
        throw new KeyNotFoundException(name);
    }
    public string State(string name)
    {
        lock (gate) { if (Blobs.TryGetValue(name, out var b)) return b.Tags[Tags.State]; }
        if (ParseJobName(name) is { } n && Replay(n.Prefix).Entry(n.Id, n.Stage) is { } e) return e.State;
        throw new KeyNotFoundException(name);
    }
    /// <summary>A queue entry exists for this old-style job name.</summary>
    public bool InQueue(string name) => ParseJobName(name) is { } n && Replay(n.Prefix).Entry(n.Id, n.Stage) != null;

    /// <summary>Adds a pending entry to the queue log (0.13.0): what the old tests did with a job blob.</summary>
    public async Task AddJob(Paths p, string stage, string id, string added, string? due = null, int priority = 0)
    {
        var url = "https://www.youtube.com/watch?v=" + id.PadRight(11, 'x');
        var job = stage == "download" ? JobDescriptor.NewDownload(url, 480, null) : JobDescriptor.NewProcess(id);
        job.Stage = stage; job.JobId = id; job.AddedAt = added; job.Priority = priority;
        var line = QLine.Add(id, stage, job, added, priority, due) .ToJson() + "\n";
        await CreateAppendBlobAsync(p.QueueLog, true, default);
        for (int i = 0; i < 20; i++)
        {
            var info = await GetAppendInfoAsync(p.QueueLog, default);
            var r = await AppendBlockAsync(p.QueueLog, System.Text.Encoding.UTF8.GetBytes(line), info!.Length, default);
            if (r.Outcome == AppendOutcome.Ok) return;
        }
        throw new InvalidOperationException("AddJob: the log kept changing");
    }

    /// <summary>Azure's Find Blobs by Tags returns only the tags the where expression names (verified on the real store): the fake does the same, so no code can lean on tags it did not ask for.</summary>
    private static BlobEntry OnlyQueried(BlobEntry e, string where)
    {
        var named = Regex.Matches(where, "\"(?<k>[^\"]+)\"").Select(m => m.Groups["k"].Value).ToHashSet();
        return e with { Tags = e.Tags.Where(t => named.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value) };
    }

    private static BlobEntry Entry(string name, Blob b) =>
        new(name, new Dictionary<string, string>(b.Tags), new Dictionary<string, string>(b.Meta), b.Lease != null ? "leased" : "available", b.Data.Length, b.Created);

    public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct)
    {
        lock (gate)
        {
            Calls.Add("find");
            if (ForbidFind) throw new ForbiddenException("no f permission");
            if (StaleFindResult != null) return Task.FromResult(StaleFindResult);
            if (StaleFind) return Task.FromResult<IReadOnlyList<BlobEntry>>([]);
            var conds = Regex.Matches(where, "\"(?<k>[^\"]+)\"='(?<v>[^']*)'").Select(m => (m.Groups["k"].Value, m.Groups["v"].Value)).ToList();
            var lt = Regex.Matches(where, "\"(?<k>[^\"]+)\" < '(?<v>[^']*)'").Select(m => (m.Groups["k"].Value, m.Groups["v"].Value)).ToList();
            var le = Regex.Matches(where, "\"(?<k>[^\"]+)\" <= '(?<v>[^']*)'").Select(m => (m.Groups["k"].Value, m.Groups["v"].Value)).ToList();
            var ge = Regex.Matches(where, "\"(?<k>[^\"]+)\" >= '(?<v>[^']*)'").Select(m => (m.Groups["k"].Value, m.Groups["v"].Value)).ToList();
            IReadOnlyList<BlobEntry> r = Blobs.Where(b => conds.All(c => b.Value.Tags.GetValueOrDefault(c.Item1) == c.Item2)
                && ge.All(c => b.Value.Tags.TryGetValue(c.Item1, out var tv) && string.CompareOrdinal(tv, c.Item2) >= 0)
                && lt.All(c => b.Value.Tags.TryGetValue(c.Item1, out var tv) && string.CompareOrdinal(tv, c.Item2) < 0)
                && le.All(c => b.Value.Tags.TryGetValue(c.Item1, out var tv) && string.CompareOrdinal(tv, c.Item2) <= 0)).Select(b => Entry(b.Key, b.Value)).Select(e => OnlyQueried(e, where)).ToList();
            return Task.FromResult(r);
        }
    }

    public Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct)
    {
        lock (gate)
        {
            Calls.Add("list"); Op("list");
            foreach (var kv in Blobs) Lapse(kv.Value);
            IReadOnlyList<BlobEntry> r = Blobs.Where(b => b.Key.StartsWith(prefix)).OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => Entry(b.Key, b.Value)).ToList();
            return Task.FromResult(r);
        }
    }

    public Task<BlobContent?> GetAsync(string name, CancellationToken ct)
    {
        lock (gate) { Op("get"); return Task.FromResult(Blobs.TryGetValue(name, out var b) ? new BlobContent(b.Data, "\"v" + b.Version + "\"") : null); }
    }

    public Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct)
    {
        lock (gate)
        {
            if (!Blobs.TryGetValue(name, out var b)) return Task.FromResult(new ConditionalResult(true, false, null, null));
            var cur = "\"v" + b.Version + "\"";
            return Task.FromResult(cur == etag ? new ConditionalResult(false, true, null, etag) : new ConditionalResult(true, true, b.Data, cur));
        }
    }

    public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyDictionary<string, string>?>(Blobs.TryGetValue(name, out var b) ? new Dictionary<string, string>(b.Tags) : null);
    }

    /// <summary>Head with a real ETag ("v&lt;version&gt;", as GetAsync gives), size and metadata.</summary>
    public Task<BlobProps?> HeadAsync(string name, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<BlobProps?>(Blobs.TryGetValue(name, out var b) ? new BlobProps(b.Data.Length, "\"v" + b.Version + "\"", new Dictionary<string, string>(b.Meta)) : null);
    }

    public int ZipPuts, ZipConflicts;
    /// <summary>Set to change the named blob (a concurrent writer) right before its next PutZipIfMatchAsync is checked.</summary>
    public Action<string>? BeforeIfMatch;
    public Task<bool> PutZipAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct)
    {
        lock (gate)
        {
            if (Blobs.TryGetValue(name, out var o) && onlyIfNew) return Task.FromResult(false);
            var b = o ?? (Blobs[name] = new Blob());
            b.Data = File.ReadAllBytes(path); b.Meta = new(metadata); b.Tags = tags == null ? new() : new(tags); b.Version++; ZipPuts++;
            return Task.FromResult(true);
        }
    }
    public Task<bool> PutZipIfMatchAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, string etag, CancellationToken ct)
    {
        lock (gate)
        {
            BeforeIfMatch?.Invoke(name); BeforeIfMatch = null;
            if (!Blobs.TryGetValue(name, out var b) || "\"v" + b.Version + "\"" != etag) { ZipConflicts++; return Task.FromResult(false); }
            b.Data = File.ReadAllBytes(path); b.Meta = new(metadata); b.Tags = tags == null ? new() : new(tags); b.Version++; ZipPuts++;
            return Task.FromResult(true);
        }
    }

    public Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct)
    {
        lock (gate)
        {
            if (!Blobs.TryGetValue(name, out var b)) return Task.FromResult(new RangeRead(null, false));
            if (ifMatch != null && "\"v" + b.Version + "\"" != ifMatch) return Task.FromResult(new RangeRead(null, true));
            return Task.FromResult(new RangeRead(b.Data.AsSpan((int)Math.Min(off, b.Data.Length), (int)Math.Max(0, Math.Min(len, b.Data.Length - off))).ToArray(), false));
        }
    }

    public Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json")
    {
        lock (gate)
        {
            Puts[name] = Puts.GetValueOrDefault(name) + 1; Op("put");
            if (Blobs.TryGetValue(name, out var lb)) Lapse(lb);
            T("put", name, (tags != null && tags.TryGetValue(Tags.State, out var st) ? st : "-") + " lease=" + leaseId + " new=" + onlyIfNew);
            if (Blobs.TryGetValue(name, out var b))
            {
                if (onlyIfNew) return Task.FromResult(false);
                if (b.Lease != null && b.Lease != leaseId) throw new StorageException(412, "LeaseIdMismatchWithBlobOperation", "lease mismatch on put");
            }
            else b = Blobs[name] = new Blob { Created = Now };
            b.Data = data; b.Tags = tags == null ? new() : new(tags); b.Meta = new(); b.Version++;
            return Task.FromResult(true);
        }
    }

    /// <summary>Set to change the named blob (a concurrent writer) right before the next PutIfMatchAsync is checked (single use; runs outside the lock).</summary>
    public Action<string>? BeforePutIfMatch;
    public Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, CancellationToken ct)
    {
        Action<string>? hook; lock (gate) { hook = BeforePutIfMatch; BeforePutIfMatch = null; }
        hook?.Invoke(name);
        lock (gate)
        {
            if (!Blobs.TryGetValue(name, out var b))
            {
                if (etag != null) return Task.FromResult(false);
                Blobs[name] = new Blob { Created = Now, Data = data, Version = 1 }; Puts[name] = Puts.GetValueOrDefault(name) + 1; Op("put");
                return Task.FromResult(true);
            }
            if (etag == null || "\"v" + b.Version + "\"" != etag) return Task.FromResult(false);
            b.Data = data; b.Version++; Puts[name] = Puts.GetValueOrDefault(name) + 1; Op("put");
            return Task.FromResult(true);
        }
    }

    public Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct) =>
        PutAsync(name, File.ReadAllBytes(path), null, null, false, ct, contentType);

    public Task DownloadToFileAsync(string name, string path, CancellationToken ct)
    {
        lock (gate) File.WriteAllBytes(path, Blobs[name].Data);
        return Task.CompletedTask;
    }

    public Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct)
    {
        lock (gate)
        {
            Op("lease");
            if (!Blobs.TryGetValue(name, out var b)) throw new StorageException(404, "BlobNotFound", "no such blob");
            Lapse(b);
            if (b.Lease != null) { T("lease-denied", name); return Task.FromResult<string?>(null); }
            b.Lease = Guid.NewGuid().ToString(); b.LeaseUntil = Now.AddSeconds(seconds); LeaseSeconds[name] = seconds; T("lease", name, b.Lease[..4]);
            return Task.FromResult<string?>(b.Lease);
        }
    }

    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct)
    {
        lock (gate)
        {
            Op("lease");
            if (Blobs.TryGetValue(name, out var rb)) Lapse(rb);
            if (!Blobs.TryGetValue(name, out var b) || b.Lease != leaseId) throw new LeaseLostException("lost");
            b.LeaseUntil = Now.AddSeconds(LeaseSeconds.GetValueOrDefault(name, 60));
            Renews[name] = Renews.GetValueOrDefault(name) + 1;
        }
        return Task.CompletedTask;
    }

    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct)
    {
        lock (gate) { Op("lease"); T("release", name, leaseId?[..4]); if (Blobs.TryGetValue(name, out var b) && b.Lease == leaseId) b.Lease = null; }
        return Task.CompletedTask;
    }

    public Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct)
    {
        lock (gate)
        {
            var b = Blobs[name];
            if (b.Lease != null && b.Lease != leaseId) throw new StorageException(412, "LeaseIdMismatch", "lease mismatch");
            b.Meta = new(metadata);
        }
        return Task.CompletedTask;
    }

    public Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct)
    {
        lock (gate) Blobs[name].Tags = new(tags);
        return Task.CompletedTask;
    }

    public Task<bool> SetTagsIfAsync(string name, IReadOnlyDictionary<string, string> tags, IReadOnlyDictionary<string, string> expect, CancellationToken ct)
    {
        lock (gate)
        {
            if (!Blobs.TryGetValue(name, out var b) || expect.Any(e => b.Tags.GetValueOrDefault(e.Key) != e.Value)) return Task.FromResult(false);
            b.Tags = new(tags); TagWrites++;
            return Task.FromResult(true);
        }
    }
    public int TagWrites;

    public Task DeleteAsync(string name, CancellationToken ct) => DeleteAsync(name, null, ct);
    public Task DeleteAsync(string name, string? leaseId, CancellationToken ct)
    {
        lock (gate)
        {
            Op("delete");
            if (Blobs.TryGetValue(name, out var b)) Lapse(b);
            if (b != null && b.Lease != null && b.Lease != leaseId) throw new StorageException(412, "LeaseIdMissing", "blob is leased");
            T("delete", name); Blobs.Remove(name); return Task.CompletedTask;
        }
    }

    // ---------------------------------------------------------------- append blobs (real semantics: appendpos condition, 412, Seal, block count)
    public Task<bool> CreateAppendBlobAsync(string name, bool onlyIfNew, CancellationToken ct)
    {
        lock (gate)
        {
            Op("put");
            if (Blobs.ContainsKey(name) && onlyIfNew) return Task.FromResult(false);
            Blobs[name] = new Blob { Append = true, Created = Now };
            return Task.FromResult(true);
        }
    }

    public Task<AppendResult> AppendBlockAsync(string name, byte[] data, long appendPos, CancellationToken ct)
    {
        Action<string>? hook; lock (gate) { hook = BeforeAppend; BeforeAppend = null; }
        hook?.Invoke(name);
        lock (gate)
        {
            Op("append");
            if (!Blobs.TryGetValue(name, out var b) || !b.Append) return Task.FromResult(new AppendResult(AppendOutcome.NotFound));
            if (b.Sealed) return Task.FromResult(new AppendResult(AppendOutcome.Sealed));
            if (b.Data.Length != appendPos) return Task.FromResult(new AppendResult(AppendOutcome.PositionMismatch));
            if (b.Blocks >= 50000) throw new StorageException(409, "BlockCountExceedsLimit", "append blob block limit");
            b.Data = [.. b.Data, .. data]; b.Blocks++; b.Version++;
            return Task.FromResult(new AppendResult(AppendOutcome.Ok, b.Data.Length, b.Blocks));
        }
    }

    public Task<AppendRead> ReadAppendAsync(string name, long offset, int maxLen, CancellationToken ct)
    {
        lock (gate)
        {
            Op("get");
            if (!Blobs.TryGetValue(name, out var b) || !b.Append) return Task.FromResult(new AppendRead([], 0, false, 0, true));
            var from = (int)Math.Min(offset, b.Data.Length);
            return Task.FromResult(new AppendRead(b.Data.AsSpan(from, Math.Min(maxLen, b.Data.Length - from)).ToArray(), b.Data.Length, b.Sealed, b.Blocks));
        }
    }

    public Task<bool?> SealAppendBlobAsync(string name, long appendPos, CancellationToken ct)
    {
        lock (gate)
        {
            Op("put");
            if (!Blobs.TryGetValue(name, out var b) || !b.Append) return Task.FromResult<bool?>(null);
            if (b.Sealed) return Task.FromResult<bool?>(true);
            if (b.Data.Length != appendPos) return Task.FromResult<bool?>(false);
            b.Sealed = true; return Task.FromResult<bool?>(true);
        }
    }

    public Task<AppendInfo?> GetAppendInfoAsync(string name, CancellationToken ct)
    {
        lock (gate)
        {
            Op("get");
            return Task.FromResult<AppendInfo?>(Blobs.TryGetValue(name, out var b) && b.Append ? new AppendInfo(b.Data.Length, b.Sealed, b.Blocks) : null);
        }
    }

    /// <summary>The whole text of an append blob (for assertions).</summary>
    public string AppendText(string name) { lock (gate) return System.Text.Encoding.UTF8.GetString(Blobs[name].Data); }
}
