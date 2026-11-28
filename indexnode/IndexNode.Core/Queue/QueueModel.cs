using System.Text.Json;
using System.Text.Json.Serialization;

namespace IndexNode;

/// <summary>One line of the queue log (jobs/queue.log), one JSON object per line. Types: add, claim, done, failed, pri, due, retry, upd, cls, drop.</summary>
public sealed class QLine
{
    [JsonPropertyName("t")] public string T { get; set; } = "";
    /// <summary>Subject: the video id, "ch-..." / "pl-..." for a source, "classify" for the singleton.</summary>
    [JsonPropertyName("s")] public string S { get; set; } = "";
    [JsonPropertyName("st")] public string? Stage { get; set; }
    [JsonPropertyName("step")] public string? Step { get; set; }
    [JsonPropertyName("by")] public string? By { get; set; }
    [JsonPropertyName("ep")] public int? Epoch { get; set; }
    [JsonPropertyName("at")] public string? At { get; set; }
    [JsonPropertyName("due")] public string? Due { get; set; }
    [JsonPropertyName("pri")] public int? Pri { get; set; }
    [JsonPropertyName("att")] public int? Attempt { get; set; }
    [JsonPropertyName("err")] public string? Error { get; set; }
    [JsonPropertyName("perm")] public bool? Permanent { get; set; }
    /// <summary>An "add" replaces a finished or failed entry only with force; otherwise it is create-only.</summary>
    [JsonPropertyName("force")] public bool? Force { get; set; }
    /// <summary>The last line of a claim: the subject is no longer claimed after it.</summary>
    [JsonPropertyName("fin")] public bool? Fin { get; set; }
    /// <summary>The entry's state after this line (add: pending | unclassified; done: done | duplicate; failed: pending | failed; cls: pending | failed).</summary>
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("stages")] public string[]? Stages { get; set; }
    /// <summary>failed: the steps already done (the next claim resumes from the first unfinished one).</summary>
    [JsonPropertyName("done")] public string[]? Done { get; set; }
    [JsonPropertyName("job")] public JobDescriptor? Job { get; set; }
    /// <summary>done (a pre-check skip): what is marked for deletion, e.g. "download.zip/video". Nothing is deleted by the line or by any node: only `indexnode trash empty` removes it, and then appends a "deleted" line.</summary>
    [JsonPropertyName("trash")] public string? Trash { get; set; }
    /// <summary>done with a trash mark: why (the pre-check's reasons).</summary>
    [JsonPropertyName("why")] public string? Why { get; set; }
    /// <summary>done (process): the pre-check's decision for this claim (new, partial, canonical, duplicate), also when it let the video through.</summary>
    [JsonPropertyName("pc")] public string? PreCheck { get; set; }
    /// <summary>0.13.2 done (process / duplicate): the pre-check's PROPOSED group (null = new), time offset (s), match share (cosine >= 0.75) and duplicate share (>= 0.85); mixed = flagged as one part of a longer cut. The search-index job is the only group writer.</summary>
    [JsonPropertyName("grp")] public string? Group { get; set; }
    [JsonPropertyName("goff")] public double? GroupOffset { get; set; }
    [JsonPropertyName("gms")] public double? MatchShare { get; set; }
    [JsonPropertyName("gds")] public double? DupShare { get; set; }
    [JsonPropertyName("gmix")] public bool? Mixed { get; set; }
    /// <summary>done (step precheck, final): the claim is released and the entry is held for this reason ("ai"): not claimable while control.json aiDramas is off.</summary>
    [JsonPropertyName("hold")] public string? Hold { get; set; }
    [JsonPropertyName("ais")] public double? AiScore { get; set; }
    /// <summary>done (step precheck): the pre-check record (decision, ai flag and score, every number it measured), so the log alone says why the entry was held.</summary>
    [JsonPropertyName("pre")] public System.Text.Json.Nodes.JsonNode? Pre { get; set; }
    /// <summary>0.13.10 seq: the drama's sequence number, assigned inside the conditional append (max + 1 of the state the append is conditional on, so two nodes never get the same one and the numbers only grow). With Seq set by the caller (the bootstrap command) the line carries explicit numbers.</summary>
    [JsonPropertyName("sq")] public long? Seq { get; set; }
    /// <summary>0.13.14 done (group) and join (backfill): the upload the drama's grouping record joins (null: it joins none) and its kind (join | mixed | new). The queue state keeps them (QSubject.JoinOf / JoinKind), so group membership needs no video.json reads.</summary>
    [JsonPropertyName("join")] public string? Join { get; set; }
    [JsonPropertyName("jk")] public string? JoinKind { get; set; }
    /// <summary>0.13.16 "pub" line: sets the subject's publish time (backfill by `jobs sample-first`), and where it came from.</summary>
    [JsonPropertyName("pub")] public string? PubTime { get; set; }
    [JsonPropertyName("pubsrc")] public string? PubSrc { get; set; }

    /// <summary>0.13.26 "enter" (the handoff into the processing queue): what the upload is to processing. "full" = downloaded (process is ready on its own), "precheck" = settled by the check without a download (a catalog and group member only).</summary>
    [JsonPropertyName("data")] public string? Data { get; set; }

    /// <summary>0.13.28 done (actors-index / search-index): the version it published (the queue is the change feed: nodes load the new immutable version once, never poll head.json / head.yaml) and the ETag of the head file it moved.</summary>
    [JsonPropertyName("ver")] public int? Ver { get; set; }
    [JsonPropertyName("ets")] public string? Ets { get; set; }

    /// <summary>0.13.32 done: the version of the step (with Step) or of the stage (without) that just finished; a line without one counts as v1. claim: the steps of a finished process entry this claim re-opens because their recorded version is below the required one (control.json versions).</summary>
    [JsonPropertyName("sv")] public int? StepVer { get; set; }
    [JsonPropertyName("redo")] public string[]? Redo { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, Protocol.Json);
    public static QLine? Parse(string line) { try { return JsonSerializer.Deserialize<QLine>(line, Protocol.Json); } catch (JsonException) { return null; } }

    public static QLine Add(string subject, string stage, JobDescriptor job, string at, int? pri = null, string? due = null, string? state = null, bool force = false) =>
        new() { T = "add", S = subject, Stage = stage, Job = job, At = at, Pri = pri ?? job.Priority, Due = due, State = state, Force = force ? true : null };
}

public sealed class QEntry
{
    public string Stage { get; set; } = "";
    public string State { get; set; } = "pending";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Priority { get; set; }
    public string Added { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Due { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Attempts { get; set; }
    public string? LastError { get; set; }
    public bool? Permanent { get; set; }
    public List<string>? StepsDone { get; set; }
    public string? FinishedAt { get; set; }
    /// <summary>0.13.32: the version the stage finished at (null = v1: lines written before versions).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Ver { get; set; }
    public string? Node { get; set; }
    public JobDescriptor? Job { get; set; }
    /// <summary>Marked for deletion by a pre-check skip ("download.zip/video"), why, and when it was really deleted (by `indexnode trash empty`; null until then).</summary>
    public string? Trash { get; set; }
    public string? TrashWhy { get; set; }
    public string? TrashDeletedAt { get; set; }
    /// <summary>0.13.1: why this entry is held ("ai": the pre-check flagged it AI and control aiDramas is off), and the score. Cleared when the entry finishes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Hold { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? HoldAiScore { get; set; }

    [JsonIgnore] public bool Final => State is "done" or "duplicate" || State == "failed";
    public bool Runnable(string nowUtc) => State == "pending" && string.CompareOrdinal(Due, nowUtc) <= 0;
}

public sealed class QSubject
{
    public string Id { get; set; } = "";
    public Dictionary<string, QEntry> Entries { get; set; } = new();
    /// <summary>The number of the last claim of this subject (the fencing token).</summary>
    public int Epoch { get; set; }
    public string? ClaimBy { get; set; }
    public string[]? ClaimStages { get; set; }
    public string? ClaimAt { get; set; }
    public string? LastAt { get; set; }
    /// <summary>0.13.1: when this subject's centers step last finished (the done line's time): the pre-check searches these centers by brute force until the search index has been built after that time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CentersAt { get; set; }
    /// <summary>0.13.10: the drama's sequence number (null until its first pre-check line, or `indexnode seq assign`).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? Seq { get; set; }
    /// <summary>0.13.14: what the group step's done line (or a backfill "join" line) recorded: the upload this drama's grouping record joins, and the kind (join | mixed | new; null = not recorded yet).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? JoinOf { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? JoinKind { get; set; }
    /// <summary>0.13.16: the upload's publish time (the last add that carried one): the claim order within a priority, newest first, for sample, check, download and process.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Pub { get; set; }
    /// <summary>0.13.26: how the upload entered the processing queue ("full" | "precheck"); only a "full" subject's process stage becomes ready on its own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Data { get; set; }
    /// <summary>0.13.32: the version each step of the process entry last finished at (absent = v1). Kept by the snapshot, also for a finished subject.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, int>? StepVer { get; set; }
    /// <summary>0.13.32: a fully downloaded upload: data is full, or (a subject migrated by queue split carries no data fact) its download is done and it is not a precheck entry (check: duplicate).</summary>
    [JsonIgnore] public bool IsFull => Data == "full" || (Data == null && Entries.GetValueOrDefault("download") is { State: "done" } && Entries.GetValueOrDefault("check") is not { State: "duplicate" });
    /// <summary>0.13.36: the value of <see cref="QueueState.Lines"/> when a line last touched this subject: a change signal (the node does not retry a claim that found nothing runnable until this moves).</summary>
    [JsonIgnore] public long Rev { get; set; }
    public int VerOf(string step) => StepVer != null && StepVer.TryGetValue(step, out var v) ? v : 1;
    [JsonIgnore] public bool Active => Entries.Values.Any(e => !e.Final);
}

/// <summary>The in-memory queue: every subject, built from the snapshot and the log lines after it. Not thread safe by itself; <see cref="JobQueue"/> guards it.</summary>
public sealed class QueueState
{
    public Dictionary<string, QSubject> Subjects { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The order a claim runs a subject's stages in.</summary>
    public static readonly string[] StageOrder = ["sample", "check", "download", "process", "group", "actors", "describe", "translate", "refresh", "classify", "availability", "search-index", "actors-index"];
    public static int Rank(string stage) { var i = Array.IndexOf(StageOrder, stage); return i < 0 ? 99 : i; }

    /// <summary>The highest sequence number handed out so far (kept in the snapshot, so dropping an old subject never lowers it).</summary>
    public long MaxSeq { get; set; }
    /// <summary>0.13.28: newest actor-index / search-index version a done line announced (0: none seen), and the ETag the last "ctl" line announced for control.json.</summary>
    [JsonIgnore] public int ActorsIndexVer { get; set; }
    [JsonIgnore] public int SearchIndexVer { get; set; }
    [JsonIgnore] public string? ControlETag { get; set; }
    /// <summary>How many log lines this node has applied since it loaded (the queue tail's "something changed" counter).</summary>
    [JsonIgnore] public long Lines { get; set; }
    public long NextSeq() => MaxSeq + 1;
    public QSubject? Get(string subject) => Subjects.GetValueOrDefault(subject);
    public QEntry? Entry(string subject, string stage) => Get(subject)?.Entries.GetValueOrDefault(stage);

    public void Apply(QLine l)
    {
        ApplyCore(l);
        if (l.S != null && Subjects.TryGetValue(l.S, out var touched)) touched.Rev = Lines;
    }

    private void ApplyCore(QLine l)
    {
        Lines++;
        if (l.T == "done" && l.Ver is { } pv) { if (l.Stage == "actors-index") ActorsIndexVer = Math.Max(ActorsIndexVer, pv); else if (l.Stage == "search-index") SearchIndexVer = Math.Max(SearchIndexVer, pv); }
        switch (l.T)
        {
            case "ctl": ControlETag = l.Ets; break;   // 0.13.28: control.json changed: the line names its new ETag, so no node polls the file
            case "add":
                {
                    var stage = l.Stage ?? l.Job?.Stage ?? "";
                    if (!Subjects.TryGetValue(l.S, out var sub)) Subjects[l.S] = sub = new QSubject { Id = l.S };
                    sub.Entries.TryGetValue(stage, out var old);
                    if (old != null && l.Force != true) return;   // create-only
                    var at = l.At ?? "";
                    sub.Entries[stage] = new QEntry { Stage = stage, State = l.State ?? "pending", Priority = l.Pri ?? l.Job?.Priority ?? 0, Added = old != null && l.Force == true && old.Added.Length > 0 ? old.Added : at, Due = l.Due ?? at, Job = l.Job, Hold = l.Hold, HoldAiScore = l.Hold != null ? l.AiScore : null };   // 0.13.23: an entry can be added held
                    sub.LastAt = at;
                    if (l.Job?.Pub is { Length: > 0 } pub) sub.Pub = pub;
                    break;
                }
            case "enter":   // 0.13.26: the download side hands an upload to processing; a subject that already exists is left alone (a repeat, or a crash between the two appends repaired)
                {
                    if (Subjects.TryGetValue(l.S, out var have)) { if (have.Data == null && l.Data == "full" && have.Entries.GetValueOrDefault("download") is { State: "done" }) have.Data = "full"; break; }   // an existing subject keeps what it has (only an old downloaded one learns it is full)
                    var sub = Subjects[l.S] = new QSubject { Id = l.S, Data = l.Data ?? "full", LastAt = l.At, Pub = l.PubTime ?? l.Job?.Pub };
                    var at = l.At ?? "";
                    if (sub.Data == "full") sub.Entries["download"] = new QEntry { Stage = "download", State = "done", Priority = l.Pri ?? l.Job?.Priority ?? 0, Added = at, FinishedAt = at, Node = l.By, Job = Slim(l.Job) };
                    else sub.Entries["check"] = new QEntry { Stage = "check", State = l.State ?? "duplicate", Priority = l.Pri ?? 0, Added = at, FinishedAt = at, Node = l.By, Job = Slim(l.Job), Hold = null };
                    if (l.Hold != null) sub.Entries["process"] = new QEntry { Stage = "process", State = "pending", Priority = l.Pri ?? 0, Added = at, Due = at, Hold = l.Hold, HoldAiScore = l.AiScore, Job = JobDescriptor.NewProcess(l.S) };
                    break;
                }
            case "hold":   // 0.13.23 (jobs hold-ai): a pending entry gets the hold
                if (Get(l.S)?.Entries.GetValueOrDefault(l.Stage ?? "") is { Final: false } he && he.State == "pending" && l.Hold != null) { he.Hold = l.Hold; he.HoldAiScore = l.AiScore; }
                else if (l.Hold == null && l.Force == true && Get(l.S)?.Entries.GetValueOrDefault(l.Stage ?? "") is { Final: false } rel) { rel.Hold = null; rel.HoldAiScore = null; }   // 0.13.44 (ai set real): a forced hold line with no hold releases the entry (an older node ignores the line and keeps the hold: it claims nothing it should not)
                break;
            case "pub":
                if (Get(l.S) is { } pubSub && l.PubTime != null) pubSub.Pub = l.PubTime;
                break;
            case "claim":
                {
                    if (!Subjects.TryGetValue(l.S, out var sub)) Subjects[l.S] = sub = new QSubject { Id = l.S };
                    sub.Epoch = l.Epoch ?? sub.Epoch + 1; sub.ClaimBy = l.By; sub.ClaimStages = l.Stages; sub.ClaimAt = l.At; sub.LastAt = l.At;
                    // 0.13.24: a stage that became READY on its own has no entry yet: the claim creates it (a finished actors-index build is replaced by the new one)
                    if (l.Redo is { Length: > 0 } && sub.Entries.TryGetValue("process", out var pe))
                    {
                        var basis = pe.Final ? NodeEngine.StandardSteps.ToList() : (pe.StepsDone ?? []).ToList(); foreach (var rs in l.Redo) basis.Remove(rs); pe.StepsDone = basis;
                        if (pe.Final) { pe.State = "pending"; pe.Attempts = 0; pe.LastError = null; pe.Permanent = null; pe.FinishedAt = null; pe.Due = l.At ?? pe.Due; }
                    }   // 0.13.32: a step below the required version runs again (its repair mode); the other steps stay done
                    foreach (var cs in l.Stages ?? []) if (StageDeps.IsDerived(cs) && (!sub.Entries.TryGetValue(cs, out var ce0) || ((cs == ActorsIndexJobs.Stage || cs == "actors") && ce0.Final))) sub.Entries[cs] = StageDeps.Materialize(sub, cs, l.At ?? "");
                    break;
                }
            case "done":
                {
                    var sub = Get(l.S); if (sub == null) break;
                    if (l.Stage != null && sub.Entries.TryGetValue(l.Stage, out var e))
                    {
                        if (l.Step != null && l.StepVer is { } sv0 && l.Stage == "process") (sub.StepVer ??= new(StringComparer.Ordinal))[l.Step] = sv0;
                        if (l.Step == null && l.StepVer is { } sv1) e.Ver = sv1;
                        if (l.Step != null) { (e.StepsDone ??= []).Remove(l.Step); e.StepsDone.Add(l.Step); if (l.Hold != null) { e.Hold = l.Hold; e.HoldAiScore = l.AiScore; } }
                        else
                        {
                            e.Hold = null; e.HoldAiScore = null;
                            if (l.Trash != null) { e.Trash = l.Trash; e.TrashWhy = l.Why; e.TrashDeletedAt = null; }
                            e.State = l.State ?? "done"; e.FinishedAt = l.At; e.Node = l.By; e.LastError = null; e.Permanent = null; if (l.Job != null) e.Job = l.Job;
                            if (e.State == "pending") { e.Due = l.Due ?? e.Due; e.Attempts = 0; e.StepsDone = null; if (l.Job != null) e.Priority = l.Job.Priority; }   // a source or the classifier: run again later
                            else if (e.State == "done") { var keep = e.Job?.Result; e.Job = Slim(e.Job); if (keep != null && e.Job != null) e.Job.Result = keep; }   // 0.13.26: a finished refresh / classify on the work queue keeps its result until the service folds it
                        }
                    }
                    if (l.Stage == "group" && l.Step == null && l.JoinKind != null) { sub.JoinOf = l.Join; sub.JoinKind = l.JoinKind; }
                    if (l.Step == "centers" && l.Stage == "process") sub.CentersAt = l.At;
                    if (l.Fin == true) EndClaim(sub, l);
                    sub.LastAt = l.At;
                    break;
                }
            case "seq":   // 0.13.10: first writer wins (unless forced by --renumber); a repeated line (a retry after a lost race) never renumbers
                {
                    if (!Subjects.TryGetValue(l.S, out var sub)) Subjects[l.S] = sub = new QSubject { Id = l.S };
                    if (l.Force == true && l.Seq == null) { sub.Seq = null; break; }   // 0.13.12: `seq clear-unstarted`: a forced line with no number clears the drama's number (0.13.11 nodes ignore it, harmlessly); MaxSeq stays, so the next number is still highest + 1
                    if ((sub.Seq == null || l.Force == true) && l.Seq != null) { sub.Seq = l.Seq;   // 0.13.11: force = `seq assign --renumber`
                     if (l.Seq > MaxSeq) MaxSeq = l.Seq.Value; }
                    break;
                }
            case "join":   // 0.13.14 `groups backfill-joins`: the join of a drama grouped before 0.13.14; never overwrites what a group step's done line recorded
                if (Get(l.S) is { JoinKind: null } js && l.JoinKind != null) { js.JoinOf = l.Join; js.JoinKind = l.JoinKind; }
                break;
            case "deleted":   // `indexnode trash empty` removed what a skip had marked
                {
                    if (Entry(l.S, l.Stage ?? "process") is { } te) te.TrashDeletedAt = l.At;
                    if (Get(l.S) is { } ts) ts.LastAt = l.At;
                    break;
                }
            case "failed":
                {
                    var sub = Get(l.S); if (sub == null) break;
                    if (l.Stage != null && sub.Entries.TryGetValue(l.Stage, out var e))
                    {
                        e.State = l.State ?? "failed"; e.Attempts = l.Attempt ?? e.Attempts; e.LastError = l.Error; e.Permanent = l.Permanent; e.Node = l.By;
                        if (l.Due != null) e.Due = l.Due;
                        if (l.Done != null) e.StepsDone = l.Done.ToList();
                        if (l.Job != null) e.Job = l.Job;
                        if (e.State == "failed") e.FinishedAt = l.At;
                    }
                    if (l.Fin == true) EndClaim(sub, l);
                    sub.LastAt = l.At;
                    break;
                }
            case "pri":
                // 0.13.46: making a source due by hand raises its refresh entry to RefreshNowPriority for the claim; that is not the source's priority, so the body keeps its own (its videos inherit the body's)
                if (Get(l.S) is { } ps) foreach (var e in ps.Entries.Values.Where(e => !e.Final)) { e.Priority = l.Pri ?? 0; if (e.Job != null && !(e.Stage == "refresh" && e.Priority == Protocol.RefreshNowPriority)) e.Job.Priority = e.Priority; }
                break;
            case "due":
                if (l.Stage != null && Entry(l.S, l.Stage) is { } de) de.Due = l.Due ?? de.Due;
                break;
            case "retry":
                if (l.Stage != null && Entry(l.S, l.Stage) is { } re) { re.State = "pending"; re.Attempts = 0; re.LastError = null; re.Permanent = null; re.Due = l.Due ?? l.At ?? re.Due; if (l.Job != null) re.Job = l.Job; if (l.Step != null && re.StepsDone != null) re.StepsDone.Remove(l.Step); }
                break;
            case "fixsteps":   // 0.13.2 jobs repair-steps: the entry's done list is corrected to what is on the store (Done = the whole list); state pending re-opens a finished entry
                if (l.Stage != null && Entry(l.S, l.Stage) is { } fe)
                {
                    fe.StepsDone = l.Done?.ToList();
                    if (l.State == "pending") { fe.State = "pending"; fe.Attempts = 0; fe.LastError = null; fe.Permanent = null; fe.FinishedAt = null; fe.Due = l.Due ?? l.At ?? fe.Due; }
                    if (Get(l.S) is { } fs) fs.LastAt = l.At ?? fs.LastAt;
                }
                break;
            case "upd":
                if (l.Stage != null && Entry(l.S, l.Stage) is { } ue) { if (l.Job != null) ue.Job = l.Job; if (l.Pri != null) ue.Priority = l.Pri.Value; if (l.Due != null) ue.Due = l.Due; }
                break;
            case "cls":
                // the classifier's decision on an unclassified download: candidate -> pending, otherwise failed for good
                if (l.Stage != null && Entry(l.S, l.Stage) is { } ce)
                {
                    ce.State = l.State ?? "failed"; if (l.Job != null) ce.Job = l.Job;
                    if (ce.State == "failed") { ce.Permanent = true; ce.LastError = l.Error; ce.FinishedAt = l.At; }
                    else ce.Due = l.Due ?? ce.Due;
                }
                break;
            case "drop":
                if (l.Stage == null) Subjects.Remove(l.S); else if (Get(l.S) is { } ds) { ds.Entries.Remove(l.Stage); if (ds.Entries.Count == 0 && ds.ClaimBy == null) Subjects.Remove(l.S); }
                break;
        }
    }

    private static void EndClaim(QSubject sub, QLine l) { if (l.Epoch == null || l.Epoch == sub.Epoch) { sub.ClaimBy = null; sub.ClaimStages = null; } }

    /// <summary>A finished entry keeps no body in the state (the snapshot stays small): the fields a status line or the site needs survive.</summary>
    private static JobDescriptor? Slim(JobDescriptor? j) => j == null ? null : new JobDescriptor { Stage = j.Stage, JobId = j.JobId, Priority = j.Priority, Title = j.Title, ChannelName = j.ChannelName, Duration = j.Duration, Source = j.Source, Name = j.Name, Kind = j.Kind, Id = j.Id, Network = j.Network, ElapsedSeconds = j.ElapsedSeconds, Pub = j.Pub, PubSource = j.PubSource, Reason = j.Reason is { } rr && rr.StartsWith("resample ", StringComparison.Ordinal) ? rr : null };   // 0.13.84: a re-sample count outlives the sample's finish

    /// <summary>The subjects with something this node can run now, best first: highest priority, then the oldest added entry, then the id. <paramref name="skip"/> leaves out subjects another node holds.</summary>
    public List<(QSubject Subject, List<QEntry> Runnable)> Candidates(string nowUtc, Func<string, bool> stageOk, Func<QSubject, QEntry, bool>? canRun = null, Func<string, bool>? skip = null, ReadyOptions? ready = null)
    {
        var res = new List<(QSubject, List<QEntry>)>();
        foreach (var s in Subjects.Values)
        {
            if (skip != null && skip(s.Id)) continue;
            var r = s.Entries.Values.Where(e => e.Runnable(nowUtc) && stageOk(e.Stage) && (canRun == null || canRun(s, e))).OrderBy(e => Rank(e.Stage)).ToList();
            // 0.13.24: actors is READY when the group step is done (no add line): computed from this in-memory state only
            if (ready != null && s.Data == "full" && stageOk("process") && StageDeps.ReadyEntry(this, s, "process", nowUtc, ready) is { } rpe && (canRun == null || canRun(s, rpe))) r.Add(rpe);
            if (ready != null && s.Id != ActorsIndexJobs.Subject && stageOk("actors") && StageDeps.ReadyEntry(this, s, "actors", nowUtc, ready) is { } rae && (canRun == null || canRun(s, rae))) r.Add(rae);
            if (ready != null && s.IsFull && !r.Any(e => e.Stage == "process") && stageOk("process") && StageDeps.StaleEntry(s, ready) is { } rse && (canRun == null || canRun(s, rse))) r.Add(rse);   // 0.13.32: a finished step below the required version is ready again (its repair mode)
            if (r.Count > 0) res.Add((s, r));
        }
        if (ready != null && stageOk(ActorsIndexJobs.Stage) && !(skip?.Invoke(ActorsIndexJobs.Subject) ?? false) && !res.Any(x => x.Item1.Id == ActorsIndexJobs.Subject))
        {   // the actors-index singleton: ready at N finished uploads not in the index, or when the oldest has waited T
            var ss = Subjects.GetValueOrDefault(ActorsIndexJobs.Subject) ?? new QSubject { Id = ActorsIndexJobs.Subject };
            if (StageDeps.ReadyEntry(this, ss, ActorsIndexJobs.Stage, nowUtc, ready) is { } rie && (canRun == null || canRun(ss, rie))) res.Add((ss, [rie]));
        }
        // 0.13.2: a due search-index entry goes before any drama work (a singleton added today must not wait behind a backlog of older priority-0 entries); then priority, added, id as before
        // 0.13.11: a due group entry goes right behind search-index and before process work: group steps are short, and a group entry added today must not wait behind a backlog of older priority-0 process entries
        return res.OrderByDescending(x => x.Item2.Any(e => e.Stage is "search-index" or "actors-index") ? 3 : x.Item2.Any(e => e.Stage == "group") ? 2 : x.Item2.Any(e => e.Stage == "actors" || StageDeps.IsVersionEntry(e)) ? 1 : 0).ThenBy(x => x.Item2.Any(e => e.Stage is "group" or "actors" || StageDeps.IsVersionEntry(e)) && !x.Item2.Any(e => e.Stage is "search-index" or "actors-index") ? x.Item1.Seq ?? long.MaxValue : 0L)   // 0.13.24: ready actors jobs in grouping seq order, no window   // 0.13.12: group entries lowest sequence number first
            .ThenByDescending(x => x.Item2.Max(e => e.Priority)).ThenByDescending(x => PubKey(x.Item1, x.Item2), StringComparer.Ordinal).ThenBy(x => x.Item2.Min(e => e.Added), StringComparer.Ordinal).ThenBy(x => x.Item1.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>0.13.16: the publish time that orders a candidate within its priority (newest first); "" (last) when the subject has none or has no sample, check, download or process entry to run.</summary>
    static string PubKey(QSubject s, List<QEntry> runnable) =>
        runnable.Any(e => e.Stage is "sample" or "check" or "download" or "process") ? s.Pub ?? runnable.Where(e => e.Stage is "sample" or "check" or "download" or "process").Select(e => e.Job?.Pub).Where(p => !string.IsNullOrEmpty(p)).DefaultIfEmpty("").Max()! : "";

    public Dictionary<string, int> PendingByStage(string nowUtc) =>
        Subjects.Values.SelectMany(s => s.Entries.Values).Where(e => e.State == "pending").GroupBy(e => e.Stage).ToDictionary(g => g.Key, g => g.Count());

    // ---- snapshot
    public sealed class SnapshotDoc
    {
        /// <summary>The format version: 1 = the pre-split layout, 2 = a split layout's queue (a node that does not know the number refuses to read it).</summary>
        public int V { get; set; } = 1;
        public int Seg { get; set; } = 1;
        public long Off { get; set; }
        public int Blocks { get; set; }
        public string At { get; set; } = "";
        public long MaxSeq { get; set; }
        /// <summary>0.13.36: the newest actor-index / search-index version a done line announced, and the ETag the last ctl line announced: kept so a node that starts from the snapshot knows them without reading head.json / control.json again.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ActorsIndexVer { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int SearchIndexVer { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ControlETag { get; set; }
        /// <summary>The segment before Seg and its final length: a reader checks it is sealed at exactly that length before it trusts this snapshot.</summary>
        public List<QSubject> Subjects { get; set; } = [];
    }

    /// <summary>A subject whose entries are all done is kept in the snapshot as just its download entry (the memory of "this video was downloaded" that the availability sweep and the duplicate checks use); one without a download entry is dropped after <see cref="DoneKeep"/>.</summary>
    public static readonly TimeSpan DoneKeep = TimeSpan.FromDays(30);

    /// <summary>The compacted state for the snapshot; the live state is not touched, so every node keeps agreeing with the log.</summary>
    public byte[] ToSnapshot(int seg, long off, int blocks, string at, DateTimeOffset? now = null, int version = 1)
    {
        var cut = Protocol.Utc((now ?? DateTimeOffset.UtcNow) - DoneKeep);
        var list = new List<QSubject>();
        foreach (var s in Subjects.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            bool allDone = s.ClaimBy == null && s.Entries.Values.All(e => e.State is "done" or "duplicate");
            if (!allDone && s.ClaimBy == null && s.Entries.Values.All(e => e.Final) && !s.Entries.Values.Any(e => e.Trash != null && e.TrashDeletedAt == null)) { list.Add(SlimFinished(s)); continue; }   // finished, some failed for good: ids, states, priority and the reason are enough
            if (!allDone) { list.Add(s); continue; }
            if (s.Entries.Values.Any(e => e.Trash != null && e.TrashDeletedAt == null)) { list.Add(s); continue; }   // a trash mark is kept until `trash empty` has acted on it
            // 0.13.35: a finished subject keeps EVERY entry in slim form (state, version, steps done, hold, priority, times, reason): the readiness checks, whatif, the actors predecessor set and the claim path read the process, group and step data of a done subject; dropping them made imported done records vanish at the next rollover
            if (s.Entries.ContainsKey("download") || s.Entries.ContainsKey("check")) list.Add(SlimFinished(s));
            else if (string.CompareOrdinal(s.LastAt ?? "", cut) >= 0) list.Add(s);
        }
        return JsonSerializer.SerializeToUtf8Bytes(new SnapshotDoc { V = version, Seg = seg, Off = off, Blocks = blocks, At = at, MaxSeq = MaxSeq, ActorsIndexVer = ActorsIndexVer, SearchIndexVer = SearchIndexVer, ControlETag = ControlETag, Subjects = list }, Protocol.Json);
    }

    /// <summary>A finished subject as the snapshot needs it: the stage states, priority, when it was added and finished, a cut-down error and a minimal job (id, url, reason, duplicate-of) so a retry or a duplicate check still works. Everything else (due, attempts, node, steps, network, titles, ...) is dropped.</summary>
    static QSubject SlimFinished(QSubject s)
    {
        var o = new QSubject { Id = s.Id, Epoch = s.Epoch, LastAt = s.LastAt, CentersAt = s.CentersAt, Seq = s.Seq, JoinOf = s.JoinOf, JoinKind = s.JoinKind, Pub = s.Pub, Data = s.Data, StepVer = s.StepVer };
        foreach (var (k, e) in s.Entries)
        {
            var j = e.Job; var keepsJob = k is "download" or "sample" or "check" or "refresh" or "classify" or "availability";
            o.Entries[k] = new QEntry
            {
                Stage = e.Stage, State = e.State, Priority = e.Priority, Added = e.Added, FinishedAt = e.FinishedAt, Permanent = e.Permanent, Ver = e.Ver, StepsDone = e.StepsDone, Hold = e.Hold, HoldAiScore = e.HoldAiScore,
                LastError = e.LastError is { Length: > 160 } le ? le[..160] : e.LastError, Trash = e.Trash, TrashWhy = e.TrashWhy, TrashDeletedAt = e.TrashDeletedAt,
                Job = j == null ? null : !keepsJob ? (j.Reason == null && j.Pub == null ? null : new JobDescriptor { Stage = j.Stage, JobId = j.JobId, Reason = j.Reason, Pub = j.Pub }) : new JobDescriptor { Pub = j.Pub, Stage = j.Stage, JobId = j.JobId, Url = j.Url, Reason = j.Reason, DuplicateOf = j.DuplicateOf, DuplicateShare = j.DuplicateShare, Permanent = j.Permanent, MaxHeight = j.MaxHeight, Format = j.Format, Kind = j.Kind, Id = j.Id, Name = j.Name, Every = j.Every, Schedule = j.Schedule, AddedAt = j.AddedAt },
            };
        }
        return o;
    }

    /// <summary>The snapshot is stored gzip-compressed (queue.snapshot.json.gz); a plain JSON one (the first 0.13.0 file) is read as well.</summary>
    public static byte[] Pack(byte[] json) { using var ms = new MemoryStream(); using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(json); return ms.ToArray(); }
    public static byte[] Unpack(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x1f || data[1] != 0x8b) return data;
        using var gz = new System.IO.Compression.GZipStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress); using var o = new MemoryStream(); gz.CopyTo(o); return o.ToArray();
    }

    /// <summary>The newest snapshot format this binary reads.</summary>
    public const int SupportedSnapshotVersion = 2;
    public static (QueueState State, SnapshotDoc Doc) FromSnapshot(byte[] data)
    {
        data = Unpack(data);
        using (var probe = JsonDocument.Parse(data))
        {
            if (probe.RootElement.ValueKind == JsonValueKind.Object && probe.RootElement.TryGetProperty("moved", out var mv)) throw new QueueMovedException(mv.GetString() ?? JobQueue.MovedHint);
            if (probe.RootElement.ValueKind == JsonValueKind.Object && probe.RootElement.TryGetProperty("v", out var vv) && vv.TryGetInt32(out var ver) && ver > SupportedSnapshotVersion) throw new QueueFormatException($"indexnode: the queue snapshot has format version {ver}; this binary reads up to {SupportedSnapshotVersion}. Update indexnode.");
        }
        var doc = JsonSerializer.Deserialize<SnapshotDoc>(data, Protocol.Json) ?? throw new InvalidDataException("empty queue snapshot");
        var st = new QueueState(); foreach (var s in doc.Subjects) { st.Subjects[s.Id] = s; if (s.Seq > st.MaxSeq) st.MaxSeq = s.Seq.Value; } st.MaxSeq = Math.Max(st.MaxSeq, doc.MaxSeq); st.ActorsIndexVer = doc.ActorsIndexVer; st.SearchIndexVer = doc.SearchIndexVer; st.ControlETag = doc.ControlETag;
        return (st, doc);
    }
}
