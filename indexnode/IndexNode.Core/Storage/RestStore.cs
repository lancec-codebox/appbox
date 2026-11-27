using Microsoft.Win32.SafeHandles;
using System.Net;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace IndexNode;

/// <summary>Raw REST over a container SAS URL with HttpClient: no SDK, no account key.</summary>
public sealed class RestStore : IStore
{
    /// <summary>0.13.26: large blocks (a 1 GB zip is 16 Put Blocks + the list, not 128): every op counts toward the storage breaker. The API version we send (Protocol.ApiVersion 2021-12-02) allows a block up to 4,000 MiB. Each block is streamed from the file handle, never buffered.</summary>
    public const int BlockSize = 64 * 1024 * 1024;
    public const int BlockParallelism = 2;
    public const int BlockRetries = 3;

    /// <summary>Where this store counts its bytes (the node's counters unless a test sets its own).</summary>
    public Traffic Counters { get; set; } = Traffic.Current;
    /// <summary>Counts every HTTP request by kind (read, write, list) for the node's storage-ops report.</summary>
    public OpCounter Ops { get; set; } = OpCounter.Current;
    /// <summary>0.13.18: the meter and circuit breaker every request of this store passes (per call site, per op, failures apart).</summary>
    public StoreMeter Meter { get; set; } = StoreMeter.Current;
    /// <summary>0.13.18: names this store answered 404 for. A missing name is not asked for again within a minute, until a write, a listing or a queue line says it exists. control.json is exempt (the kill switch must be seen within one poll).</summary>
    public NegativeCache Negative { get; set; } = new();
    private bool KnownMissing(string name) => !name.EndsWith("jobs/control.json", StringComparison.Ordinal) && Negative.IsMissing(name);
    private void NoteMissing(string name) { if (!name.EndsWith("jobs/control.json", StringComparison.Ordinal)) Negative.Missing(name); }
    private (string Site, string Op, string? Name) Count(HttpRequestMessage req, string? site = null)
    {
        var q = req.RequestUri?.Query ?? ""; var path = req.RequestUri?.AbsolutePath ?? "";
        var name = Uri.UnescapeDataString(path.Length > containerPath.Length + 1 ? path[(containerPath.Length + 1)..] : "");
        string op;
        if (req.Method == HttpMethod.Get && (q.Contains("comp=list") || q.Contains("comp=blobs"))) { Ops.List(); op = "list"; }
        else if (req.Method == HttpMethod.Get || req.Method == HttpMethod.Head)
        {
            Ops.Read();
            op = req.Method == HttpMethod.Head ? "head" : req.Headers.Range != null ? "range" : q.Contains("comp=tags") ? "tags" : q.Contains("comp=metadata") ? "meta" : "get";
        }
        else
        {
            Ops.Write();
            op = req.Method == HttpMethod.Delete ? "delete" : q.Contains("comp=lease") ? "lease" : q.Contains("comp=appendblock") ? "append" : q.Contains("comp=block") ? "put" : q.Contains("comp=metadata") || q.Contains("comp=tags") ? "meta" : "put";
        }
        if (site == null) site = Meter.Before(op, name); else Meter.Before(op, name, site);
        return (site, op, name);
    }

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

    /// <summary>
    /// 0.13.5: a read that went to a Nexis snapshot (the blob was caught mid-dehydrate) and got 404: Nexis deletes that ephemeral snapshot a few minutes after the transition, so a missing snapshot is not a missing blob.
    /// Back to the base blob: wait for the transition to finish (look again with the store's retry backoff, about 2 minutes in all), hydrate when it is a ghost, then read on (from a fresh snapshot when it is still transitioning).
    /// Only a base blob that 404s is gone (the repeated read answers that).
    /// </summary>
    private async Task<HttpResponseMessage> SendReadAsync(string name, Func<HttpRequestMessage> make, CancellationToken ct, HttpCompletionOption opt = HttpCompletionOption.ResponseContentRead)
    {
        bool viaSnapshot = ReadSnapshots.ContainsKey(name);
        var r = await SendAsync(make, ct, opt: opt);
        if (r.StatusCode != HttpStatusCode.NotFound || !viaSnapshot) return r;
        r.Dispose();
        Log.Info($"nexis: the snapshot of {name} is gone: back to the blob, waiting for the transition to finish");
        ReadSnapshots.TryRemove(name, out _);
        var blob = GhostBlobFor?.Invoke(name) ?? new RestGhostBlob(this, name);
        for (int i = 1; ; i++)
        {
            try
            {
                var read = await NexisAccess.EnsureReadableAsync(blob, ct, failOnEmptyNonGhost: false);
                if (read.Snapshot != null) ReadSnapshots[name] = read.Snapshot;
                break;
            }
            catch (PermanentFailure e) when (i < 6) { Log.Info($"nexis: {name} is still changing state ({e.Message}), looking again"); await Task.Delay(RetryDelay(i), ct); }
        }
        return await SendAsync(make, ct, opt: opt);
    }

    // ---- 0.13.8: one ghost-aware reader. Identity of a blob is its CONTENT (real length + idx_sha256), never its ETag: a Nexis transition (dehydrate, hydrate) changes the ETag and nothing else.
    private sealed record Ident(long Length, string? Sha);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Ident> idents = new();     // name|etag -> what that ETag held when we saw it
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> etagAlias = new();  // name|etag a caller holds -> the current ETag of the same content
    private static string K(string name, string? etag) => name + "|" + etag;
    private void Know(string name, string? etag, long length, string? sha) { if (etag != null && length >= 0) { if (idents.Count > 20000) idents.Clear(); idents[K(name, etag)] = new Ident(length, string.IsNullOrEmpty(sha) ? null : sha); } }
    private static IReadOnlyDictionary<string, string> MetaOf(HttpResponseMessage r) { var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (var h in r.Headers) if (h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase)) d[h.Key[10..]] = h.Value.FirstOrDefault() ?? ""; return d; }
    private static string? ShaOf(HttpResponseMessage r) => r.Headers.TryGetValues("x-ms-meta-idx_sha256", out var v) ? v.FirstOrDefault() : null;
    private static long? TotalOf(HttpResponseMessage r) => r.Content.Headers.ContentRange?.Length;

    /// <summary>The state of a blob that a read found odd (empty, short, gone, changed): look at it and make it readable. True when the read is worth repeating (hydrated, snapshot set, or the blob is simply active now).</summary>
    private async Task<bool> SettleAsync(string name, CancellationToken ct)
    {
        if (VideoAggregate.IsAggregate(name)) return false;   // the aggregate is never ghosted and never hydrated through the nexis cache
        for (int i = 1; ; i++)
        {
            try { return await HydrateGhostAsync(name, ct) || (await (GhostBlobFor?.Invoke(name) ?? new RestGhostBlob(this, name)).ReadAsync(ct)) is { Length: > 0 }; }
            catch (PermanentFailure e) when (i < 6) { Log.Info($"nexis: {name} is still changing state ({e.Message}), looking again"); await Task.Delay(RetryDelay(i), ct); }
        }
    }

    public Task PrepareReadAsync(string name, CancellationToken ct) => VideoAggregate.IsAggregate(name) ? Task.CompletedTask : NexisAccess.PrepareAsync(this, name, ct);   // no nexis touch/hydrate for videos.agg

    private Uri ReadUri(string name) => BlobUri(name, ReadSnapshots.TryGetValue(name, out var snap) ? "snapshot=" + Uri.EscapeDataString(snap) : null);

    public Uri BlobUri(string name, string? extra = null)
    {
        var path = string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
        return new Uri($"{origin}{containerPath}/{path}?{(extra == null ? "" : extra + "&")}{sasQuery}");
    }

    private Uri ContainerUri(string extra) => new($"{origin}{containerPath}?{extra}&{sasQuery}");

    private string Scrub(string s) => Redact.Sas(s, sasUrl);

    /// <summary>0.13.80: the name this process sends as x-indexnode-node on every store request (the node name, else the machine), so a storage proxy
    /// (ddindex-blob, owner 2026-11-27) counts traffic per node. Azure ignores the header. Set once at start-up; null sends nothing.</summary>
    public static string? ClientName { get; set; }

    private HttpRequestMessage Req(HttpMethod m, Uri uri)
    {
        var r = new HttpRequestMessage(m, uri);
        r.Headers.TryAddWithoutValidation("x-ms-version", Protocol.ApiVersion);
        if (ClientName is { Length: > 0 } cn) r.Headers.TryAddWithoutValidation("x-indexnode-node", cn);
        return r;
    }

    private static string EncodeTags(IReadOnlyDictionary<string, string> tags) =>
        string.Join('&', tags.Select(t => $"{Uri.EscapeDataString(t.Key)}={Uri.EscapeDataString(t.Value)}"));

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, int retries = 3, HttpCompletionOption opt = HttpCompletionOption.ResponseContentRead)
    {
        string? site = null;   // 0.13.18: the call site is read once, on the way in (a retry after an await no longer has the caller on the stack)
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var req = make();
                var cnt = Count(req, site); site = cnt.Site;
                var sent = req.Content?.Headers.ContentLength ?? 0;
                var resp = await http.SendAsync(req, opt, ct);
                if (req.Method == HttpMethod.Put && resp.IsSuccessStatusCode) Negative.Exists(cnt.Name);   // a write says the name exists now
                if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotModified) Meter.Failed(cnt.Site, cnt.Op, cnt.Name, (int)resp.StatusCode);
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
                DateTimeOffset? created = DateTimeOffset.TryParse(props?.Element("Creation-Time")?.Value ?? props?.Element("Last-Modified")?.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var ct0) ? ct0 : null;
                all.Add(new BlobEntry(b.Element("Name")!.Value, ParseTagSet(b.Element("Tags")), meta, props?.Element("LeaseState")?.Value, size, created, props?.Element("Etag")?.Value is { Length: > 0 } et ? (et.StartsWith('"') ? et : "\"" + et.Trim('"') + "\"") : null));
            }
            Negative.ExistAll(all.Select(b => b.Name));   // a listing says these exist
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

    public Task<BlobContent?> GetAsync(string name, CancellationToken ct) => GetAsync(name, ct, 0);
    private async Task<BlobContent?> GetAsync(string name, CancellationToken ct, int depth)
    {
        if (KnownMissing(name)) return null;
        using var r = await SendReadAsync(name, () => Req(HttpMethod.Get, ReadUri(name)), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) { NoteMissing(name); return null; }
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob");
        var body = await r.Content.ReadAsByteArrayAsync(ct);
        if (body.Length == 0 && depth < 5 && await SettleAsync(name, ct)) return await GetAsync(name, ct, depth + 1);   // a ghost: hydrated, read again
        Know(name, r.Headers.ETag?.Tag, body.Length, ShaOf(r));
        return new BlobContent(body, r.Headers.ETag?.Tag, MetaOf(r));
    }

    /// <summary>Bytes off..off+len of a blob (a ghost is hydrated first, like any read).</summary>
    public async Task<byte[]> GetRangeAsync(string name, long off, int len, CancellationToken ct)
    {
        if (len <= 0) return [];   // no HTTP form for an empty range: off+len-1 < 0 made RangeHeaderValue throw "to ('-1') must be non-negative"
        var got = await GetRangeAsync(name, off, len, null, ct);
        return got.Data ?? throw new FileNotFoundException(name + " is gone");
    }

    public Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct) => GetIfChangedAsync(name, etag, ct, 0);
    private async Task<ConditionalResult> GetIfChangedAsync(string name, string? etag, CancellationToken ct, int depth)
    {
        if (etag != null && etagAlias.TryGetValue(K(name, etag), out var cur)) etag = cur;   // the same content under a newer ETag (a ghost transition) is not a change
        using var r = await SendReadAsync(name, () =>
        {
            var q = Req(HttpMethod.Get, ReadUri(name));
            if (etag != null) q.Headers.TryAddWithoutValidation("If-None-Match", etag);
            return q;
        }, ct);
        if (r.StatusCode == HttpStatusCode.NotModified) return new(false, true, null, etag);
        if (r.StatusCode == HttpStatusCode.NotFound) return new(true, false, null, null);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob (conditional)");
        var body = await r.Content.ReadAsByteArrayAsync(ct);
        if (body.Length == 0 && depth < 5 && await SettleAsync(name, ct)) return await GetIfChangedAsync(name, null, ct, depth + 1);
        Know(name, r.Headers.ETag?.Tag, body.Length, ShaOf(r));
        return new(true, true, body, r.Headers.ETag?.Tag, MetaOf(r));
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

    /// <summary>Put Blob with If-Match (or If-None-Match: * for a null etag): false on 412/409/404.</summary>
    public Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, CancellationToken ct) => PutIfMatchAsync(name, data, etag, null, ct);
    public async Task<bool> PutIfMatchAsync(string name, byte[] data, string? etag, IReadOnlyDictionary<string, string>? meta, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name));
            q.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            if (meta != null) foreach (var kv in meta) q.Headers.TryAddWithoutValidation("x-ms-meta-" + kv.Key, kv.Value);   // the metadata rides the same request
            q.Headers.TryAddWithoutValidation(etag == null ? "If-None-Match" : "If-Match", etag ?? "*");
            q.Content = new ByteArrayContent(data);
            q.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            return q;
        }, ct);
        if (r.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Put Blob");
        return true;
    }

    /// <summary>A read-only stream over [offset, offset+length) of an open file handle (positional reads, so blocks in flight share the handle).</summary>
    internal sealed class FileSegmentStream(SafeFileHandle h, long offset, long length) : Stream
    {
        long pos;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int o, int c) => Read(buffer.AsSpan(o, c));
        public override int Read(Span<byte> buffer) { var n = (int)Math.Min(buffer.Length, length - pos); if (n <= 0) return 0; n = RandomAccess.Read(h, buffer[..n], offset + pos); pos += n; return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = (int)Math.Min(buffer.Length, length - pos); if (n <= 0) return 0; n = await RandomAccess.ReadAsync(h, buffer[..n], offset + pos, ct); pos += n; return n; }
        public override Task<int> ReadAsync(byte[] buffer, int o, int c, CancellationToken ct) => ReadAsync(buffer.AsMemory(o, c), ct).AsTask();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    public static string BlockId(int index) => Convert.ToBase64String(Encoding.ASCII.GetBytes($"blk{index:D8}"));

    public static string BlockListXml(IEnumerable<string> ids)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
        foreach (var id in ids) sb.Append("<Latest>").Append(id).Append("</Latest>");
        return sb.Append("</BlockList>").ToString();
    }

    /// <summary>Size, ETag and metadata. 0.13.0: this never hydrates a ghost (a metadata check must not make Nexis bring 1 GB back); only reading the contents does. A ghost's size is what is committed (0) and its ETag and metadata are the blob's own.</summary>
    public Task<BlobProps?> HeadAsync(string name, CancellationToken ct) => HeadAsync(name, ct, 0);
    private async Task<BlobProps?> HeadAsync(string name, CancellationToken ct, int depth)
    {
        if (KnownMissing(name)) return null;
        using var r = await SendReadAsync(name, () => Req(HttpMethod.Head, ReadUri(name)), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) { NoteMissing(name); return null; }
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob Properties");
        long size = r.Content.Headers.ContentLength ?? 0;
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in r.Headers) if (h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase)) meta[h.Key[10..]] = h.Value.FirstOrDefault() ?? "";
        var etag = r.Headers.ETag?.Tag;
        // 0.13.8: a ghost (or a blob mid-transition) HEADs as 0 committed bytes; its length is the recorded ghostd_size. The length of a blob is content, whatever state Nexis has it in. Nothing is hydrated here.
        // The tags are read after the HEAD, so the state may have moved on (hydrated meanwhile: HEAD again); a blob with no ghost tags and 0 bytes is really empty.
        if (size == 0 && r.Headers.TryGetValues("x-ms-tag-count", out var tc) && int.TryParse(tc.FirstOrDefault(), out var ntags) && ntags > 0 && r.RequestMessage?.RequestUri?.Query.Contains("snapshot=") != true)
        {
            var t = await GetTagsAsync(name, ct);
            if (t == null) return null;
            if (t.GetValueOrDefault("ghostd_state") is "ghost" or "transitioning" && long.TryParse(t.GetValueOrDefault("ghostd_size"), out var real) && real > 0) size = real;
            else if (t.GetValueOrDefault("ghostd_state") == "active" && long.TryParse(t.GetValueOrDefault("ghostd_size"), out var rr) && rr > 0 && depth < 4) return await HeadAsync(name, ct, depth + 1);
        }
        Know(name, etag, size, meta.GetValueOrDefault("idx_sha256"));
        return new BlobProps(size, etag, meta);
    }

    // ---------------------------------------------------------------- append blobs (the queue log)

    private static bool Sealed(HttpResponseMessage r) => r.Headers.TryGetValues("x-ms-blob-sealed", out var v) && v.FirstOrDefault() == "true";
    private static int Blocks(HttpResponseMessage r) => r.Headers.TryGetValues("x-ms-blob-committed-block-count", out var v) && int.TryParse(v.FirstOrDefault(), out var n) ? n : 0;
    private static string? ErrorCode(HttpResponseMessage r) => r.Headers.TryGetValues("x-ms-error-code", out var c) ? c.FirstOrDefault() : null;

    public async Task<bool> CreateAppendBlobAsync(string name, bool onlyIfNew, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name));
            q.Headers.TryAddWithoutValidation("x-ms-blob-type", "AppendBlob");
            if (onlyIfNew) q.Headers.TryAddWithoutValidation("If-None-Match", "*");
            q.Content = new ByteArrayContent([]);
            q.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-ndjson");
            return q;
        }, ct);
        if (onlyIfNew && r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) return false;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Create Append Blob");
        return true;
    }

    public async Task<AppendResult> AppendBlockAsync(string name, byte[] data, long appendPos, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name, "comp=appendblock"));
            if (appendPos >= 0) q.Headers.TryAddWithoutValidation("x-ms-blob-condition-appendpos", appendPos.ToString(System.Globalization.CultureInfo.InvariantCulture));   // 0.13.37: a negative position = unconditional (the sequence blobs)
            q.Content = new ByteArrayContent(data);
            return q;
        }, ct);
        if (r.StatusCode == HttpStatusCode.PreconditionFailed) return new(AppendOutcome.PositionMismatch);
        if (r.StatusCode == HttpStatusCode.NotFound) return new(AppendOutcome.NotFound);
        if (r.StatusCode == HttpStatusCode.Conflict && (ErrorCode(r) ?? "").Contains("Sealed", StringComparison.OrdinalIgnoreCase)) return new(AppendOutcome.Sealed);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Append Block");
        long start = appendPos >= 0 ? appendPos : r.Headers.TryGetValues("x-ms-blob-append-offset", out var ao) && long.TryParse(ao.FirstOrDefault(), out var aov) ? aov : 0;
        return new(AppendOutcome.Ok, start + data.Length, Blocks(r), r.Headers.Date);
    }

    /// <summary>One ranged read from the last byte already known (offset - 1: always satisfiable, and it is the newline that ends the previous line), so an idle poll is one request that also says whether the segment is sealed. Offset 0 of an empty blob is a 416: its length comes from a HEAD.</summary>
    public async Task<AppendRead> ReadAppendAsync(string name, long offset, int maxLen, CancellationToken ct)
    {
        long from = Math.Max(0, offset - 1);
        using var r = await SendAsync(() => { var q = Req(HttpMethod.Get, BlobUri(name)); q.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, from + maxLen); return q; }, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return new([], 0, false, 0, true);
        if (r.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var info = await GetAppendInfoAsync(name, ct);
            return info == null ? new([], 0, false, 0, true) : new([], info.Length, info.Sealed, info.Blocks);
        }
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob (append read)");
        var body = await r.Content.ReadAsByteArrayAsync(ct);
        long total = r.Content.Headers.ContentRange?.Length ?? (from + body.Length);
        if (offset > 0)
        {
            if (body.Length == 0 || body[0] != (byte)'\n') throw new InvalidDataException($"{name}: the byte before offset {offset} is not a newline: the log was changed under us");
            body = body[1..];
        }
        return new(body, total, Sealed(r), Blocks(r));
    }

    public async Task<bool?> SealAppendBlobAsync(string name, long appendPos, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name, "comp=seal"));
            q.Headers.TryAddWithoutValidation("x-ms-blob-condition-appendpos", appendPos.ToString(System.Globalization.CultureInfo.InvariantCulture));
            q.Content = new ByteArrayContent([]);
            return q;
        }, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (r.StatusCode == HttpStatusCode.PreconditionFailed) return false;
        if (r.StatusCode == HttpStatusCode.Conflict && (ErrorCode(r) ?? "").Contains("Sealed", StringComparison.OrdinalIgnoreCase)) return true;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Seal Append Blob");
        return true;
    }

    public async Task<AppendInfo?> GetAppendInfoAsync(string name, CancellationToken ct)
    {
        using var r = await SendAsync(() => Req(HttpMethod.Head, BlobUri(name)), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob Properties");
        return new(r.Content.Headers.ContentLength ?? 0, Sealed(r), Blocks(r));
    }

    /// <summary>
    /// 0.13.8: THE ranged read. Identity is content: a read pinned to an ETag that the blob no longer has (412) is looked at, not failed: when the blob still exists with the length (and idx_sha256, when both
    /// sides have one) it had under that ETag, the ETag change was a Nexis transition and the read goes on with the current ETag (remembered, so the next read of the old ETag is not even refused).
    /// An empty answer (416, or no bytes) from a blob that is a ghost or transitioning is settled (hydrate, or read the snapshot, or wait for a vanished snapshot's blob to settle) and read again.
    /// Only a 404 on the base blob (data null) or different content (Mismatch) is an error.
    /// </summary>
    public async Task<RangeRead> GetRangeAsync(string name, long off, int len, string? ifMatch, CancellationToken ct)
    {
        if (len <= 0) return new([], false);
        if (KnownMissing(name)) return new(null, false);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var tag = ifMatch != null && etagAlias.TryGetValue(K(name, ifMatch), out var al) ? al : ifMatch;
            using var r = await SendReadAsync(name, () =>
            {
                var q = Req(HttpMethod.Get, ReadUri(name)); q.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(off, off + len - 1);
                if (tag != null && !ReadSnapshots.ContainsKey(name)) q.Headers.TryAddWithoutValidation("If-Match", tag);   // a snapshot has its own ETag: content identity is checked below instead
                return q;
            }, ct);
            if (r.StatusCode == HttpStatusCode.NotFound) { NoteMissing(name); return new(null, false); }
            if (r.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                var h = await HeadAsync(name, ct);
                if (h == null) return new(null, false);
                if (ifMatch == null || h.ETag == null) { if (attempt < 7) continue; return new(null, true); }
                if (!idents.TryGetValue(K(name, ifMatch), out var id)) return new(null, true);                                   // an ETag we never saw: cannot prove it is the same content
                var sha = h.Metadata.GetValueOrDefault("idx_sha256");
                if (id.Length != h.Size || (id.Sha != null && !string.IsNullOrEmpty(sha) && id.Sha != sha)) return new(null, true);   // different bytes
                etagAlias[K(name, ifMatch)] = h.ETag; Know(name, h.ETag, id.Length, id.Sha ?? (string.IsNullOrEmpty(sha) ? null : sha));
                Log.Info($"{name}: ETag changed without a change of content (ghost transition): reading on with the new ETag");
                continue;
            }
            if (r.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {   // nothing at that offset: a ghost has 0 committed bytes. Settle it and ask again; the state may also have changed since (so look a few times before believing the blob is short)
                if (attempt < 5) { await SettleAsync(name, ct); continue; }
                return new([], false);
            }
            if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob (range)");
            var data = await r.Content.ReadAsByteArrayAsync(ct);
            if (data.Length == 0 && attempt < 5) { await SettleAsync(name, ct); continue; }
            return new(data, false);
        }
        throw new PermanentFailure($"blob {name} kept changing while it was being read; not retrying");
    }

    /// <summary>True when the blob is a Nexis ghost or mid-transition with nothing committed (its content is in blocks or a snapshot).</summary>
    private async Task<bool> IsEmptyGhostAsync(string name, CancellationToken ct)
    {
        var s = await (GhostBlobFor?.Invoke(name) ?? new RestGhostBlob(this, name)).ReadAsync(ct);
        return s != null && s.Length == 0 && (s.State is "ghost" or "transitioning" || (s.Tags.TryGetValue("ghostd_size", out var z) && long.TryParse(z, out var n) && n > 0));
    }

    public async Task<bool> PutZipAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct) =>
        await PutFileCoreAsync(name, path, "application/zip", null, metadata, tags, onlyIfNew, ct);

    /// <summary>One Put Block List with If-Match: 412 (changed) or 404 (gone) leave the blob alone and return false.</summary>
    public async Task<bool> PutZipIfMatchAsync(string name, string path, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string>? tags, string etag, CancellationToken ct) =>
        await PutFileCoreAsync(name, path, "application/zip", null, metadata, tags, false, ct, etag);

    public Task PutFileAsync(string name, string path, string contentType, IProgress<long>? progress, CancellationToken ct) =>
        PutFileCoreAsync(name, path, contentType, progress, null, null, false, ct);

    private async Task<bool> PutFileCoreAsync(string name, string path, string contentType, IProgress<long>? progress, IReadOnlyDictionary<string, string>? metadata, IReadOnlyDictionary<string, string>? tags, bool onlyIfNew, CancellationToken ct, string? ifMatch = null)
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
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var r = await SendAsync(() =>
                        {
                            var q = Req(HttpMethod.Put, BlobUri(name, $"comp=block&blockid={Uri.EscapeDataString(ids[i])}"));
                            q.Content = new StreamContent(new FileSegmentStream(handle, (long)i * BlockSize, len)) { Headers = { ContentLength = len } };   // streamed from the handle: no block-sized buffer
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
            if (ifMatch != null) q.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            q.Content = new StringContent(BlockListXml(ids), Encoding.UTF8, "application/xml");
            return q;
        }, ct);
        if (onlyIfNew && (rl.StatusCode == HttpStatusCode.Conflict || rl.StatusCode == HttpStatusCode.PreconditionFailed)) return false;
        if (ifMatch != null && (rl.StatusCode == HttpStatusCode.PreconditionFailed || rl.StatusCode == HttpStatusCode.NotFound)) return false;
        if (ifMatch != null && (rl.StatusCode == HttpStatusCode.PreconditionFailed || rl.StatusCode == HttpStatusCode.NotFound)) return false;
        if (!rl.IsSuccessStatusCode) throw await Fail(rl, "Put Block List");
        return true;
    }

    /// <summary>0.13.32: the GET-first whole read: one GET, no tags, no touch, no HEAD. The response has the length and ETag: data at its length is the blob; only a 0-byte answer goes into the hydrate flow (SettleAsync) and is read again.</summary>
    /// <summary>Get Blob Tags with the response's Date header: the SERVER's clock, which is what ghostd_last_access must carry (never the node's).</summary>
    private async Task<(IReadOnlyDictionary<string, string>? Tags, DateTimeOffset? Date)> TagsWithDateAsync(string name, CancellationToken ct)
    {
        using var r = await SendAsync(() => Req(HttpMethod.Get, BlobUri(name, "comp=tags")), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return (null, null);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob Tags");
        return (ParseTagSet(XDocument.Parse(await r.Content.ReadAsStringAsync(ct)).Root), r.Headers.Date);
    }

    public TimeSpan TouchMemo { get; set; } = TimeSpan.FromMinutes(5);
    public int TouchTries { get; set; } = 5;
    /// <summary>A blob with no ghostd_state yet also gets the touch (Nexis may start tracking it). Off only for a store whose SAS cannot write tags.</summary>
    public bool UntaggedTouch { get; set; } = true;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> touched = new();
    /// <summary>
    /// 0.13.32: touch first, as a conditional read-modify-write, then read by the state touched under (Nexis learns a blob is in use only from ghostd_last_access; reads do not update it).
    /// 1. Get Blob Tags (state, the full tag set, and the response's Date = server time). 2. Set Blob Tags: every tag as read, ghostd_last_access = that Date, conditional on ghostd_state = the state just read.
    /// 3. A failed condition (412) starts again from 1, at most <see cref="TouchTries"/> times, then a transient failure. 4. By the state: active - read; transitioning - read from ghostd_snapshot (no hydrate); ghost - hydrate, then read; pending / unknown - wait, from 1.
    /// 5. A blob with no ghostd_state (never tracked by Nexis) is touched too (adds ghostd_last_access only; see the note in the code on the missing absence condition). A blob this process touched within <see cref="TouchMemo"/> skips 1-3. False when the blob does not exist.
    /// </summary>
    private async Task<bool> TouchForReadAsync(string name, CancellationToken ct)
    {
        if (VideoAggregate.IsAggregate(name)) return true;
        if (touched.TryGetValue(name, out var at) && DateTime.UtcNow - at < TouchMemo) return true;
        int lost = 0, waits = 0;
        while (true)
        {
            var (tags, date) = await TagsWithDateAsync(name, ct);
            if (tags == null) { NoteMissing(name); return false; }
            if (!tags.ContainsKey("ghostd_state"))
            {   // never tagged by Nexis (it may start to): the touch adds ghostd_last_access and nothing else. The tag grammar of x-ms-if-tags has NO absence test (a predicate on a missing key is false -> 412), so this one write cannot be conditional:
                // it replaces the tag set as read. The window is the time between our tags read and this write; every OTHER tag read is written back unchanged. (Known gap, reported to the owner.)
                if (date is { } du && UntaggedTouch)
                {
                    try { await SetTagsAsync(name, new Dictionary<string, string>(tags) { ["ghostd_last_access"] = du.UtcDateTime.ToString("o") }, null, ct); touched[name] = DateTime.UtcNow; }
                    catch (ForbiddenException e) { Log.Info($"nexis: could not touch {name} ({e.Message})"); }
                }
                ReadSnapshots.TryRemove(name, out _); return true;
            }
            var st = tags.GetValueOrDefault("ghostd_state");
            if (st is not ("active" or "transitioning" or "ghost"))
            {   // pending or unknown: a short wait, then from step 1
                touched.TryRemove(name, out _);
                if (++waits >= 6) throw new StorageException(503, "Pending", $"blob {name} stayed {st ?? "(no state)"}; try again later");
                await Task.Delay(RetryDelay(waits), ct); continue;
            }
            if (date is { } d)
            {
                var next = new Dictionary<string, string>(tags) { ["ghostd_last_access"] = d.UtcDateTime.ToString("o") };
                bool ok;
                try { ok = await SetTagsIfAsync(name, next, new Dictionary<string, string> { ["ghostd_state"] = st }, ct); }
                catch (ForbiddenException e) { Log.Info($"nexis: could not touch {name} ({e.Message})"); ok = true; }
                if (!ok)
                {
                    if (++lost >= TouchTries) throw new StorageException(503, "TouchLost", $"touch of {name} lost to a state change {lost} times (last state {st}); try again later");
                    continue;   // the state moved between the read and the touch: from step 1
                }
            }
            else Log.Info($"nexis: no Date header on the tags response for {name}: not touching");
            if (st == "active") { if (date != null) touched[name] = DateTime.UtcNow; ReadSnapshots.TryRemove(name, out _); return true; }
            touched.TryRemove(name, out _);
            if (st == "transitioning" && tags.GetValueOrDefault("ghostd_snapshot") is { Length: > 0 } snap) { ReadSnapshots[name] = snap; return true; }
            if (st == "ghost") { await SettleAsync(name, ct); ReadSnapshots.TryRemove(name, out _); return true; }   // hydrated (Nexis's own commit)
            if (++waits >= 6) throw new StorageException(503, "Pending", $"blob {name} is transitioning with no snapshot; try again later");
            await Task.Delay(RetryDelay(waits), ct);
        }
    }

    public async Task<BlobProps?> DownloadToFileWithPropsAsync(string name, string path, CancellationToken ct)
    {
        if (KnownMissing(name)) return null;
        if (!await TouchForReadAsync(name, ct)) return null;
        for (int depth = 0; ; depth++)
        {
            using var r = await SendReadAsync(name, () => Req(HttpMethod.Get, ReadUri(name)), ct, HttpCompletionOption.ResponseHeadersRead);
            if (r.StatusCode == HttpStatusCode.NotFound) { NoteMissing(name); return null; }
            if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob");
            if (r.Content.Headers.ContentLength == 0 && depth < 6 && !VideoAggregate.IsAggregate(name) && !(r.Headers.TryGetValues("x-ms-tag-count", out var tcv) && tcv.FirstOrDefault() == "0"))
            {
                touched.TryRemove(name, out _);
                // 0 bytes: look at the tags (1 op) and branch on ghostd_state. Data at full length never gets here, whatever the state.
                var tags = await GetTagsAsync(name, ct);
                if (tags == null) { NoteMissing(name); return null; }
                var st = tags.GetValueOrDefault("ghostd_state");
                if (st == "ghost")
                {   // hydrate (commit the uncommitted blocks), conditional on the ETag we saw; a lost race just reads again
                    var blob = GhostBlobFor?.Invoke(name) ?? new RestGhostBlob(this, name);
                    try { await blob.CommitGhostAsync(new GhostState(0, r.Headers.ETag?.Tag ?? "", tags, MetaOf(r)), ct); Log.Info($"nexis: hydrated {name}"); }
                    catch (InvalidOperationException e) { throw new PermanentFailure($"blob {name} cannot be read: state ghost ({e.Message}); not retrying"); }
                    catch (Exception e) when (NexisAccess.IsConditionNotMet(e)) { Log.Info($"nexis: {name} changed under us (ghost), reading again"); }
                    ReadSnapshots.TryRemove(name, out _);
                    continue;
                }
                if (st == "transitioning" && tags.GetValueOrDefault("ghostd_snapshot") is { Length: > 0 } snap)
                {   // caught mid-dehydrate: the data is in the snapshot; no hydrate, no fight with the ghoster
                    ReadSnapshots[name] = snap; continue;
                }
                if (st is "transitioning" or "pending" || (st != null && st != "active")) { await Task.Delay(RetryDelay(depth + 1), ct); ReadSnapshots.TryRemove(name, out _); continue; }
                if (st == "active" && long.TryParse(tags.GetValueOrDefault("ghostd_size"), out var gs) && gs > 0) { ReadSnapshots.TryRemove(name, out _); continue; }   // hydrated meanwhile: read again
                // untagged or active with no recorded size: really empty
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using (var fs = File.Create(path)) await r.Content.CopyToAsync(fs, ct);
            long len = new FileInfo(path).Length; var etag = r.Headers.ETag?.Tag; Know(name, etag, len, ShaOf(r));
            return new BlobProps(len, etag, MetaOf(r));
        }
    }

    public Task DownloadToFileAsync(string name, string path, CancellationToken ct) => DownloadToFileAsync(name, path, ct, 0);
    private async Task DownloadToFileAsync(string name, string path, CancellationToken ct, int depth)
    {
        using var r = await SendReadAsync(name, () => Req(HttpMethod.Get, ReadUri(name)), ct, HttpCompletionOption.ResponseHeadersRead);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Blob");
        if (r.Content.Headers.ContentLength == 0 && depth < 5 && await SettleAsync(name, ct)) { await DownloadToFileAsync(name, path, ct, depth + 1); return; }
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

    // ---------------------------------------------------------------- block blobs (the aggregate video.json)

    public async Task PutBlockAsync(string name, string blockId, byte[] data, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name, "comp=block&blockid=" + Uri.EscapeDataString(blockId)));
            q.Content = new ByteArrayContent(data);
            return q;
        }, ct);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Put Block");
    }

    public async Task<BlockList?> GetBlockListAsync(string name, CancellationToken ct)
    {
        using var r = await SendAsync(() => Req(HttpMethod.Get, BlobUri(name, "comp=blocklist&blocklisttype=all")), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Get Block List");
        var doc = XDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        static List<BlockItem> Items(XElement? e) => (e?.Elements("Block") ?? []).Select(b => new BlockItem(b.Element("Name")!.Value, long.Parse(b.Element("Size")!.Value, System.Globalization.CultureInfo.InvariantCulture))).ToList();
        return new BlockList(r.Headers.ETag?.Tag, Items(doc.Root?.Element("CommittedBlocks")), Items(doc.Root?.Element("UncommittedBlocks")));
    }

    public async Task<bool> PutBlockListAsync(string name, IReadOnlyList<string> blockIds, string? etag, CancellationToken ct)
    {
        var xml = BlockListXml(blockIds);
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name, "comp=blocklist"));
            q.Headers.TryAddWithoutValidation(etag == null ? "If-None-Match" : "If-Match", etag ?? "*");
            q.Content = new StringContent(xml, Encoding.UTF8, "application/xml");
            q.Headers.TryAddWithoutValidation("x-ms-blob-content-type", "application/json");
            return q;
        }, ct);
        if (r.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Put Block List");
        return true;
    }

    /// <summary>Put Blob with If-Match (or If-None-Match: *), answering the ETag the write produced (from the response header; no extra request).</summary>
    public async Task<(bool Ok, string? ETag)> PutIfMatchWithETagAsync(string name, byte[] data, string? etag, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Put, BlobUri(name));
            q.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            q.Headers.TryAddWithoutValidation(etag == null ? "If-None-Match" : "If-Match", etag ?? "*");
            q.Content = new ByteArrayContent(data);
            q.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            return q;
        }, ct);
        if (r.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound) return (false, null);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Put Blob");
        return (true, r.Headers.ETag?.Tag);
    }
}
