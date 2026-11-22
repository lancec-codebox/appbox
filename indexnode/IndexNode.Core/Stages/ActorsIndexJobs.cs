namespace IndexNode;

/// <summary>
/// 0.13.23: the singleton queue subject "actors-index" (stage "actors-index"), the twin of <see cref="SearchIndexJobs"/>: the actor index is rebuilt as uploads go through the actors stage, so an upload only brute-forces the predecessors
/// that finished since the last build. An actors run appends "add actors-index" due in <see cref="Delay"/> (in the same append as its done line); an add while one is pending is a no-op. Any processor node claims it (standard, non-AI work);
/// the build is the single writer of the actor index and of actors/state.json.
/// </summary>
public static class ActorsIndexJobs
{
    public const string Subject = "actors-index", Stage = "actors-index";
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(5);

    public static JobDescriptor NewJob(DateTimeOffset now) => new() { Stage = Stage, JobId = Subject, Kind = "sweep", Name = "actors index", AddedAt = Protocol.Utc(now) };

    /// <summary>The add line for the current state, or null when one is pending (or being built). A finished or failed one is added again (force).</summary>
    public static QLine? AddLine(QueueState st, DateTimeOffset now, TimeSpan delay)
    {
        var e = st.Entry(Subject, Stage);
        if (e != null && !e.Final) return null;
        return QLine.Add(Subject, Stage, NewJob(now), Protocol.Utc(now), null, Protocol.Utc(now + delay), null, force: e != null);
    }
}
