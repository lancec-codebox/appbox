using System.Net;
using System.Text;
using System.Xml.Linq;
namespace IndexNode.Tests;

/// <summary>0.13.30: a minimal Azure Blob REST server over a <see cref="FakeStore"/>, so a test can drive the real <see cref="RestStore"/> (its request counting, its breaker) with no network.
/// Serves Get/Head/Range (206), Put (If-None-Match, If-Match), Set Tags, Delete and List Blobs. Every request is counted in <see cref="Requests"/>.</summary>
public sealed class FakeBlobHttp(FakeStore fs) : HttpMessageHandler
{
    public int Requests;
    /// <summary>Every request as "METHOD blob-name[?comp]", in order (tests count the requests per blob).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Log { get; } = new();
    public static RestStore Rest(FakeStore fs, StoreMeter meter, out FakeBlobHttp h)
    {
        h = new FakeBlobHttp(fs);
        var rs = new RestStore(new HttpClient(h), "https://acct.blob.core.windows.net/c?sv=1&sig=x") { Meter = meter, Counters = new Traffic(), Ops = new OpCounter(), RetryDelay = _ => TimeSpan.Zero, Negative = new NegativeCache() };
        rs.GhostBlobFor = name => new Plain(fs, name);   // the SDK ghost client is not on this wire: blobs here are never ghosts
        return rs;
    }
    sealed class Plain(FakeStore fs, string name) : IGhostBlob
    {
        public string Name => name;
        public async Task<GhostState?> ReadAsync(CancellationToken ct) { var p = await fs.HeadAsync(name, ct); return p == null ? null : new GhostState(p.Size, p.ETag ?? "\"0\"", new Dictionary<string, string>(), p.Metadata); }
        public Task<long?> SnapshotLengthAsync(string snapshot, CancellationToken ct) => Task.FromResult<long?>(null);
        public Task CommitGhostAsync(GhostState state, CancellationToken ct) => Task.CompletedTask;
    }

    static HttpResponseMessage R(HttpStatusCode c, byte[]? body = null, string? etag = null, IReadOnlyDictionary<string, string>? meta = null)
    {
        var r = new HttpResponseMessage(c); if (body != null) r.Content = new ByteArrayContent(body); else r.Content = new ByteArrayContent([]);
        if (etag != null) r.Headers.TryAddWithoutValidation("ETag", etag);
        if (meta != null) foreach (var kv in meta) r.Headers.TryAddWithoutValidation("x-ms-meta-" + kv.Key, kv.Value);
        return r;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage q, CancellationToken ct)
    {
        Interlocked.Increment(ref Requests); Log.Enqueue(q.Method + " " + Uri.UnescapeDataString(q.RequestUri!.AbsolutePath.TrimStart('/')).Split('/', 2).Last() + (q.RequestUri.Query.Contains("comp=") ? "?" + System.Web.HttpUtility.ParseQueryString(q.RequestUri.Query)["comp"] : ""));
        var u = q.RequestUri!; var qs = System.Web.HttpUtility.ParseQueryString(u.Query); var comp = qs["comp"];
        var name = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/')); name = name.Contains('/') ? name[(name.IndexOf('/') + 1)..] : "";
        if (qs["restype"] == "container")
        {
            var list = await fs.ListAsync(qs["prefix"] ?? "", false, true, ct);
            var x = new XDocument(new XElement("EnumerationResults", new XElement("Blobs", list.Select(b => new XElement("Blob", new XElement("Name", b.Name), new XElement("Properties", new XElement("Content-Length", b.Size), new XElement("Etag", b.ETag ?? "")), new XElement("Metadata", b.Metadata.Select(m => new XElement(m.Key, m.Value))))))));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(x.ToString(), Encoding.UTF8, "application/xml") };
        }
        if (comp == "tags")
        {
            if (q.Method == HttpMethod.Put)
            {
                var doc = XDocument.Parse(await q.Content!.ReadAsStringAsync(ct)); var d = doc.Descendants("Tag").ToDictionary(t => t.Element("Key")!.Value, t => t.Element("Value")!.Value);
                try { await fs.SetTagsAsync(name, d, null, ct); } catch (Exception) { return R(HttpStatusCode.NotFound); } return R(HttpStatusCode.NoContent);
            }
            var tags = await fs.GetTagsAsync(name, ct); if (tags == null) return R(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new XDocument(new XElement("Tags", new XElement("TagSet", tags.Select(t => new XElement("Tag", new XElement("Key", t.Key), new XElement("Value", t.Value)))))).ToString(), Encoding.UTF8, "application/xml") };
        }
        if (q.Method == HttpMethod.Delete) { try { await fs.DeleteAsync(name, ct); return R(HttpStatusCode.Accepted); } catch (Exception) { return R(HttpStatusCode.NotFound); } }
        if (q.Method == HttpMethod.Put && comp == "appendblock")
        {   // 0.13.37: Append Block (the sequence blobs: no condition; the queue: x-ms-blob-condition-appendpos)
            long pos = q.Headers.TryGetValues("x-ms-blob-condition-appendpos", out var ap) && long.TryParse(ap.First(), out var apv) ? apv : -1;
            var res = await fs.AppendBlockAsync(name, await q.Content!.ReadAsByteArrayAsync(ct), pos, ct);
            if (res.Outcome == AppendOutcome.NotFound) return R(HttpStatusCode.NotFound);
            if (res.Outcome == AppendOutcome.PositionMismatch) return R(HttpStatusCode.PreconditionFailed);
            if (res.Outcome == AppendOutcome.Sealed) { var c = R(HttpStatusCode.Conflict); c.Headers.TryAddWithoutValidation("x-ms-error-code", "BlobIsSealed"); return c; }
            var ok = R(HttpStatusCode.Created); ok.Headers.TryAddWithoutValidation("x-ms-blob-committed-block-count", res.Blocks.ToString()); ok.Headers.TryAddWithoutValidation("x-ms-blob-append-offset", (res.Length - (await q.Content!.ReadAsByteArrayAsync(ct)).Length).ToString());
            ok.Headers.Date = res.Date ?? DateTimeOffset.UtcNow; return ok;
        }
        if (q.Method == HttpMethod.Put && q.Headers.TryGetValues("x-ms-blob-type", out var bt) && bt.First() == "AppendBlob")
            return await fs.CreateAppendBlobAsync(name, q.Headers.TryGetValues("If-None-Match", out var cn) && cn.First() == "*", ct) ? R(HttpStatusCode.Created) : R(HttpStatusCode.Conflict);
        if (q.Method == HttpMethod.Put)
        {
            var data = await q.Content!.ReadAsByteArrayAsync(ct); var ifm = q.Headers.TryGetValues("If-Match", out var a) ? a.First() : null; var inm = q.Headers.TryGetValues("If-None-Match", out var b) ? b.First() : null;
            var meta = q.Headers.Where(h => h.Key.StartsWith("x-ms-meta-")).ToDictionary(h => h.Key[10..], h => h.Value.First());
            bool ok = ifm != null ? await fs.PutIfMatchAsync(name, data, ifm, meta, ct) : await fs.PutAsync(name, data, null, null, inm == "*", ct);
            if (!ok) return R(ifm != null && !fs.Exists(name) ? HttpStatusCode.NotFound : inm == "*" ? HttpStatusCode.Conflict : HttpStatusCode.PreconditionFailed);
            return R(HttpStatusCode.Created, null, (await fs.HeadAsync(name, ct))?.ETag);
        }
        var props = await fs.HeadAsync(name, ct); if (props == null) return R(HttpStatusCode.NotFound);
        if (q.Method == HttpMethod.Head) { var h = R(HttpStatusCode.OK, null, props.ETag, props.Metadata); h.Content.Headers.ContentLength = props.Size; return h; }
        if (q.Headers.Range?.Ranges.FirstOrDefault() is { } rg)
        {
            if ((rg.From ?? 0) >= props.Size) return R(HttpStatusCode.RequestedRangeNotSatisfiable);
            long from = rg.From ?? 0, to = Math.Min(rg.To ?? props.Size - 1, props.Size - 1);
            if (q.Headers.TryGetValues("If-Match", out var im) && im.First() != props.ETag) return R(HttpStatusCode.PreconditionFailed);
            var bytes = (await fs.GetRangeAsync(name, from, (int)(to - from + 1), null, ct)).Data ?? []; var r = R(HttpStatusCode.PartialContent, bytes, props.ETag, props.Metadata);
            r.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, props.Size); return r;
        }
        if (q.Headers.TryGetValues("If-None-Match", out var nm) && nm.First() == props.ETag) return R(HttpStatusCode.NotModified, null, props.ETag);
        var got = await fs.GetAsync(name, ct); return R(HttpStatusCode.OK, got!.Data, props.ETag, props.Metadata);
    }
}
