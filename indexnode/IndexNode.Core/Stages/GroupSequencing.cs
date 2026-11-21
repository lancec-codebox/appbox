namespace IndexNode;

/// <summary>0.13.10: dramas are grouped in sequence order. A drama's group step waits while an earlier drama (lower seq) is still in flight (process or group not finished), so every drama is compared against all earlier ones, whatever order the nodes finish in.</summary>
public static class GroupSequencing
{
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(2);
    public const string Stage = "group";

    /// <summary>
    /// The earlier dramas this one waits for. The group step needs only their VECTORS, not their grouping: a lower-seq drama blocks only while it has NO frames file yet AND its process entry is neither final nor held (it is still on its way to centers).
    /// A lower drama stuck in its own group step, failed, held for AI or already holding frames never blocks, so group steps of a bootstrap run in parallel and one stuck step blocks nobody.
    /// </summary>
    public static async Task<List<string>> BlockersAsync(JobQueue q, IStore store, Paths paths, string id, CancellationToken ct)
    {
        var cands = q.Read(st =>
        {
            var me = st.Get(id)?.Seq; var res = new List<string>(); if (me == null) return res;
            foreach (var s in st.Subjects.Values)
                if (s.Id != id && s.Seq is { } sq && sq < me && s.Entries.TryGetValue("process", out var p) && !p.Final && p.Hold == null) res.Add(s.Id);
            return res;
        });
        var blockers = new List<string>();
        foreach (var c in cands.OrderBy(x => x, StringComparer.Ordinal))
            if (await store.HeadAsync(paths.FramesFile(c), ct) == null) blockers.Add(c);
        return blockers;
    }

    public static JobDescriptor NewJob(string id, int priority) => new() { Stage = Stage, JobId = id, Priority = priority, AddedAt = Protocol.NowUtc() };
}
