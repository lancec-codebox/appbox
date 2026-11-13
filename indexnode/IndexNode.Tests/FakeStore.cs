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
        public int Version;
    }

    public readonly Dictionary<string, Blob> Blobs = new();
    public readonly Dictionary<string, int> Renews = new();
    public readonly Dictionary<string, int> Puts = new();
    public readonly List<string> Calls = new();
    public bool ForbidFind;
    public bool StaleFind; // Find Blobs by Tags answers empty, as when the tag index lags
    private readonly object gate = new();

    public Blob this[string name] { get { lock (gate) return Blobs[name]; } }
    public bool Exists(string name) { lock (gate) return Blobs.ContainsKey(name); }
    public string? LeaseOf(string name) { lock (gate) return Blobs[name].Lease; }
    public int RenewCount(string name) { lock (gate) return Renews.GetValueOrDefault(name); }
    public int PutCount(string name) { lock (gate) return Puts.GetValueOrDefault(name); }
    public JobDescriptor Job(string name) { lock (gate) return JobDescriptor.Parse(Blobs[name].Data); }
    public string State(string name) { lock (gate) return Blobs[name].Tags[Tags.State]; }

    public async Task AddJob(Paths p, string stage, string id, string added, string? due = null)
    {
        var url = "https://www.youtube.com/watch?v=" + id.PadRight(11, 'x');
        var job = stage == "download" ? JobDescriptor.NewDownload(url, 480, null) : stage == "comments" ? JobDescriptor.NewComments(id, url) : JobDescriptor.NewProcess(id);
        job.JobId = id; job.AddedAt = added;
        await PutAsync(p.Job(stage, id), job.ToBytes(), Tags.Job(stage, "pending", added, due), null, true, default);
    }

    private static BlobEntry Entry(string name, Blob b) =>
        new(name, new Dictionary<string, string>(b.Tags), new Dictionary<string, string>(b.Meta), b.Lease != null ? "leased" : "available", b.Data.Length);

    public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct)
    {
        lock (gate)
        {
            Calls.Add("find");
            if (ForbidFind) throw new ForbiddenException("no f permission");
            if (StaleFind) return Task.FromResult<IReadOnlyList<BlobEntry>>([]);
            var conds = Regex.Matches(where, "\"(?<k>[^\"]+)\"='(?<v>[^']*)'").Select(m => (m.Groups["k"].Value, m.Groups["v"].Value)).ToList();
            var le = Regex.Matches(where, "\"(?<k>[^\"]+)\" <= '(?<v>[^']*)'").Select(m => (m.Groups["k"].Value, m.Groups["v"].Value)).ToList();
            IReadOnlyList<BlobEntry> r = Blobs.Where(b => conds.All(c => b.Value.Tags.GetValueOrDefault(c.Item1) == c.Item2)
                && le.All(c => b.Value.Tags.TryGetValue(c.Item1, out var tv) && string.CompareOrdinal(tv, c.Item2) <= 0)).Select(b => Entry(b.Key, b.Value)).ToList();
            return Task.FromResult(r);
        }
    }

    public Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct)
    {
        lock (gate)
        {
            Calls.Add("list");
            IReadOnlyList<BlobEntry> r = Blobs.Where(b => b.Key.StartsWith(prefix)).OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => Entry(b.Key, b.Value)).ToList();
            return Task.FromResult(r);
        }
    }

    public Task<BlobContent?> GetAsync(string name, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(Blobs.TryGetValue(name, out var b) ? new BlobContent(b.Data, "\"v" + b.Version + "\"") : null);
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

    public Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json")
    {
        lock (gate)
        {
            Puts[name] = Puts.GetValueOrDefault(name) + 1;
            if (Blobs.TryGetValue(name, out var b))
            {
                if (onlyIfNew) return Task.FromResult(false);
                if (b.Lease != null && b.Lease != leaseId) throw new StorageException(412, "LeaseIdMismatchWithBlobOperation", "lease mismatch on put");
            }
            else b = Blobs[name] = new Blob();
            b.Data = data; b.Tags = tags == null ? new() : new(tags); b.Meta = new(); b.Version++;
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
            var b = Blobs[name];
            if (b.Lease != null) return Task.FromResult<string?>(null);
            b.Lease = Guid.NewGuid().ToString();
            return Task.FromResult<string?>(b.Lease);
        }
    }

    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct)
    {
        lock (gate)
        {
            if (!Blobs.TryGetValue(name, out var b) || b.Lease != leaseId) throw new LeaseLostException("lost");
            Renews[name] = Renews.GetValueOrDefault(name) + 1;
        }
        return Task.CompletedTask;
    }

    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct)
    {
        lock (gate) { if (Blobs.TryGetValue(name, out var b) && b.Lease == leaseId) b.Lease = null; }
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

    public Task DeleteAsync(string name, CancellationToken ct) { lock (gate) Blobs.Remove(name); return Task.CompletedTask; }
}
