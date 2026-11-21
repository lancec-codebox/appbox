namespace IndexNode;

/// <summary>0.13.10 bootstrap: `indexnode seq assign [--dry-run]` gives every existing drama without a sequence number one, ordered by its first pre-check (video.json precheck.at), else its centers step, else its last queue activity, then id; numbers continue after the highest.
/// `indexnode groups enqueue [--dry-run]` adds the group step for every drama whose centers are done and that has no group record yet (create-only: an existing group entry is left alone).</summary>
public static class SeqCommand
{
    public sealed record Report(int Dramas, int Assigned, int Already, long First, long Last, int PreLog = 0, int Renumbered = 0);

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
        var inLog = q.Read(st => st.Subjects.Values.Where(s => s.Entries.ContainsKey("process") && s.Entries["process"].State != "duplicate").ToDictionary(s => s.Id, s => (s.CentersAt, s.LastAt, s.Seq, HasLine: true), StringComparer.Ordinal));
        var head = await SearchData.LoadAsync(store, paths, null, ct);
        var headRows = head?.Uploads.Select(u => u.Id).ToList() ?? [];
        var frames = new HashSet<string>(StringComparer.Ordinal); var fp = paths.Prefix + "search/frames/";
        foreach (var b in await store.ListAsync(fp, false, false, ct)) { if (b.Name.EndsWith(".bin", StringComparison.Ordinal)) { var id = b.Name[fp.Length..^4]; if (id.Length > 0 && !id.Contains('/')) frames.Add(id); } }
        var known = q.Read(st => st.Subjects.Values.Where(s => s.Entries.Count > 0 || s.Seq != null).Select(s => s.Id).ToHashSet(StringComparer.Ordinal));
        var all = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id) { if (seen.Add(id)) all.Add(id); }
        // dramas with no log line: head index row order, then (deterministic) the rest of the frames files by id
        var noLine = headRows.Where(id => !inLog.ContainsKey(id)).ToList(); noLine.AddRange(frames.Where(id => !inLog.ContainsKey(id) && !headRows.Contains(id)).OrderBy(x => x, StringComparer.Ordinal));
        foreach (var id in noLine) Add(id);
        var keyed = new List<(string Key, string Id)>();
        await Parallel.ForEachAsync(inLog, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (t, c) =>
        {
            string? at = null;
            try { at = (string?)(await VideoJson.ReadAsync(store, paths, t.Key, c))?.Root["precheck"]?["at"]; } catch (Exception e) when (e is not OperationCanceledException) { }
            lock (keyed) keyed.Add((at ?? t.Value.CentersAt ?? t.Value.LastAt ?? "\uffff", t.Key));
        });
        foreach (var k in keyed.OrderBy(k => k.Key, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal)) Add(k.Id);
        if (renumber) foreach (var id in q.Read(st => st.Subjects.Values.Where(x => x.Seq != null).OrderBy(x => x.Seq).Select(x => x.Id).ToList())) Add(id);   // a numbered subject outside the set is renumbered too: no two share a number
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
                long n0 = 1; var lines = order.Select(id => new QLine { T = "seq", S = id, Seq = n0++, At = at, By = "cli", Force = true }).ToList();
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
        throw new ArgumentException("seq assign [--dry-run] [--renumber]");
    }
}
