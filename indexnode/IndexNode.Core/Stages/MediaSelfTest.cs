using System.Globalization;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.37 "indexnode media selftest": the table calls the node relies on, run against the configured table in a scratch partition selftest-&lt;guid&gt; (never the real partitions), one pass or fail per call; the scratch rows are deleted at the end.
/// Run it against the test table before a node gets a table SAS.
/// </summary>
public static class MediaSelfTest
{
    public sealed record Result(string Call, bool Ok, string Detail);
    public const int Rows = 1100;   // more than one query page (1000)

    public static async Task<List<Result>> RunAsync(IMediaTableAdmin t, CancellationToken ct, string? partition = null)
    {
        var part = partition ?? "selftest-" + Guid.NewGuid().ToString("N"); var res = new List<Result>();
        async Task Step(string name, Func<Task<string>> f)
        {
            try { res.Add(new(name, true, await f())); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { res.Add(new(name, false, e.GetType().Name + ": " + Redact.Sas(e.Message))); }
        }
        string Need(bool ok, string what) => ok ? what : throw new InvalidOperationException("unexpected: " + what);
        const string rk = "one";
        try
        {
            await Step("InsertOrMerge (PATCH, no If-Match) creates a row", async () =>
            {
                await t.InsertOrMergeAsync(part, rk, new JsonObject { ["a"] = "x", ["n"] = 7, ["b"] = true }, ct);
                var r = await t.GetAsync(part, rk, null, ct); return Need(r != null && r.Str("a") == "x" && r.Num("n") == 7 && r.Bool("b") == true, "row created, string/int/bool read back");
            });
            await Step("InsertOrMerge merges into an existing row (keeps other properties)", async () =>
            {
                await t.InsertOrMergeAsync(part, rk, new JsonObject { ["c"] = "y" }, ct);
                var r = await t.GetAsync(part, rk, null, ct); return Need(r != null && r.Str("a") == "x" && r.Str("c") == "y", "a kept, c added");
            });
            await Step("Merge (If-Match: *) changes only the named property", async () =>
            {
                await t.MergeAsync(part, rk, new JsonObject { ["a"] = "z" }, ct);
                var r = await t.GetAsync(part, rk, null, ct); return Need(r != null && r.Str("a") == "z" && r.Str("c") == "y" && r.Num("n") == 7, "a changed, c and n kept");
            });
            await Step("Merge of a missing row falls back to insert", async () =>
            {
                await t.MergeAsync(part, "missing", new JsonObject { ["m"] = 1 }, ct);
                var r = await t.GetAsync(part, "missing", null, ct); return Need(r != null && r.Num("m") == 1, "row exists after the merge");
            });
            await Step("Edm.DateTime property round trip", async () =>
            {
                var at = new DateTimeOffset(2026, 11, 23, 16, 5, 6, 789, TimeSpan.Zero);
                await t.MergeAsync(part, rk, new JsonObject { ["when"] = at.UtcDateTime.ToString("o"), ["when@odata.type"] = "Edm.DateTime" }, ct);
                var r = await t.GetAsync(part, rk, ["when"], ct); var s = r?.Str("when");
                return Need(s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var back) && back == at, "read back " + s);
            });
            await Step("point read with $select returns only the selected properties", async () =>
            {
                var r = await t.GetAsync(part, rk, ["a"], ct); return Need(r != null && r.Str("a") == "z" && r.Props["c"] == null && r.Props["n"] == null, "only a");
            });
            await Step("point read of a missing row is null", async () => Need(await t.GetAsync(part, "nope", null, ct) == null, "null"));
            await Step("row Timestamp is set and moves forward on a merge", async () =>
            {
                var r1 = await t.GetAsync(part, rk, null, ct); await Task.Delay(50, ct); await t.MergeAsync(part, rk, new JsonObject { ["t"] = 1 }, ct); var r2 = await t.GetAsync(part, rk, null, ct);
                bool p1 = DateTimeOffset.TryParse(r1?.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var a), p2 = DateTimeOffset.TryParse(r2?.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var b);
                return Need(p1 && p2 && b > a, $"{r1?.Timestamp} -> {r2?.Timestamp}");
            });
            await Step($"batch of 100 rows (one request)", async () =>
            {
                await t.BatchInsertOrMergeAsync(part, Enumerable.Range(0, 100).Select(i => ("b" + i.ToString("D4"), new JsonObject { ["i"] = i, ["even"] = i % 2 == 0 })).ToList(), ct);
                var r = await t.GetAsync(part, "b0099", null, ct); return Need(r != null && r.Num("i") == 99, "row 99 present");
            });
            await Step($"{Rows} rows loaded (11 batches)", async () =>
            {
                for (int s = 100; s < Rows; s += 100)
                    await t.BatchInsertOrMergeAsync(part, Enumerable.Range(s, Math.Min(100, Rows - s)).Select(i => ("b" + i.ToString("D4"), new JsonObject { ["i"] = i, ["even"] = i % 2 == 0 })).ToList(), ct);
                return "loaded";
            });
            await Step("query with $filter and continuation (more than 1000 rows)", async () =>
            {
                var all = await t.QueryAsync($"PartitionKey eq '{part}' and i ge 0", ["i"], ct);
                return Need(all.Count == Rows, $"{all.Count} rows with i ge 0 (expected {Rows}: every page followed)");
            });
            await Step("query with a boolean filter and $select", async () =>
            {
                var ev = await t.QueryAsync($"PartitionKey eq '{part}' and even eq true", ["i"], ct);
                return Need(ev.Count == Rows / 2 && ev.All(r => r.Num("i") is { } i && i % 2 == 0 && r.Props["even"] == null), $"{ev.Count} even rows, only i selected");
            });
            await Step("query by Timestamp (changed since)", async () =>
            {
                await Task.Delay(50, ct); await t.MergeAsync(part, rk, new JsonObject { ["t"] = 2 }, ct);   // the row becomes the newest of the partition: everything else was written before it
                var one = await t.GetAsync(part, rk, null, ct); var ts = DateTimeOffset.Parse(one!.Timestamp!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                var q = await t.QueryAsync($"PartitionKey eq '{part}' and Timestamp ge datetime'{ts.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}'", null, ct);
                return Need(q.Count == 1 && q[0].RowKey == rk, $"{q.Count} row(s) at or after the newest row's own Timestamp (expected exactly that row)");
            });
        }
        finally
        {
            await Step("cleanup: scratch partition deleted", async () =>
            {
                var keys = (await t.QueryAsync($"PartitionKey eq '{part}'", ["i"], CancellationToken.None)).Select(r => r.RowKey).ToList();
                for (int s = 0; s < keys.Count; s += 100) await t.DeleteBatchAsync(part, keys.Skip(s).Take(100).ToList(), CancellationToken.None);
                var left = await t.QueryAsync($"PartitionKey eq '{part}'", ["i"], CancellationToken.None);
                return Need(left.Count == 0, $"{keys.Count} row(s) deleted, 0 left");
            });
        }
        return res;
    }
}
