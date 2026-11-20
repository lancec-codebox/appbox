namespace IndexNode;

/// <summary>
/// 0.13.1: the singleton queue subject "search-index" (stage "search-index"): the global search index is rebuilt as we go. A process claim that wrote a new centers.zip appends
/// "add search-index" due in <see cref="Delay"/> (in the same append as the step's done line); an add while one is pending is a no-op. Any processor node claims it (standard, non-AI work).
/// </summary>
public static class SearchIndexJobs
{
    public const string Subject = "search-index", Stage = "search-index";
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(15);

    public static JobDescriptor NewJob(DateTimeOffset now) => new() { Stage = Stage, JobId = Subject, Kind = "sweep", Name = "search index", AddedAt = Protocol.Utc(now) };

    /// <summary>The subjects whose process centers step finished after the index build time (all that have a centers time when the build time is unknown), oldest first.</summary>
    public static IReadOnlyList<string> FreshUploads(JobQueue q, DateTimeOffset? builtAt)
    {
        var cut = builtAt == null ? "" : Protocol.Utc(builtAt.Value);
        return q.Read(st => st.Subjects.Values.Where(s => s.CentersAt != null && string.CompareOrdinal(s.CentersAt, cut) > 0).OrderBy(s => s.CentersAt, StringComparer.Ordinal).Select(s => s.Id).ToList());
    }

    /// <summary>The add line for the current state, or null when one is pending (or being built): a pending entry is never added twice. A finished or failed one is added again (force).</summary>
    public static QLine? AddLine(QueueState st, DateTimeOffset now, TimeSpan delay)
    {
        var e = st.Entry(Subject, Stage);
        if (e != null && !e.Final) return null;
        return QLine.Add(Subject, Stage, NewJob(now), Protocol.Utc(now), null, Protocol.Utc(now + delay), null, force: e != null);
    }
}
