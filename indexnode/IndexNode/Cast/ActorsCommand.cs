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

public static class ActorsCommandCast
{
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
    const string Use = "actors build --ids <file> --out-local <dir> [...]  |  actors add <id>... [--dry-run]  |  actors run <id>... [--dry-run] [--out-local <dir>] [--prefix <p>] [--manual] [--preds all|listed]  |  actors index [--dry-run] [--out-local <dir>] [--prefix <p>]  |  actors rows [--dry-run]   (0.13.38: the actor count of the published index, and one row per actor into the media table)   (shared: [--child-probe <file>] [--tiebreak-cut 0.64] [--threads N])";

    /// <summary>0.13.32: the cast step's repair mode for the claimed upload: the repaired cast state under index/actors/; false when its grouping record is not there yet (the step then records v1).</summary>
    public static async Task<bool> CastRepairAsync(ActorsStage stage, JobContext ctx, CancellationToken ct)
    {
        var vj = await VideoJson.ReadAsync(ctx.Store, ctx.Paths, ctx.BlobName, ct); if (vj?.Root["grouping"] is not System.Text.Json.Nodes.JsonObject) return false;
        var q = ctx.Queue; var io = new ActorsIO(ctx.Store, ctx.Paths);
        var r = await stage.RepairAsync(io, ctx.BlobName, false, Math.Max(1, ctx.ThreadShare()), ct, q == null ? null : up => q.Read(st => st.Get(up) is { } sub && sub.Entries.Values.Any(e => e.Hold == "ai")), q?.Read(ActorsStage.GroupOfQueue));
        Log.Info($"cast repair {r.Id}: " + (r.Skipped ?? (r.Reused ? $"already repaired ({r.Units} units)" : $"{r.Units} units ({r.FacesUsed} faces, {r.ChildUnits} child), {r.R50Units} r50 vectors")));
        return r.Skipped == null;
    }

    public static ActorsStage NewStage(IStore store, HttpClient http, Args a, bool dry, string? outLocal, ActorsBuild.Probe? probe)
    {
        double cut = a.Get("tiebreak-cut") is { } tc ? double.Parse(tc, CultureInfo.InvariantCulture) : 0.64; string? path = null; var gate = new object();
        string Path_() { lock (gate) return path ??= CastReembed.ModelPathAsync("w600k_r50", http, default).GetAwaiter().GetResult(); }
        return new ActorsStage(store, probe, cut, dry, outLocal) { MakeR50 = () => new ArcFace(Path_(), 1) };
    }

    public static async Task<int> RunAsync(Args a)
    {
        var sub = a.Positional.FirstOrDefault(); if (sub == "build") return await ActorsBuild.Main2(a);
        if (sub is not ("add" or "run" or "index" or "rows")) throw new ArgumentException(Use);
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
        var io = new ActorsIO(store, paths, dry, outLocal);
        if (sub == "rows")
        {   // 0.13.38: every actor of the published index gets (or refreshes) its row; with --dry-run or no table only the counts are printed
            var (ix, _) = await ActorsIndex.LoadAsync(new ActorsIO(store, paths), ct, withVec: false);
            if (ix == null) { Console.WriteLine("no actor index yet"); return 1; }
            var stats = ActorRows.Stats(ix.Uploads); var rows = ActorRows.Rows(null, stats, ix.Version);
            Console.WriteLine($"actor index v{ix.Version}: {ix.Uploads.Count} uploads, {ix.Uploads.Sum(u => u.Units.Count)} units, {stats.Count} actors, {ActorRows.Multi(stats)} in 2+ productions (most productions for one actor: {stats.Values.Select(s => s.Prods).DefaultIfEmpty(0).Max()})");
            foreach (var g in stats.Values.GroupBy(s => Math.Min(s.Prods, 6)).OrderBy(g => g.Key)) Console.WriteLine($"  in {g.Key}{(g.Key == 6 ? "+" : "")} production(s): {g.Count()} actors");
            if (dry || MediaTables.For(store) is not { } table) { Console.WriteLine($"{(dry ? "dry run" : "no media table configured")}: {rows.Count} rows not written; store ops {StoreMeter.Current.Total - ops0}"); return 0; }
            int reqs = await ActorRows.WriteAsync(table, rows, ct);
            Console.WriteLine($"{rows.Count} actor rows written to partition '{ActorRows.Partition}' ({reqs} table requests); store ops {StoreMeter.Current.Total - ops0}"); return 0;
        }
        if (sub == "index")
        {
            Dictionary<string, VideoJson.Doc?>? aiDocs = null;   // the whole aggregate, read once on the first check (no per-upload reads for the uploads it holds)
            var rep = await ActorsIndex.BuildAsync(io, ct, async id => { aiDocs ??= await VideoJson.ReadManyAsync(store, paths, new List<string>(), ct, all: true); return ActorsStage.AiFlag((aiDocs.TryGetValue(id, out var d) ? d : await VideoJson.ReadAsync(store, paths, id, ct))?.Root); }, 2);
            Console.WriteLine(rep.Note); foreach (var l in io.Log) Console.WriteLine("  " + l); Console.WriteLine($"store ops {StoreMeter.Current.Total - ops0}"); return 0;
        }
        if (ids.Count == 0) throw new ArgumentException(Use);
        var probe = await ActorsBuild.ResolveProbeAsync(store, paths, a.Get("child-probe"), ct) ?? throw new InvalidOperationException($"no child probe: give --child-probe <file> or upload it to {ActorsBuild.StoreProbe(paths)} (without it no child unit is left out)");
        var stage = NewStage(store, http, a, dry, outLocal, probe); int rc = 0; Func<string, IReadOnlyList<string>?> predsOf = _ => null;   // null: the stage reads every upload with a lower grouping.seq from the aggregate
        var predsMode = a.Get("preds"); if (predsMode is not (null or "all" or "listed")) throw new ArgumentException("--preds is all (default) or listed");
        // test option "listed": only the ids on the command line that have a lower seq, in seq order (AI predecessors are still skipped by the stage)
        // 0.13.28: the CLI runs the node's code path: the processing queue (snapshot + tail) is loaded once, read-only, and feeds predecessors, AI flags and groups; --preds listed keeps its test behaviour
        var pq = await JobQueue.OpenAsync(store, paths, "cli", false, ct);
         int threads = a.Get("threads") is { } th ? int.Parse(th) : cfg.EffectiveThreads;
        foreach (var id in ids)
        {
            try
            {
                var preds = predsMode == "listed" ? await ActorsCommandSupport.ListedAsync(store, paths, id, ids, ct) : predsOf(id);
                var r = predsMode == "listed" ? await stage.RunAsync(io, id, a.Has("manual"), threads, ct, preds, null, up => pq.Read(st => st.Entry(up, "actors")?.Hold == "ai" || st.Entry(up, "process")?.Hold == "ai"), pq.Read(ActorsStage.GroupOfQueue), pq.Read(st => st.ActorsIndexVer)) : await stage.RunWithQueueAsync(io, pq, id, a.Has("manual"), threads, ct);
                Console.WriteLine(r.Skipped != null ? $"  {id}: skipped: {r.Skipped}" : $"  {id}: {r.Kind} (production {r.Prod}); {r.FacesUsed} faces, {r.Units} units ({r.ChildUnits} child units left out); {r.Links} links ({r.BandLinked}/{r.Band} band pairs linked, {r.R50Units} r50 units computed); {r.Predecessors} predecessor upload(s) read, {r.PredecessorsAi} AI-flagged ignored; actors {string.Join(",", r.Actors)}");
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException) { Console.Error.WriteLine($"  {id}: {e.Message}"); rc = 1; }
        }
        foreach (var l in io.Log) Console.WriteLine("  " + l);
        Console.WriteLine($"{(dry ? "dry run, nothing written to the store; " : outLocal != null ? $"written under {outLocal}, the store was only read; " : "")}store ops {StoreMeter.Current.Total - ops0} ({StoreMeter.Current.TotalFailed} failed)");
        return rc;
    }
}
