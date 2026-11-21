namespace IndexNode;

/// <summary>indexnode jobs migrate | list | priority | retry | release, and indexnode video &lt;id&gt;: all read or edit the queue (0.13.0), never job blobs.</summary>
public static class JobsCommand
{
    public static async Task<int> RunAsync(Args a)
    {
        var sub = a.Positional.FirstOrDefault();
        var (_, store, paths, http) = Commands.Open(a); using var _h = http;
        if (sub != "migrate") await JobQueue.RequireSnapshotAsync(store, paths, default);
        switch (sub)
        {
            case "migrate":
            {
                if (a.Get("to") != "log") throw new ArgumentException("jobs migrate --to log [--dry-run]");
                bool dry = a.Has("dry-run") || a.Has("verify");
                try
                {
                    var rep = await Migration.RunAsync(store, paths, dry, a.Has("force"), Protocol.NowUtc(), default);
                    foreach (var l in rep.Lines()) Console.WriteLine(dry ? "[dry run] " + l : l);
                    return 0;
                }
                catch (InvalidOperationException e) { Console.Error.WriteLine("indexnode: " + e.Message); return 1; }
            }
            case "list": case null:
                return await ListAsync(a, await Load(store, paths));
            case "priority":
            {
                var q = await Load(store, paths);
                var id = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("jobs priority <subject> N");
                int p = PriorityTool.ParseOption(a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("jobs priority <subject> N"));
                var r = await PriorityTool.SetAsync(q, p, [id], default);
                Console.WriteLine($"{id}: {(r.Changed > 0 ? "priority " + p : "unchanged (" + string.Join(", ", r.Skipped.Keys) + ")")}");
                return r.Changed > 0 ? 0 : 1;
            }
            case "retry":
            {
                var q = await Load(store, paths);
                var stage = a.Get("stage"); var subject = a.Positional.ElementAtOrDefault(1);
                var at = Protocol.NowUtc();
                var lines = q.Read(st => st.Subjects.Values.Where(s => subject == null || s.Id == subject).SelectMany(s => s.Entries.Values.Where(e => e.State == "failed" && (stage == null || e.Stage == stage) && e.Job?.Reason?.StartsWith("classify:", StringComparison.Ordinal) != true).Select(e => new QLine { T = "retry", S = s.Id, Stage = e.Stage, Due = at, At = at })).ToList());
                if (lines.Count > 0) await q.AppendLinesAsync(lines, default);
                foreach (var l in lines.Take(50)) Console.WriteLine($"requeued {l.Stage}/{l.S}");
                Console.WriteLine($"{lines.Count} job(s) moved back to pending");
                return 0;
            }
            case "repair-steps":
            {
                // 0.13.2: steps listed as done whose output is not on the store come out of the done list; a finished subject that lacks an output is re-opened with only the missing steps
                bool dry = a.Has("dry-run");
                var q = await Load(store, paths);
                var rep = await RepairSteps.RunAsync(q, store, paths, dry, default);
                Console.WriteLine($"{rep.Subjects} process subject(s): {rep.Fixed} {(dry ? "would be fixed" : "fixed")}, {rep.StepsRemoved} step(s) {(dry ? "would be removed" : "removed")} from done lists, {rep.Reopened} finished subject(s) {(dry ? "would be re-opened" : "re-opened")}{(rep.Claimed > 0 ? $", {rep.Claimed} under a claim left alone" : "")}{(dry ? " (dry run: nothing written)" : "")}");
                foreach (var (s, n) in rep.PerStep) Console.WriteLine($"  {s}: {n}");
                return 0;
            }
            case "release":
            {
                // frees a subject whose worker is gone without waiting for its lease: deletes the active file when its lease has lapsed, else says who holds it
                var q = await Load(store, paths);
                var id = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("jobs release <subject>");
                var name = paths.Active(id);
                var lease = await store.AcquireLeaseAsync(name, Protocol.LeaseSeconds, default).ConfigureAwait(false);
                if (lease == null) { Console.Error.WriteLine($"{id}: its lease is held by a live worker; it frees itself when that worker stops or loses it (60 s)"); return 1; }
                await store.DeleteAsync(name, lease, default);
                Console.WriteLine($"{id}: released (the next claim starts from the first unfinished step)");
                return 0;
            }
            default: throw new ArgumentException("jobs migrate --to log [--dry-run] | repair-steps [--dry-run] | list [--stage S] [--held ai] | priority <subject> N | retry [--stage S] [<subject>] | release <subject>");
        }
    }

    /// <summary>indexnode trash list | empty --older-than &lt;age&gt; [--dry-run]: the manual cleanup of what a pre-check skip marked. No node ever runs it.</summary>
    public static async Task<int> TrashAsync(Args a)
    {
        var sub = a.Positional.FirstOrDefault() ?? "list";
        var (_, store, paths, http) = Commands.Open(a); using var _h = http;
        await JobQueue.RequireSnapshotAsync(store, paths, default);
        var q = await Load(store, paths);
        switch (sub)
        {
            case "list":
            {
                var items = q.Read(Trash.List);
                foreach (var i in items) Console.WriteLine($"{i.Subject}  marked {i.MarkedAt}  {i.Mark}{(i.DeletedAt != null ? "  DELETED " + i.DeletedAt : "")}  duplicate of {i.DuplicateOf} (share {i.Share:0.00})  why: {i.Why}");
                Console.WriteLine($"{items.Count} marked ({items.Count(x => x.DeletedAt == null)} not deleted yet)");
                return 0;
            }
            case "empty":
            {
                var age = Trash.ParseAge(a.Get("older-than") ?? throw new ArgumentException("trash empty --older-than <age, e.g. 30d> [--dry-run]"));
                bool dry = a.Has("dry-run");
                var res = await Trash.EmptyAsync(q, store, paths, age, dry, default);
                foreach (var (i, blobs) in res) Console.WriteLine($"{(dry ? "would remove" : "removed")} {i.Subject}: {(blobs.Length == 0 ? "(no video blob left)" : string.Join(", ", blobs.Select(b => b[(b.LastIndexOf('/') + 1)..])))}  marked {i.MarkedAt}  why: {i.Why}");
                Console.WriteLine($"{res.Count} video(s) {(dry ? "would be removed (dry run: nothing written)" : "removed")}");
                return 0;
            }
            default: throw new ArgumentException("trash list | trash empty --older-than <age> [--dry-run]");
        }
    }

    private static async Task<JobQueue> Load(IStore store, Paths paths) { var q = new JobQueue(store, paths, "cli"); await q.LoadAsync(default); return q; }

/// <summary>`jobs list [--stage S] [--held ai] [--top N]`: the pending entries (best first), or the entries held for a reason. 0.13.11: a stage filter works for every stage (group included), and an empty answer says so.</summary>
    public static Task<int> ListAsync(Args a, JobQueue q)
    {
        int top = a.Get("top") is { } t ? int.Parse(t) : 20; int n = 0;
        if (a.Get("held") is { } held)
        {
            foreach (var (sid, e) in q.Read(s => s.Subjects.Values.SelectMany(x => x.Entries.Values.Where(e => e.Hold == held && !e.Final && (a.Get("stage") == null || e.Stage == a.Get("stage"))).Select(e => (x.Id, e))).OrderBy(x => x.Id, StringComparer.Ordinal).Take(top).ToList()))
            { Console.WriteLine($"{e.Priority,6}  {e.Added}  {e.Stage}/{sid}  held={e.Hold}{(e.HoldAiScore != null ? $" score={e.HoldAiScore:0.000}" : "")}"); n++; }
            if (n == 0) Console.WriteLine($"(nothing held={held})");
            return Task.FromResult(0);
        }
        foreach (var row in PriorityTool.List(q, a.Get("stage"), top)) { Console.WriteLine($"{row.Priority,6}  {row.Added}  {row.Stage}/{row.Id}"); n++; }
        if (n == 0) Console.WriteLine(a.Get("stage") is { } st ? $"(no pending {st} entries)" : "(no pending entries)");
        return Task.FromResult(0);
    }


    /// <summary>indexnode video &lt;id&gt;: what the queue knows about the video (entries, steps done, claim, last error).</summary>
    public static async Task<int> VideoAsync(Args a)
    {
        var id = a.Get("video") ?? a.Positional.FirstOrDefault() ?? throw new ArgumentException("video <id>");
        var (_, store, paths, http) = Commands.Open(a); using var _h = http;
        await JobQueue.RequireSnapshotAsync(store, paths, default);
        var q = await Load(store, paths);
        foreach (var l in Describe(q, id)) Console.WriteLine(l);
        return 0;
    }

    public static List<string> Describe(JobQueue q, string id)
    {
        var s = q.Read(st => st.Get(id));
        var lines = new List<string>();
        if (s == null) { lines.Add($"{id}: not on the queue"); return lines; }
        lines.Add($"{id}: {(s.ClaimBy != null ? $"claimed by {s.ClaimBy} (epoch {s.Epoch}, since {s.ClaimAt}, stages {string.Join('+', s.ClaimStages ?? [])})" : "not claimed")}");
        foreach (var e in s.Entries.Values.OrderBy(e => QueueState.Rank(e.Stage)))
        {
            var detail = new List<string> { e.State, $"priority {e.Priority}", $"added {e.Added}" };
            if (e.State == "pending" && e.Due.Length > 0) detail.Add("due " + e.Due);
            if (e.StepsDone is { Count: > 0 }) detail.Add("steps done " + string.Join(',', e.StepsDone));
            if (e.Attempts > 0) detail.Add($"attempts {e.Attempts}");
            if (e.FinishedAt != null) detail.Add("finished " + e.FinishedAt + (e.Node != null ? " by " + e.Node : ""));
            if (e.LastError != null) detail.Add("error: " + Redact.Sas(e.LastError));
            lines.Add($"  {e.Stage,-10} {string.Join("; ", detail)}");
        }
        return lines;
    }
}
