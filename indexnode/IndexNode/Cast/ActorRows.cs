using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.38: every actor of the index has an entry, named or not: one row per actor in the media table (PartitionKey "actor", RowKey the actor id, e.g. A000123) with what the index knows of it
/// (productions, uploads, units, faces, the index version that last changed it). The name votes of PROTOCOL "Actor names" are added to the same row later (v_&lt;upload hex&gt; properties); nothing here touches them: every write is an Insert Or Merge.
/// An actor that no longer has a unit (it was merged into another) keeps its row with units = 0. The index (uploads.json, state.json) stays the source of the actor ids; the rows are its queryable copy.
/// </summary>
public static class ActorRows
{
    public const string Partition = "actor";
    public sealed record Stat(int Prods, int Uploads, int Units, int Faces);

    /// <summary>What the index holds of each actor.</summary>
    public static Dictionary<string, Stat> Stats(IEnumerable<(string Id, string ETag, List<ActorsIndex.IUnit> Units)> uploads)
    {
        var prods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal); var ups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var units = new Dictionary<string, int>(StringComparer.Ordinal); var faces = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var u in uploads) foreach (var x in u.Units)
        {
            if (string.IsNullOrEmpty(x.Actor)) continue;
            (prods.TryGetValue(x.Actor, out var p) ? p : prods[x.Actor] = new(StringComparer.Ordinal)).Add(string.IsNullOrEmpty(x.Prod) ? u.Id : x.Prod);
            (ups.TryGetValue(x.Actor, out var s) ? s : ups[x.Actor] = new(StringComparer.Ordinal)).Add(u.Id);
            units[x.Actor] = units.GetValueOrDefault(x.Actor) + 1; faces[x.Actor] = faces.GetValueOrDefault(x.Actor) + x.N;
        }
        return units.ToDictionary(kv => kv.Key, kv => new Stat(prods[kv.Key].Count, ups[kv.Key].Count, kv.Value, faces[kv.Key]), StringComparer.Ordinal);
    }

    /// <summary>The actors in two or more productions.</summary>
    public static int Multi(Dictionary<string, Stat> stats) => stats.Values.Count(s => s.Prods > 1);

    /// <summary>The rows to write: every actor whose numbers differ from <paramref name="before"/> (all of them when it is null), and the actors that had units before and have none now.</summary>
    public static List<(string RowKey, JsonObject Props)> Rows(Dictionary<string, Stat>? before, Dictionary<string, Stat> now, int version)
    {
        var rows = new List<(string, JsonObject)>();
        foreach (var (id, s) in now.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (before == null || !before.TryGetValue(id, out var b) || b != s) rows.Add((id, Props(s, version)));
        if (before != null) foreach (var id in before.Keys.Where(k => !now.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal)) rows.Add((id, Props(new Stat(0, 0, 0, 0), version)));
        return rows;
    }
    static JsonObject Props(Stat s, int version) => new() { ["prods"] = s.Prods, ["uploads"] = s.Uploads, ["units"] = s.Units, ["faces"] = s.Faces, ["ver"] = version };

    /// <summary>Writes the rows in batches of 100 (one request each). Returns the requests made.</summary>
    public static async Task<int> WriteAsync(IMediaTable table, List<(string RowKey, JsonObject Props)> rows, CancellationToken ct)
    {
        int n = 0;
        for (int i = 0; i < rows.Count; i += 100) { await table.BatchInsertOrMergeAsync(Partition, rows.Skip(i).Take(100).ToList(), ct); n++; }
        return n;
    }
}
