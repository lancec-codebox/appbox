using System.Text.Json;

namespace IndexNode;

/// <summary>What <c>indexnode jobs migrate --to log</c> found and would write (or wrote).</summary>
public sealed class MigrationReport
{
    public int JobBlobs { get; set; }
    public int Subjects { get; set; }
    public int Running { get; set; }
    public SortedDictionary<string, SortedDictionary<string, int>> Entries { get; } = new();
    public SortedDictionary<string, int> Notes { get; } = new();
    public bool Written { get; set; }
    public bool AlreadyMigrated { get; set; }
    public int SeenFiles { get; set; }
    public int Holds { get; set; }
    public int LeasedHolds { get; set; }
    public int GpuMarkers { get; set; }
    /// <summary>The job blobs as they are today, per stage and state (to compare with <see cref="Entries"/>; one process entry stands for several step blobs).</summary>
    public SortedDictionary<string, SortedDictionary<string, int>> Blobs { get; } = new();
    public void CountBlob(string stage, string state) { if (!Blobs.TryGetValue(stage, out var d)) Blobs[stage] = d = new(); d[state] = d.GetValueOrDefault(state) + 1; }
    public void Count(string stage, string state) { if (!Entries.TryGetValue(stage, out var d)) Entries[stage] = d = new(); d[state] = d.GetValueOrDefault(state) + 1; }
    public void Note(string n) => Notes[n] = Notes.GetValueOrDefault(n) + 1;

    public IEnumerable<string> Lines()
    {
        yield return $"job blobs read: {JobBlobs}; subjects: {Subjects}";
        foreach (var (stage, d) in Blobs) yield return $"  blobs   {stage,-12} {string.Join("  ", d.Select(k => k.Key + "=" + k.Value))}";
        yield return "  queue entries (compare with the blobs; the step jobs of one drama become one process entry):";
        foreach (var (stage, d) in Entries) yield return $"  {stage,-12} {string.Join("  ", d.Select(k => k.Key + "=" + k.Value))}";
        foreach (var (n, c) in Notes) yield return $"  note: {n}: {c}";
        yield return $"  not carried over: holds={Holds} (leased: {LeasedHolds}), gpu-ready markers={GpuMarkers}, comments jobs={Notes.Where(k => k.Key.StartsWith("comments jobs")).Sum(k => k.Value)}";
        if (AlreadyMigrated) yield return "the queue snapshot already exists (use --force to build it again)";
        yield return Written ? $"written: first segment (empty), queue snapshot{(SeenFiles > 0 ? ", " + SeenFiles + " sources/<id>/seen.json" : "")}; the old job blobs are left in place" : "nothing written (dry run, or refused)";
    }
}

/// <summary>
/// 0.13.0 one-time conversion of today's job blobs (jobs/&lt;stage&gt;/&lt;id&gt;.json with idx_* tags) into the first queue snapshot. One subject per drama: its pending standard step jobs become one <c>process</c> entry with the steps already done;
/// a drama with only describe / translate pending gets those entries; download, comments, refresh, classify and availability jobs carry over as they are. The old blobs stay; nothing is deleted.
/// </summary>
public static class Migration
{
    public static async Task<(QueueState State, MigrationReport Report)> BuildAsync(IStore store, Paths paths, string at, CancellationToken ct)
    {
        var rep = new MigrationReport(); var state = new QueueState();
        var all = (await store.ListAsync(paths.Prefix + "jobs/", true, false, ct)).Where(b => b.Name.EndsWith(".json", StringComparison.Ordinal) && !b.Name.Contains("/active/") && !b.Name.EndsWith("queue.snapshot.json", StringComparison.Ordinal)).ToList();
        rep.JobBlobs = all.Count;
        rep.Running = all.Count(b => b.LeaseState == "leased");
        foreach (var b in all) { var rel0 = b.Name[(paths.Prefix + "jobs/").Length..]; var sl = rel0.IndexOf('/'); if (sl > 0) rep.CountBlob(rel0[..sl] == "watch" ? "refresh" : rel0[..sl], St(b)); }
        var holds = await store.ListAsync(paths.HoldsDir, false, false, ct); rep.Holds = holds.Count; rep.LeasedHolds = holds.Count(h => h.LeaseState == "leased");
        rep.GpuMarkers = (await store.ListAsync(paths.Prefix + GpuRouting.MarkerDir, false, false, ct)).Count;
        if (rep.LeasedHolds > 0) { rep.Running += rep.LeasedHolds; }
        if (rep.Running > 0) return (state, rep);   // refused: the caller says so
        var byDrama = new Dictionary<string, Dictionary<string, BlobEntry>>(StringComparer.Ordinal);
        foreach (var b in all)
        {
            var rel = b.Name[(paths.Prefix + "jobs/").Length..]; var slash = rel.IndexOf('/'); if (slash < 0) continue;
            var stage = rel[..slash]; var id = Path.GetFileNameWithoutExtension(rel);
            if (stage == "watch") stage = "refresh";
            if (stage == "comments") { rep.Note(St(b) == "pending" ? "comments jobs not carried over (pending)" : "comments jobs not carried over (finished)"); continue; }   // owner: comments are part of the download now
            if (Protocol.IsStep(stage) || stage == "process") { if (!byDrama.TryGetValue(id, out var g)) byDrama[id] = g = new(); g[stage] = b; continue; }
            await OneAsync(store, state, rep, stage, id, b, at, paths, ct);
        }
        foreach (var (id, steps) in byDrama) AddDrama(state, rep, id, steps, at);
        rep.Subjects = state.Subjects.Count;
        return (state, rep);
    }

    private static string St(BlobEntry b) => b.Tags.GetValueOrDefault(Tags.State) ?? "pending";

    private static async Task<JobDescriptor?> Body(IStore store, BlobEntry b, CancellationToken ct)
    {
        try { return await store.GetAsync(b.Name, ct) is { } c ? JobDescriptor.Parse(c.Data) : null; }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return null; }
    }

    private static async Task OneAsync(IStore store, QueueState state, MigrationReport rep, string stage, string id, BlobEntry b, string at, Paths paths, CancellationToken ct)
    {
        var s = St(b); var added = b.Tags.GetValueOrDefault(Tags.Added) ?? at; var due = Tags.DueOf(b.Tags); if (due.Length == 0) due = added;
        int pri = Tags.PriorityOf(b.Tags);
        string subject = id;
        JobDescriptor? body = null;
        bool needBody = !(stage == "download" && s == "done");
        if (needBody) body = await Body(store, b, ct);
        if (stage == "classify") { subject = ClassifyJobs.Subject; if (body == null) body = new JobDescriptor { Stage = "classify", JobId = ClassifyJobs.SingletonId, Name = "classify all" }; }
        if (stage == "availability") { subject = AvailabilityStage.SweepId; }
        if (stage == "classify" && id != ClassifyJobs.SingletonId) { rep.Note("per-channel classify jobs are not migrated (the singleton decides everything)"); return; }
        if (body != null) { body.Stage = stage; body.Priority = pri; }
        if (!state.Subjects.TryGetValue(subject, out var sub)) state.Subjects[subject] = sub = new QSubject { Id = subject, LastAt = at };
        if (stage == "refresh" && body != null)
        {
            // an old default (a channel every 1d) becomes dynamic; every other schedule is kept. The tier and the inactive rule apply on the source's first refresh, not here.
            if (body.Kind == "channel" && Schedule.Of(body) is { Kind: "every" } sc && sc.Interval == TimeSpan.FromDays(1) && s != "failed") { body.Schedule = "dynamic"; body.Every = null; rep.Note("channel sources on 'every 1d' made dynamic"); }
            body.Run ??= RefreshStage.KindOf(body);
        }
        var e = new QEntry { Stage = stage, State = s, Priority = pri, Added = added, Due = due, Job = body, Attempts = body?.Attempts ?? 0, LastError = body?.LastError, Permanent = body?.Permanent, FinishedAt = body?.FinishedAt, Node = body?.NodeId };
        if (stage == "download" && s == "done") e.Job = new JobDescriptor { Stage = "download", JobId = id, Priority = pri };
        sub.Entries[stage] = e;
        rep.Count(stage, s);
    }

    private static void AddDrama(QueueState state, MigrationReport rep, string id, Dictionary<string, BlobEntry> jobs, string at)
    {
        if (!state.Subjects.TryGetValue(id, out var sub)) state.Subjects[id] = sub = new QSubject { Id = id, LastAt = at };
        var std = NodeEngine.StandardSteps;
        var stdJobs = std.Where(jobs.ContainsKey).ToList();
        var pending = stdJobs.Where(x => St(jobs[x]) == "pending").ToList();
        var failed = stdJobs.Where(x => St(jobs[x]) == "failed").ToList();
        // a legacy all-in-one process job is the same thing as its steps
        if (jobs.TryGetValue("process", out var legacy) && St(legacy) == "pending" && pending.Count == 0 && failed.Count == 0) pending = std.Where(x => !stdJobs.Contains(x) || St(jobs[x]) != "done").ToList();
        if (pending.Count > 0 || failed.Count > 0)
        {
            var all = pending.Select(x => jobs[x]).Concat(failed.Select(x => jobs[x])).ToList();
            var done = std.Where(x => !pending.Contains(x) && !failed.Contains(x)).ToList();
            var added = all.Select(b => b.Tags.GetValueOrDefault(Tags.Added) ?? at).OrderBy(x => x, StringComparer.Ordinal).First();
            var due = pending.Count > 0 ? pending.Select(x => Tags.DueOf(jobs[x].Tags)).Where(x => x.Length > 0).DefaultIfEmpty(added).Min()! : added;
            int pri = all.Max(b => Tags.PriorityOf(b.Tags));
            bool dead = pending.Count == 0;
            sub.Entries["process"] = new QEntry { Stage = "process", State = dead ? "failed" : "pending", Priority = pri, Added = added, Due = due, StepsDone = done, Job = new JobDescriptor { Stage = "process", JobId = id, Priority = pri, AddedAt = added }, Attempts = dead ? Protocol.MaxAttempts : 0, LastError = dead ? "a step job failed before the migration" : null, Permanent = dead ? true : null };
            rep.Count("process", dead ? "failed" : "pending");
        }
        foreach (var ai in new[] { "describe", "translate" })
            if (jobs.TryGetValue(ai, out var b) && St(b) is "pending" or "failed" && !sub.Entries.ContainsKey(ai))
            {
                // a pending entry is carried only when its inputs are done (describe: transcribe, ocrsubs and cast; translate: describe); otherwise the follow-up of the step that unlocks it adds it
                if (St(b) == "pending")
                {
                    bool ready = ai == "describe"
                        ? new[] { "transcribe", "ocrsubs", "cast" }.All(x => !pending.Contains(x) && !failed.Contains(x) && (!jobs.ContainsKey(x) || St(jobs[x]) == "done") && !(sub.Entries.TryGetValue("process", out var pe) && pe.StepsDone?.Contains(x) != true))
                        : jobs.TryGetValue("describe", out var dj) && St(dj) == "done";
                    if (!ready) { rep.Note($"{ai} entries not carried over (inputs not done: the follow-up adds them)"); continue; }
                }
                var added = b.Tags.GetValueOrDefault(Tags.Added) ?? at; int pri = Tags.PriorityOf(b.Tags);
                sub.Entries[ai] = new QEntry { Stage = ai, State = St(b), Priority = pri, Added = added, Due = Tags.DueOf(b.Tags) is { Length: > 0 } d ? d : added, Job = new JobDescriptor { Stage = ai, JobId = id, Priority = pri, AddedAt = added } };
                rep.Count(ai, St(b));
            }
        if (sub.Entries.Count == 0) { state.Subjects.Remove(id); rep.Note("dramas with every step job done need no entry"); }
    }

    /// <summary>Writes the empty first segment and the first snapshot (and the sources' seen lists); the old blobs stay. Returns false (nothing written) when the snapshot exists and <paramref name="force"/> is off.</summary>
    public static async Task<MigrationReport> RunAsync(IStore store, Paths paths, bool dryRun, bool force, string at, CancellationToken ct)
    {
        var (state, rep) = await BuildAsync(store, paths, at, ct);
        if (rep.Running > 0) throw new InvalidOperationException($"{rep.Running} job(s) or hold(s) are running right now (leased); stop the nodes (or wait) and run the migration again");
        if ((await store.GetAsync(paths.QueueSnapshotGz, ct) != null || await store.GetAsync(paths.QueueSnapshot, ct) != null) && !force) { rep.AlreadyMigrated = true; return rep; }
        if (dryRun) return rep;
        foreach (var s in state.Subjects.Values)
            if (s.Entries.TryGetValue("refresh", out var e) && e.Job?.Seen is { Count: > 0 } seen)
            {
                await store.PutAsync(paths.SourceSeen(s.Id), JsonSerializer.SerializeToUtf8Bytes(seen, Protocol.Json), null, null, false, ct); rep.SeenFiles++;
                e.Job.Seen = [];   // the snapshot, like a log line, does not carry the list
            }
        await store.CreateAppendBlobAsync(paths.QueueSegment(1), true, ct);
        await store.PutAsync(paths.QueueSnapshotGz, QueueState.Pack(state.ToSnapshot(1, 0, 0, at)), null, null, false, ct);
        rep.Written = true;
        return rep;
    }
}
