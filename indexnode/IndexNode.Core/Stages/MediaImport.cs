using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.37 "media import [--out-local DIR | --from-local DIR]": the one-off move of today's aggregate into the table `media`. One aggregate read, rows built with <see cref="MediaRecords.Props"/> (every section, plus the flat fields),
/// written 100 per partition transaction (Insert Or Merge: idempotent, never drops what another writer put there). --out-local writes the rows and a report (counts, planned table ops) and touches nothing else; --from-local applies those rows.
/// </summary>
public static class MediaImport
{
    public const int BatchSize = 100;
    public sealed record Report(int Uploads, int Rows, int InBlob, int Batches, long Bytes);

    public static List<(string Id, JsonObject Props)> BuildRows(IReadOnlyDictionary<string, VideoJson.Doc> docs)
    {
        var rows = new List<(string, JsonObject)>();
        foreach (var kv in docs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var root = kv.Value.Root; var p = MediaRecords.Props(root, root.Select(x => x.Key).Where(n => n is not ("v" or "id" or "rev"))); p[MediaRecords.DocRev] = kv.Value.Rev;
            p["data"] = root["download"] != null ? "full" : "precheck";
            rows.Add((kv.Key, p));
        }
        return rows;
    }

    public static Report Plan(List<(string Id, JsonObject Props)> rows, int uploads) =>
        new(uploads, rows.Count, rows.Count(r => r.Props[MediaRecords.InBlob] != null), (rows.Count + BatchSize - 1) / BatchSize, rows.Sum(r => (long)r.Props.ToJsonString().Length));

    public static string ReportText(Report r) => $"uploads {r.Uploads}, rows {r.Rows}, rows with a section left in its blob {r.InBlob}, planned table operations {r.Batches} batch(es) of up to {BatchSize}, about {r.Bytes / 1024} KB";

    public static void WriteLocal(List<(string Id, JsonObject Props)> rows, Report rep, string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "rows.jsonl"), rows.Select(r => new JsonObject { ["id"] = r.Id, ["props"] = r.Props.DeepClone() }.ToJsonString()));
        File.WriteAllText(Path.Combine(dir, "report.txt"), ReportText(rep) + "\n");
    }

    public static List<(string Id, JsonObject Props)> ReadLocal(string dir) =>
        File.ReadAllLines(Path.Combine(dir, "rows.jsonl")).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).Select(o => ((string)o["id"]!, (JsonObject)o["props"]!)).ToList();

    public static async Task<int> ApplyAsync(IMediaTable table, List<(string Id, JsonObject Props)> rows, CancellationToken ct)
    {
        int batches = 0;
        for (int i = 0; i < rows.Count; i += BatchSize)
        {
            await table.BatchInsertOrMergeAsync(MediaTables.Upload, rows.Skip(i).Take(BatchSize).Select(r => (r.Id, r.Props)).ToList(), ct); batches++;
        }
        return batches;
    }

    /// <summary>Reads the aggregate once (not the table) and builds the rows.</summary>
    public static async Task<(List<(string Id, JsonObject Props)> Rows, Report Rep)> FromAggregateAsync(IStore store, Paths paths, CancellationToken ct)
    {
        var docs = await VideoAggregate.ReadManyAsync(store, paths, null, ct) ?? throw new InvalidOperationException("there is no aggregate to import from");
        var rows = BuildRows(docs); return (rows, Plan(rows, docs.Count));
    }
}
