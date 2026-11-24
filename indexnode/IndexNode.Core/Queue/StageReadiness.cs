using System.Globalization;

namespace IndexNode;

/// <summary>0.13.24 settings of the readiness check (config actors.index-min-uploads / actors.index-max-wait-minutes).</summary>
public sealed record ReadyOptions(int IndexMinUploads = 25, int IndexMaxWaitMinutes = 10)
{
    /// <summary>0.13.37 run --repair-only: a claim takes only the stale versioned-step repairs and actors / actors-index; never pending or missing process work (download, process steps, group...).</summary>
    public bool RepairOnly { get; init; }
    /// <summary>0.13.32: the version control.json requires of a step or stage (0 / 1 = none) and the version this node's code can make.</summary>
    public Func<string, int> Required { get; init; } = _ => 0;
    public Func<string, int> Code { get; init; } = StepVersions.Code;
    public TimeSpan IndexMaxWait => TimeSpan.FromMinutes(Math.Max(0, IndexMaxWaitMinutes));
}

/// <summary>
/// 0.13.24: stages that become READY on their own. A stage declares what it needs (<see cref="Needs"/>); for a subject it is ready when everything it needs is done, its own entry does not exist yet (an existing entry is the ordinary
/// path: pending, due, held, final), no hold is on it, the node has the stage on, and no live lease holds the subject (the caller's skip set). Nobody appends "add actors": the claim itself creates the entry (QueueState.Apply "claim").
/// Everything here is computed from the in-memory state the node already holds: no store reads. The other chains (download -> process, sample -> check, ...) can be put in this table later; only actors and actors-index are in it now.
/// </summary>
public static class StageDeps
{
    /// <summary>stage -> the stages of the same subject that must be done first.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Needs = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["actors"] = ["group", "cast"] };   // 0.13.32: (stage or step, minimum version) below
    /// <summary>0.13.32: stage -> (stage or step of the same subject, the version it must have finished at). actors needs its group step (any version) and the process subject's cast step at v2 (the repaired cast state).</summary>
    public static readonly IReadOnlyDictionary<string, (string Dep, int Min)[]> VersionedNeeds = new Dictionary<string, (string, int)[]>(StringComparer.Ordinal) { ["actors"] = [("group", 1), ("cast", StepVersions.CastRepaired)] };
    public static readonly string[] Derived = ["process", "actors", ActorsIndexJobs.Stage];

    public static bool IsDerived(string stage) => Array.IndexOf(Derived, stage) >= 0;

    /// <summary>An actors entry with a hold (the AI hold, put where the flag is known: the group step's done / the import) is never ready; a manual "actors add" replaces the entry and so releases it.</summary>
    public static bool Held(QEntry e) => e.Stage == "actors" && e.Hold != null;

    /// <summary>A dependency is met when the stage (an entry) or the step (of the process entry) is done AND finished at the minimum version; a recorded line without a version is v1.</summary>
    public static bool NeedMet(QSubject s, string dep, int min)
    {
        if (Array.IndexOf(StepVersions.Steps, dep) >= 0 || NodeEngine.StandardSteps.Contains(dep))
            return s.Entries.TryGetValue("process", out var pe) && (pe.State == "done" || pe.StepsDone?.Contains(dep) == true) && s.VerOf(dep) >= min;
        return s.Entries.TryGetValue(dep, out var e) && e.State == "done" && (e.Ver ?? 1) >= min;
    }
    static bool NeedsDone(QSubject s, string stage) => VersionedNeeds.TryGetValue(stage, out var n) && n.All(x => NeedMet(s, x.Dep, x.Min));

    /// <summary>0.13.32: the versioned steps of a FINISHED process entry whose recorded version is below the one control requires and this node's code can make: ready again (their repair mode), nothing is queued by hand. Not for an AI-held or not fully downloaded upload.</summary>
    public static List<string> StaleSteps(QSubject s, ReadyOptions ro)
    {
        var res = new List<string>();
        if (!s.IsFull || s.Entries.GetValueOrDefault("process") is not { State: "done" } || s.Entries.Values.Any(e => e.Hold == "ai")) return res;
        if (s.Entries.GetValueOrDefault("group") is not { State: "done" }) return res;   // the cast repair needs the grouping record (a join uses only the faces on its retained frames)
        foreach (var step in StepVersions.Steps) { int req = ro.Required(step); if (req > 1 && s.VerOf(step) < req && ro.Code(step) >= req) res.Add(step); }
        return res;
    }

    /// <summary>0.13.36: the stages a claim of <paramref name="s"/> would run, in order (null = nothing). The ONE rule the node's claim and `queue whatif` share: a subject the ready view offers is never one the claim then finds empty. <paramref name="stageOk"/> / <paramref name="canRun"/> are the node's stage switch and entry gate.</summary>
    public static string[]? ClaimStages(QueueState state, QSubject s, string now, Func<string, bool> stageOk, Func<QSubject, QEntry, bool> canRun, ReadyOptions ro)
    {
        var st = s.Entries.Values.Where(e => e.Runnable(now) && stageOk(e.Stage) && canRun(s, e) && (!ro.RepairOnly || RepairStage(e.Stage))).Select(e => e.Stage).ToList();
        foreach (var d in Derived.Where(d => !ro.RepairOnly || RepairStage(d) && d != "process"))   // 0.13.24: a stage that is ready on its own (no entry yet) is claimed too; the claim line creates its entry
            if (!st.Contains(d) && stageOk(d) && ReadyEntry(state, s, d, now, ro) is { } re && canRun(s, re)) st.Add(d);
        if (!st.Contains("process") && s.IsFull && stageOk("process") && StaleEntry(s, ro) is { } sre && canRun(s, sre)) st.Add("process");   // 0.13.32: a step below the required version is ready again (0.13.36: IsFull, as the ready view: an upload with no data fact but a done download is repaired too)
        return st.Count == 0 ? null : st.OrderBy(QueueState.Rank).ToArray();
    }

    /// <summary>0.13.37: the stages a --repair-only node may claim (process only through the stale-step path).</summary>
    public static bool RepairStage(string stage) => stage is "actors" or ActorsIndexJobs.Stage;

    public const string VersionReason = "version";
    public static bool IsVersionEntry(QEntry e) => e.Stage == "process" && e.State == "pending" && e.Job?.Reason == VersionReason;
    /// <summary>The process entry a claim would re-open for the stale steps of <paramref name="s"/> (not stored: the claim line's Redo creates the real change), or null.</summary>
    public static QEntry? StaleEntry(QSubject s, ReadyOptions ro)
    {
        var stale = StaleSteps(s, ro); if (stale.Count == 0 || s.Entries.GetValueOrDefault("process") is not { } pe) return null;
        return new QEntry { Stage = "process", State = "pending", Priority = pe.Priority, Added = pe.Added, Due = "", StepsDone = NodeEngine.StandardSteps.Where(x => !stale.Contains(x)).ToList(), Job = new JobDescriptor { Stage = "process", JobId = s.Id, Priority = pe.Priority, Reason = VersionReason } };
    }

    /// <summary>The entry a claim would create for <paramref name="stage"/> on <paramref name="s"/>, or null when the stage is not ready there.</summary>
    public static QEntry? ReadyEntry(QueueState st, QSubject s, string stage, string nowUtc, ReadyOptions ro)
    {
        if (stage == "process")
        {   // 0.13.26: an upload that entered the processing queue as "full" (downloaded) has its process stage ready on its own: no "add process" line (the download no longer appends one)
            if (s.Data != "full" || s.Entries.ContainsKey("process") || s.Entries.GetValueOrDefault("download") is not { State: "done" } dl) return null;
            return MakeProcess(s, dl, "");
        }
        if (stage == "actors")
        {
            if (s.Id == ActorsIndexJobs.Subject || !NeedsDone(s, "actors")) return null;
            if (s.Entries.TryGetValue("actors", out var ae0) && !(ae0.State == "done" && (ae0.Ver ?? 1) < ro.Required("actors") && ro.Code("actors") >= ro.Required("actors"))) return null;   // 0.13.32: an existing entry is the ordinary path, unless it finished before the repaired state (v1) and its cast is repaired now: it runs again, on the repaired units
            return Make("actors", s.Id, s.Entries.GetValueOrDefault("group")?.Priority ?? 0, "");
        }
        if (stage == ActorsIndexJobs.Stage && s.Id == ActorsIndexJobs.Subject)
        {
            var e = s.Entries.GetValueOrDefault(ActorsIndexJobs.Stage); var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            if (e != null && !(e.State == "done" || (e.State == "failed" && e.FinishedAt != null && DateTimeOffset.Parse(e.FinishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) + TimeSpan.FromMinutes(5) <= now))) return null;   // pending: the ordinary path
            var (n, oldest) = Unindexed(st, e is { State: "done" } ? e.Added : "");
            if (n == 0 || oldest == null) return null;
            bool ready = n >= Math.Max(1, ro.IndexMinUploads) || DateTimeOffset.Parse(oldest, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) + ro.IndexMaxWait <= now;
            return ready ? new QEntry { Stage = stage, State = "pending", Due = "", Added = "", Job = ActorsIndexJobs.NewJob(now) } : null;
        }
        return null;
    }

    /// <summary>How many finished actors uploads are newer than the cut (the claim time of the last finished index build; "" = none yet), and the oldest finish time among them.</summary>
    public static (int Count, string? Oldest) Unindexed(QueueState st, string cut)
    {
        int n = 0; string? oldest = null;
        foreach (var s in st.Subjects.Values)
            if (s.Entries.TryGetValue("actors", out var a) && a.State == "done" && a.FinishedAt is { Length: > 0 } f && string.CompareOrdinal(f, cut) > 0) { n++; if (oldest == null || string.CompareOrdinal(f, oldest) < 0) oldest = f; }
        return (n, oldest);
    }

    static QEntry MakeProcess(QSubject s, QEntry dl, string at)
    {
        var p = JobDescriptor.NewProcess(s.Id); p.Steps = null; p.Priority = dl.Priority; p.AddedAt = at.Length > 0 ? at : p.AddedAt; p.Pub = s.Pub ?? dl.Job?.Pub;
        return new QEntry { Stage = "process", State = "pending", Priority = dl.Priority, Due = "", Added = at, Job = p };
    }

    static QEntry Make(string stage, string subject, int pri, string at) => new() { Stage = stage, State = "pending", Priority = pri, Due = "", Added = at, Job = new JobDescriptor { Stage = stage, JobId = subject, Priority = pri, AddedAt = at } };

    /// <summary>The entry a claim line creates for a derived stage (same for every node reading the log).</summary>
    public static QEntry Materialize(QSubject s, string stage, string at)
        => stage == "process" ? MakeProcess(s, s.Entries["download"], at) : stage == ActorsIndexJobs.Stage ? new QEntry { Stage = stage, State = "pending", Due = "", Added = at, Job = ActorsIndexJobs.NewJob(DateTimeOffset.Parse(at.Length > 0 ? at : Protocol.NowUtc(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)) }
        : Make(stage, s.Id, s.Entries.GetValueOrDefault("group")?.Priority ?? 0, at);
}
