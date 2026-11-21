namespace IndexNode;

/// <summary>0.13.10 bootstrap: `indexnode seq assign [--dry-run]` gives every existing drama without a sequence number one, ordered by its first pre-check (video.json precheck.at), else its centers step, else its last queue activity, then id; numbers continue after the highest.
/// `indexnode groups enqueue [--dry-run]` adds the group step for every drama whose centers are done and that has no group record yet (create-only: an existing group entry is left alone).</summary>
public static class SeqCommand
{
    public sealed record Report(int Dramas, int Assigned, int Already, long First, long Last);

    public static async Task<Report> AssignAsync(JobQueue q, IStore store, Paths paths, bool dry, CancellationToken ct)
    {
        await q.PollAsync(ct);
        var todo = q.Read(st => st.Subjects.Values.Where(s => s.Seq == null && s.Entries.ContainsKey("process") && s.Entries["process"].State != "duplicate").Select(s => (s.Id, s.CentersAt, s.LastAt)).ToList());
        int already = q.Read(st => st.Subjects.Values.Count(s => s.Seq != null));
        var keyed = new List<(string Key, string Id)>();
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (t, c) =>
        {
            string? at = null;
            try { at = (string?)(await VideoJson.ReadAsync(store, paths, t.Id, c))?.Root["precheck"]?["at"]; } catch (Exception e) when (e is not OperationCanceledException) { }
            lock (keyed) keyed.Add((at ?? t.CentersAt ?? t.LastAt ?? "\uffff", t.Id));
        });
        var order = keyed.OrderBy(k => k.Key, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal).Select(k => k.Id).ToList();
        long next = q.Read(st => st.NextSeq()); long first = next;
        if (!dry)
        {
            var at = q.NowUtc();
            foreach (var chunk in JobQueue.Chunks(order, id => id.Length + 60))
            {
                var ids = chunk;
                // numbers are decided inside the conditional append: a node that assigned one meanwhile is respected (first writer wins)
                await q.AppendLinesAsync(st => { var n = st.NextSeq(); return ids.Where(id => st.Get(id) is { Seq: null }).Select(id => new QLine { T = "seq", S = id, Seq = n++, At = at, By = "cli" }).ToList(); }, ct);
            }
        }
        return new Report(todo.Count + already, order.Count, already, first, first + order.Count - 1);
    }

    public sealed record EnqueueReport(int Candidates, int Added, int NoSeq, int Existing);

    public static async Task<EnqueueReport> EnqueueGroupsAsync(JobQueue q, bool dry, CancellationToken ct)
    {
        await q.PollAsync(ct);
        var now = q.NowUtc(); var lines = new List<QLine>(); int noSeq = 0, existing = 0, cand = 0;
        foreach (var s in q.Read(st => st.Subjects.Values.Where(x => x.Entries.TryGetValue("process", out var p) && p.State == "done" || x.CentersAt != null).OrderBy(x => x.Seq ?? long.MaxValue).ThenBy(x => x.Id, StringComparer.Ordinal).ToList()))
        {
            cand++;
            if (s.Entries.ContainsKey("group")) { existing++; continue; }
            if (s.Seq == null) { noSeq++; continue; }
            lines.Add(QLine.Add(s.Id, "group", GroupSequencing.NewJob(s.Id, 0), now));
        }
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
            var r = await AssignAsync(q, store, paths, dry, cts.Token);
            Console.WriteLine($"seq assign{(dry ? " (dry run)" : "")}: {r.Dramas} dramas, {r.Already} already numbered, {r.Assigned} {(dry ? "would be" : "")} numbered {(r.Assigned > 0 ? $"{r.First}..{r.Last}" : "")}");
            return 0;
        }
        throw new ArgumentException("seq assign [--dry-run]");
    }
}
