using System.Globalization;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.37 (`run --shadow --out-local DIR`): the table side of the shadow contract. Reads go through to the real table (point reads, queries) with the local overlay merged on top; every write (Merge, Insert Or Merge, Batch) goes to
/// <c>DIR/table/&lt;partition&gt;/&lt;rowkey&gt;.json</c>, merged the way Table merges (named properties replaced, the rest kept). Nothing is ever sent to the real table but reads.
/// A query is the real query (same filter) with the overlay merged onto the rows it returns, plus the overlay-only rows that match the filter (a plain `prop op value` clause list joined by and / or; a filter with parentheses adds no overlay-only rows).
/// </summary>
public sealed class ShadowMediaTable(IMediaTable real, string dir) : IMediaTable
{
    readonly string root = Path.Combine(dir, "table"); readonly object gate = new();
    public long Writes;
    public IMediaTable Real => real;
    string FileOf(string p, string r) => Path.Combine(root, Uri.EscapeDataString(p), Uri.EscapeDataString(r) + ".json");
    static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    JsonObject? Local(string p, string r) { var f = FileOf(p, r); return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject : null; }

    void Put(string p, string r, JsonObject props)
    {
        lock (gate)
        {
            var cur = Local(p, r) ?? new JsonObject();
            foreach (var kv in props) if (!kv.Key.Contains('@')) cur[kv.Key] = kv.Value?.DeepClone();
            var f = FileOf(p, r); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, cur.ToJsonString()); Writes++;
        }
    }

    public Task MergeAsync(string p, string r, JsonObject props, CancellationToken ct) { Put(p, r, props); return Task.CompletedTask; }
    public Task InsertOrMergeAsync(string p, string r, JsonObject props, CancellationToken ct) { Put(p, r, props); return Task.CompletedTask; }
    public Task BatchInsertOrMergeAsync(string p, IReadOnlyList<(string RowKey, JsonObject Props)> rows, CancellationToken ct) { foreach (var (rk, props) in rows) Put(p, rk, props); return Task.CompletedTask; }

    static TableRow Overlay(TableRow? baseRow, string p, string r, JsonObject? over, IReadOnlyCollection<string>? select)
    {
        var o = new JsonObject(); if (baseRow != null) foreach (var kv in baseRow.Props) o[kv.Key] = kv.Value?.DeepClone();
        if (over != null) foreach (var kv in over) o[kv.Key] = kv.Value?.DeepClone();
        if (select is { Count: > 0 }) foreach (var k in o.Select(kv => kv.Key).ToList()) if (!select.Contains(k)) o.Remove(k);
        return new TableRow(p, r, baseRow?.Timestamp ?? Now(), o);
    }

    public async Task<TableRow?> GetAsync(string p, string r, IReadOnlyCollection<string>? select, CancellationToken ct)
    {
        var over = Local(p, r);
        var real0 = await real.GetAsync(p, r, over == null ? select : null, ct);   // with an overlay the whole row is read, then cut
        if (real0 == null && over == null) return null;
        return Overlay(real0, p, r, over, select);
    }

    public async Task<List<TableRow>> QueryAsync(string? filter, IReadOnlyCollection<string>? select, CancellationToken ct)
    {
        var rows = await real.QueryAsync(filter, null, ct);
        var res = new List<TableRow>(); var seen = new HashSet<(string, string)>();
        foreach (var row in rows) { seen.Add((row.PartitionKey, row.RowKey)); res.Add(Overlay(row, row.PartitionKey, row.RowKey, Local(row.PartitionKey, row.RowKey), select)); }
        if (Directory.Exists(root))
            foreach (var pd in Directory.GetDirectories(root)) foreach (var f in Directory.GetFiles(pd, "*.json"))
            {
                var p = Uri.UnescapeDataString(Path.GetFileName(pd)); var r = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(f));
                if (seen.Contains((p, r))) continue;
                // a row only the overlay knows, or one the real query filtered out: the merged row decides
                var merged = Overlay(await real.GetAsync(p, r, null, ct), p, r, Local(p, r), null);
                if (Matches(filter, merged)) res.Add(select is { Count: > 0 } ? Overlay(null, p, r, merged.Props, select) with { Timestamp = merged.Timestamp } : merged);
            }
        // rows whose merged state no longer matches the filter drop out
        return res.Where(x => Matches(filter, Overlay(null, x.PartitionKey, x.RowKey, x.Props, null) with { })).ToList();
    }

    /// <summary>A small OData filter: clauses `prop op literal` (eq ne gt ge lt le) joined by and / or (and binds tighter). Anything it cannot read (parentheses, functions) matches, so a row is never dropped by a filter this cannot judge.</summary>
    internal static bool Matches(string? filter, TableRow row)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (filter.Contains('(')) return true;
        foreach (var orPart in System.Text.RegularExpressions.Regex.Split(filter, @"\s+or\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            bool all = true;
            foreach (var cl in System.Text.RegularExpressions.Regex.Split(orPart, @"\s+and\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var m = System.Text.RegularExpressions.Regex.Match(cl.Trim(), @"^(\w+)\s+(eq|ne|gt|ge|lt|le)\s+(.+)$");
                if (!m.Success) continue;
                if (!Clause(row, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Trim())) { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }

    static bool Clause(TableRow row, string prop, string op, string lit)
    {
        string? have = prop switch { "PartitionKey" => row.PartitionKey, "RowKey" => row.RowKey, _ => row.Props[prop] is JsonValue v ? v.ToString() : null };
        int c;
        if (lit.StartsWith('\'')) { var s = lit.Trim('\'').Replace("''", "'"); if (have == null) return op == "ne"; c = string.CompareOrdinal(have, s); }
        else if (lit is "true" or "false") { if (have == null) return op == "ne"; c = string.Compare(have.ToLowerInvariant(), lit, StringComparison.Ordinal); }
        else if (double.TryParse(lit, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { if (!double.TryParse(have, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return op == "ne"; c = h.CompareTo(d); }
        else { var s = lit.Contains('\'') ? lit[(lit.IndexOf('\'') + 1)..].TrimEnd('\'') : lit; if (have == null) return op == "ne"; c = string.CompareOrdinal(have, s); }
        return op switch { "eq" => c == 0, "ne" => c != 0, "gt" => c > 0, "ge" => c >= 0, "lt" => c < 0, _ => c <= 0 };
    }
}

/// <summary>0.13.37: the table of a command whose stores stand for "local only" (--out-local without the explicit live command): reads pass, a write is a bug and throws.</summary>
public sealed class ReadOnlyMediaTable(IMediaTable real) : IMediaTable
{
    static Exception No() => new InvalidOperationException("this command writes locally only (--out-local): a write to the media table was refused");
    public Task MergeAsync(string p, string r, JsonObject props, CancellationToken ct) => throw No();
    public Task InsertOrMergeAsync(string p, string r, JsonObject props, CancellationToken ct) => throw No();
    public Task BatchInsertOrMergeAsync(string p, IReadOnlyList<(string RowKey, JsonObject Props)> rows, CancellationToken ct) => throw No();
    public Task<TableRow?> GetAsync(string p, string r, IReadOnlyCollection<string>? select, CancellationToken ct) => real.GetAsync(p, r, select, ct);
    public Task<List<TableRow>> QueryAsync(string? filter, IReadOnlyCollection<string>? select, CancellationToken ct) => real.QueryAsync(filter, select, ct);
}
