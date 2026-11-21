namespace IndexNode;

/// <summary>
/// 0.13.2 "indexnode jobs repair-steps [--dry-run]": for every process subject, the steps listed as done whose output is not on the store are taken out of its done list, as batched "fixsteps" lines (one append per batch).
/// A finished subject that lacks a step output is re-opened with only the missing steps; that claim runs no pre-check (it is decided from the store) and adds the search-index follow-up after centers as usual.
/// A subject under a claim is left alone. Outputs are found with one listing of videos/ (names only, never a hydrate).
/// </summary>
public static class RepairSteps
{
    public sealed record Report(int Subjects, int Fixed, int StepsRemoved, int Reopened, SortedDictionary<string, int> PerStep, int Claimed);

    public static async Task<Report> RunAsync(JobQueue queue, IStore store, Paths paths, bool dryRun, CancellationToken ct)
    {
        await queue.PollAsync(ct);
        var pre = paths.Prefix + "videos/"; var zips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in await store.ListAsync(pre, false, false, ct)) { if (b.Name.EndsWith(".zip", StringComparison.Ordinal)) zips.Add(b.Name); }
        var at = queue.NowUtc(); var lines = new List<QLine>(); var per = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int subjects = 0, fixedN = 0, removed = 0, reopened = 0, claimed = 0;
        var std = NodeEngine.StandardSteps; var checkedSteps = NodeEngine.StandardSteps.Where(s => s != "sync").ToArray();
        var items = queue.Read(st => st.Subjects.Values.Where(s => s.Entries.ContainsKey("process")).Select(s => (s.Id, s.ClaimBy, E: s.Entries["process"])).OrderBy(x => x.Id, StringComparer.Ordinal).ToList());
        foreach (var (id, claimBy, e) in items)
        {
            subjects++;
            if (claimBy != null) { claimed++; continue; }
            bool finishedOk = e.State == "done";
            if (!finishedOk && e.State is not ("pending" or "failed")) continue;   // duplicates and other ends have no steps to run
            var done = finishedOk ? std.ToList() : (e.StepsDone ?? []).ToList();   // a finished process entry says every step
            var gone = checkedSteps.Where(s => done.Contains(s) && !zips.Contains(paths.Video(id, s + ".zip"))).ToList();
            if (gone.Count == 0) continue;
            foreach (var g in gone) { done.Remove(g); per[g] = per.GetValueOrDefault(g) + 1; }
            fixedN++; removed += gone.Count; if (finishedOk) reopened++;
            lines.Add(new QLine { T = "fixsteps", S = id, Stage = "process", Done = done.ToArray(), State = finishedOk ? "pending" : null, Due = at, At = at });
        }
        if (!dryRun) foreach (var chunk in JobQueue.Chunks(lines, l => l.ToJson().Length)) { var c = chunk; await queue.AppendLinesAsync(c, ct); }
        return new Report(subjects, fixedN, removed, reopened, per, claimed);
    }
}
