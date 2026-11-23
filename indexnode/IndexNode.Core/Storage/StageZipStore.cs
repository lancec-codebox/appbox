using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>A store whose stages are committed as whole zips (the 0.10.0 layout).</summary>
public interface IStageStore
{
    /// <summary>Zips what the stage wrote for the drama and uploads it whole, with its metadata. <paramref name="merge"/> keeps the members of the zip already there (comments rounds, a partial redo) that were not rewritten; <paramref name="onlyIfNew"/> leaves an existing zip alone (false returned). True when a zip was written.</summary>
    Task<bool> CommitStageAsync(string videoId, string stage, string nodeId, bool merge, bool onlyIfNew, CancellationToken ct);
    /// <summary>Drops what a stage staged without uploading it (a failed or released run).</summary>
    void DiscardStaged(string videoId, string? stage = null);
}

/// <summary>The zip behind a read changed (ETag no longer matches): reopen and read again.</summary>
public sealed class StaleZipException(string name) : IOException($"{name} changed while it was being read");

/// <summary>Reads a zip blob by ranges, each pinned to the ETag the directory was read at.</summary>
public sealed class BlobZipSource(IStore store, string name, long length, string? etag, string? sha = null) : IZipSource
{
    readonly BlobPin pin = new(name, etag, length, sha);
    public long Length => length;
    public async Task<byte[]> ReadAsync(long off, int len, CancellationToken ct)
    {
        var r = await GhostSafeRead.RangeAsync(store, pin, off, len, ct);
        if (r.Mismatch) throw new StaleZipException(name);
        return r.Data ?? throw new FileNotFoundException(name + " is gone");
    }
}

/// <summary>
/// Presents a drama's stage zips as the per-file layout the stages and readers use (index/videos/&lt;id&gt;/frames/0003.mjpeg, cast/clusters.json, ...): reads come from the zip
/// (downloaded into a local cache, or one member range-read with If-Match), writes are staged locally and become one whole zip per stage at <see cref="CommitStageAsync"/>.
/// video.mp4 and compact.mp4 pass through as blobs. downloaded.json is download.zip's idx_summary. Cast face crops are one joined file per 10-minute chunk (cast/crops/NNNN.jpgs + NNNN.json) behind the same cand_*.jpg names.
/// </summary>
public sealed partial class StageZipStore(IStore inner, string cacheDir) : IStore, IStageStore
{
    public Task<bool> CreateAppendBlobAsync(string name, bool onlyIfNew, CancellationToken ct) => inner.CreateAppendBlobAsync(name, onlyIfNew, ct);
    public Task<AppendResult> AppendBlockAsync(string name, byte[] data, long appendPos, CancellationToken ct) => inner.AppendBlockAsync(name, data, appendPos, ct);
    public Task<AppendRead> ReadAppendAsync(string name, long offset, int maxLen, CancellationToken ct) => inner.ReadAppendAsync(name, offset, maxLen, ct);
    public Task<bool?> SealAppendBlobAsync(string name, long appendPos, CancellationToken ct) => inner.SealAppendBlobAsync(name, appendPos, ct);
    public Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, CancellationToken ct) => inner.PutIfMatchAsync(name, data, etag, ct);
    public Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, IReadOnlyDictionary<string, string> meta, CancellationToken ct) => inner.PutIfMatchAsync(name, data, etag, meta, ct);
    public Task<AppendInfo?> GetAppendInfoAsync(string name, CancellationToken ct) => inner.GetAppendInfoAsync(name, ct);
    public Task PutBlockAsync(string name, string blockId, byte[] data, CancellationToken ct) => inner.PutBlockAsync(name, blockId, data, ct);
    public Task<BlockList?> GetBlockListAsync(string name, CancellationToken ct) => inner.GetBlockListAsync(name, ct);
    public Task<bool> PutBlockListAsync(string name, IReadOnlyList<string> blockIds, string? etag, CancellationToken ct) => inner.PutBlockListAsync(name, blockIds, etag, ct);
    public Task<(bool Ok, string? ETag)> PutIfMatchWithETagAsync(string name, byte[] data, string? etag, CancellationToken ct) => inner.PutIfMatchWithETagAsync(name, data, etag, ct);
    public IStore Inner => inner;
    public static IStore? Find(IStore s) { for (int i = 0; i < 8 && s != null; i++) { if (s is StageZipStore z) return z; s = s switch { LocalCacheStore l => l.Inner, RecordingStore r => r.Inner, RedirectStore d => d.Inner, _ => null! }; } return null; }
    public static IStageStore? Stage(IStore s) => Find(s) as IStageStore;
    public static string Wrap(string? dir) => dir ?? Path.Combine(Path.GetTempPath(), "indexnode", "zips-" + Environment.ProcessId);
    public static IStore Wrap(IStore store, string? dir = null) => store is StageZipStore ? store : new StageZipStore(store, Wrap(dir));

    /// <summary>A zip at most this big is downloaded whole into the cache; a bigger one is range-read.</summary>
    public long WholeZipLimit { get; set; } = 64L << 20;
    static readonly AsyncLocal<bool> wholeAlways = new();
    /// <summary>0.13.30: inside the returned scope every zip this store opens is fetched whole in one GET (cast.zip, frames.zip) and then read locally: no range-read series. For jobs that read most of a zip anyway.</summary>
    public static IDisposable WholeZips() { var was = wholeAlways.Value; wholeAlways.Value = true; return new Scope(() => wholeAlways.Value = was); }
    sealed class Scope(Action done) : IDisposable { public void Dispose() => done(); }

    public const int CropChunkSeconds = 600;

    [GeneratedRegex(@"^(?<p>.*?)videos/(?<id>[^/]+)/(?<m>.+)$")] private static partial Regex NameRx();
    [GeneratedRegex(@"^frames/\d+\.json$")] private static partial Regex FrameIdxRx();
    [GeneratedRegex(@"^cast/cand_(?<hs>hs_)?(?<c>\d+)_(?<k>\d+)\.jpg$")] private static partial Regex CropRx();

    public static bool TryParse(string name, out string prefix, out string id, out string member)
    {
        var m = NameRx().Match(name); prefix = m.Groups["p"].Value; id = m.Groups["id"].Value; member = m.Groups["m"].Value;
        return m.Success;
    }
    private static string ZipBlob(string prefix, string id, string stage) => $"{prefix}videos/{id}/{stage}.zip";
    private static bool IsZipName(string member) => member.EndsWith(".zip", StringComparison.Ordinal) && !member.Contains('/');
    private static bool Staged(string member) => !StageLayout.IsRaw(member) && !IsZipName(member) && !StageLayout.IsRetired(member) && !StageLayout.IsPassThrough(member) && member != "processed.json";

    private sealed class Pending { public readonly ConcurrentDictionary<string, string> Files = new(); public byte[]? Downloaded; }
    private readonly ConcurrentDictionary<string, Pending> pending = new();   // zip blob -> what is staged for it
    private sealed class View { public required string Blob; public string? ETag; public required IZipSource Src; public required Dictionary<string, ZipEntry> Entries; public required IReadOnlyDictionary<string, string> Meta; public JsonObject? CropMap; }
    private readonly ConcurrentDictionary<string, View> views = new();
    private static string H(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..16];
    private string StageDir(string zipBlob) => Path.Combine(cacheDir, "stage", H(zipBlob));

    private async Task<View?> ViewAsync(string blob, CancellationToken ct, bool fresh = false)
    {
        if (!fresh && views.TryGetValue(blob, out var v)) return v;
        views.TryRemove(blob, out _);
        if (LocalCacheStore.Unwrap(inner) is RestStore rest && !fresh) { try { await NexisAccess.PrepareAsync(rest, blob, ct); } catch (OperationCanceledException) { throw; } catch (Exception e) { Log.Info($"zip {blob}: touch failed ({e.Message})"); } }   // a ghosted zip is restored first
        var h = await ZipOpen.HeadReadableAsync(inner, blob, ct, mustBeZip: false);
        if (h == null) return null;
        IZipSource src;
        if (h.Size <= WholeZipLimit || wholeAlways.Value)
        {
            var local = Path.Combine(cacheDir, "zips", H(blob) + "-" + H(h.ETag ?? h.Size.ToString() + h.Metadata.GetValueOrDefault("idx_sha256", "")) + ".zip");
            if (!File.Exists(local)) { Directory.CreateDirectory(Path.GetDirectoryName(local)!); var tmp = local + "." + Guid.NewGuid().ToString("N")[..6]; await inner.DownloadToFileAsync(blob, tmp, ct); File.Move(tmp, local, true); }
            src = new FileZipSource(local);
        }
        else src = new BlobZipSource(inner, blob, h.Size, h.ETag, h.Metadata.GetValueOrDefault("idx_sha256"));
        var entries = (await ZipFormat.ReadDirectoryAsync(src, ct)).Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);
        return views[blob] = new View { Blob = blob, ETag = h.ETag, Src = src, Entries = entries, Meta = h.Metadata };
    }

    /// <summary>Runs a read against the zip's view; a stale view (the zip was rewritten) is reopened once.</summary>
    private async Task<T?> WithViewAsync<T>(string blob, Func<View, Task<T?>> read, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { var v = await ViewAsync(blob, ct, attempt > 0); if (v == null) return default; return await read(v); }
            catch (Exception e) when (attempt == 0 && (e is StaleZipException || e is InvalidDataException || e is FileNotFoundException)) { views.TryRemove(blob, out _); }
        }
    }

    private async Task<byte[]?> ReadMemberAsync(string blob, string member, CancellationToken ct) =>
        await WithViewAsync<byte[]>(blob, async v => v.Entries.TryGetValue(member, out var e) ? await ZipFormat.ReadMemberAsync(v.Src, e, ct) : null, ct);

    private async Task<byte[]?> ReadCropAsync(string prefix, string id, Match m, CancellationToken ct)
    {
        var blob = ZipBlob(prefix, id, "cast");
        var mapBytes = await ReadMemberAsync(blob, StageLayout.CropsDir + "map.json", ct); if (mapBytes == null) return null;
        var key = "cand_" + m.Groups["hs"].Value + m.Groups["c"].Value + "_" + m.Groups["k"].Value;
        if (JsonNode.Parse(mapBytes)?[key] is not JsonArray loc) return null;
        int chunk = loc[0]!.GetValue<int>(), i = loc[1]!.GetValue<int>();
        var idx = JsonNode.Parse(await ReadMemberAsync(blob, $"{StageLayout.CropsDir}{chunk:D4}.json", ct) ?? throw new InvalidDataException("crop index missing"))!.AsArray();
        var data = await ReadMemberAsync(blob, $"{StageLayout.CropsDir}{chunk:D4}.jpgs", ct) ?? throw new InvalidDataException("crop file missing");
        return data.AsSpan(idx[i]![0]!.GetValue<int>(), idx[i]![1]!.GetValue<int>()).ToArray();
    }

    // ------------------------------------------------------------------ reads
    public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct) => inner.FindByTagsAsync(where, ct);

    public async Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct)
    {
        var real = await inner.ListAsync(prefix, withTags, withMetadata, ct);
        // a listing inside one drama also shows what its zips hold, under the per-file names
        var m = Regex.Match(prefix, @"^(?<p>.*?)videos/(?<id>[^/]+)/");
        if (!m.Success) return real;
        string p = m.Groups["p"].Value, id = m.Groups["id"].Value; var dirPrefix = $"{p}videos/{id}/";
        var all = prefix == dirPrefix ? real : await inner.ListAsync(dirPrefix, false, false, ct);
        var result = real.ToList(); var seen = result.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var z in all.Where(b => IsZipName(b.Name[dirPrefix.Length..]) && b.Name[dirPrefix.Length..] != "video.zip"))
        {
            var v = await ViewAsync(z.Name, ct); if (v == null) continue;
            foreach (var e in v.Entries.Values)
            {
                if (e.Name.StartsWith(StageLayout.CropsDir, StringComparison.Ordinal)) continue;   // the joined crop files are an implementation detail
                var n = dirPrefix + e.Name; if (n.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(n)) result.Add(new BlobEntry(n, new Dictionary<string, string>(), new Dictionary<string, string>(), null, e.Size));
            }
            if (z.Name == ZipBlob(p, id, "cast") && v.Entries.ContainsKey(StageLayout.CropsDir + "map.json") && await ReadMemberAsync(z.Name, StageLayout.CropsDir + "map.json", ct) is { } mb)
                foreach (var kv in JsonNode.Parse(mb)!.AsObject()) { var n = $"{dirPrefix}cast/{kv.Key}.jpg"; if (n.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(n)) result.Add(new BlobEntry(n, new Dictionary<string, string>(), new Dictionary<string, string>(), null, 0)); }
        }
        if (prefix.Length <= (dirPrefix + "processed.json").Length && (dirPrefix + "processed.json").StartsWith(prefix, StringComparison.Ordinal) && seen.Add(dirPrefix + "processed.json") && await VirtualProcessedAsync(p, id, ct) != null) result.Add(new BlobEntry(dirPrefix + "processed.json", new Dictionary<string, string>(), new Dictionary<string, string>(), null, 0));
        foreach (var kv in pending) if (kv.Key.StartsWith(dirPrefix, StringComparison.Ordinal))
            foreach (var f in kv.Value.Files.Keys) { var n = dirPrefix + f; if (n.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(n)) result.Add(new BlobEntry(n, new Dictionary<string, string>(), new Dictionary<string, string>(), null, new FileInfo(kv.Value.Files[f]).Length)); }
        return result.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The members of a drama that are still loose files (0.9 layout): nothing but raw videos, zips and retired/pass-through files is not loose.</summary>
    public async Task<List<string>> LooseFilesAsync(string prefix, string id, CancellationToken ct)
    {
        var dir = $"{prefix}videos/{id}/";
        return (await inner.ListAsync(dir, false, false, ct)).Select(b => b.Name[dir.Length..]).Where(m => !StageLayout.IsRaw(m) && !IsZipName(m) && !StageLayout.IsPassThrough(m)).ToList();
    }

    /// <summary>processed.json no longer exists: it reads as {v, asrModel, inputs} once frames.zip and transcribe.zip are there, from their metadata.</summary>
    private async Task<byte[]?> VirtualProcessedAsync(string prefix, string id, CancellationToken ct)
    {
        var f = await inner.HeadAsync(ZipBlob(prefix, id, "frames"), ct); var t = await inner.HeadAsync(ZipBlob(prefix, id, "transcribe"), ct);
        if (f == null || t == null) return null;
        var shas = new List<string?>(); foreach (var st in StageLayout.Stages) shas.Add((st == "frames" ? f : st == "transcribe" ? t : await inner.HeadAsync(ZipBlob(prefix, id, st), ct))?.Metadata.GetValueOrDefault("idx_sha256"));
        return JsonSerializer.SerializeToUtf8Bytes(new { v = 2, asrModel = Protocol.AsrModelName, inputs = StageLayout.Inputs(shas) });
    }

    private bool Resolve(string name, out string prefix, out string id, out string member, out string zipBlob)
    {
        zipBlob = ""; if (!TryParse(name, out prefix, out id, out member) || !Staged(member)) return false;
        var stage = member == "downloaded.json" ? "download" : StageLayout.StageOf(member); if (stage == null) return false;
        zipBlob = ZipBlob(prefix, id, stage); return true;
    }

    public async Task<BlobContent?> GetAsync(string name, CancellationToken ct)
    {
        if (TryParse(name, out var pp, out var pid, out var pm) && pm == "processed.json") return await VirtualProcessedAsync(pp, pid, ct) is { } vp ? new(vp, null) : null;
        if (!Resolve(name, out var prefix, out var id, out var member, out var blob)) return await inner.GetAsync(name, ct);
        if (member == "downloaded.json")
        {
            if (pending.TryGetValue(blob, out var pd) && pd.Downloaded != null) return new(pd.Downloaded, null);
            var h = await inner.HeadAsync(blob, ct);
            return h?.Metadata.GetValueOrDefault("idx_summary") is { Length: > 2 } s ? new(Encoding.UTF8.GetBytes(s), null) : null;
        }
        if (FrameIdxRx().IsMatch(member))   // the sscd pass's rewrite of a frames index wins over frames.zip's
        {
            var sb = ZipBlob(prefix, id, "sscd"); var sm = "sscd/" + member;
            if (pending.TryGetValue(sb, out var sp) && sp.Files.TryGetValue(sm, out var spath) && File.Exists(spath)) return new(await File.ReadAllBytesAsync(spath, ct), null);
            if (await ReadMemberAsync(sb, sm, ct) is { } so) return new(so, null);
        }
        if (pending.TryGetValue(blob, out var p) && p.Files.TryGetValue(member, out var path) && File.Exists(path)) return new(await File.ReadAllBytesAsync(path, ct), null);
        if (CropRx().Match(member) is { Success: true } cm && member != "" ) { var cb = await WithViewAsync<byte[]>(blob, async _ => await ReadCropAsync(prefix, id, cm, ct), ct); if (cb != null) return new(cb, null); }
        return await ReadMemberAsync(blob, member, ct) is { } data ? new(data, null) : null;
    }

    public async Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct)
    {
        if (!Resolve(name, out _, out _, out _, out _)) return await inner.GetIfChangedAsync(name, etag, ct);
        var c = await GetAsync(name, ct); return new(c != null, c != null, c?.Data, null);
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct)
    {
        if (TryParse(name, out var pp, out var pid, out var pm) && pm == "processed.json") return await VirtualProcessedAsync(pp, pid, ct) != null ? new Dictionary<string, string>() : null;
        if (!Resolve(name, out var prefix, out var id, out var member, out var blob)) return await inner.GetTagsAsync(name, ct);
        if (pending.TryGetValue(blob, out var p) && (p.Files.ContainsKey(member) || (member == "downloaded.json" && p.Downloaded != null))) return new Dictionary<string, string>();
        if (member == "downloaded.json") return (await inner.HeadAsync(blob, ct))?.Metadata.ContainsKey("idx_summary") == true ? new Dictionary<string, string>() : null;
        var v = await ViewAsync(blob, ct); if (v == null) return null;
        if (v.Entries.ContainsKey(member)) return new Dictionary<string, string>();
        return CropRx().IsMatch(member) && await GetAsync(name, ct) != null ? new Dictionary<string, string>() : null;
    }

    // ------------------------------------------------------------------ writes (staged)
    private string Stage(string zipBlob, string member, out Pending p)
    {
        p = pending.GetOrAdd(zipBlob, _ => new Pending());
        var path = Path.Combine(StageDir(zipBlob), H(member) + "-" + Path.GetFileName(member)); Directory.CreateDirectory(StageDir(zipBlob)); return path;
    }

    public async Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json")
    {
        if (TryParse(name, out _, out _, out var pm0) && pm0 == "processed.json") return true;   // no marker any more
        if (!Resolve(name, out _, out _, out var member, out var blob)) return await inner.PutAsync(name, data, tags, leaseId, onlyIfNew, ct, contentType);
        if (member == "downloaded.json") { pending.GetOrAdd(blob, _ => new Pending()).Downloaded = data; return true; }   // download.zip's summary, not a member
        var path = Stage(blob, member, out var p); await File.WriteAllBytesAsync(path, data, ct); p.Files[member] = path; return true;
    }

    public async Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct)
    {
        if (!Resolve(name, out _, out _, out var member, out var blob)) { await inner.PutFileAsync(name, path, contentType, progress, ct); return; }
        var to = Stage(blob, member, out var p); File.Copy(path, to, true); p.Files[member] = to;
    }

    public async Task DownloadToFileAsync(string name, string path, CancellationToken ct)
    {
        if (!Resolve(name, out _, out _, out _, out _)) { await inner.DownloadToFileAsync(name, path, ct); return; }
        var c = await GetAsync(name, ct) ?? throw new FileNotFoundException(name);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); await File.WriteAllBytesAsync(path, c.Data, ct);
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        if (!Resolve(name, out _, out _, out var member, out var blob)) { await inner.DeleteAsync(name, ct); return; }
        if (pending.TryGetValue(blob, out var p)) p.Files.TryRemove(member, out _);   // a stored member goes away with the next whole zip, never singly
    }

    public Task DeleteAsync(string name, string? leaseId, CancellationToken ct) => leaseId == null ? DeleteAsync(name, ct) : inner.DeleteAsync(name, leaseId, ct);
    public Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct) => inner.AcquireLeaseAsync(name, seconds, ct);
    public Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.RenewLeaseAsync(name, leaseId, ct);
    public Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct) => inner.ReleaseLeaseAsync(name, leaseId, ct);
    public Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct) => inner.SetMetadataAsync(name, metadata, leaseId, ct);
    public Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct) => inner.SetTagsAsync(name, tags, leaseId, ct);
    public Task<bool> SetTagsIfAsync(string name, IReadOnlyDictionary<string, string> tags, IReadOnlyDictionary<string, string> expect, CancellationToken ct) => inner.SetTagsIfAsync(name, tags, expect, ct);
    public Task<BlobProps?> HeadAsync(string name, CancellationToken ct) => inner.HeadAsync(name, ct);
    /// <summary>A range of a member that lives in a stage zip (frames/0005.mjpeg inside frames.zip): a stored member is range-read inside the zip (only those bytes are fetched), a deflated one is read whole and sliced. Not-a-member names go to the store as before.</summary>
    public async Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct)
    {
        if (!Resolve(name, out _, out _, out var member, out var blob) || member == "downloaded.json") return await inner.GetRangeAsync(name, off, len, ifMatch, ct);
        if (pending.TryGetValue(blob, out var p) && p.Files.TryGetValue(member, out var path) && File.Exists(path))
        { var all = await File.ReadAllBytesAsync(path, ct); return off >= all.Length ? new RangeRead([], false) : new RangeRead(all.AsSpan((int)off, (int)Math.Min(len, all.Length - off)).ToArray(), false); }
        var data = await WithViewAsync<byte[]>(blob, async v =>
        {
            if (!v.Entries.TryGetValue(member, out var e)) return null;
            if (off >= e.Size) return [];
            int n = (int)Math.Min(len, e.Size - off);
            if (e.Method != 0) return (await ZipFormat.ReadMemberAsync(v.Src, e, ct)).AsSpan((int)off, n).ToArray();
            var lh = await v.Src.ReadAsync(e.LocalOffset, 30, ct);
            if (lh.Length < 30 || BitConverter.ToUInt32(lh, 0) != 0x04034b50) throw new InvalidDataException($"bad local header for {e.Name}");
            long start = e.LocalOffset + 30 + BitConverter.ToUInt16(lh, 26) + BitConverter.ToUInt16(lh, 28);
            return await v.Src.ReadAsync(start + off, n, ct);   // stored: the member's bytes are the file's bytes (the whole-member CRC is not checked for a part)
        }, ct);
        return new RangeRead(data, false);
    }
    public async Task<bool> PutZipIfMatchAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, string etag, CancellationToken ct)
    {
        var ok = await inner.PutZipIfMatchAsync(name, path, metadata, tags, etag, ct);
        views.TryRemove(name, out _);
        return ok;
    }
    public Task<bool> PutZipAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct) => inner.PutZipAsync(name, path, metadata, tags, onlyIfNew, ct);

    // ------------------------------------------------------------------ commit
    public void DiscardStaged(string videoId, string? stage = null)
    {
        foreach (var k in pending.Keys.Where(k => stage == null ? (k.Contains($"/videos/{videoId}/") || k.StartsWith($"videos/{videoId}/")) : (k.EndsWith($"/videos/{videoId}/{stage}.zip", StringComparison.Ordinal) || k == $"videos/{videoId}/{stage}.zip")).ToList())
            if (pending.TryRemove(k, out var p)) { try { Directory.Delete(StageDir(k), true); } catch { } }
    }

    public async Task<bool> CommitStageAsync(string videoId, string stage, string nodeId, bool merge, bool onlyIfNew, CancellationToken ct)
    {
        var blob = pending.Keys.FirstOrDefault(k => k.EndsWith($"/videos/{videoId}/{stage}.zip", StringComparison.Ordinal) || k == $"videos/{videoId}/{stage}.zip");
        if (blob == null || !pending.TryGetValue(blob, out var p) || (p.Files.IsEmpty && p.Downloaded == null)) return false;
        var files = new Dictionary<string, string>(p.Files, StringComparer.Ordinal);
        var work = Path.Combine(cacheDir, "build", Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(work);
        try
        {
            if (merge && await ViewAsync(blob, ct, true) is { } old)
                foreach (var e in old.Entries.Values.Where(e => !files.ContainsKey(e.Name) && !e.Name.StartsWith(StageLayout.CropsDir, StringComparison.Ordinal)))
                { var f = Path.Combine(work, "old-" + H(e.Name)); await File.WriteAllBytesAsync(f, await ZipFormat.ReadMemberAsync(old.Src, e, ct), ct); files[e.Name] = f; }
            var zip = Path.Combine(work, stage + ".zip");
            var built = await StageZipBuilder.BuildAsync(stage, files, p.Downloaded, zip, ct);
            var prefix = blob[..(blob.Length - $"videos/{videoId}/{stage}.zip".Length)];
            var md = await StageZipBuilder.MetadataAsync(inner, prefix, videoId, stage, ZipFormat.Sha256Of(zip), nodeId, built.Summary, ct);
            var wrote = await inner.PutZipAsync(blob, zip, md, null, onlyIfNew, ct);
            views.TryRemove(blob, out _);
            if (pending.TryRemove(blob, out _)) { try { Directory.Delete(StageDir(blob), true); } catch { } }
            return wrote;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}

/// <summary>Builds a stage's zip: member order, the joined cast crops and the summary.</summary>
public static class StageZipBuilder
{
    public sealed record Built(List<ZipEntry> Entries, string Summary);

    public static async Task<Built> BuildAsync(string stage, IReadOnlyDictionary<string, string> files, byte[]? downloaded, string outPath, CancellationToken ct)
    {
        var members = new List<ZipFormat.Member>(); var small = new Dictionary<string, byte[]>();
        if (downloaded != null) small["downloaded.json"] = downloaded;
        var rest = files.Where(f => f.Key != "downloaded.json").ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        if (files.TryGetValue("downloaded.json", out var dj)) small["downloaded.json"] = await File.ReadAllBytesAsync(dj, ct);
        if (stage == "describe" && files.TryGetValue("describe.json", out var dd)) small["describe.json"] = await File.ReadAllBytesAsync(dd, ct);
        if (stage == "cast") PackCrops(rest, members);
        foreach (var kv in rest.OrderBy(k => k.Key, StringComparer.Ordinal)) members.Add(new(kv.Key, kv.Value));
        var entries = await Task.Run(() => ZipFormat.Write(outPath, members), ct);
        return new(entries, StageLayout.SummaryOf(stage, small));
    }

    private static readonly System.Text.RegularExpressions.Regex Crop = new(@"^cast/cand_(?<hs>hs_)?(?<c>\d+)_(?<k>\d+)\.jpg$");

    /// <summary>cast/cand_[hs_]C_K.jpg become cast/crops/NNNN.jpgs (the JPEGs back to back, one chunk per 10 minutes of the drama) + NNNN.json ([[off,len],...]) + map.json (name -> [chunk, index]).</summary>
    private static void PackCrops(Dictionary<string, string> files, List<ZipFormat.Member> members)
    {
        var crops = files.Where(f => Crop.IsMatch(f.Key)).ToList(); if (crops.Count == 0) return;
        var times = new Dictionary<string, double>();   // cluster -> k -> t from cast/cand_C.json
        double TimeOf(string c, string k)
        {
            if (!times.ContainsKey("load" + c)) { times["load" + c] = 1; if (files.TryGetValue($"cast/cand_{c}.json", out var p) && File.Exists(p)) try { foreach (var e in JsonNode.Parse(File.ReadAllBytes(p))!.AsArray()) times[$"{c}_{e!["k"]!.GetValue<int>()}"] = e["t"]!.GetValue<double>(); } catch { } }
            return times.TryGetValue($"{c}_{k}", out var t) ? t : 0;
        }
        var byChunk = new SortedDictionary<int, List<(string Key, string Path)>>();
        foreach (var kv in crops.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var m = Crop.Match(kv.Key); int chunk = (int)(TimeOf(m.Groups["c"].Value, m.Groups["k"].Value) / StageZipStore.CropChunkSeconds);
            if (!byChunk.TryGetValue(chunk, out var l)) byChunk[chunk] = l = [];
            l.Add(("cand_" + m.Groups["hs"].Value + m.Groups["c"].Value + "_" + m.Groups["k"].Value, kv.Value)); files.Remove(kv.Key);
        }
        var map = new JsonObject();
        foreach (var (chunk, list) in byChunk)
        {
            var ms = new MemoryStream(); var idx = new JsonArray();
            for (int i = 0; i < list.Count; i++) { var b = File.ReadAllBytes(list[i].Path); idx.Add(new JsonArray((int)ms.Position, b.Length)); ms.Write(b); map[list[i].Key] = new JsonArray(chunk, i); }
            members.Add(new($"{StageLayout.CropsDir}{chunk:D4}.jpgs", null, ms.ToArray())); members.Add(new($"{StageLayout.CropsDir}{chunk:D4}.json", null, Encoding.UTF8.GetBytes(idx.ToJsonString())));
        }
        members.Add(new($"{StageLayout.CropsDir}map.json", null, Encoding.UTF8.GetBytes(map.ToJsonString())));
    }

    /// <summary>idx_v, idx_sha256, idx_inputs (the hash of the input zips' idx_sha256), idx_node, idx_at, idx_summary.</summary>
    public static async Task<Dictionary<string, string>> MetadataAsync(IStore store, string prefix, string id, string stage, string sha, string nodeId, string summary, CancellationToken ct)
    {
        var shas = new List<string?>();
        foreach (var inp in StageLayout.InputStages.GetValueOrDefault(stage) ?? [])
            shas.Add((await store.HeadAsync($"{prefix}videos/{id}/{inp}.zip", ct))?.Metadata.GetValueOrDefault("idx_sha256"));
        return StageLayout.Metadata(sha, StageLayout.Inputs(shas), nodeId, summary);
    }
}

/// <summary>Commit helpers for stage code: a no-op over a store that is not a stage-zip store (the loose layout of tests and --local runs).</summary>
public static class StageCommit
{
    public static Task<bool> CommitAsync(IStore store, string videoId, string stage, string nodeId, bool merge, CancellationToken ct, bool onlyIfNew = false) =>
        StageZipStore.Stage(store) is { } z ? z.CommitStageAsync(videoId, stage, nodeId, merge, onlyIfNew, ct) : Task.FromResult(false);
}
