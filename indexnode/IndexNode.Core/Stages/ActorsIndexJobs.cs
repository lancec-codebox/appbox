namespace IndexNode;

/// <summary>
/// 0.13.24: the singleton queue subject "actors-index" (stage "actors-index"). It is READY on its own (see <see cref="StageDeps"/>): when at least N finished actors uploads are not in the index yet (default 25) or the oldest of them has waited T (default 10 minutes),
/// so a tail is never left. A node takes the singleton's lease, builds the next version and moves the head; the build is the single writer of actors. No add line, no debounce.
/// </summary>
public static class ActorsIndexJobs
{
    public const string Subject = "actors-index", Stage = "actors-index";
    public static JobDescriptor NewJob(DateTimeOffset now) => new() { Stage = Stage, JobId = Subject, Kind = "sweep", Name = "actors index", AddedAt = Protocol.Utc(now) };
}
