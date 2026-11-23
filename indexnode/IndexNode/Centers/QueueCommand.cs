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

    public sealed record ProcessImportReport(int Subjects, int NoProcess, int Imported, int Partial, int NotFull, int Claimed, int Appends);
    public static readonly string[] ProcessEvidence = ["cast", "centers", "frames", "sscd"];

    /// <summary>0.13.32 queue import-done process [--dry-run|--out-local FILE]: a processing subject whose download is done but that has NO process entry (processed before the queue tracked process) and whose stored outputs show it was processed (cast.zip, centers.zip, frames.zip, sscd.zip, from ONE listing of videos/, names only) gets
    /// "enter data=full" (when it has no data fact), "add process (state done)" and a "fixsteps" line with the steps whose output is present (cast stays v1: a done line without a version). Batched, paced; idempotent (the append drops what the queue has meanwhile).</summary>
    public static async Task<ProcessImportReport> ImportDoneProcessAsync(JobQueue q, IStore store, Paths paths, bool dry, CancellationToken ct, OpPacer? pacer = null, string? outLocal = null)
    {
        await q.PollAsync(ct);
        var zips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in await store.ListAsync(paths.Prefix + "videos/", false, false, ct)) if (b.Name.EndsWith(".zip", StringComparison.Ordinal)) zips.Add(b.Name);
        var at = q.NowUtc(); var lines = new List<QLine>(); int subjects = 0, noProc = 0, imported = 0, partial = 0, notFull = 0, claimed = 0;
        var items = q.Read(st => st.Subjects.Values.Where(s => s.Id != ActorsIndexJobs.Subject && !s.Id.StartsWith("ch-") && !s.Id.StartsWith("pl-")).OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => (s.Id, s.IsFull, s.Data, Has: s.Entries.ContainsKey("process"), s.ClaimBy, Pri: s.Entries.GetValueOrDefault("download")?.Priority ?? 0)).ToList());
        foreach (var (id, full, data, has, claimBy, pri) in items)
        {
            subjects++; if (has) continue; noProc++;
            if (!full) { notFull++; continue; }
            if (claimBy != null) { claimed++; continue; }
            var present = NodeEngine.StandardSteps.Where(s => s != "sync" && zips.Contains(paths.Video(id, s + ".zip"))).ToList();
            if (!ProcessEvidence.All(e => present.Contains(e))) { partial++; continue; }
            var done = NodeEngine.StandardSteps.Where(s => s == "sync" || present.Contains(s)).ToArray();
            if (data == null) lines.Add(new QLine { T = "enter", S = id, Data = "full", At = at });
            lines.Add(QLine.Add(id, "process", new JobDescriptor { Stage = "process", JobId = id, Priority = pri, AddedAt = at }, at, pri, null, "done"));
            lines.Add(new QLine { T = "fixsteps", S = id, Stage = "process", Done = done, At = at });
            imported++;
        }
        int appends = 0;
        if (outLocal != null) File.WriteAllLines(outLocal, lines.Select(l => l.ToJson()));
        else if (!dry)
            foreach (var chunk in JobQueue.Chunks(lines, l => l.ToJson().Length))
            {
                if (pacer != null) await pacer.WaitAsync(ct);
                await q.AppendLinesAsync(st => chunk.Where(l => l.T switch { "add" => st.Entry(l.S, "process") == null, "fixsteps" => st.Entry(l.S, "process") is { State: "done" } pe && pe.StepsDone == null, "enter" => st.Get(l.S)?.Data == null, _ => true }).ToList(), ct); appends++;
            }
        return new ProcessImportReport(subjects, noProc, imported, partial, notFull, claimed, appends);
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

    /// <summary>0.13.32 queue whatif --stage S [--set k=v]... [--top N] [--ids]: read-only readiness check with in-memory control overrides (see <see cref="WhatIf"/>).</summary>
    static async Task<int> WhatIfAsync(Args a, IStore store, Paths paths, CancellationToken ct)
    {
        var stage = a.Get("stage") ?? throw new ArgumentException("queue whatif --stage <stage or step> [--set versions.cast=2] [--set stage.actors=run] [--top N] [--ids]");
        int top = a.Get("top") is { } t ? int.Parse(t) : 20; long ops0 = StoreMeter.Current.Total;
        var q = await JobQueue.OpenAsync(store, paths, "whatif", a.Has("download"), ct);
        var ctl = WhatIf.Apply(ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data), a.Multi.GetValueOrDefault("set") ?? []);
        var liveLeases = new HashSet<string>(StringComparer.Ordinal);   // a claim counts only while its active file holds a live lease (or is brand new): a node that stopped leaves the file behind and the lease lapses
        foreach (var b in await store.ListAsync(paths.ActiveDir, false, false, ct)) if (b.LeaseState == "leased" || b.Created is { } cr && DateTimeOffset.UtcNow - cr < JobQueue.NewActiveGrace) liveLeases.Add(Path.GetFileNameWithoutExtension(b.Name));
        var rep = q.Read(st => WhatIf.Run(st, stage, ctl, Protocol.Utc(DateTimeOffset.UtcNow), new ReadyOptions(), top, liveLeases.Contains));
        Console.WriteLine($"whatif {rep.Stage} (read-only; overrides: {(a.Multi.GetValueOrDefault("set") is { Count: > 0 } sets ? string.Join(", ", sets) : "none")}): {rep.Ready} of {rep.Total} subject(s) would be READY now");
        foreach (var n in rep.Notes) Console.WriteLine("  " + n);
        if (rep.Ready > 0) Console.WriteLine($"  first {rep.First.Count}: " + (a.Has("ids") ? string.Join(", ", rep.First) : $"{rep.First.Count} shown (add --ids to list them)"));
        Console.WriteLine($"  not ready: {rep.Total - rep.Ready}");
        foreach (var (why, n) in rep.Blockers) Console.WriteLine($"    {n,7}  {why}");
        Console.WriteLine($"store ops {StoreMeter.Current.Total - ops0}");
        return 0;
    }

    public static async Task<int> RunAsync(Args a, IStore? remote = null)
    {
        var (_, store, paths, _) = CentersCommands.OpenStore(a, remote);
        bool dry = a.Has("dry-run"); using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (a.Positional.FirstOrDefault() == "split") return await SplitAsync(a, store, paths, dry, cts.Token);
        if (a.Positional.FirstOrDefault() == "whatif") return await WhatIfAsync(a, store, paths, cts.Token);
        if (a.Positional.FirstOrDefault() == "rebuild-snapshot")
        {   // 0.13.35: replay the sealed segments and put back the done entries the old compaction dropped (conditional on the snapshot's ETag)
            long rbOps = StoreMeter.Current.Total; var rb = await QueueRebuild.RunAsync(store, paths, !dry, cts.Token);
            Console.Write(rb.Text(dry)); Console.WriteLine($"store ops {StoreMeter.Current.Total - rbOps}"); return rb.SegmentsOk && (dry || rb.Written || rb.EntriesRestored == 0) ? 0 : 1;
        }
        if (a.Positional.FirstOrDefault() == "import-done" && a.Positional.Skip(1).FirstOrDefault() == "process")
        {
            await JobQueue.RequireSnapshotAsync(store, paths, cts.Token); var pq = new JobQueue(store, paths, "cli"); await pq.LoadAsync(cts.Token); long pops = StoreMeter.Current.Total;
            var pr = await ImportDoneProcessAsync(pq, store, paths, dry, cts.Token, new OpPacer(() => StoreMeter.Current.Total, () => StoreMeter.Current.Limits.MaxPerMin), a.Get("out-local"));
            Console.WriteLine($"{(dry || a.Get("out-local") != null ? "dry run, nothing written: " : "")}{pr.Subjects} subjects, {pr.NoProcess} without a process entry: {pr.Imported} {(dry ? "would be " : "")}imported as processed (done process, data full), {pr.Partial} lack one of cast/centers/frames/sscd.zip, {pr.NotFull} not a fully downloaded upload, {pr.Claimed} claimed; {pr.Appends} append(s); store ops {StoreMeter.Current.Total - pops}");
            return 0;
        }
        if (a.Positional.FirstOrDefault() != "import-done" || a.Positional.Skip(1).FirstOrDefault() != "group") throw new ArgumentException("queue import-done group [--dry-run]");
        await JobQueue.RequireSnapshotAsync(store, paths, cts.Token); var q = new JobQueue(store, paths, "cli"); await q.LoadAsync(cts.Token); long ops0 = StoreMeter.Current.Total;
        var r = await ImportDoneGroupAsync(q, store, paths, dry, cts.Token, new OpPacer(() => StoreMeter.Current.Total, () => StoreMeter.Current.Limits.MaxPerMin));
        Console.WriteLine($"{(dry ? "dry run, nothing written: " : "")}{r.Grouped} uploads with a grouping record; {r.GroupDone} group done {(dry ? "would be " : "")}added, {r.Seqs} seq, {r.Joins} join, {r.Holds} AI holds on actors; {r.Appends} append(s); store ops {StoreMeter.Current.Total - ops0}");
        return 0;
    }
}
