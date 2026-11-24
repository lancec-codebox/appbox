namespace IndexNode;

/// <summary>
/// 0.13.43 (owner: "the group should never have to wait"): a drama gets its sequence number when its group step STARTS, as that step's first action, and only once its vectors exist.
/// Its predecessors are the dramas with a lower number. Each of them was numbered at its own group step, so each has its vectors already: there is nothing to wait for, whatever order the nodes finish in.
/// A drama that is processed but has not started its group step has no number: later group steps do not see it, and when its own step starts it gets a higher number and sees them. Nothing points forward, so there are no cycles.
/// (0.13.10 to 0.13.42 numbered a drama when its pre-check started and made later group steps wait for it; one drama that was started and then set aside held up every group step after it.)
/// </summary>
public static class GroupSequencing
{
    public const string Stage = "group";

    /// <summary>
    /// Is <paramref name="other"/> a predecessor of the drama numbered <paramref name="seq"/>? With a number: a lower one. Without a number: only a drama from before numbering
    /// (nothing of it is in flight: no unfinished process entry, and its group entry is absent or done). A drama still on its way to its own group step is NOT a predecessor: it will be numbered above this one and see it.
    /// A drama the queue does not know (only the index has it) is from before the queue, so it is a predecessor.
    /// </summary>
    public static bool IsEarlier(QSubject? other, long seq)
    {
        if (other == null) return true;
        if (other.Seq is { } o) return o < seq;
        if (other.Entries.TryGetValue("process", out var p) && !p.Final) return false;
        if (other.Entries.TryGetValue(Stage, out var g) && g.State is not ("done" or "duplicate")) return false;
        return true;
    }

    /// <summary>
    /// 0.13.12: is a drama STARTED? It has a frames file, or its process entry has any steps done or is done, or it is claimed right now, or its video.json has precheck.at. Only started dramas carry a sequence number: an unstarted one (downloaded, process pending) gets its number when its pre-check starts.
    /// This is the part decided by the queue alone; the frames file and video.json are the caller's (they are in the store).
    /// </summary>
    public static bool StartedInQueue(QSubject s) =>
        s.ClaimBy != null && s.Entries.Values.Any(e => !e.Final && s.ClaimStages != null) ||
        s.Entries.TryGetValue("process", out var p) && (p.State == "done" || p.StepsDone is { Count: > 0 });

    /// <summary>The ids that have a frames file (one list of index/search/frames/).</summary>
    public static async Task<HashSet<string>> ListFramesAsync(IStore store, Paths paths, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal); var fp = paths.Prefix + "search/frames/";
        foreach (var b in await store.ListAsync(fp, false, false, ct)) if (b.Name.EndsWith(".bin", StringComparison.Ordinal)) { var id = b.Name[fp.Length..^4]; if (id.Length > 0 && !id.Contains('/')) set.Add(id); }
        return set;
    }

    public static JobDescriptor NewJob(string id, int priority) => new() { Stage = Stage, JobId = id, Priority = priority, AddedAt = Protocol.NowUtc() };
}
