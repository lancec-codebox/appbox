using System.Text;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.37: the predecessor list. Append blobs index/data/seq/seq.&lt;k&gt; hold one line per assigned upload, "&lt;id&gt;,&lt;ai 0|1&gt;\n". Appends are unconditional (each is atomic, so concurrent writers always get different block counts);
/// seq = k * 50,000 + the committed block count the append returned. At the block limit the writer creates seq.&lt;k+1&gt; (If-None-Match; a 409 means another writer made it) and appends there.
/// The first occurrence of an id is its position; readers skip later repeats. The row of the media table keeps seq, seqBlob, seqPos, seqOff and aiFlag.
/// </summary>
public static class SeqBlobs
{
    /// <summary>Azure's append-blob block limit (a test lowers it to exercise the rollover).</summary>
    public static int BlockLimit { get; set; } = 50000;
    public static string Name(Paths p, int k) => $"{p.Prefix}data/seq/seq.{k}";
    public static string Dir(Paths p) => $"{p.Prefix}data/seq/";
    public sealed record Pos(int Blob, int Count, long Offset) { public long Seq => (long)Blob * BlockLimit + Count; }
    public sealed record Entry(string Id, bool Ai, int Blob, int Count, long Offset) { public long Seq => (long)Blob * BlockLimit + Count; }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStore, int[]> lastBlob = new();
    static int[] Last(IStore s) { for (int i = 0; i < 8; i++) s = s switch { MeteredStore m => m.Inner, RecordingStore r => r.Inner, LocalCacheStore l => l.Inner, StageZipStore z => z.Inner, _ => s }; return lastBlob.GetValue(s, _ => new int[1]); }

    /// <summary>Appends "id,ai" and returns where it landed (blob, committed block count, offset of its line). No condition, no read.</summary>
    public static async Task<Pos> AppendAsync(IStore store, Paths paths, string id, bool ai, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($"{id},{(ai ? 1 : 0)}\n"); var last = Last(store);
        for (int guard = 0; guard < 64; guard++)
        {
            int k = Volatile.Read(ref last[0]); var name = Name(paths, k);
            AppendResult r;
            try { r = await store.AppendBlockAsync(name, bytes, -1, ct); }
            catch (StorageException e) when (e.Status == 409 && (e.Code ?? "").Contains("BlockCount", StringComparison.OrdinalIgnoreCase)) { Interlocked.CompareExchange(ref last[0], k + 1, k); await store.CreateAppendBlobAsync(Name(paths, k + 1), true, ct); continue; }
            if (r.Outcome == AppendOutcome.NotFound) { await store.CreateAppendBlobAsync(name, true, ct); continue; }   // false (409 / 412) = another writer made it: append there
            if (r.Outcome == AppendOutcome.Sealed) { Interlocked.CompareExchange(ref last[0], k + 1, k); continue; }
            if (r.Outcome != AppendOutcome.Ok) continue;
            if (r.Blocks >= BlockLimit + 1) { Interlocked.CompareExchange(ref last[0], k + 1, k); continue; }
            return new Pos(k, r.Blocks, r.Length - bytes.Length);
        }
        throw new IOException("the sequence blobs kept refusing the append");
    }

    /// <summary>Every entry in order, up to the end of the last blob (first occurrence of an id wins; repeats are skipped). <paramref name="prefix"/> = entries already read, with the offset to continue from (a reader that holds a prefix reads only what follows).</summary>
    public static async Task<List<Entry>> ReadAsync(IStore store, Paths paths, CancellationToken ct, List<Entry>? prefix = null)
    {
        var res = prefix == null ? new List<Entry>() : new List<Entry>(prefix); var seen = new HashSet<string>(res.Select(e => e.Id), StringComparer.Ordinal);
        int k = res.Count == 0 ? 0 : res[^1].Blob; long off = res.Count == 0 ? 0 : res[^1].Offset + Encoding.UTF8.GetByteCount(res[^1].Id) + 3; int count = res.Count == 0 ? 0 : res[^1].Count;
        for (; ; k++, off = 0, count = 0)
        {
            var name = Name(paths, k); bool any = false;
            for (; ; )
            {
                var rd = await store.ReadAppendAsync(name, off, 1 << 20, ct);
                if (rd.NotFound) { if (k > 0 || any) goto done; goto done; }
                any = true;
                if (rd.Data.Length == 0) break;
                int start = 0;
                for (int i = 0; i < rd.Data.Length; i++)
                {
                    if (rd.Data[i] != (byte)'\n') continue;
                    var line = Encoding.UTF8.GetString(rd.Data, start, i - start); long lineOff = off + start; start = i + 1; count++;
                    int c = line.LastIndexOf(','); if (c <= 0) continue;
                    var id = line[..c]; if (seen.Add(id)) res.Add(new Entry(id, line[(c + 1)..] == "1", k, count, lineOff));
                }
                off += start; if (start == 0 || off >= rd.Length) break;
            }
            if (!any) break;
            if (count < BlockLimit) break;   // a blob below the limit is the last one
        }
        done:
        return res;
    }

    /// <summary>The group step's first action: the row has no seq, so append blindly, read the blobs (needed for the predecessors anyway), take the FIRST occurrence of the id and merge seq, seqBlob, seqPos, seqOff, aiFlag into the row. Returns the entries before this upload.</summary>
    public static async Task<(Entry Mine, List<Entry> Before)> AssignAsync(IStore store, Paths paths, IMediaTable? table, string id, bool ai, CancellationToken ct)
    {
        TableRow? row = table == null ? null : await table.GetAsync(MediaTables.Upload, id, ["seq", "seqBlob", "seqPos", "seqOff", "aiFlag"], ct);   // 1 op: skip the append when the row already has its seq
        bool had = row?.Props["seq"] != null;
        if (!had) await AppendAsync(store, paths, id, ai, ct);
        var all = await ReadAsync(store, paths, ct);
        var idx = all.FindIndex(e => e.Id == id);
        if (idx < 0) throw new IOException($"{id}: not in the sequence blobs after the append");
        var mine = all[idx];
        if (table != null && !had) await table.MergeAsync(MediaTables.Upload, id, new JsonObject { ["seq"] = mine.Seq, ["seqBlob"] = mine.Blob, ["seqPos"] = mine.Count, ["seqOff"] = mine.Offset, ["aiFlag"] = ai }, ct);
        return (mine, all.Take(idx).ToList());
    }

    /// <summary>The predecessors an actors stage compares with: every entry before this upload except the AI-flagged ones (unless added by hand).</summary>
    public static IEnumerable<Entry> ActorsPredecessors(IEnumerable<Entry> before, ISet<string>? manual = null) => before.Where(e => !e.Ai || manual?.Contains(e.Id) == true);

    /// <summary>The renumbering plan: eligible uploads (id, ai) in their current order get 1..N (the pin base); the rest are dropped. Pure: the output is the old to new map and the dropped list.</summary>
    public sealed record Plan(List<(string Id, bool Ai, long? Old, long New)> Map, List<string> Dropped, List<string> Lines);
    public static Plan Renumber(IEnumerable<(string Id, bool Ai, bool Eligible, long? Seq)> uploads)
    {
        var ordered = uploads.OrderBy(u => u.Seq ?? long.MaxValue).ThenBy(u => u.Id, StringComparer.Ordinal).ToList();
        var map = new List<(string, bool, long?, long)>(); var dropped = new List<string>(); var lines = new List<string>();
        foreach (var u in ordered) { if (!u.Eligible) { dropped.Add(u.Id); continue; } map.Add((u.Id, u.Ai, u.Seq, map.Count + 1L)); lines.Add($"{u.Id},{(u.Ai ? 1 : 0)}"); }
        return new Plan(map, dropped, lines);
    }

    /// <summary>Renumbering (queue drained, no live leases): reads every upload row (seq, aiFlag, data, process state), plans, and either writes the plan to a local folder (--out-local: map.tsv, dropped.txt, seq.N.txt, report.txt) or applies it (--from-local: delete index/data/seq/, one block per line into seq.0.., merge the new seq into each row). Idempotent: a second run plans the same numbers.</summary>
    public static async Task<Plan> PlanFromTableAsync(IMediaTable table, CancellationToken ct)
    {
        var rows = await VideoJson.QueryUploadsAsync(table, null, null, ["seq", "aiFlag", "data", "eligible"], ct);
        return Renumber(rows.Select(r => (r.RowKey, r.Props["aiFlag"] is { } f && f.ToString() == "true", Eligible(r), (long?)(r.Props["seq"] is { } sq && long.TryParse(sq.ToString(), out var n) ? n : null))));
    }
    /// <summary>Eligible = fully processed (data=full and a done process / cast); the table's "eligible" flat field says so when the stage marks wrote it, else a seq already assigned by the group step.</summary>
    static bool Eligible(TableRow r) => r.Props["eligible"] is { } e ? e.ToString() == "true" : r.Props["seq"] != null && (r.Str("data") ?? "full") == "full";

    public static string ReportText(Plan p) => $"eligible {p.Map.Count}, dropped {p.Dropped.Count}, ai {p.Map.Count(m => m.Ai)}\n" + string.Join("\n", p.Map.Select(m => $"{m.Id}\t{m.Old?.ToString() ?? "-"}\t{m.New}\t{(m.Ai ? "ai" : "")}"));
    public static void WriteLocal(Plan p, string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "map.tsv"), string.Concat(p.Map.Select(m => $"{m.Id}\t{m.Old?.ToString() ?? ""}\t{m.New}\t{(m.Ai ? 1 : 0)}\n")));
        File.WriteAllText(Path.Combine(dir, "dropped.txt"), string.Concat(p.Dropped.Select(d => d + "\n")));
        File.WriteAllText(Path.Combine(dir, "report.txt"), ReportText(p));
    }
    public static Plan ReadLocal(string dir)
    {
        var map = File.ReadAllLines(Path.Combine(dir, "map.tsv")).Where(l => l.Length > 0).Select(l => l.Split('\t')).Select(c => (c[0], c[3] == "1", c[1].Length == 0 ? (long?)null : long.Parse(c[1]), long.Parse(c[2]))).ToList();
        var dropped = File.ReadAllLines(Path.Combine(dir, "dropped.txt")).Where(l => l.Length > 0).ToList();
        return new Plan(map, dropped, map.Select(m => $"{m.Item1},{(m.Item2 ? 1 : 0)}").ToList());
    }
    public static async Task ApplyAsync(IStore store, Paths paths, IMediaTable? table, Plan p, CancellationToken ct)
    {
        foreach (var b in await store.ListAsync(Dir(paths), false, false, ct)) await store.DeleteAsync(b.Name, ct);
        Volatile.Write(ref Last(store)[0], 0);
        int k = 0, n = 0; await store.CreateAppendBlobAsync(Name(paths, 0), false, ct);
        var placed = new List<(string Id, bool Ai, Pos Pos)>();
        foreach (var l in p.Lines)
        {
            var c = l.LastIndexOf(','); var pos = await AppendAsync(store, paths, l[..c], l[(c + 1)..] == "1", ct); placed.Add((l[..c], l[(c + 1)..] == "1", pos)); n++; _ = k;
        }
        if (table == null) return;
        foreach (var (id, ai, pos) in placed) await table.MergeAsync(MediaTables.Upload, id, new JsonObject { ["seq"] = pos.Seq, ["seqBlob"] = pos.Blob, ["seqPos"] = pos.Count, ["seqOff"] = pos.Offset, ["aiFlag"] = ai }, ct);
        foreach (var d in p.Dropped) await table.MergeAsync(MediaTables.Upload, d, new JsonObject { ["seq"] = null, ["seqBlob"] = null, ["seqPos"] = null, ["seqOff"] = null }, ct);
    }

    /// <summary>The blobs of a plan (a local preview of the layout): seq.0, seq.1, ... holding the lines in blocks (one block per line, at most 50,000 per blob). Seqs count from 1 (block count of the append).</summary>
    public static List<(string Name, byte[] Data)> Layout(Paths paths, IReadOnlyList<string> lines)
    {
        var res = new List<(string, byte[])>();
        for (int k = 0, i = 0; i < lines.Count; k++) { var chunk = lines.Skip(i).Take(BlockLimit).ToList(); i += chunk.Count; res.Add((Name(paths, k), Encoding.UTF8.GetBytes(string.Concat(chunk.Select(l => l + "\n"))))); }
        return res;
    }
}
