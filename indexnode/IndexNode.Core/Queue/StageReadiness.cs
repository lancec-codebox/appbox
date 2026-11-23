using System.Globalization;

namespace IndexNode;

/// <summary>0.13.24 settings of the readiness check (config actors.index-min-uploads / actors.index-max-wait-minutes).</summary>
public sealed record ReadyOptions(int IndexMinUploads = 25, int IndexMaxWaitMinutes = 10)
{
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
    public static readonly IReadOnlyDictionary<string, string[]> Needs = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["actors"] = ["group"] };
    public static readonly string[] Derived = ["process", "actors", ActorsIndexJobs.Stage];

    public static bool IsDerived(string stage) => Array.IndexOf(Derived, stage) >= 0;

    /// <summary>An actors entry with a hold (the AI hold, put where the flag is known: the group step's done / the import) is never ready; a manual "actors add" replaces the entry and so releases it.</summary>
    public static bool Held(QEntry e) => e.Stage == "actors" && e.Hold != null;

    static bool NeedsDone(QSubject s, string stage) => Needs.TryGetValue(stage, out var n) && n.All(x => s.Entries.TryGetValue(x, out var e) && e.State == "done");

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
            if (s.Id == ActorsIndexJobs.Subject || s.Entries.ContainsKey("actors") || !NeedsDone(s, "actors")) return null;
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
