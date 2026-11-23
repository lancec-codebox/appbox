using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>Creating, finding and deciding unclassified download entries, and the singleton classify entry (queue subject "classify").</summary>
public static class ClassifyJobs
{
    public const string SingletonId = "all";
    public const string Subject = "classify";

    /// <summary>Makes sure the classify singleton exists on the queue (create-only); one that ended failed or done is made pending again.</summary>
    public static Task EnsureSingletonAsync(JobQueue queue, CancellationToken ct = default, string? now = null)
    {
        var at = now ?? Protocol.NowUtc();
        return queue.AppendLinesAsync(st =>
        {
            var e = st.Entry(Subject, "classify");
            if (e == null) return [QLine.Add(Subject, "classify", new JobDescriptor { Stage = "classify", JobId = SingletonId, Name = "classify all", AddedAt = at }, at)];
            if (e.State is "failed" or "done" && st.Get(Subject)?.ClaimBy == null) return [new QLine { T = "retry", S = Subject, Stage = "classify", Due = at, At = at }];
            return null;
        }, ct);
    }

    /// <summary>Adds the download entry as unclassified (create-only) and makes sure the singleton exists. False when the video was already on the queue.</summary>
    public static async Task<bool> CreateUnclassifiedAsync(JobQueue queue, JobDescriptor d, CancellationToken ct = default)
    {
        bool made = false;
        await queue.AppendLinesAsync(st =>
        {
            made = false;
            if (st.Entry(d.JobId, "download") != null) return null;
            made = true;
            var lines = new List<QLine> { QLine.Add(d.JobId, "download", d, d.AddedAt, d.Priority, null, Tags.Unclassified) };
            var c = st.Entry(Subject, "classify");
            if (c == null) lines.Add(QLine.Add(Subject, "classify", new JobDescriptor { Stage = "classify", JobId = SingletonId, Name = "classify all", AddedAt = d.AddedAt }, d.AddedAt));
            else if (c.State is "failed" or "done" && st.Get(Subject)?.ClaimBy == null) lines.Add(new QLine { T = "retry", S = Subject, Stage = "classify", Due = d.AddedAt, At = d.AddedAt });
            return lines;
        }, ct);
        return made;
    }

    /// <summary>
    /// The batch of <see cref="CreateUnclassifiedAsync"/>: every descriptor not on the queue yet becomes an unclassified download entry, and the singleton is ensured, in ONE append per <see cref="JobQueue.BatchChunk"/> descriptors
    /// (decided against the state as it is; a 412 re-decides the whole chunk). Returns the ids that were made (the rest were already there).
    /// </summary>
    public static async Task<List<string>> CreateUnclassifiedBatchAsync(JobQueue queue, IReadOnlyList<JobDescriptor> ds, CancellationToken ct = default)
    {
        var madeAll = new List<string>();
        foreach (var chunk in JobQueue.Chunks(ds, d => d.ToBytes().Length))
        {
            var made = new List<string>();
            await queue.AppendLinesAsync(st =>
            {
                made.Clear(); var lines = new List<QLine>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in chunk)
                {
                    if (st.Entry(d.JobId, "download") != null || !seen.Add(d.JobId)) continue;
                    made.Add(d.JobId); lines.Add(QLine.Add(d.JobId, "download", d, d.AddedAt, d.Priority, null, Tags.Unclassified));
                }
                if (lines.Count == 0) return null;
                var c = st.Entry(Subject, "classify"); var at = chunk[0].AddedAt;
                if (c == null) lines.Add(QLine.Add(Subject, "classify", new JobDescriptor { Stage = "classify", JobId = SingletonId, Name = "classify all", AddedAt = at }, at));
                else if (c.State is "failed" or "done" && st.Get(Subject)?.ClaimBy == null) lines.Add(new QLine { T = "retry", S = Subject, Stage = "classify", Due = at, At = at });
                return lines;
            }, ct);
            madeAll.AddRange(made);
        }
        return madeAll;
    }

    /// <summary>The unclassified download entries, oldest first (added, then id): read from the queue's memory, no listing.</summary>
    public static List<(string Id, QEntry Entry)> ListUnclassified(JobQueue queue) => ListUnclassified(queue.Read(st => st));
    public static List<(string Id, QEntry Entry)> ListUnclassified(QueueState state) =>
        state.Subjects.Values.Where(s => s.Entries.TryGetValue("download", out var e) && e.State == Tags.Unclassified).Select(s => (Id: s.Id, Entry: s.Entries["download"])).ToList()
            .OrderBy(x => x.Entry.Added, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();

    private static readonly string[] DeadAvailability = ["private", "deleted", "unavailable"];
    private static readonly string[] DeadTitles = ["[Private video]", "[Deleted video]"];

    /// <summary>Private, deleted or unavailable according to the listing, or a flat listing entry with no duration and a placeholder title.</summary>
    public static bool IsUnavailable(JobDescriptor d) =>
        d.Availability != null && DeadAvailability.Contains(d.Availability.Trim().ToLowerInvariant())
        || d.Duration == null && d.Title != null && DeadTitles.Contains(d.Title.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string UnavailableReason(JobDescriptor d) => d.Availability != null && DeadAvailability.Contains(d.Availability.Trim().ToLowerInvariant()) ? $"listed as {d.Availability.Trim().ToLowerInvariant()}" : $"listed as {d.Title}";

    /// <summary>The "cls" line that records a decision: candidate -> pending, otherwise failed + permanent with reason "classify: kind reason".</summary>
    public static QLine Decision(string id, JobDescriptor d, bool candidate, string kind, string reason, string at)
    {
        if (candidate) { d.Classified = null; return new QLine { T = "cls", S = id, Stage = "download", State = "pending", Due = at, At = at, Job = d }; }
        var why = ($"classify: {kind} {reason}").Trim(); if (why.Length > 300) why = why[..300];
        d.Reason = why; d.LastError = why; d.Permanent = true; d.FinishedAt = at;
        return new QLine { T = "cls", S = id, Stage = "download", State = "failed", Error = why, At = at, Job = d };
    }
}
