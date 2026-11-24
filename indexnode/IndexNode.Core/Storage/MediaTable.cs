using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>One row of the Azure Table `media`: the keys, the server Timestamp (the row's version) and the properties (strings, numbers, booleans).</summary>
public sealed record TableRow(string PartitionKey, string RowKey, string? Timestamp, JsonObject Props)
{
    public string? Str(string name) => Props[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public double? Num(string name) => Props[name] is JsonValue v ? (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : null) : null;   // a number parsed from JSON answers as double; one set in code (a test fake, an overlay) keeps its CLR type
    public bool? Bool(string name) => Props[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    /// <summary>A section property: the JSON string it holds, parsed (null when absent or not an object).</summary>
    public JsonObject? Section(string name) { var s = Str(name); if (string.IsNullOrEmpty(s)) return null; try { return JsonNode.Parse(s) as JsonObject; } catch (JsonException) { return null; } }
}

/// <summary>0.13.37: the Azure Table `media` (per-upload facts). Every call is one request (a query is one request per page of up to 1000 rows; a batch is one request of up to 100 rows of one partition).</summary>
public interface IMediaTable
{
    /// <summary>Merge Entity (If-Match: *): writes only the named properties; when the row does not exist yet, Insert Or Merge. No read.</summary>
    Task MergeAsync(string partition, string rowKey, JsonObject props, CancellationToken ct);
    /// <summary>Insert Or Merge Entity: the row is created, or the named properties are merged into it (the migration: idempotent, and it never drops what another writer put there).</summary>
    Task InsertOrMergeAsync(string partition, string rowKey, JsonObject props, CancellationToken ct);
    /// <summary>One batch (at most 100 rows, one partition) of Insert Or Merge operations.</summary>
    Task BatchInsertOrMergeAsync(string partition, IReadOnlyList<(string RowKey, JsonObject Props)> rows, CancellationToken ct);
    /// <summary>A point read; null when the row does not exist. <paramref name="select"/> names the properties to return (null: all).</summary>
    Task<TableRow?> GetAsync(string partition, string rowKey, IReadOnlyCollection<string>? select, CancellationToken ct);
    /// <summary>A query (OData $filter, $select), every page followed. Null filter = the whole table.</summary>
    Task<List<TableRow>> QueryAsync(string? filter, IReadOnlyCollection<string>? select, CancellationToken ct);
}

/// <summary>What the conformance command needs beyond the node's calls: delete (batched, If-Match *) to clean its scratch partition. Only the REST client and the test fake have it.</summary>
public interface IMediaTableAdmin : IMediaTable
{
    Task DeleteBatchAsync(string partition, IReadOnlyList<string> rowKeys, CancellationToken ct);
}

/// <summary>Where the media table of a node lives (a store's table, found through the wrappers the same way VideoJson finds its cache).</summary>
public static class MediaTables
{
    public const string Upload = "upload";
    public const int MaxStringChars = 32000;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStore, IMediaTable> attached = new();
    /// <summary>Attaches the table to this store object; <see cref="For"/> finds it from this store or from any wrapper over it.</summary>
    public static void Attach(IStore store, IMediaTable? table) { attached.Remove(store); if (table != null) attached.Add(store, table); }
    /// <summary>0.13.37: control media-table-read (default off), kept PER STORE (beside the table that store carries; not a process-wide switch). Readers use <see cref="ForRead"/>; writers and marks use <see cref="For"/>.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStore, System.Runtime.CompilerServices.StrongBox<bool>> reads = new();
    /// <summary>Turns the readers of the store that carries the table on or off (the control watcher calls this with every control.json it applies). A store with no table: nothing to set.</summary>
    public static void SetReads(IStore store, bool on) { if (Owner(store) is { } o) Volatile.Write(ref reads.GetValue(o, _ => new(false)).Value, on); }
    public static bool ReadsOn(IStore store) => Owner(store) is { } o && reads.TryGetValue(o, out var b) && Volatile.Read(ref b.Value);
    /// <summary>The prefix of the selftest's scratch partitions: the only partitions a delete may name (the REST client and the fake both refuse any other).</summary>
    public const string ScratchPrefix = "selftest-";
    public static void RequireScratch(string partition) { if (!partition.StartsWith(ScratchPrefix, StringComparison.Ordinal) || partition.Length <= ScratchPrefix.Length) throw new InvalidOperationException($"table delete refused: partition '{partition}' is not a {ScratchPrefix}<id> scratch partition (nothing deletes upload data)"); }
    public static IMediaTable? ForRead(IStore store) => ReadsOn(store) ? For(store) : null;
    public static IMediaTable? For(IStore store) => Owner(store) is { } o && attached.TryGetValue(o, out var t) ? t : null;
    /// <summary>The store object the table is attached to, found from that store or from any wrapper over it.</summary>
    static IStore? Owner(IStore store)
    {
        for (int i = 0; i < 10 && store != null; i++)
        {
            if (attached.TryGetValue(store, out _)) return store;
            store = store switch { MeteredStore m => m.Inner, RecordingStore r => r.Inner, LocalCacheStore l => l.Inner, StageZipStore z => z.Inner, _ => null! };   // 0.13.37: NOT through a ShadowStore (it has its own overlay, attached to it) nor a RedirectStore (its writes would reach the live table): the walk stops there
        }
        return null;
    }
}

/// <summary>Counts every table request in the store meter (the step budget and the breaker see them like blob operations).</summary>
public sealed class MeteredMediaTable(IMediaTable inner, StoreMeter? meter = null) : IMediaTable
{
    public IMediaTable Inner => inner;
    StoreMeter M => meter ?? StoreMeter.Current;
    string B(string op, string name) => M.Before(op, "table/" + name);
    public Task MergeAsync(string p, string r, JsonObject props, CancellationToken ct) { B("table", p + "/" + r); return inner.MergeAsync(p, r, props, ct); }
    public Task InsertOrMergeAsync(string p, string r, JsonObject props, CancellationToken ct) { B("table", p + "/" + r); return inner.InsertOrMergeAsync(p, r, props, ct); }
    public Task BatchInsertOrMergeAsync(string p, IReadOnlyList<(string RowKey, JsonObject Props)> rows, CancellationToken ct) { B("table", p + "/batch"); return inner.BatchInsertOrMergeAsync(p, rows, ct); }
    public Task<TableRow?> GetAsync(string p, string r, IReadOnlyCollection<string>? select, CancellationToken ct) { B("table", p + "/" + r); return inner.GetAsync(p, r, select, ct); }
    public Task<List<TableRow>> QueryAsync(string? filter, IReadOnlyCollection<string>? select, CancellationToken ct) => inner.QueryAsync(filter, select, ct);   // each page is metered by the REST client (the page count is not known here)
}

/// <summary>Azure Table REST with a table SAS URL (https://&lt;account&gt;.table.core.windows.net/media?&lt;sas&gt;): x-ms-version 2020-12-06, JSON nometadata. No SDK.</summary>
public sealed class RestMediaTable : IMediaTableAdmin
{
    public const string ApiVersion = "2020-12-06";
    readonly HttpClient http; readonly string origin, table, sasQuery, sasUrl; readonly StoreMeter? meter;
    public RestMediaTable(HttpClient http, string tableSasUrl, StoreMeter? meter = null)
    {
        this.http = http; sasUrl = tableSasUrl; this.meter = meter; var u = new Uri(tableSasUrl);
        origin = u.GetLeftPart(UriPartial.Authority); table = u.AbsolutePath.Trim('/'); sasQuery = u.Query.TrimStart('?');
    }
    static string Q(string s) => s.Replace("'", "''");
    string Entity(string p, string r) => $"{origin}/{table}(PartitionKey='{Uri.EscapeDataString(Q(p))}',RowKey='{Uri.EscapeDataString(Q(r))}')";
    string Scrub(string s) => Redact.Sas(s, sasUrl);

    /// <summary>The SAS's expiry (the se= field), null when it has none.</summary>
    public static DateTimeOffset? Expiry(string sasUrl)
    {
        foreach (var kv in new Uri(sasUrl).Query.TrimStart('?').Split('&'))
            if (kv.StartsWith("se=", StringComparison.Ordinal) && DateTimeOffset.TryParse(Uri.UnescapeDataString(kv[3..]), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)) return d;
        return null;
    }

    HttpRequestMessage Req(HttpMethod m, string url, string? query = null)
    {
        var q = new HttpRequestMessage(m, url + "?" + (string.IsNullOrEmpty(query) ? "" : query + "&") + sasQuery);
        q.Headers.TryAddWithoutValidation("x-ms-version", ApiVersion); q.Headers.TryAddWithoutValidation("Accept", "application/json;odata=nometadata");
        q.Headers.TryAddWithoutValidation("DataServiceVersion", "3.0;NetFx"); q.Headers.TryAddWithoutValidation("MaxDataServiceVersion", "3.0;NetFx");
        return q;
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, string what, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            (meter ?? StoreMeter.Current).Before("table", what);
            HttpResponseMessage r;
            try { r = await http.SendAsync(make(), ct); }
            catch (HttpRequestException) when (attempt < 5) { await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt * attempt), ct); continue; }
            if ((r.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError or HttpStatusCode.GatewayTimeout or (HttpStatusCode)408) && attempt < 5)
            { r.Dispose(); await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt), ct); continue; }
            return r;
        }
    }

    async Task<Exception> Fail(HttpResponseMessage r, string what) => new StorageException((int)r.StatusCode, "TableError", Scrub($"{what}: HTTP {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}"));

    static StringContent Body(string p, string r, JsonObject props, bool keys)
    {
        var o = new JsonObject(); foreach (var kv in props) o[kv.Key] = kv.Value?.DeepClone();
        if (keys) { o["PartitionKey"] = p; o["RowKey"] = r; }
        return new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json");
    }

    public async Task MergeAsync(string partition, string rowKey, JsonObject props, CancellationToken ct)
    {
        using var r = await SendAsync(() => { var q = Req(HttpMethod.Patch, Entity(partition, rowKey)); q.Headers.TryAddWithoutValidation("If-Match", "*"); q.Content = Body(partition, rowKey, props, false); return q; }, $"merge {partition}/{rowKey}", ct);
        if (r.StatusCode == HttpStatusCode.NotFound) { await InsertOrMergeAsync(partition, rowKey, props, ct); return; }
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Merge Entity");
    }

    public async Task InsertOrMergeAsync(string partition, string rowKey, JsonObject props, CancellationToken ct)
    {
        using var r = await SendAsync(() => { var q = Req(HttpMethod.Patch, Entity(partition, rowKey)); q.Content = Body(partition, rowKey, props, false); return q; }, $"upsert {partition}/{rowKey}", ct);
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Insert Or Merge Entity");
    }

    public async Task BatchInsertOrMergeAsync(string partition, IReadOnlyList<(string RowKey, JsonObject Props)> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return; if (rows.Count > 100) throw new ArgumentException("a table batch holds at most 100 rows");
        var batch = "batch_" + Guid.NewGuid().ToString("N"); var change = "changeset_" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append($"--{batch}\r\nContent-Type: multipart/mixed; boundary={change}\r\n\r\n");
        foreach (var (rk, props) in rows)
        {
            sb.Append($"--{change}\r\nContent-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n");
            sb.Append($"MERGE {Entity(partition, rk)}?{sasQuery} HTTP/1.1\r\nContent-Type: application/json\r\nAccept: application/json;odata=nometadata\r\n\r\n");
            sb.Append(Body(partition, rk, props, false).ReadAsStringAsync(ct).Result).Append("\r\n");
        }
        sb.Append($"--{change}--\r\n--{batch}--\r\n");
        var text = sb.ToString();
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Post, $"{origin}/$batch"); q.Content = new StringContent(text, Encoding.UTF8); q.Content.Headers.Remove("Content-Type");
            q.Content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/mixed; boundary={batch}"); return q;
        }, $"batch {partition} x{rows.Count}", ct);
        var resp = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new StorageException((int)r.StatusCode, "TableError", Scrub("Batch: HTTP " + (int)r.StatusCode + " " + resp));
        foreach (var line in resp.Split('\n')) if (line.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) && !line.StartsWith("HTTP/1.1 2", StringComparison.Ordinal)) throw new StorageException(400, "TableError", Scrub("Batch failed: " + resp[..Math.Min(resp.Length, 800)]));
    }

    public async Task DeleteBatchAsync(string partition, IReadOnlyList<string> rowKeys, CancellationToken ct)
    {
        MediaTables.RequireScratch(partition);   // nothing deletes upload data: the only deletes are the selftest's own scratch rows
        if (rowKeys.Count == 0) return; if (rowKeys.Count > 100) throw new ArgumentException("a table batch holds at most 100 rows");
        var batch = "batch_" + Guid.NewGuid().ToString("N"); var change = "changeset_" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append($"--{batch}\r\nContent-Type: multipart/mixed; boundary={change}\r\n\r\n");
        foreach (var rk in rowKeys)
        {
            sb.Append($"--{change}\r\nContent-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n");
            sb.Append($"DELETE {Entity(partition, rk)}?{sasQuery} HTTP/1.1\r\nAccept: application/json;odata=nometadata\r\nIf-Match: *\r\n\r\n");
        }
        sb.Append($"--{change}--\r\n--{batch}--\r\n");
        var text = sb.ToString();
        using var r = await SendAsync(() =>
        {
            var q = Req(HttpMethod.Post, $"{origin}/$batch"); q.Content = new StringContent(text, Encoding.UTF8); q.Content.Headers.Remove("Content-Type");
            q.Content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/mixed; boundary={batch}"); return q;
        }, $"delete batch {partition} x{rowKeys.Count}", ct);
        var resp = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new StorageException((int)r.StatusCode, "TableError", Scrub("Delete batch: HTTP " + (int)r.StatusCode + " " + resp));
        foreach (var line in resp.Split('\n')) if (line.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) && !line.StartsWith("HTTP/1.1 2", StringComparison.Ordinal)) throw new StorageException(400, "TableError", Scrub("Delete batch failed: " + resp));
    }

    public async Task<TableRow?> GetAsync(string partition, string rowKey, IReadOnlyCollection<string>? select, CancellationToken ct)
    {
        using var r = await SendAsync(() => Req(HttpMethod.Get, Entity(partition, rowKey), select is { Count: > 0 } ? "$select=" + Uri.EscapeDataString(string.Join(',', select)) : null), $"get {partition}/{rowKey}", ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw await Fail(r, "Query Entity");
        return Row(JsonNode.Parse(await r.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new InvalidDataException("table row is not an object"));
    }

    public async Task<List<TableRow>> QueryAsync(string? filter, IReadOnlyCollection<string>? select, CancellationToken ct)
    {
        var res = new List<TableRow>(); string? np = null, nr = null;
        do
        {
            var qs = new List<string>(); if (filter != null) qs.Add("$filter=" + Uri.EscapeDataString(filter));
            if (select is { Count: > 0 }) qs.Add("$select=" + Uri.EscapeDataString(string.Join(',', new[] { "PartitionKey", "RowKey", "Timestamp" }.Concat(select).Distinct())));
            if (np != null) { qs.Add("NextPartitionKey=" + Uri.EscapeDataString(np)); if (nr != null) qs.Add("NextRowKey=" + Uri.EscapeDataString(nr)); }
            using var r = await SendAsync(() => Req(HttpMethod.Get, $"{origin}/{table}()", string.Join('&', qs)), "query", ct);
            if (!r.IsSuccessStatusCode) throw await Fail(r, "Query Entities");
            if (JsonNode.Parse(await r.Content.ReadAsStringAsync(ct)) is JsonObject o && o["value"] is JsonArray a) foreach (var e in a) if (e is JsonObject eo) res.Add(Row(eo));
            np = r.Headers.TryGetValues("x-ms-continuation-NextPartitionKey", out var v1) ? v1.FirstOrDefault() : null;
            nr = r.Headers.TryGetValues("x-ms-continuation-NextRowKey", out var v2) ? v2.FirstOrDefault() : null;
        } while (np != null);
        return res;
    }

    static TableRow Row(JsonObject o)
    {
        var props = new JsonObject(); foreach (var kv in o) if (kv.Key is not ("PartitionKey" or "RowKey" or "Timestamp") && !kv.Key.Contains('@')) props[kv.Key] = kv.Value?.DeepClone();
        return new TableRow((string?)o["PartitionKey"] ?? "", (string?)o["RowKey"] ?? "", (string?)o["Timestamp"], props);
    }
}
