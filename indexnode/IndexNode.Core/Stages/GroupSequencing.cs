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

    /// <summary>
    /// 0.13.12: the node-wide decision which group entries can run, kept in memory: the ids with a frames file (listed at start; re-listed at most every <see cref="Relist"/> when a would-be blocker is not in the set; a present id stays present), and L, the lowest sequence number among the dramas whose process entry is
    /// not final, not held and that have no frames file. A group entry is runnable only if its drama has frames and its seq is below L (or there is no L): a blocked one is never claimed and nothing is appended for it.
    /// </summary>
    public sealed class Gate(IStore store, Paths paths)
    {
        public static readonly TimeSpan Relist = TimeSpan.FromMinutes(2), SummaryEvery = TimeSpan.FromMinutes(5);
        readonly HashSet<string> frames = new(StringComparer.Ordinal); DateTimeOffset listedAt = DateTimeOffset.MinValue, summaryAt = DateTimeOffset.MinValue; bool listed;
        public int Lists { get; private set; }

        public sealed record View(HashSet<string> Frames, long? L, string? LId, int Waiting, int Runnable)
        {
            public bool Runnable_(QSubject s) => Frames.Contains(s.Id) && (s.Seq is not { } sq || L is not { } l || sq < l);
        }

        public async Task<View> RefreshAsync(JobQueue q, DateTimeOffset now, CancellationToken ct)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var (blockers, groupIds) = q.Read(st =>
                {
                    var bl = new List<(long Seq, string Id)>(); var gi = new List<string>();
                    foreach (var s in st.Subjects.Values)
                    {
                        if (s.Seq is { } sq && s.Entries.TryGetValue("process", out var p) && !p.Final && p.Hold == null) bl.Add((sq, s.Id));
                        if (s.Entries.TryGetValue("group", out var g) && !g.Final) gi.Add(s.Id);
                    }
                    return (bl, gi);
                });
                bool missing = blockers.Any(b => !frames.Contains(b.Id)) || groupIds.Any(id => !frames.Contains(id));
                if (!listed || (missing && pass == 0 && now - listedAt >= Relist))
                {
                    foreach (var id in await ListFramesAsync(store, paths, ct)) frames.Add(id);   // a present id stays present
                    listed = true; listedAt = now; Lists++; if (pass == 0) continue;
                }
                var low = blockers.Where(b => !frames.Contains(b.Id)).OrderBy(b => b.Seq).Cast<(long Seq, string Id)?>().FirstOrDefault();
                var v = new View(frames, low?.Seq, low?.Id, 0, 0);
                var (w, r) = q.Read(st => { int w = 0, r = 0; foreach (var s in st.Subjects.Values) if (s.Entries.TryGetValue("group", out var g) && g.State == "pending") { if (v.Runnable_(s)) r++; else w++; } return (w, r); });
                return v with { Waiting = w, Runnable = r };
            }
            throw new InvalidOperationException("unreachable");
        }

        /// <summary>"group: N waiting behind seq L (id), M runnable", at most every <see cref="SummaryEvery"/>; null in between.</summary>
        public string? Summary(View v, DateTimeOffset now)
        {
            if (v.Waiting == 0 || now - summaryAt < SummaryEvery) return null;
            summaryAt = now; return $"group: {v.Waiting} waiting behind seq {(v.L?.ToString() ?? "-")} ({v.LId ?? "-"}), {v.Runnable} runnable";
        }
    }

    public static JobDescriptor NewJob(string id, int priority) => new() { Stage = Stage, JobId = id, Priority = priority, AddedAt = Protocol.NowUtc() };
}
