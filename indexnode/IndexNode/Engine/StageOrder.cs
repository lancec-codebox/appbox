namespace IndexNode;

/// <summary>Splits a node's CPU threads between its workers: each worker uses only its own share.</summary>
public static class ThreadSplit
{
    public static int PerWorker(int threads, int workers, int? perWorker) => perWorker is > 0 ? perWorker.Value : Math.Max(1, Math.Max(1, threads) / Math.Max(1, workers));
}

/// <summary>A drama with pending CPU step jobs: its sort key (highest priority, oldest pending step) and its pending step jobs by stage.</summary>
public sealed record DramaPick(string Id, BlobEntry Key, Dictionary<string, BlobEntry> Members);

/// <summary>What a worker looks at first. Stages outside the drama pass: the stage whose best claimable job comes first in one queue order (idx_priority descending, idx_added, name). CPU step stages: whole dramas, ordered by their highest priority and oldest pending step.</summary>
public static class StageOrder
{
    public const int MaxExamined = 20;

    /// <summary>The CPU steps a worker runs on one drama in one pass, in this order (only those the node runs and that are pending).</summary>
    public static readonly string[] PassOrder = ["frames", "sscd", "thumbs", "transcribe", "ocrsubs", "cast", "sync"];

    public static async Task<List<(string Stage, BlobEntry Head)>> HeadsAsync(IEnumerable<string> stages, Func<string, CancellationToken, Task<IReadOnlyList<BlobEntry>>> pending,
        Func<string, BlobEntry, CancellationToken, Task<bool>> eligible, CancellationToken ct)
    {
        var heads = new Dictionary<string, string>(); var entries = new List<BlobEntry>();
        foreach (var stage in stages)
        {
            try
            {
                foreach (var b in Tags.ClaimOrder(await pending(stage, ct)).Take(MaxExamined))
                    if (await eligible(stage, b, ct)) { heads[b.Name] = stage; entries.Add(b); break; }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* a stage whose queue cannot be read now does not hold the others back */ }
        }
        return Tags.ClaimOrder(entries).Select(b => (heads[b.Name], b)).ToList();
    }

    public static async Task<List<string>> RankAsync(IEnumerable<string> stages, Func<string, CancellationToken, Task<IReadOnlyList<BlobEntry>>> pending,
        Func<string, BlobEntry, CancellationToken, Task<bool>> eligible, CancellationToken ct) =>
        (await HeadsAsync(stages, pending, eligible, ct)).Select(x => x.Stage).ToList();

    /// <summary>The dramas (at most <paramref name="max"/>) with a claimable pending step in <paramref name="stages"/>, best first: priority descending, then the oldest pending step's idx_added, then id.</summary>
    public static async Task<List<DramaPick>> DramasAsync(IEnumerable<string> stages, Func<string, CancellationToken, Task<IReadOnlyList<BlobEntry>>> pending,
        Func<string, BlobEntry, CancellationToken, Task<bool>> eligible, CancellationToken ct, int max = 5)
    {
        var groups = new Dictionary<string, Dictionary<string, BlobEntry>>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            try
            {
                foreach (var b in await pending(stage, ct))
                {
                    var id = Path.GetFileNameWithoutExtension(b.Name);
                    if (!groups.TryGetValue(id, out var g)) groups[id] = g = new Dictionary<string, BlobEntry>();
                    g[stage] = b;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        var picks = groups.Select(g => new DramaPick(g.Key, new BlobEntry(g.Key,
            Tags.Job("pass", "pending", g.Value.Values.Select(b => b.Tags.GetValueOrDefault(Tags.Added) ?? "").OrderBy(x => x, StringComparer.Ordinal).First(), null, g.Value.Values.Max(b => Tags.PriorityOf(b.Tags))),
            new Dictionary<string, string>(), "available", 0), g.Value)).ToList();
        var result = new List<DramaPick>();
        foreach (var key in Tags.ClaimOrder(picks.Select(p => p.Key)).Take(MaxExamined))
        {
            var pick = picks.First(p => ReferenceEquals(p.Key, key));
            foreach (var (stage, b) in pick.Members) if (await eligible(stage, b, ct)) { result.Add(pick); break; }
            if (result.Count >= max) break;
        }
        return result;
    }
}
