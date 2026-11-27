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
            case "work":
            {   // 0.13.77 (read only): the download work queue the service fills: live entries by stage, state, priority and readiness
                var wq = new JobQueue(store, paths.DownloadWork, "cli"); await wq.LoadAsync(default); var now = Protocol.NowUtc();
                var rows = wq.Read(st => st.Subjects.Values.SelectMany(x => x.Entries.Values.Where(e => !e.Final).Select(e => (x.Id, e.Stage, e.State, e.Priority, Ready: string.CompareOrdinal(e.Due ?? "", now) <= 0, Claimed: x.ClaimBy != null))).ToList());
                foreach (var g in rows.GroupBy(r => (r.Stage, r.State, r.Priority, r.Ready, r.Claimed)).OrderByDescending(g => g.Key.Priority).ThenBy(g => g.Key.Stage))
                    Console.WriteLine($"{g.Count(),6}  {g.Key.Stage,-9} {g.Key.State,-10} pri {g.Key.Priority,4}  {(g.Key.Ready ? "ready" : "due later")}{(g.Key.Claimed ? ", claimed" : "")}   e.g. {g.First().Id}");
                Console.WriteLine($"{rows.Count} live entries");
                return 0;
            }
            case "list": case null:
                return await ListAsync(a, a.Has("download") ? await JobQueue.OpenAsync(store, paths, "cli", true, default) : await Load(store, paths));   // 0.13.77: --download lists the full download queue
            case "states":
            {   // 0.13.74 (read only): every entry of the given uploads in the processing and the download queue, one line per upload: id <tab> realm:stage=state[:hold] ...
                var ids = File.ReadAllLines(a.Get("ids") ?? throw new ArgumentException("jobs states --ids=<file> [--out=<file>]")).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                var pq = await Load(store, paths); var dq = await JobQueue.OpenAsync(store, paths, "cli", true, default);
                string Of(JobQueue q, string realm, string id) => q.Read(st => st.Get(id) is { } s ? string.Join(" ", s.Entries.Values.Select(e => $"{realm}:{e.Stage}={e.State}{(e.Hold != null ? ":" + e.Hold : "")}{(e.State == "failed" && e.Job?.Reason is { } why ? "[" + why.Replace(" ", "_")[..Math.Min(60, why.Length)] + "]" : "")}")) : "");
                var lines = ids.Select(id => id + "\t" + (Of(pq, "p", id) + " " + Of(dq, "d", id)).Trim()).ToList();
                if (a.Get("out") is { } o) File.WriteAllLines(o, lines); else foreach (var l in lines) Console.WriteLine(l);
                Console.Error.WriteLine($"{ids.Count} uploads; known to a queue: {lines.Count(l => l.Split('\t')[1].Length > 0)}");
                return 0;
            }
            case "priority":
            {
                if (a.Get("ids") is { } idf)
                {   // 0.13.74: one priority for every upload of a file, in the full download queue (the owner's picks); --dry-run counts only
                    var list = File.ReadAllLines(idf).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).Distinct().ToList();
                    int pp = PriorityTool.ParseOption(a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("jobs priority --ids=<file> N"));
                    var dq = await JobQueue.OpenAsync(store, paths, "cli", true, default);
                    if (a.Has("dry-run")) { int live = list.Count(x => dq.Read(st => st.Get(x)) is { } s && s.Entries.Values.Any(e => !e.Final)); Console.WriteLine($"[dry run] {list.Count} id(s); with an open download-queue entry (would get priority {pp}): {live}"); return 0; }
                    var rr = await PriorityTool.SetAsync(dq, pp, list, default);
                    Console.WriteLine($"priority {pp}: {rr.Changed} upload(s) changed; skipped {string.Join(", ", rr.Skipped.Select(k => $"{k.Value} {k.Key}"))}"); return 0;
                }
                var id = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("jobs priority <subject> N");
                var q = await JobQueue.OpenForVideoAsync(store, paths, "cli", id, default);   // processing first, then download
                int p = PriorityTool.ParseOption(a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("jobs priority <subject> N"));
                var r = await PriorityTool.SetAsync(q, p, [id], default);
                Console.WriteLine($"{id}: {(r.Changed > 0 ? "priority " + p : "unchanged (" + string.Join(", ", r.Skipped.Keys) + ")")}");
                return r.Changed > 0 ? 0 : 1;
            }
            case "retry":
            {
                var stage = a.Get("stage"); var subject = a.Positional.ElementAtOrDefault(1);
                var q = subject != null ? await JobQueue.OpenForVideoAsync(store, paths, "cli", subject, default) : await Load(store, paths);
                var at = Protocol.NowUtc();
                var lines = q.Read(st => st.Subjects.Values.Where(s => subject == null || s.Id == subject).SelectMany(s => s.Entries.Values.Where(e => e.State == "failed" && (stage == null || e.Stage == stage) && e.Job?.Reason?.StartsWith("classify:", StringComparison.Ordinal) != true).Select(e => new QLine { T = "retry", S = s.Id, Stage = e.Stage, Due = at, At = at })).ToList());
                if (lines.Count > 0) await q.AppendLinesAsync(lines, default);
                foreach (var l in lines.Take(50)) Console.WriteLine($"requeued {l.Stage}/{l.S}");
                Console.WriteLine($"{lines.Count} job(s) moved back to pending");
                return 0;
            }
            case "hold-ai":
            {
                bool dry = a.Has("dry-run"); var q = await Load(store, paths); var ops0 = StoreMeter.Current.Total;
                var r = await AiHold.HoldPendingAsync(q, store, paths, dry, default, Console.WriteLine);
                Console.WriteLine($"{r.Download} download and {r.Process} process entr(ies) {(dry ? "would be" : "were")} held (ai); {r.Skipped} pending subject(s) not AI-flagged by a pre-check or sample check, {r.Unreadable} without a readable video.json; {r.Batches} append(s){(dry ? " (dry run: nothing written)" : "")}");
                Console.WriteLine($"store ops of this migration: {StoreMeter.Current.Total - ops0} ({StoreMeter.Current.TotalFailed} failed)");
                if (!dry) Console.WriteLine("release them with: indexnode control ai-dramas on");
                return 0;
            }
            case "sample-first":
            {
                bool dry = a.Has("dry-run");
                if (a.Has("download"))
                {   // 0.13.52: the full download queue is the service's (it converts at every top-up); this only counts what it would convert
                    var dq = await JobQueue.OpenAsync(store, paths, "cli", true, default);
                    var (n, withSample, held, uncl) = dq.Read(st => (
                        SampleFirst.Plan(st, Protocol.NowUtc(), out _, out _, out _, out _).Count(l => l.T == "add" && l.Stage == "sample" && st.Entry(l.S, "download") is { State: "pending", Hold: null }),
                        st.Subjects.Values.Count(s => s.Entries.GetValueOrDefault("download") is { State: "pending" } && (s.Entries.ContainsKey("sample") || s.Entries.ContainsKey("check"))),
                        st.Subjects.Values.Count(s => s.Entries.GetValueOrDefault("download") is { State: "pending", Hold: not null }),
                        st.Subjects.Values.Count(s => s.Entries.GetValueOrDefault("download") is { State: Tags.Unclassified })));
                    Console.WriteLine($"download queue: {n} pending download(s) of uploads never sampled (the service turns them into sample entries at its next top-up); {withSample} pending download(s) already sampled or checked; {held} held; {uncl} unclassified; pending samples: {dq.Read(st => st.Subjects.Values.Count(s => s.Entries.GetValueOrDefault("sample") is { State: "pending" }))}");
                    if (a.Get("since") is { } since)
                    {   // the downloads finished since then: was each upload sampled or checked first, and when
                        var rows = dq.Read(st => st.Subjects.Values.Where(s => s.Entries.GetValueOrDefault("download") is { State: "done" } d && string.CompareOrdinal(d.FinishedAt ?? "", since) >= 0)
                            .Select(s => (Sample: s.Entries.GetValueOrDefault("sample"), Check: s.Entries.GetValueOrDefault("check"))).ToList());
                        int both = rows.Count(r => r.Sample != null || r.Check != null), none = rows.Count - both;
                        int sampledBefore = rows.Count(r => r.Sample is { State: "done" } sm && string.CompareOrdinal(sm.FinishedAt ?? "", since) < 0), sampledAfter = rows.Count(r => r.Sample is { State: "done" } sm && string.CompareOrdinal(sm.FinishedAt ?? "", since) >= 0);
                        Console.WriteLine($"downloads finished since {since}: {rows.Count}; with a sample or check entry: {both} (sample done before then: {sampledBefore}, since then: {sampledAfter}; check entry only: {rows.Count(r => r.Sample == null && r.Check != null)}); with neither: {none}");
                        foreach (var g in rows.Where(r => r.Sample is { State: "done" }).GroupBy(r => (r.Sample!.FinishedAt ?? "")[..Math.Min(10, (r.Sample!.FinishedAt ?? "").Length)]).OrderBy(g => g.Key)) Console.WriteLine($"  sampled on {g.Key}: {g.Count()}");
                    }
                    return 0;
                }
                var q = await Load(store, paths);
                var ops0 = StoreMeter.Current.Total;
                var rep = await SampleFirst.RunAsync(q, dry, default, store, paths);
                Console.WriteLine($"store ops of this migration: {StoreMeter.Current.Total - ops0} ({StoreMeter.Current.TotalFailed} failed)");
                Console.WriteLine($"{rep.Downloads} pending download(s) {(dry ? "would become" : "became")} sample entries (priority and due kept), {rep.Checks} pending process entr(ies) {(dry ? "would get" : "got")} a check entry ahead of the process step, {rep.AlreadyDone} already sample-first, {rep.Claimed} under a claim left alone, {rep.Pubs} publish time(s) {(dry ? "would be" : "were")} filled in [{rep.PubSources}] (info.json, else the source's members.json, else an estimate from its listing order){(dry ? " (dry run: nothing written)" : $", {rep.Batches} batched append(s)")}");
                if (!dry) Console.WriteLine("lift the process hold with: indexnode control run --stage process");
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
        var q = await JobQueue.OpenForVideoAsync(store, paths, "cli", id, default);   // processing first, then download
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
