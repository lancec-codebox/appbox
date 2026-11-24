using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>0.13.37 "indexnode media verify [--limit N]": read-only. For N uploads (ids from the table, in key order) the row is compared with the video.json blob section by section; every difference is listed. It gates turning table reads on.</summary>
public static class MediaVerify
{
    public sealed record Diff(string Id, string Section, string Kind, string Detail);
    public sealed record Report(int Checked, int Clean, List<Diff> Diffs);

    public static async Task<Report> RunAsync(IMediaTable table, IStore store, Paths paths, int? limit, CancellationToken ct)
    {
        var rows = (await VideoJson.QueryUploadsAsync(table, null, null, null, ct)).OrderBy(r => r.RowKey, StringComparer.Ordinal).ToList();
        if (limit is { } n) rows = rows.Take(n).ToList();
        var diffs = new List<Diff>(); int clean = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var before = diffs.Count;
            if (row.Num(MediaRecords.DocRev) == null) diffs.Add(new(row.RowKey, "(row)", "no-docRev", "the row mirrors no whole document: readers would not use it"));
            VideoJson.Doc? blob;
            try { blob = await VideoJson.ReadBlobAsync(store, paths, row.RowKey, ct, fresh: true); }
            catch (InvalidDataException e) { diffs.Add(new(row.RowKey, "(blob)", "unreadable", e.Message)); continue; }
            if (blob == null) { diffs.Add(new(row.RowKey, "(blob)", "missing", "the row has no video.json")); continue; }
            if (row.Num(MediaRecords.DocRev) is { } dr && (long)dr != blob.Rev) diffs.Add(new(row.RowKey, "(row)", "docRev", $"row docRev {(long)dr}, blob rev {blob.Rev}"));
            var inBlob = (row.Str(MediaRecords.InBlob) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var rowDoc = MediaRecords.DocOf(row);
            var names = blob.Root.Select(k => k.Key).Concat(rowDoc.Select(k => k.Key)).Where(k => k is not ("v" or "id" or "rev")).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
            foreach (var name in names)
            {
                var b = blob.Root[name]; var r = rowDoc[name];
                if (inBlob.Contains(name)) { if (b == null) diffs.Add(new(row.RowKey, name, "in-blob", "listed in inBlob but the blob has no such section")); continue; }
                var want = b == null ? null : MediaRecords.SectionText(name, b);   // the same shape the writer stores (grouping without its stretches)
                if (r == null && b != null && want != null) diffs.Add(new(row.RowKey, name, "missing-in-row", "the blob has it, the row does not"));
                else if (r != null && b == null) diffs.Add(new(row.RowKey, name, "missing-in-blob", "the row has it, the blob does not"));
                else if (r != null && want != null && !JsonNode.DeepEquals(JsonNode.Parse(want), r)) diffs.Add(new(row.RowKey, name, "differs", Short(want) + "  <>  " + Short(r.ToJsonString())));
            }
            if (diffs.Count == before) clean++;
        }
        return new(rows.Count, clean, diffs);
    }
    static string Short(string s) => s.Length <= 120 ? s : s[..120] + "...";
}
