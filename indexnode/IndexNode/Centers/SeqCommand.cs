namespace IndexNode;

/// <summary>0.13.10 bootstrap: `indexnode seq assign [--dry-run]` gives every existing drama without a sequence number one, ordered by its first pre-check (video.json precheck.at), else its centers step, else its last queue activity, then id; numbers continue after the highest.
/// `indexnode groups enqueue [--dry-run]` adds the group step for every drama whose centers are done and that has no group record yet (create-only: an existing group entry is left alone).</summary>
public static class SeqCommand
{
    /// <summary>
    /// 0.13.12: the dramas that are STARTED: a frames file, or process steps done / done, or claimed now, or video.json precheck.at. Everything else with a process entry (downloaded, pending) is unstarted and carries no number.
    /// </summary>
    public static async Task<HashSet<string>> StartedAsync(JobQueue q, IStore store, Paths paths, CancellationToken ct, HashSet<string>? frames = null)
    {
        frames ??= await GroupSequencing.ListFramesAsync(store, paths, ct);
        var started = new HashSet<string>(frames, StringComparer.Ordinal);
        var rest = new List<string>();
        foreach (var s in q.Read(st => st.Subjects.Values.ToList()))
        {
            if (started.Contains(s.Id)) continue;
            if (GroupSequencing.StartedInQueue(s)) started.Add(s.Id); else if (s.Entries.ContainsKey("process")) rest.Add(s.Id);
        }
        // the rest: precheck.at in video.json
        var view1 = await VideoAggregate.OpenViewAsync(store, paths, ct);
        await Parallel.ForEachAsync(rest, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (id, c) =>
        {
            string? at = null; try { at = (string?)(await VideoJson.ReadViaAsync(view1, store, paths, id, c))?.Root["precheck"]?["at"]; } catch (Exception e) when (e is not OperationCanceledException) { }
            if (at != null) lock (started) started.Add(id);
        });
        return started;
    }

    public sealed record BackfillReport(int Grouped, int Missing, int Read, int NoRecord, int Added);

    /// <summary>
    /// 0.13.14: `groups backfill-joins [--dry-run]`: every drama whose group step is done but whose queue subject has no join record (grouped before 0.13.14): its video.json is read ONCE and a "join" line (join, jk) is appended, in batches.
    /// Idempotent: a drama that has a record is skipped, and the append re-checks the state, so a second run (or a racing node) adds nothing. A drama with no grouping record yet is left alone.
    /// </summary>
    public static async Task<BackfillReport> BackfillJoinsAsync(JobQueue q, IStore store, Paths paths, bool dry, CancellationToken ct)
    {
        await q.PollAsync(ct);
        var all = q.Read(st => st.Subjects.Values.Where(s => s.Entries.TryGetValue("group", out var g) && g.State == "done").Select(s => (s.Id, Has: s.JoinKind != null)).ToList());
        var missing = all.Where(x => !x.Has).Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, (string? Join, string Kind)>(StringComparer.Ordinal); int reads = 0;
        var view2 = await VideoAggregate.OpenViewAsync(store, paths, ct);
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (id, c) =>
        {
            Interlocked.Increment(ref reads);
            try
            {
                var g = (await VideoJson.ReadViaAsync(view2, store, paths, id, c))?.Root["grouping"] as System.Text.Json.Nodes.JsonObject; var pr = GroupProposal.FromJson(g?["proposal"] as System.Text.Json.Nodes.JsonObject);
                if (g != null && pr != null) found[id] = (pr.Kind == GroupProposal.JoinKind ? pr.JoinUpload : null, pr.Kind);
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        });
        int added = 0;
        if (!dry)
        {
            var at = q.NowUtc();
            foreach (var chunk in JobQueue.Chunks(found.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(), id => id.Length * 2 + 120))
            {
                var ids = chunk;
                await q.AppendLinesAsync(st => { var l = ids.Where(id => st.Get(id) is { JoinKind: null }).Select(id => new QLine { T = "join", S = id, Join = found[id].Join, JoinKind = found[id].Kind, At = at }).ToList(); Interlocked.Add(ref added, l.Count); return l; }, ct);
            }
        }
        return new BackfillReport(all.Count, missing.Count, reads, missing.Count - found.Count, dry ? found.Count : added);
    }

    public sealed record RerunReport(int Joins, int Added, int AlreadyPending);

    /// <summary>
    /// 0.13.13: `groups rerun --kind join`: re-adds (force) the group entry of every drama whose grouping record is a join. A rerun keeps its sequence number and rewrites its record and retained file. Idempotent (a drama whose entry is already pending is left alone) and it never cascades:
    /// later members compare against earlier members' FULL frames files, which a rerun does not touch.
    /// </summary>
    public static async Task<RerunReport> RerunGroupsAsync(JobQueue q, IStore store, Paths paths, string kind, bool dry, CancellationToken ct)
    {
        if (kind != GroupProposal.JoinKind) throw new ArgumentException("groups rerun --kind join [--dry-run] (only join is supported)");
        await q.PollAsync(ct);
        var cand = q.Read(st => st.Subjects.Values.Where(s => s.Seq != null && s.Entries.TryGetValue("group", out var g) && g.State == "done").Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());
        var joins = new List<string>();
        var view3 = await VideoAggregate.OpenViewAsync(store, paths, ct);
        await Parallel.ForEachAsync(cand, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (id, c) =>
        {
            try { var g = (await VideoJson.ReadViaAsync(view3, store, paths, id, c))?.Root["grouping"] as System.Text.Json.Nodes.JsonObject; if (GroupProposal.FromJson(g?["proposal"] as System.Text.Json.Nodes.JsonObject)?.Kind == GroupProposal.JoinKind) lock (joins) joins.Add(id); }
            catch (Exception e) when (e is not OperationCanceledException) { }
        });
        joins.Sort(StringComparer.Ordinal);
        var pending = q.Read(st => joins.Count(id => st.Entry(id, "group") is { State: "pending" }));
        int added = 0;
        if (!dry)
        {
            var at = q.NowUtc();
            foreach (var chunk in JobQueue.Chunks(joins, id => id.Length + 200))
            {
                var ids = chunk;
                // the entry goes back to pending (a force add); one already pending or claimed is left alone, so a second run adds nothing
                await q.AppendLinesAsync(st => { var l = ids.Where(id => st.Entry(id, "group") is { State: "done" }).Select(id => QLine.Add(id, "group", GroupSequencing.NewJob(id, 0), at, force: true)).ToList(); Interlocked.Add(ref added, l.Count); return l; }, ct);
            }
        }
        return new RerunReport(joins.Count, dry ? joins.Count - pending : added, pending);
    }

    public sealed record ClearReport(
int Cleared, int Started, List<string> First);

    /// <summary>0.13.12: one clear line (force, no number) per numbered drama that is not started, batched. Allowed after group steps have finished: the order of the rest does not change (gaps are fine), and a cleared drama had no frames, so no finished group record changes.</summary>
    public static async Task<ClearReport> ClearUnstartedAsync(JobQueue q, IStore store, Paths paths, bool dry, CancellationToken ct)
    {
        await q.PollAsync(ct);
        var started = await StartedAsync(q, store, paths, ct);
        var clear = q.Read(st => st.Subjects.Values.Where(s => s.Seq != null && !started.Contains(s.Id)).OrderBy(s => s.Seq).Select(s => s.Id).ToList());
        if (!dry && clear.Count > 0)
        {
            var at = q.NowUtc();
            foreach (var chunk in JobQueue.Chunks(clear, id => id.Length + 60)) { var ids = chunk; await q.AppendLinesAsync(st => ids.Where(id => st.Get(id) is { Seq: not null }).Select(id => new QLine { T = "seq", S = id, Force = true, At = at, By = "cli" }).ToList(), ct); }
        }
        return new ClearReport(clear.Count, started.Count, clear.Take(5).ToList());
    }

    /// <summary>
    /// 0.13.43, once, at the cutover and with no node running: the dramas that carry a number from the old rule (given when their pre-check started) but have not been grouped and are still on their way
    /// (their process entry is not finished, or their group entry is not done). Their numbers are cleared, so each gets its number when its group step starts and then sees every drama grouped before it.
    /// A drama whose group step is done keeps its number, and so does one from before numbering that has no entry in flight.
    /// </summary>
    public static bool NumberedUngrouped(QSubject s) =>
        s.Seq != null && s.Entries.GetValueOrDefault(GroupSequencing.Stage) is not { State: "done" or "duplicate" } &&
        (s.Entries.TryGetValue("process", out var p) && !p.Final || s.Entries.TryGetValue(GroupSequencing.Stage, out var g) && !g.Final);

    public static async Task<ClearReport> ClearUngroupedAsync(JobQueue q, bool dry, CancellationToken ct)
    {
        await q.PollAsync(ct);
        var (clear, keep) = q.Read(st => (st.Subjects.Values.Where(NumberedUngrouped).OrderBy(s => s.Seq).Select(s => s.Id).ToList(), st.Subjects.Values.Count(s => s.Seq != null && !NumberedUngrouped(s))));
        if (!dry && clear.Count > 0)
        {
            var at = q.NowUtc();
            foreach (var chunk in JobQueue.Chunks(clear, id => id.Length + 60)) { var ids = chunk; await q.AppendLinesAsync(st => ids.Where(id => st.Get(id) is { } x && NumberedUngrouped(x)).Select(id => new QLine { T = "seq", S = id, Force = true, At = at, By = "cli" }).ToList(), ct); }
        }
        return new ClearReport(clear.Count, keep, clear.Take(5).ToList());
    }

    public sealed record Report(
int Dramas, int Assigned, int Already, long First, long Last, int PreLog = 0, int Renumbered = 0);

    /// <summary>
    /// 0.13.11: EVERY drama with a frames file (or a row in the head index) gets a number, not only those with a process entry in the queue log (most dramas were processed before the log existed, migration 2026-11-20 18:12Z).
    /// Order: the dramas with NO queue line first, in the head index's row order (deterministic, no blob times); then the log-ordered ones by their first pre-check (video.json precheck.at), else their centers step, else their last queue activity, then id.
    /// <paramref name="renumber"/>: renumber everyone by that rule (numbers restart at 1); refused when any group step has finished (the group records were made against the old numbers).
    /// </summary>
    public static async Task<Report> AssignAsync(JobQueue q, IStore store, Paths paths, bool dry, CancellationToken ct, bool renumber = false)
    {
        await q.PollAsync(ct);
        if (renumber)
        {
            var done = q.Read(st => st.Subjects.Values.Where(s => s.Entries.TryGetValue("group", out var g) && g.State == "done").Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());
            if (done.Count > 0) throw new InvalidOperationException($"seq assign --renumber refused: {done.Count} group step(s) have finished (e.g. {done[0]}); their grouping records depend on the current numbers");
        }
        // the dramas: every frames file and every row of the head index, plus every subject of the log with a process entry
        var started = await StartedAsync(q, store, paths, ct);
        var inLog = q.Read(st => st.Subjects.Values.Where(s => s.Entries.ContainsKey("process") && s.Entries["process"].State != "duplicate" && started.Contains(s.Id)).ToDictionary(s => s.Id, s => (s.CentersAt, s.LastAt, s.Seq, HasLine: true), StringComparer.Ordinal));
        var head = await SearchData.LoadAsync(store, paths, null, ct);
        var headRows = head?.Uploads.Select(u => u.Id).ToList() ?? [];
        var frames = new HashSet<string>(StringComparer.Ordinal); var fp = paths.Prefix + "search/frames/";
        foreach (var b in await store.ListAsync(fp, false, false, ct)) { if (b.Name.EndsWith(".bin", StringComparison.Ordinal)) { var id = b.Name[fp.Length..^4]; if (id.Length > 0 && !id.Contains('/')) frames.Add(id); } }
        var known = q.Read(st => st.Subjects.Values.Where(s => s.Entries.Count > 0 || s.Seq != null).Select(s => s.Id).ToHashSet(StringComparer.Ordinal));
        var all = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id) { if (seen.Add(id)) all.Add(id); }
        // dramas with no log line: head index row order, then (deterministic) the rest of the frames files by id
        var noLine = headRows.Where(id => !inLog.ContainsKey(id) && started.Contains(id)).ToList(); noLine.AddRange(frames.Where(id => !inLog.ContainsKey(id) && !headRows.Contains(id) && started.Contains(id)).OrderBy(x => x, StringComparer.Ordinal));
        foreach (var id in noLine) Add(id);
        var keyed = new List<(string Key, string Id)>();
        var view4 = await VideoAggregate.OpenViewAsync(store, paths, ct);
        await Parallel.ForEachAsync(inLog, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (t, c) =>
        {
            string? at = null;
            try { at = (string?)(await VideoJson.ReadViaAsync(view4, store, paths, t.Key, c))?.Root["precheck"]?["at"]; } catch (Exception e) when (e is not OperationCanceledException) { }
            lock (keyed) keyed.Add((at ?? t.Value.CentersAt ?? t.Value.LastAt ?? "\uffff", t.Key));
        });
        foreach (var k in keyed.OrderBy(k => k.Key, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal)) Add(k.Id);
        if (renumber) foreach (var id in q.Read(st => st.Subjects.Values.Where(x => x.Seq != null && started.Contains(x.Id)).OrderBy(x => x.Seq).Select(x => x.Id).ToList())) Add(id);   // a started numbered subject outside the set is renumbered too: no two share a number
        var cur = q.Read(st => all.ToDictionary(id => id, id => st.Get(id)?.Seq, StringComparer.Ordinal));
        var order = renumber ? all : all.Where(id => cur[id] == null).ToList();
        int already = renumber ? 0 : all.Count - order.Count; int renumbered = renumber ? all.Count(id => cur[id] != null) : 0;
        long first = renumber ? 1 : q.Read(st => st.NextSeq());
        if (!dry && order.Count > 0)
        {
            var at = q.NowUtc();
            if (renumber)
            {
                // explicit numbers from 1 in the rule's order; a seq line overrides in the log (renumber = true on the line)
                long n0 = 1; var lines = q.Read(st => st.Subjects.Values.Where(x => x.Seq != null && !started.Contains(x.Id)).Select(x => new QLine { T = "seq", S = x.Id, Force = true, At = at, By = "cli" }).ToList());   // unstarted dramas carry no number: cleared first, so none collides with the new ones
                lines.AddRange(order.Select(id => new QLine { T = "seq", S = id, Seq = n0++, At = at, By = "cli", Force = true }));
                foreach (var chunk in JobQueue.Chunks(lines, l => l.ToJson().Length)) { var ch = chunk; await q.AppendLinesAsync(ch, ct); }
            }
            else foreach (var chunk in JobQueue.Chunks(order, id => id.Length + 60))
            {
                var ids = chunk;
                // numbers are decided inside the conditional append: a node that assigned one meanwhile is respected (first writer wins)
                await q.AppendLinesAsync(st => { var n = st.NextSeq(); return ids.Where(id => st.Get(id) is not { Seq: not null }).Select(id => new QLine { T = "seq", S = id, Seq = n++, At = at, By = "cli" }).ToList(); }, ct);
            }
        }
        return new Report(all.Count, order.Count, already, first, first + order.Count - 1, noLine.Count, renumbered);
    }


    public sealed record EnqueueReport(int Candidates, int Added, int NoSeq, int Existing);

    /// <summary>0.13.11: the group step for every NUMBERED drama that has a frames file and no group entry (create-only), in sequence order; dramas processed before the queue log have no process entry, only the frames file.</summary>
    public static async Task<EnqueueReport> EnqueueGroupsAsync(JobQueue q, IStore store, Paths paths, bool dry, CancellationToken ct)
    {
        await q.PollAsync(ct);
        var now = q.NowUtc(); var lines = new List<QLine>(); int noSeq = 0, existing = 0, cand = 0;
        var frames = new HashSet<string>(StringComparer.Ordinal); var fp = paths.Prefix + "search/frames/";
        foreach (var bl in await store.ListAsync(fp, false, false, ct)) if (bl.Name.EndsWith(".bin", StringComparison.Ordinal)) { var id = bl.Name[fp.Length..^4]; if (id.Length > 0 && !id.Contains('/')) frames.Add(id); }
        foreach (var s in q.Read(st => st.Subjects.Values.Where(x => frames.Contains(x.Id) || x.Entries.TryGetValue("process", out var p) && p.State == "done" || x.CentersAt != null).OrderBy(x => x.Seq ?? long.MaxValue).ThenBy(x => x.Id, StringComparer.Ordinal).ToList()))
        {
            cand++;
            if (s.Entries.ContainsKey("group")) { existing++; continue; }
            if (s.Seq == null) { noSeq++; continue; }
            lines.Add(QLine.Add(s.Id, "group", GroupSequencing.NewJob(s.Id, 0), now));
        }
        // a frames file with no subject in the queue at all (never numbered): counted, not added
        noSeq += frames.Count(id => q.Read(st => st.Get(id) == null));
        if (!dry) foreach (var chunk in JobQueue.Chunks(lines, l => l.ToJson().Length)) { var c = chunk; await q.AppendLinesAsync(c, ct); }
        return new EnqueueReport(cand, lines.Count, noSeq, existing);
    }


    public static async Task<int> RunAsync(Args a, IStore? remote = null)
    {
        var (_, store, paths, _) = CentersCommands.OpenStore(a, remote);
        bool dry = a.Has("dry-run"); using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        await JobQueue.RequireSnapshotAsync(store, paths, cts.Token);
        var q = new JobQueue(store, paths, "cli"); await q.LoadAsync(cts.Token);
        if (a.Positional.FirstOrDefault() == "assign")
        {
            var r = await AssignAsync(q, store, paths, dry, cts.Token, a.Has("renumber"));
            Console.WriteLine($"seq assign{(a.Has("renumber") ? " --renumber" : "")}{(dry ? " (dry run)" : "")}: {r.Dramas} dramas ({r.PreLog} with no queue line, numbered first in the head index's order), {r.Already} already numbered, {r.Assigned} {(dry ? "would be" : "")} numbered {(r.Assigned > 0 ? $"{r.First}..{r.Last}" : "")}");
            return 0;
        }
        if (a.Positional.FirstOrDefault() == "clear-unstarted")
        {
            var c = await ClearUnstartedAsync(q, store, paths, dry, cts.Token);
            Console.WriteLine($"seq clear-unstarted{(dry ? " (dry run)" : "")}: {c.Cleared} unstarted dramas {(dry ? "would lose" : "lost")} their number ({c.Started} started dramas keep theirs){(c.First.Count > 0 ? "; first: " + string.Join(", ", c.First) : "")}");
            return 0;
        }
        if (a.Positional.FirstOrDefault() == "clear-ungrouped")
        {
            var c = await ClearUngroupedAsync(q, dry, cts.Token);
            Console.WriteLine($"seq clear-ungrouped{(dry ? " (dry run)" : "")}: {c.Cleared} dramas numbered under the old rule and not yet grouped {(dry ? "would lose" : "lost")} their number ({c.Started} numbered dramas keep theirs){(c.First.Count > 0 ? "; first: " + string.Join(", ", c.First) : "")}");
            return 0;
        }
        throw new ArgumentException("seq assign [--dry-run] [--renumber] | seq clear-unstarted [--dry-run] | seq clear-ungrouped [--dry-run]");
    }
}
