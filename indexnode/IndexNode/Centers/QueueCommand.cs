using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.24 `indexnode queue import-done group [--dry-run]`: the one-time import that makes the queue the only source of completion. Every upload with a grouping record in the aggregate whose queue subject has no done group entry gets
/// "add group (state done)", its seq (when the queue has none) and its join record; an AI-flagged one that has no actors entry also gets the actors hold. Batched appends, paced under the breaker. Idempotent: what the queue already has is skipped.
/// </summary>
public static class QueueCommand
{
    public sealed record ImportReport(int Grouped, int GroupDone, int Seqs, int Joins, int Holds, int Appends);

    public static async Task<ImportReport> ImportDoneGroupAsync(JobQueue q, IStore store, Paths paths, bool dry, CancellationToken ct, OpPacer? pacer = null)
    {
        await q.PollAsync(ct);
        var docs = await VideoJson.ReadManyAsync(store, paths, new List<string>(), ct, all: true);   // the aggregate: one list + one GET
        var have = q.Read(st => st.Subjects.Values.ToDictionary(s => s.Id, s => (Done: s.Entries.TryGetValue("group", out var g) && g.State is "done" or "duplicate", s.Seq, s.JoinKind, Actors: s.Entries.ContainsKey("actors")), StringComparer.Ordinal));
        var lines = new List<QLine>(); int grouped = 0, done = 0, seqs = 0, joins = 0, holds = 0; var at = q.NowUtc();
        foreach (var (id, d) in docs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (d?.Root["grouping"] is not JsonObject g) continue; grouped++;
            have.TryGetValue(id, out var h); var hasQ = have.ContainsKey(id);
            if (!hasQ || !h.Done) { lines.Add(QLine.Add(id, "group", new JobDescriptor { Stage = "group", JobId = id, AddedAt = at }, at, null, null, "done", force: hasQ && !h.Done && false)); done++; }
            if ((!hasQ || h.Seq == null) && ActorsStage.SeqOf(g) is { } sq) { lines.Add(new QLine { T = "seq", S = id, Seq = sq, At = at }); seqs++; }
            if ((!hasQ || h.JoinKind == null) && GroupProposal.FromJson(g["proposal"] as JsonObject) is { } pr) { lines.Add(new QLine { T = "join", S = id, Join = pr.Kind == GroupProposal.JoinKind ? pr.JoinUpload : null, JoinKind = pr.Kind, At = at }); joins++; }
            if ((!hasQ || !h.Actors) && ActorsStage.AiFlag(d.Root)) { lines.Add(new QLine { T = "add", S = id, Stage = "actors", Job = new JobDescriptor { Stage = "actors", JobId = id, AddedAt = at }, At = at, Hold = "ai", Pri = 0 }); holds++; }
        }
        int appends = 0;
        if (!dry)
            foreach (var chunk in JobQueue.Chunks(lines, l => 200))
            {
                if (pacer != null) await pacer.WaitAsync(ct);
                // the append re-checks the state: a line whose subject already has what it carries is dropped (a racing import or node adds nothing twice)
                await q.AppendLinesAsync(st => chunk.Where(l => l.T switch { "add" when l.Stage == "group" => !(st.Entry(l.S, "group")?.State is "done" or "duplicate"), "add" => st.Entry(l.S, "actors") == null, "seq" => st.Get(l.S)?.Seq == null, "join" => st.Get(l.S) is not { JoinKind: not null }, _ => true }).ToList(), ct); appends++;
            }
        return new ImportReport(grouped, done, seqs, joins, holds, appends);
    }

    /// <summary>queue split [--dry-run] [--out-local DIR | --from-local DIR] [--work-max N]: --out-local writes the plan (three snapshots) to DIR for inspection and changes nothing in the store; --from-local applies that folder to the store. Without either, plans in memory (--dry-run) or plans and applies in one go.</summary>
    static async Task<int> SplitAsync(Args a, IStore store, Paths paths, bool dry, CancellationToken ct)
    {
        int workMax = a.Get("work-max") is { } wm ? int.Parse(wm) : QueueSplit.DefaultWorkMax; long ops0 = StoreMeter.Current.Total;
        string? outDir = a.Get("out-local"), fromDir = a.Get("from-local");
        if (fromDir != null)
        {
            var r = await QueueSplit.ApplyAsync(store, paths, fromDir, () => DateTimeOffset.UtcNow, ct);
            Console.WriteLine(r.Message + $"; store ops {StoreMeter.Current.Total - ops0}"); return r.Written > 0 || r.AlreadySplit ? 0 : 1;
        }
        if (await store.HeadAsync(paths.Moved, ct) != null) { Console.WriteLine("already split: index/jobs/moved.json exists, nothing to do"); return 0; }
        await JobQueue.RequireSnapshotAsync(store, paths, ct); var q = new JobQueue(store, paths, "cli"); await q.LoadAsync(ct);
        string dir = outDir ?? Path.Combine(Path.GetTempPath(), "indexnode-split-" + Guid.NewGuid().ToString("N")[..8]);
        var rep = await QueueSplit.WriteLocalAsync(q, paths, dir, dry, workMax, ct);
        Console.WriteLine(rep.Text(dry) + (dry ? "" : $"\nplan written to {dir}"));
        if (dry || outDir != null) return 0;
        var ap = await QueueSplit.ApplyAsync(store, paths, dir, () => DateTimeOffset.UtcNow, ct);
        Console.WriteLine(ap.Message + $"; store ops {StoreMeter.Current.Total - ops0}"); return ap.Written > 0 ? 0 : 1;
    }

    public static async Task<int> RunAsync(Args a, IStore? remote = null)
    {
        var (_, store, paths, _) = CentersCommands.OpenStore(a, remote);
        bool dry = a.Has("dry-run"); using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (a.Positional.FirstOrDefault() == "split") return await SplitAsync(a, store, paths, dry, cts.Token);
        if (a.Positional.FirstOrDefault() != "import-done" || a.Positional.Skip(1).FirstOrDefault() != "group") throw new ArgumentException("queue import-done group [--dry-run]");
        await JobQueue.RequireSnapshotAsync(store, paths, cts.Token); var q = new JobQueue(store, paths, "cli"); await q.LoadAsync(cts.Token); long ops0 = StoreMeter.Current.Total;
        var r = await ImportDoneGroupAsync(q, store, paths, dry, cts.Token, new OpPacer(() => StoreMeter.Current.Total, () => StoreMeter.Current.Limits.MaxPerMin));
        Console.WriteLine($"{(dry ? "dry run, nothing written: " : "")}{r.Grouped} uploads with a grouping record; {r.GroupDone} group done {(dry ? "would be " : "")}added, {r.Seqs} seq, {r.Joins} join, {r.Holds} AI holds on actors; {r.Appends} append(s); store ops {StoreMeter.Current.Total - ops0}");
        return 0;
    }
}
