using System.Net;
using System.Text;
using System.Text.Json.Nodes;
namespace IndexNode.Tests;

/// <summary>0.13.37: a minimal Azure Table REST server over a <see cref="FakeMediaTable"/>, so a test can drive the real <see cref="RestMediaTable"/> with no network. Serves Merge / Insert Or Merge (PATCH), point reads and queries; counts requests.</summary>
public sealed class FakeTableHttp(FakeMediaTable t) : HttpMessageHandler
{
    public int Requests;
    public System.Collections.Concurrent.ConcurrentQueue<string> Log { get; } = new();
    public static RestMediaTable Rest(FakeMediaTable t, StoreMeter meter, out FakeTableHttp h) { h = new FakeTableHttp(t); return new RestMediaTable(new HttpClient(h), "https://acct.table.core.windows.net/media?sv=1&sig=x", meter); }
    static HttpResponseMessage R(HttpStatusCode c, string? json = null) => new(c) { Content = new StringContent(json ?? "", Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage q, CancellationToken ct)
    {
        Interlocked.Increment(ref Requests);
        var path = Uri.UnescapeDataString(q.RequestUri!.AbsolutePath); Log.Enqueue(q.Method + " " + path);
        var qs = System.Web.HttpUtility.ParseQueryString(q.RequestUri.Query);
        var m = System.Text.RegularExpressions.Regex.Match(path, @"\(PartitionKey='(?<p>[^']*)',RowKey='(?<r>[^']*)'\)");
        if (q.Method == HttpMethod.Patch && m.Success)
        {
            var props = JsonNode.Parse(await q.Content!.ReadAsStringAsync(ct)) as JsonObject ?? new JsonObject();
            if (q.Headers.Contains("If-Match")) { if (await t.GetAsync(m.Groups["p"].Value, m.Groups["r"].Value, null, ct) == null) return R(HttpStatusCode.NotFound); await t.MergeAsync(m.Groups["p"].Value, m.Groups["r"].Value, props, ct); }
            else await t.InsertOrMergeAsync(m.Groups["p"].Value, m.Groups["r"].Value, props, ct);
            return R(HttpStatusCode.NoContent);
        }
        if (q.Method == HttpMethod.Get && m.Success)
        {
            var sel = qs["$select"]?.Split(',');
            var row = await t.GetAsync(m.Groups["p"].Value, m.Groups["r"].Value, sel, ct); if (row == null) return R(HttpStatusCode.NotFound);
            var o = (JsonObject)row.Props.DeepClone(); o["PartitionKey"] = row.PartitionKey; o["RowKey"] = row.RowKey; o["Timestamp"] = row.Timestamp; return R(HttpStatusCode.OK, o.ToJsonString());
        }
        if (q.Method == HttpMethod.Get && path.EndsWith("()"))
        {
            var rows = await t.QueryAsync(qs["$filter"], qs["$select"]?.Split(','), ct); var a = new JsonArray();
            foreach (var row in rows) { var o = (JsonObject)row.Props.DeepClone(); o["PartitionKey"] = row.PartitionKey; o["RowKey"] = row.RowKey; o["Timestamp"] = row.Timestamp; a.Add(o); }
            return R(HttpStatusCode.OK, new JsonObject { ["value"] = a }.ToJsonString());
        }
        return R(HttpStatusCode.BadRequest, "unsupported " + q.Method + " " + path);
    }
}
