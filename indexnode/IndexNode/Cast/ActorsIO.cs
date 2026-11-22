using System.Text;

namespace IndexNode;

/// <summary>
/// Every read and write of the actors stage under index/actors/ goes through this. Reads come from the overlay first (what this run wrote), then the store; writes go to the store, or (dry run) nowhere, or (--out-local) into a local folder,
/// and in both of those the overlay keeps them so a run over several uploads sees its own earlier output. A write with an ETag is a compare-and-swap against the overlay / store ETag.
/// </summary>
public sealed class ActorsIO(IStore store, Paths paths, bool dry = false, string? outLocal = null)
{
    public IStore Store => store; public Paths Paths => paths; public bool Dry => dry; public bool Local => outLocal != null; public bool Writes => !dry && outLocal == null;
    readonly Dictionary<string, (byte[] Data, string ETag, IReadOnlyDictionary<string, string> Tags)> mem = new(StringComparer.Ordinal); readonly HashSet<string> gone = new(StringComparer.Ordinal); int tick;
    public List<string> Log = [];   // "put name (n bytes)" lines of a dry or local run

    string? LocalPath(string name) => outLocal == null ? null : Path.Combine(outLocal, name.Replace('/', Path.DirectorySeparatorChar));
    static string LocalETag(byte[] d) => "\"L" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(d))[..16] + "\"";
    /// <summary>--out-local: what an earlier process wrote under the folder is part of the overlay (it is what the next run and the index read).</summary>
    byte[]? LocalBytes(string name) { var p = LocalPath(name); return p != null && !gone.Contains(name) && File.Exists(p) ? File.ReadAllBytes(p) : null; }

    public async Task<BlobContent?> GetAsync(string name, CancellationToken ct)
    {
        if (mem.TryGetValue(name, out var m)) return new BlobContent(m.Data, m.ETag); if (gone.Contains(name)) return null;
        if (LocalBytes(name) is { } lb) return new BlobContent(lb, LocalETag(lb));
        return await store.GetAsync(name, ct);
    }
    /// <summary>Bytes of one blob (a vector row), from the overlay or by a ranged read that must still match the ETag (Mismatch: the file moved).</summary>
    public async Task<RangeRead> GetRangeAsync(string name, long off, int len, string? etag, CancellationToken ct)
        => mem.TryGetValue(name, out var m) ? new RangeRead(m.Data.AsSpan((int)off, len).ToArray(), false) : LocalBytes(name) is { } lb ? new RangeRead(lb.AsSpan((int)off, len).ToArray(), false) : await store.GetRangeAsync(name, off, len, etag, ct);
    public async Task<List<(string Name, string ETag, bool Manual)>> ListAsync(string prefix, CancellationToken ct)
    {
        var res = (await store.ListAsync(prefix, true, false, ct)).Where(e => !gone.Contains(e.Name)).ToDictionary(e => e.Name, e => (e.Name, e.ETag ?? "", e.Tags.GetValueOrDefault("manual") == "1"), StringComparer.Ordinal);
        if (LocalPath(prefix) is { } lp0 && outLocal != null) { var dir = Path.GetDirectoryName(lp0.TrimEnd(Path.DirectorySeparatorChar, '/')) ?? outLocal; if (Directory.Exists(outLocal)) foreach (var f in Directory.EnumerateFiles(outLocal, "*", SearchOption.AllDirectories)) { var n = Path.GetRelativePath(outLocal, f).Replace(Path.DirectorySeparatorChar, '/'); if (n.StartsWith(prefix, StringComparison.Ordinal) && !gone.Contains(n) && !mem.ContainsKey(n)) res[n] = (n, LocalETag(File.ReadAllBytes(f)), false); } }
        foreach (var kv in mem) if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) res[kv.Key] = (kv.Key, kv.Value.ETag, kv.Value.Tags.GetValueOrDefault("manual") == "1");
        return res.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
    }
    /// <summary>Creates (etag null: only if absent) or replaces (etag: only while it is still current). False when someone else changed it.</summary>
    public async Task<bool> PutAsync(string name, byte[] data, string? etag, bool create, bool manual, CancellationToken ct)
    {
        var tags = manual ? new Dictionary<string, string> { ["manual"] = "1" } : null;
        if (Writes)
        {
            if (etag != null) return await store.PutIfMatchAsync(name, data, etag, ct);   // (tags are not carried by a conditional put: a manual unit file keeps its tag from its creation)
            return await store.PutAsync(name, data, tags, null, create, ct, "application/octet-stream");
        }
        var cur = mem.TryGetValue(name, out var m) ? m.ETag : LocalBytes(name) is { } lb0 ? LocalETag(lb0) : null; if (etag != null && cur != null && cur != etag) return false; if (create && (cur != null || (outLocal == null && await store.HeadAsync(name, ct) != null) || (outLocal != null && await store.HeadAsync(name, ct) != null))) return false;
        mem[name] = (data, "m" + ++tick, tags ?? new Dictionary<string, string>()); gone.Remove(name); Log.Add($"put {name} ({data.Length} bytes)");
        if (outLocal != null) { var p = Path.Combine(outLocal, name.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(p)!); await File.WriteAllBytesAsync(p, data, ct); }
        return true;
    }
    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        if (Writes) { try { await store.DeleteAsync(name, ct); } catch (Exception e) when (e is StorageException { Status: 404 } or FileNotFoundException) { } return; }
        mem.Remove(name); gone.Add(name); Log.Add($"delete {name}");
        if (outLocal != null) { var p = Path.Combine(outLocal, name.Replace('/', Path.DirectorySeparatorChar)); if (File.Exists(p)) File.Delete(p); }
    }
}
