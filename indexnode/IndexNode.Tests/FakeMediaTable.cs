using System.Globalization;
using System.Text.Json.Nodes;

namespace IndexNode.Tests;

/// <summary>An in-memory Azure Table `media`: merge, insert-or-merge, point reads, queries with $filter (eq ne ge gt le lt, and, or, parentheses; strings, booleans, numbers, datetime'...'), $select and continuation pages, and batches (at most 100 rows of one partition). Counts every request in Requests.</summary>
public sealed class FakeMediaTable : IMediaTableAdmin
{
    readonly object gate = new();
    readonly SortedDictionary<(string P, string R), (JsonObject Props, DateTimeOffset Ts)> rows = new();
    public int Requests, Merges, InsertOrMerges, Gets, Queries, Batches, Pages;
    public int PageSize = 1000;
    public Func<string, string, JsonObject, Exception?>? Fault;   // (partition, rowKey, props) -> exception to throw, once per call
    public readonly List<(string Op, string P, string R, JsonObject Props)> Log = [];
    DateTimeOffset clock = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
    public Func<DateTimeOffset>? Now;
    DateTimeOffset Tick() { clock = clock.AddMilliseconds(1); return Now?.Invoke() ?? clock; }

    void Req() => Requests++;

    public Task MergeAsync(string p, string r, JsonObject props, CancellationToken ct)
    {
        lock (gate) { Req(); Merges++; Log.Add(("merge", p, r, (JsonObject)props.DeepClone())); if (Fault?.Invoke(p, r, props) is { } ex) throw ex; Put(p, r, props, merge: true); }
        return Task.CompletedTask;
    }

    public Task InsertOrMergeAsync(string p, string r, JsonObject props, CancellationToken ct)
    {
        lock (gate) { Req(); InsertOrMerges++; Log.Add(("upsert", p, r, (JsonObject)props.DeepClone())); if (Fault?.Invoke(p, r, props) is { } ex) throw ex; Put(p, r, props, merge: true); }
        return Task.CompletedTask;
    }

    public Task BatchInsertOrMergeAsync(string p, IReadOnlyList<(string RowKey, JsonObject Props)> batch, CancellationToken ct)
    {
        if (batch.Count > 100) throw new ArgumentException("a batch holds at most 100 rows");
        lock (gate) { Req(); Batches++; foreach (var (rk, props) in batch) { Log.Add(("batch", p, rk, (JsonObject)props.DeepClone())); Put(p, rk, props, merge: true); } }
        return Task.CompletedTask;
    }

    void Put(string p, string r, JsonObject props, bool merge)
    {
        var cur = rows.TryGetValue((p, r), out var have) && merge ? (JsonObject)have.Props.DeepClone() : new JsonObject();
        foreach (var kv in props) if (!kv.Key.Contains('@')) cur[kv.Key] = kv.Value?.DeepClone();
        rows[(p, r)] = (cur, Tick());
    }

    static TableRow ToRow(string p, string r, JsonObject props, DateTimeOffset ts, IReadOnlyCollection<string>? select)
    {
        var o = new JsonObject(); foreach (var kv in props) if (select == null || select.Count == 0 || select.Contains(kv.Key)) o[kv.Key] = kv.Value?.DeepClone();
        return new TableRow(p, r, ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture), o);
    }

    public Task<TableRow?> GetAsync(string p, string r, IReadOnlyCollection<string>? select, CancellationToken ct)
    {
        lock (gate) { Req(); Gets++; return Task.FromResult<TableRow?>(rows.TryGetValue((p, r), out var v) ? ToRow(p, r, v.Props, v.Ts, select) : null); }
    }

    public Task<List<TableRow>> QueryAsync(string? filter, IReadOnlyCollection<string>? select, CancellationToken ct)
    {
        lock (gate)
        {
            Queries++; var res = new List<TableRow>(); var pred = filter == null ? null : new Odata(filter);
            var matched = rows.Where(kv => pred == null || pred.Matches(kv.Key.P, kv.Key.R, kv.Value.Ts, kv.Value.Props)).ToList();
            int pages = Math.Max(1, (matched.Count + PageSize - 1) / PageSize); Requests += pages; Pages += pages;
            foreach (var kv in matched) res.Add(ToRow(kv.Key.P, kv.Key.R, kv.Value.Props, kv.Value.Ts, select));
            return Task.FromResult(res);
        }
    }

    public Task DeleteBatchAsync(string p, IReadOnlyList<string> rowKeys, CancellationToken ct) { MediaTables.RequireScratch(p); lock (gate) { Req(); foreach (var rk in rowKeys) rows.Remove((p, rk)); } return Task.CompletedTask; }
    public int Count => rows.Count;
    public JsonObject? Props(string p, string r) { lock (gate) return rows.TryGetValue((p, r), out var v) ? (JsonObject)v.Props.DeepClone() : null; }
    public IReadOnlyList<(string P, string R)> Keys() { lock (gate) return rows.Keys.ToList(); }
    public string Dump() { lock (gate) return string.Join("\n", rows.Select(kv => $"{kv.Key.P}|{kv.Key.R}|{kv.Value.Props.ToJsonString()}")); }

    /// <summary>The OData subset the node uses.</summary>
    sealed class Odata
    {
        readonly List<string> tok = []; int i;
        public Odata(string f)
        {
            int k = 0;
            while (k < f.Length)
            {
                char c = f[k];
                if (char.IsWhiteSpace(c)) { k++; continue; }
                if (c is '(' or ')') { tok.Add(c.ToString()); k++; continue; }
                if (c == '\'') { int e = k + 1; var sb = new System.Text.StringBuilder(); while (e < f.Length) { if (f[e] == '\'' && e + 1 < f.Length && f[e + 1] == '\'') { sb.Append('\''); e += 2; continue; } if (f[e] == '\'') break; sb.Append(f[e++]); } tok.Add("'" + sb); k = e + 1; continue; }
                int s = k; while (k < f.Length && !char.IsWhiteSpace(f[k]) && f[k] != '(' && f[k] != ')' && f[k] != '\'') k++;
                var w = f[s..k];
                if (w == "datetime" && k < f.Length && f[k] == '\'') { int e = f.IndexOf('\'', k + 1); tok.Add("dt" + f[(k + 1)..e]); k = e + 1; continue; }
                tok.Add(w);
            }
        }
        string p = "", r = ""; DateTimeOffset ts; JsonObject props = null!;
        public bool Matches(string pk, string rk, DateTimeOffset t, JsonObject pr) { p = pk; r = rk; ts = t; props = pr; i = 0; return Or(); }
        bool Or() { bool v = And(); while (i < tok.Count && tok[i] == "or") { i++; bool r2 = And(); v = v || r2; } return v; }
        bool And() { bool v = Atom(); while (i < tok.Count && tok[i] == "and") { i++; bool r2 = Atom(); v = v && r2; } return v; }
        bool Atom()
        {
            if (tok[i] == "(") { i++; bool v = Or(); i++; return v; }
            if (tok[i] == "not") { i++; return !Atom(); }
            string name = tok[i++], op = tok[i++], lit = tok[i++];
            object? val = name switch { "PartitionKey" => p, "RowKey" => r, "Timestamp" => ts, _ => props[name] is JsonValue jv ? (jv.TryGetValue<string>(out var s) ? s : jv.TryGetValue<bool>(out var b) ? b : jv.TryGetValue<double>(out var d) ? d : null) : null };
            object? rhs = lit.StartsWith('\'') ? lit[1..] : lit.StartsWith("dt") ? DateTimeOffset.Parse(lit[2..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal) : lit is "true" or "false" ? lit == "true" : double.Parse(lit, CultureInfo.InvariantCulture);
            if (val == null) return false;
            int cmp = val is string a && rhs is string b2 ? string.CompareOrdinal(a, b2) : val is bool x && rhs is bool y ? x.CompareTo(y) : val is DateTimeOffset d1 && rhs is DateTimeOffset d2 ? d1.CompareTo(d2) : val is double n1 && rhs is double n2 ? n1.CompareTo(n2) : 2;
            if (cmp == 2) return false;
            return op switch { "eq" => cmp == 0, "ne" => cmp != 0, "ge" => cmp >= 0, "gt" => cmp > 0, "le" => cmp <= 0, "lt" => cmp < 0, _ => throw new NotSupportedException(op) };
        }
    }
}
