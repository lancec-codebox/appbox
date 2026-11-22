using System.Globalization;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>Where the queue meets the actors stage: the manual add line, `actors add`, `actors run`, `actors index` and the options of the node's stage.</summary>
public static class ActorsAdd
{
    public static bool AiFlagged(JsonObject? root) => AiHold.Flagged(root);

    /// <summary>The add line of a manual add: the job's reason marks it as added by hand (the stage keeps such an upload in even when it is flagged); a finished or failed entry is queued again (force).</summary>
    public static QLine ManualLine(string id, QEntry? existing, int priority) => QLine.Add(id, "actors", new JobDescriptor { Stage = "actors", JobId = id, Priority = priority, AddedAt = Protocol.NowUtc(), Reason = ActorsStage.ManualReason }, Protocol.NowUtc(), null, null, null, force: existing != null);
}

public static class ActorsBackfill
{
    public sealed record Report(List<QLine> Lines, int Queued, int AiFlagged, int HasEntry, int GroupNotDone, int Unreadable);

    /// <summary>The uploads whose group step is done, that are not AI-flagged and have no actors entry yet, in seq order (an upload with no seq last, by id): ordinary add lines (reason NOT "manual"). At most <paramref name="limit"/> (0 = all).</summary>
    public static async Task<Report> PlanAsync(IStore store, Paths paths, JobQueue q, int limit, CancellationToken ct)
    {
        // every upload that has a grouping record (the aggregate, then the per-upload files for the queue's uploads it lacks): the queue's group entry is not the source (92k subjects were grouped before the queue tracked it)
        var docs = await VideoJson.ReadManyAsync(store, paths, new List<string>(), ct, all: true);
        var queued = q.Read(st => st.Subjects.Values.Select(s => (s.Id, s.Seq, Has: s.Entries.ContainsKey("actors"), Pri: s.Entries.GetValueOrDefault("group")?.Priority ?? 0)).ToList());
        var lacking = queued.Where(x => !docs.ContainsKey(x.Id)).Select(x => x.Id).ToList();
        if (lacking.Count > 0) foreach (var kv in await VideoJson.ReadManyAsync(store, paths, lacking, ct)) docs[kv.Key] = kv.Value;
        var has = queued.Where(x => x.Has).Select(x => x.Id).ToHashSet(StringComparer.Ordinal); var pri = queued.ToDictionary(x => x.Id, x => x.Pri, StringComparer.Ordinal);
        int hasN = 0, notDone = 0, ai = 0, bad = 0; var todo = new List<(string Id, long Seq)>();
        foreach (var (id, d) in docs)
        {
            if (d?.Root["grouping"] is not JsonObject g) { notDone++; continue; }
            if (has.Contains(id)) { hasN++; continue; }
            if (ActorsStage.AiFlag(d.Root)) { ai++; continue; }
            todo.Add((id, ActorsStage.SeqOf(g) ?? long.MaxValue));
        }
        var lines = new List<QLine>(); var now = Protocol.NowUtc();
        foreach (var (id, _) in todo.OrderBy(x => x.Seq).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            if (limit > 0 && lines.Count >= limit) break;
            lines.Add(QLine.Add(id, "actors", new JobDescriptor { Stage = "actors", JobId = id, Priority = pri.GetValueOrDefault(id), AddedAt = now }, now));
        }
        return new Report(lines, lines.Count, ai, hasN, notDone, bad);
    }
}

public static class ActorsBackfillRun
{
    public sealed record BatchReport(int Batches, int Queued, List<int> BatchSizes);

    /// <summary>Queues the backfill <paramref name="batch"/> uploads at a time: the next batch is released only after an actor-index build includes the previous one (every upload of it is in the index, or its actors entry ended without units: AI-flagged, failed), so an upload brute-forces at most about one batch of predecessors. <paramref name="wait"/> is called between polls (the CLI sleeps; a test runs the stage and the build there).</summary>
    public static async Task<BatchReport> RunAsync(IStore store, Paths paths, JobQueue q, int batch, int limit, Func<Task> wait, Action<string> say, CancellationToken ct, int maxPolls = 100000)
    {
        int queued = 0; var sizes = new List<int>(); var ioR = new ActorsIO(store, paths);
        while (limit <= 0 || queued < limit)
        {
            var take = limit > 0 ? Math.Min(batch, limit - queued) : batch;
            var rep = await ActorsBackfill.PlanAsync(store, paths, q, take, ct); if (rep.Lines.Count == 0) break;
            await q.AppendLinesAsync(rep.Lines, ct); queued += rep.Lines.Count; sizes.Add(rep.Lines.Count); say($"  batch {sizes.Count}: queued {rep.Lines.Count} ({queued} so far); waiting for an actor-index build that includes them");
            var ids = rep.Lines.Select(l => l.S!).ToList();
            for (int poll = 0; ; poll++)
            {
                if (poll > maxPolls) throw new TimeoutException("the actor index never included the batch");
                await q.LoadAsync(ct); var (idx, _) = await ActorsIndex.LoadAsync(ioR, ct); var inIdx = idx?.Ids ?? [];
                var open = ids.Where(id => !inIdx.Contains(id) && !q.Read(st => st.Entry(id, "actors") is { Final: true } && true)).ToList();
                var endedNoUnits = ids.Where(id => !inIdx.Contains(id) && q.Read(st => st.Entry(id, "actors") is { Final: true })).ToList();
                if (open.Count == 0 && endedNoUnits.Count == 0) break;
                // an ended entry that is not in the index yet: wait for a build unless it has no units file (skipped)
                var pending = open.Count; foreach (var id in endedNoUnits) if (await store.HeadAsync(ActorsData.UnitFile(paths, id), ct) != null) pending++;
                if (pending == 0) break;
                await wait();
            }
        }
        return new BatchReport(sizes.Count, queued, sizes);
    }
}

public static class ActorsCommandSupport
{
    /// <summary>--preds listed: the predecessors of <paramref name="id"/> (lower grouping.seq, seq order) restricted to the listed ids.</summary>
    public static async Task<IReadOnlyList<string>> ListedAsync(IStore store, Paths paths, string id, IReadOnlyList<string> listed, CancellationToken ct)
    {
        var vj = await VideoJson.ReadAsync(store, paths, id, ct); var all = await ActorsStage.PredecessorsAsync(store, paths, id, ActorsStage.SeqOf(vj?.Root["grouping"] as JsonObject), listed, ct);
        var set = listed.ToHashSet(StringComparer.Ordinal); return all.Where(set.Contains).ToList();
    }
}

public static class ActorsCommand
{
    const string Use = "actors build --ids <file> --out-local <dir> [...]  |  actors add <id>... [--dry-run]  |  actors run <id>... [--dry-run] [--out-local <dir>] [--prefix <p>] [--manual] [--preds all|listed]  |  actors backfill [--dry-run] [--limit N] [--batch N]  |  actors index [--dry-run] [--out-local <dir>] [--prefix <p>]   (shared: [--child-probe <file>] [--tiebreak-cut 0.64] [--threads N])";

    public static ActorsStage NewStage(IStore store, HttpClient http, Args a, bool dry, string? outLocal)
    {
        var probe = ActorsBuild.LoadProbe(a.Get("child-probe")); double cut = a.Get("tiebreak-cut") is { } tc ? double.Parse(tc, CultureInfo.InvariantCulture) : 0.64; string? path = null; var gate = new object();
        string Path_() { lock (gate) return path ??= CastReembed.ModelPathAsync("w600k_r50", http, default).GetAwaiter().GetResult(); }
        return new ActorsStage(store, probe, cut, dry, outLocal) { MakeR50 = () => new ArcFace(Path_(), 1) };
    }

    public static async Task<int> RunAsync(Args a)
    {
        var sub = a.Positional.FirstOrDefault(); if (sub == "build") return await ActorsBuild.Main2(a);
        if (sub is not ("add" or "run" or "index" or "backfill")) throw new ArgumentException(Use);
        var (cfg, store, paths, http) = CentersCommands.OpenStore(a); using var _h = http; bool dry = a.Has("dry-run"); var ct = CancellationToken.None; var outLocal = a.Get("out-local");
        if (outLocal != null) outLocal = Path.GetFullPath(outLocal);
        var ids = a.Positional.Skip(1).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList(); var ops0 = StoreMeter.Current.Total;
        if (sub == "add")
        {
            if (ids.Count == 0) throw new ArgumentException(Use);
            await JobQueue.RequireSnapshotAsync(store, paths, ct); var q = new JobQueue(store, paths, "cli"); await q.LoadAsync(ct); var lines = new List<QLine>();
            foreach (var id in ids)
            {
                var vj = await VideoJson.ReadAsync(store, paths, id, ct); var ex = q.Read(st => st.Entry(id, "actors")); var ai = ActorsAdd.AiFlagged(vj?.Root);
                if (vj == null) { Console.WriteLine($"  {id}: no video.json (skipped)"); continue; }
                if (!q.Read(st => st.Entry(id, "group")?.Final == true)) { Console.WriteLine($"  {id}: its group step is not done (skipped)"); continue; }
                lines.Add(ActorsAdd.ManualLine(id, ex, q.Read(st => st.Entry(id, "group")?.Priority ?? 0))); Console.WriteLine($"  {id}: {(dry ? "would queue" : "queued")} actors, added by hand{(ai ? " (AI-flagged: kept in)" : "")}{(ex != null ? $" (was {ex.State})" : "")}");
            }
            if (!dry && lines.Count > 0) await q.AppendLinesAsync(lines, ct);
            Console.WriteLine($"{(dry ? "dry run, nothing written: " : "")}{lines.Count} of {ids.Count} upload(s) {(dry ? "would be " : "")}queued; store ops {StoreMeter.Current.Total - ops0}");
            return lines.Count == ids.Count ? 0 : 1;
        }
        if (sub == "backfill")
        {
            await JobQueue.RequireSnapshotAsync(store, paths, ct); var bq = new JobQueue(store, paths, "cli"); await bq.LoadAsync(ct); int limit = a.Get("limit") is { } lm ? int.Parse(lm) : 0;
            if (!dry && a.Get("batch") is { } bs)
            {
                var br = await ActorsBackfillRun.RunAsync(store, paths, bq, int.Parse(bs) is > 0 and var bn ? bn : throw new ArgumentException("--batch must be 1 or more"), limit, () => Task.Delay(TimeSpan.FromSeconds(30)), Console.WriteLine, ct);
                Console.WriteLine($"{br.Queued} upload(s) queued in {br.Batches} batch(es) of up to {bs}, each released after an actor-index build included the one before; store ops {StoreMeter.Current.Total - ops0}"); return 0;
            }
            var rep = await ActorsBackfill.PlanAsync(store, paths, bq, limit, ct);
            if (!dry) for (int i = 0; i < rep.Lines.Count; i += 500) { await bq.AppendLinesAsync(rep.Lines.Skip(i).Take(500).ToList(), ct); Console.WriteLine($"  queued {Math.Min(i + 500, rep.Lines.Count)} of {rep.Lines.Count}"); }
            foreach (var l in rep.Lines.Take(dry ? 50 : 0)) Console.WriteLine("  would queue " + l.S);
            Console.WriteLine($"{(dry ? "dry run, nothing written: " : "")}{rep.Queued} upload(s) {(dry ? "would be " : "")}queued in seq order (normal adds, not manual); {rep.AiFlagged} AI-flagged left out, {rep.HasEntry} already have an actors entry, {rep.GroupNotDone} group step not done, {rep.Unreadable} without a readable video.json; store ops {StoreMeter.Current.Total - ops0}");
            return 0;
        }
        var io = new ActorsIO(store, paths, dry, outLocal);
        if (sub == "index")
        {
            Dictionary<string, VideoJson.Doc?>? aiDocs = null;   // the whole aggregate, read once on the first check (no per-upload reads for the uploads it holds)
            var rep = await ActorsIndex.BuildAsync(io, ct, async id => { aiDocs ??= await VideoJson.ReadManyAsync(store, paths, new List<string>(), ct, all: true); return ActorsStage.AiFlag((aiDocs.TryGetValue(id, out var d) ? d : await VideoJson.ReadAsync(store, paths, id, ct))?.Root); }, 2);
            Console.WriteLine(rep.Note); foreach (var l in io.Log) Console.WriteLine("  " + l); Console.WriteLine($"store ops {StoreMeter.Current.Total - ops0}"); return 0;
        }
        if (ids.Count == 0) throw new ArgumentException(Use);
        var stage = NewStage(store, http, a, dry, outLocal); int rc = 0; Func<string, IReadOnlyList<string>?> predsOf = _ => null;   // null: the stage reads every upload with a lower grouping.seq from the aggregate
        var predsMode = a.Get("preds"); if (predsMode is not (null or "all" or "listed")) throw new ArgumentException("--preds is all (default) or listed");
        // test option "listed": only the ids on the command line that have a lower seq, in seq order (AI predecessors are still skipped by the stage)
         int threads = a.Get("threads") is { } th ? int.Parse(th) : cfg.EffectiveThreads;
        foreach (var id in ids)
        {
            try
            {
                var preds = predsMode == "listed" ? await ActorsCommandSupport.ListedAsync(store, paths, id, ids, ct) : predsOf(id);
                var r = await stage.RunAsync(io, id, a.Has("manual"), threads, ct, preds);
                Console.WriteLine(r.Skipped != null ? $"  {id}: skipped: {r.Skipped}" : $"  {id}: {r.Kind} (production {r.Prod}); {r.FacesUsed} faces, {r.Units} units ({r.ChildUnits} child units left out); {r.Links} links ({r.BandLinked}/{r.Band} band pairs linked, {r.R50Units} r50 units computed); {r.Predecessors} predecessor upload(s) read, {r.PredecessorsAi} AI-flagged ignored; actors {string.Join(",", r.Actors)}");
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException) { Console.Error.WriteLine($"  {id}: {e.Message}"); rc = 1; }
        }
        foreach (var l in io.Log) Console.WriteLine("  " + l);
        Console.WriteLine($"{(dry ? "dry run, nothing written to the store; " : outLocal != null ? $"written under {outLocal}, the store was only read; " : "")}store ops {StoreMeter.Current.Total - ops0} ({StoreMeter.Current.TotalFailed} failed)");
        return rc;
    }
}
