using System.Runtime.CompilerServices;

namespace IndexNode;

/// <summary>0.13.18: the node's cached LIST of the frames folder (index/search/frames/): name, size, ETag and idx_sha256 of every frames file, from ONE listing (with metadata) per relist. FramesReader.OpenAsync takes size and
/// ETag from here instead of a HEAD per file; a name the listing lacks, or a read that finds the file changed, falls back to one HEAD (and the meter's negative cache keeps a missing name from being asked again within a minute).</summary>
public sealed class FramesListing
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    static readonly ConditionalWeakTable<IStore, FramesListing> perStore = new();
    public static FramesListing For(IStore store) => perStore.GetValue(store, _ => new FramesListing());
    public static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    readonly object gate = new();
    Dictionary<string, BlobProps> files = new(StringComparer.Ordinal);
    DateTimeOffset listedAt = DateTimeOffset.MinValue;
    string? prefixListed;
    public int Lists;

    /// <summary>Replaces what is known from a fresh listing of <paramref name="prefix"/> (entries ending .bin).</summary>
    public void Load(string prefix, IEnumerable<BlobEntry> entries)
    {
        var d = new Dictionary<string, BlobProps>(StringComparer.Ordinal);
        foreach (var b in entries) if (b.Name.EndsWith(".bin", StringComparison.Ordinal))
            d[b.Name] = new BlobProps(b.Size, b.ETag, b.Metadata);
        lock (gate) { files = d; listedAt = Now(); prefixListed = prefix; Lists++; }
    }

    /// <summary>One LIST (with metadata) of the frames folder, unless a listing younger than the TTL is held.</summary>
    public async Task EnsureAsync(IStore store, Paths paths, CancellationToken ct)
    {
        var fp = paths.Prefix + "search/frames/";
        lock (gate) { if (prefixListed == fp && Now() - listedAt < Ttl) return; }
        Load(fp, await store.ListAsync(fp, false, true, ct));
    }

    public BlobProps? Get(string blob) { lock (gate) return files.TryGetValue(blob, out var p) ? p : null; }
    public void Set(string blob, BlobProps p) { lock (gate) files[blob] = p; }
    public void Forget(string blob) { lock (gate) files.Remove(blob); }
    /// <summary>A write of this frames file: the listed size and ETag are stale.</summary>
    public static void Wrote(IStore store, string blob) { if (perStore.TryGetValue(store, out var l)) l.Forget(blob); }
}
