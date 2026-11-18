using System.Net;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace IndexNode;

/// <summary>Raw REST over a container SAS URL with HttpClient: no SDK, no account key.</summary>
public sealed class RestStore : IStore
{
    public const int BlockSize = 8 * 1024 * 1024;
    public const int BlockParallelism = 4;
    public const int BlockRetries = 3;

    /// <summary>Where this store counts its bytes (the node's counters unless a test sets its own).</summary>
    public Traffic Counters { get; set; } = Traffic.Current;

    private readonly HttpClient http;
    private readonly string origin;        // https://acct.blob.core.windows.net
    private readonly string containerPath; // /container
    private readonly string sasQuery;      // sv=...&sig=...  (no leading ?)
    private readonly string sasUrl;

    /// <summary>Delay before retry n (1-based). Tests set this to zero.</summary>
    public Func<int, TimeSpan> RetryDelay { get; set; } = n => TimeSpan.FromSeconds(Math.Pow(2, n));

    public RestStore(HttpClient http, string containerSasUrl)
    {
        this.http = http;
        sasUrl = containerSasUrl;
        var u = new Uri(containerSasUrl);
        origin = u.GetLeftPart(UriPartial.Authority);
        containerPath = u.AbsolutePath.TrimEnd('/');
        sasQuery = u.Query.TrimStart('?');
    }

    /// <summary>The SAS's permissions (the sp= field only).</summary>
    public static string? Permissions(string sasUrl)
    {
        var q = new Uri(sasUrl).Query.TrimStart('?');
        foreach (var kv in q.Split('&'))
            if (kv.StartsWith("sp=", StringComparison.Ordinal)) return Uri.UnescapeDataString(kv[3..]);
        return null;
    }

    /// <summary>Blobs Nexis caught mid-dehydrate: reads (Get Blob, download) of these names go to this snapshot of the blob; nothing is written to the blob.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> ReadSnapshots { get; } = new();

    /// <summary>How a blob is inspected and hydrated (the real one talks Azure Blob REST; tests set a fake).</summary>
    public Func<string, IGhostBlob>? GhostBlobFor { get; set; }

    /// <summary>
    /// Nexis ghosts every big blob after 20 minutes (0 bytes committed, tag ghostd_state=ghost, the data in uncommitted blocks). A read that comes back with 0 bytes looks at the blob: a ghost is hydrated (its blocks
    /// committed, Nexis's own HydrateBlobAsync, conditional on the ETag; a 412 re-reads) and read again, so no reader sees the empty body. A blob that really is empty (not a ghost) comes back as it is.
    /// Returns true when the blob was made readable (hydrated, or a snapshot to read from was set).
    /// </summary>
    private async Task<bool> HydrateGhostAsync(string name, CancellationToken ct)
    {
        var blob = GhostBlobFor?.Invoke(name) ?? new RestGhostBlob(this, name);
        var read = await NexisAccess.EnsureReadableAsync(blob, ct, failOnEmptyNonGhost: false);
        if (read.Snapshot != null) ReadSnapshots[name] = read.Snapshot; else ReadSnapshots.TryRemove(name, out _);
        // the read that came back empty is stale when the blob is now committed with content (we hydrated it, or another reader did first: the 412 path)
        return read.Repaired || read.Snapshot != null || (await blob.ReadAsync(ct))?.Length > 0;
    }

    private Uri ReadUri(string name) => BlobUri(name, ReadSnapshots.TryGetValue(name, out var snap) ? "snapshot=" + Uri.EscapeDataString(snap) : null);

    public Uri BlobUri(string name, string? extra = null)
    {
        var path = string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
        return new Uri($"{origin}{containerPath}/{path}?{(extra == null ? "" : extra + "&")}{sasQuery}");
    }

    private Uri ContainerUri(string extra) => new($"{origin}{containerPath}?{extra}&{sasQuery}");

    private string Scrub(string s) => Redact.Sas(s, sasUrl);

    private HttpRequestMessage Req(HttpMethod m, Uri uri)
    {
        var r = new HttpRequestMessage(m, uri);
        r.Headers.TryAddWithoutValidation("x-ms-version", Protocol.ApiVersion);
        return r;
    }

    private static string EncodeTags(IReadOnlyDictionary<string, string> tags) =>
        string.Join('&', tags.Select(t => $"{Uri.EscapeDataString(t.Key)}={Uri.EscapeDataString(t.Value)}"));

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, int retries = 3, HttpCompletionOption opt = HttpCompletionOption.ResponseContentRead)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var req = make();
                var sent = req.Content?.Headers.ContentLength ?? 0;
                var resp = await http.SendAsync(req, opt, ct);
                Counters.Add(Traffic.StorageWrite, sent);
                Counters.Add(Traffic.StorageRead, resp.Content.Headers.ContentLength ?? 0);
                if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt < retries)
                    {
                        resp.Dispose();
                        await Task.Delay(RetryDelay(attempt + 1), ct);
                        continue;
                    }
                }
                return resp;
            }
            catch (HttpRequestException e) when (attempt < retries)
            {
                Log.Info($"storage: transient error ({Scrub(e.Message)}), retry {attempt + 1}");
                await Task.Delay(RetryDelay(attempt + 1), ct);
            }
        }
    }

    private async Task<StorageException> Fail(HttpResponseMessage r, string op)
    {
        string body = "";
        try { body = await r.Content.ReadAsStringAsync(); } catch { }
        r.Headers.TryGetValues("x-ms-error-code", out var codes);
        var code = codes?.FirstOrDefault();
        var msg = Scrub($"{op} failed: HTTP {(int)r.StatusCode} {code} {(body.Length > 300 ? body[..300] : body)}");
        return (int)r.StatusCode == 403 ? new ForbiddenException(msg) : new StorageException((int)r.StatusCode, code, msg);
    }

    private static IReadOnlyDictionary<string, string> ParseTagSet(XElement? tagsEl)
    {
        var d = new Dictionary<string, string>();
        if (tagsEl == null) return d;
        foreach (var t in tagsEl.Descendants("Tag"))
            d[t.Element("Key")?.Value ?? ""] = t.Element("Value")?.Value ?? "";
        return d;
    }

    private async Task<List<BlobEntry>> Paged(Func<string?, Uri> uriFor, string op, CancellationToken ct)
    {
        var all = new List<BlobEntry>();
        string? marker = null;
        do
        {
            using var r = await SendAsync(() => Req(HttpMethod.Get, uriFor(marker)), ct);
            if (!r.IsSuccessStatusCode) throw await Fail(r, op);
            var doc = XDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            foreach (var b in doc.Descendants("Blob"))
            {
                var meta = new Dictionary<string, string>();
                foreach (var m in b.Element("Metadata")?.Elements() ?? []) meta[m.Name.LocalName] = m.Value;
                var props = b.Element("Properties");
                long.TryParse(props?.Element("Content-Length")?.Value, out var size);
                all.Add(new BlobEntry(b.Element("Name")!.Value, ParseTagSet(b.Element("Tags")), meta, props?.Element("LeaseState")?.Value, size));
            }
            marker = doc.Root?.Element("NextMarker")?.Value;
            if (string.IsNullOrEmpty(marker)) marker = null;
        } while (marker != null);
        return all;
    }

    public async Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct)
    {
        var list = await Paged(m => ContainerUri($"restype=container&comp=blobs&where={Uri.EscapeDataString(where)}" + (m == null ? "" : $"&marker={Uri.EscapeDataString(m)}")), "Find Blobs by Tags", ct);
        return list;
    }

    public Task<IReadOnlyList<BlobEntry>> ListAsync(string prefix, bool withTags, bool withMetadata, CancellationToken ct)
    {
        var include = string.Join(',', new[] { withTags ? "tags" : null, withMetadata ? "metadata" : null }.Where(x => x != null));
        return Paged(m => ContainerUri($"restype=container&comp=list&prefix={Uri.EscapeDataString(prefix)}" + (include.Length > 0 ? $"&include={include}" : "") + (m == null ? "" : $"&marker={Uri.EscapeDataString(m)}")), "List Blobs", ct)
            .ContinueWith(t => (IReadOnlyList<BlobEntry>)t.Result, ct);
    }

    public async Task<BlobContent?> GetAsync(string name, CancellationToken ct)
    {
        using var r = await SendAsync(() => Req(HttpMethod.Get, ReadUri(name)), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob");
        var body = await r.Content.ReadAsByteArrayAsync(ct);
        if (body.Length == 0 && !ReadSnapshots.ContainsKey(name) && await HydrateGhostAsync(name, ct)) return await GetAsync(name, ct);   // a ghost: hydrated, read again
        return new BlobContent(body, r.Headers.ETag?.Tag);
    }

    /// <summary>Bytes off..off+len of a blob (a ghost is hydrated first, like any read).</summary>
    public async Task<byte[]> GetRangeAsync(string name, long off, int len, CancellationToken ct)
    {
        if (!ReadSnapshots.ContainsKey(name)) await HydrateGhostAsync(name, ct);
        using var r = await SendAsync(() => { var q = Req(HttpMethod.Get, ReadUri(name)); q.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(off, off + len - 1); return q; }, ct);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob (range)");
        return await r.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Get, BlobUri(name));
            if (etag != null) q.Headers.TryAddWithoutValidation("If-None-Match", etag);
            return q;
        }, ct);
        if (r.StatusCode == HttpStatusCode.NotModified) return new(false, true, null, etag);
        if (r.StatusCode == HttpStatusCode.NotFound) return new(true, false, null, null);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob (conditional)");
        var body = await r.Content.ReadAsByteArrayAsync(ct);
        if (body.Length == 0 && !ReadSnapshots.ContainsKey(name) && await HydrateGhostAsync(name, ct)) return await GetIfChangedAsync(name, null, ct);
        return new(true, true, body, r.Headers.ETag?.Tag);
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string name, CancellationToken ct)
    {
        using var r = await SendAsync(() => Req(HttpMethod.Get, BlobUri(name, "comp=tags")), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob Tags");
        return ParseTagSet(XDocument.Parse(await r.Content.ReadAsStringAsync(ct)).Root);
    }

    public async Task<bool> PutAsync(string name, byte[] data, IReadOnlyDictionary<string, string>? tags, string? leaseId, bool onlyIfNew, CancellationToken ct, string contentType = "application/json")
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name));
            q.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            if (tags is { Count: > 0 }) q.Headers.TryAddWithoutValidation("x-ms-tags", EncodeTags(tags));
            if (leaseId != null) q.Headers.TryAddWithoutValidation("x-ms-lease-id", leaseId);
            if (onlyIfNew) q.Headers.TryAddWithoutValidation("If-None-Match", "*");
            q.Content = new ByteArrayContent(data);
            q.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return q;
        }, ct);
        if (onlyIfNew && (r.StatusCode == HttpStatusCode.Conflict || r.StatusCode == HttpStatusCode.PreconditionFailed)) return false;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Put Blob");
        return true;
    }

    public static string BlockId(int index) => Convert.ToBase64String(Encoding.ASCII.GetBytes($"blk{index:D8}"));

    public static string BlockListXml(IEnumerable<string> ids)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
        foreach (var id in ids) sb.Append("<Latest>").Append(id).Append("</Latest>");
        return sb.Append("</BlockList>").ToString();
    }

    public async Task<BlobProps?> HeadAsync(string name, CancellationToken ct)
    {
        for (int pass = 0; ; pass++)
        {
            using var r = await SendAsync(() => Req(HttpMethod.Head, ReadUri(name)), ct);
            if (r.StatusCode == HttpStatusCode.NotFound) return null;
            if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob Properties");
            long size = r.Content.Headers.ContentLength ?? 0;
            if (size == 0 && pass == 0 && !ReadSnapshots.ContainsKey(name) && await HydrateGhostAsync(name, ct)) continue;   // a ghost: hydrated, asked again
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in r.Headers) if (h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase)) meta[h.Key[10..]] = h.Value.FirstOrDefault() ?? "";
            return new BlobProps(size, r.Headers.ETag?.Tag, meta);
        }
    }

    public async Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Get, ReadUri(name)); q.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(off, off + len - 1);
            if (ifMatch != null) q.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            return q;
        }, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return new(null, false);
        if (r.StatusCode == HttpStatusCode.PreconditionFailed) return new(null, true);
        if (r.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return new([], false);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob (range)");
        return new(await r.Content.ReadAsByteArrayAsync(ct), false);
    }

    public async Task<bool> PutZipAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct) =>
        await PutFileCoreAsync(name, path, "application/zip", null, metadata, tags, onlyIfNew, ct);

    public Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct) =>
        PutFileCoreAsync(name, path, contentType, progress, null, null, false, ct);

    private async Task<bool> PutFileCoreAsync(string name, string path, string contentType, IProgress<long>? progress, IReadOnlyDictionary<string, string>? metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct)
    {
        var length = new FileInfo(path).Length;
        if (length == 0) { return await PutAsync(name, [], tags, null, onlyIfNew, ct, contentType); }
        int count = (int)((length + BlockSize - 1) / BlockSize);
        var ids = Enumerable.Range(0, count).Select(BlockId).ToList();
        long done = 0;
        using var gate = new SemaphoreSlim(BlockParallelism);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var len = (int)Math.Min(BlockSize, length - (long)i * BlockSize);
                var buf = new byte[len];
                await RandomAccess.ReadAsync(handle, buf, (long)i * BlockSize, ct);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var r = await SendAsync(() =>
                        {
                            var q = Req(HttpMethod.Put, BlobUri(name, $"comp=block&blockid={Uri.EscapeDataString(ids[i])}"));
                            q.Content = new ByteArrayContent(buf);
                            return q;
                        }, ct, retries: 0);
                        if (!r.IsSuccessStatusCode) throw await Fail(r, "Put Block");
                        break;
                    }
                    catch (Exception e) when (attempt < BlockRetries && e is not OperationCanceledException && !(e is StorageException { Status: >= 400 and < 500 and not 429 }))
                    {
                        Log.Info($"storage: block {i} failed ({Scrub(e.Message)}), retry {attempt + 1}/{BlockRetries}");
                        await Task.Delay(RetryDelay(attempt + 1), ct);
                    }
                }
                progress?.Report(Interlocked.Add(ref done, len));
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);

        using var rl = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name, "comp=blocklist"));
            q.Headers.TryAddWithoutValidation("x-ms-blob-content-type", contentType);
            if (metadata != null) foreach (var kv in metadata) q.Headers.TryAddWithoutValidation("x-ms-meta-" + kv.Key, kv.Value);
            if (tags is { Count: > 0 }) q.Headers.TryAddWithoutValidation("x-ms-tags", EncodeTags(tags));
            if (onlyIfNew) q.Headers.TryAddWithoutValidation("If-None-Match", "*");
            q.Content = new StringContent(BlockListXml(ids), Encoding.UTF8, "application/xml");
            return q;
        }, ct);
        if (onlyIfNew && (rl.StatusCode == HttpStatusCode.Conflict || rl.StatusCode == HttpStatusCode.PreconditionFailed)) return false;
        if (!rl.IsSuccessStatusCode) throw await Fail(rl, "Put Block List");
        return true;
    }

    public async Task DownloadToFileAsync(string name, string path, CancellationToken ct)
    {
        using var r = await SendAsync(() => Req(HttpMethod.Get, ReadUri(name)), ct, opt: HttpCompletionOption.ResponseHeadersRead);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob");
        if (r.Content.Headers.ContentLength == 0 && !ReadSnapshots.ContainsKey(name) && await HydrateGhostAsync(name, ct)) { await DownloadToFileAsync(name, path, ct); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var fs = File.Create(path);
        await r.Content.CopyToAsync(fs, ct);
    }

    private async Task<HttpResponseMessage> Lease(string name, string action, string? leaseId, int? seconds, CancellationToken ct)
    {
        return await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name, "comp=lease"));
            q.Headers.TryAddWithoutValidation("x-ms-lease-action", action);
            if (seconds != null) q.Headers.TryAddWithoutValidation("x-ms-lease-duration", seconds.Value.ToString());
            if (leaseId != null) q.Headers.TryAddWithoutValidation("x-ms-lease-id", leaseId);
            q.Content = new ByteArrayContent([]);
            return q;
        }, ct);
    }

    public async Task<string?> AcquireLeaseAsync(string name, int seconds, CancellationToken ct)
    {
        using var r = await Lease(name, "acquire", null, seconds, ct);
        if (r.StatusCode == HttpStatusCode.Conflict) return null;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Lease acquire");
        return r.Headers.GetValues("x-ms-lease-id").First();
    }

    public async Task RenewLeaseAsync(string name, string leaseId, CancellationToken ct)
    {
        using var r = await Lease(name, "renew", leaseId, null, ct);
        if (r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            throw new LeaseLostException($"lease on {name} lost (HTTP {(int)r.StatusCode})");
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Lease renew");
    }

    public async Task ReleaseLeaseAsync(string name, string leaseId, CancellationToken ct)
    {
        using var r = await Lease(name, "release", leaseId, null, ct);
        if (r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return; // already gone
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Lease release");
    }

    public async Task SetMetadataAsync(string name, IReadOnlyDictionary<string, string> metadata, string? leaseId, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name, "comp=metadata"));
            foreach (var kv in metadata) q.Headers.TryAddWithoutValidation("x-ms-meta-" + kv.Key, kv.Value);
            if (leaseId != null) q.Headers.TryAddWithoutValidation("x-ms-lease-id", leaseId);
            q.Content = new ByteArrayContent([]);
            return q;
        }, ct);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Set Blob Metadata");
    }

    public async Task<bool> SetTagsIfAsync(string name, IReadOnlyDictionary<string, string> tags, IReadOnlyDictionary<string, string> expect, CancellationToken ct)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><Tags><TagSet>");
        foreach (var t in tags)
            xml.Append("<Tag><Key>").Append(SecurityElement.Escape(t.Key)).Append("</Key><Value>").Append(SecurityElement.Escape(t.Value)).Append("</Value></Tag>");
        xml.Append("</TagSet></Tags>");
        var cond = string.Join(" AND ", expect.Select(e => $"\"{e.Key}\"='{e.Value.Replace("'", "''")}'"));
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name, "comp=tags"));
            q.Headers.TryAddWithoutValidation("x-ms-if-tags", cond);
            q.Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml");
            return q;
        }, ct);
        if (r.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound or HttpStatusCode.Conflict) return false;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Set Blob Tags (if tags)");
        return true;
    }

    public async Task SetTagsAsync(string name, IReadOnlyDictionary<string, string> tags, string? leaseId, CancellationToken ct)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><Tags><TagSet>");
        foreach (var t in tags)
            xml.Append("<Tag><Key>").Append(SecurityElement.Escape(t.Key)).Append("</Key><Value>").Append(SecurityElement.Escape(t.Value)).Append("</Value></Tag>");
        xml.Append("</TagSet></Tags>");
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name, "comp=tags"));
            if (leaseId != null) q.Headers.TryAddWithoutValidation("x-ms-lease-id", leaseId);
            q.Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml");
            return q;
        }, ct);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Set Blob Tags");
    }

    public Task DeleteAsync(string name, CancellationToken ct) => DeleteAsync(name, null, ct);

    public async Task DeleteAsync(string name, string? leaseId, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Delete, BlobUri(name));
            if (leaseId != null) q.Headers.TryAddWithoutValidation("x-ms-lease-id", leaseId);
            q.Headers.TryAddWithoutValidation("x-ms-delete-snapshots", "include");
            return q;
        }, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Delete Blob");
    }
}
