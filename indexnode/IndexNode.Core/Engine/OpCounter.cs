namespace IndexNode;

/// <summary>0.13.0: storage operations (requests) of this node by kind, so the cost of finding work shows in status: reads, writes and lists per minute over the last five minutes.</summary>
public sealed class OpCounter
{
    public static OpCounter Current { get; } = new();
    public const int WindowMinutes = 5;
    private readonly object gate = new();
    private readonly long[] reads = new long[WindowMinutes], writes = new long[WindowMinutes], lists = new long[WindowMinutes];
    private long bucket;   // minute number of the newest bucket
    private long totalReads, totalWrites, totalLists;
    private readonly Func<DateTimeOffset> now;
    public OpCounter(Func<DateTimeOffset>? clock = null) { now = clock ?? (() => DateTimeOffset.UtcNow); bucket = Minute(); }
    private long Minute() => now().ToUnixTimeSeconds() / 60;

    private void Roll()
    {
        var m = Minute();
        for (long b = bucket + 1; b <= m && b <= bucket + WindowMinutes; b++) { int i = (int)(b % WindowMinutes); reads[i] = writes[i] = lists[i] = 0; }
        bucket = m;
    }

    public void Read() { lock (gate) { Roll(); reads[(int)(bucket % WindowMinutes)]++; totalReads++; } }
    public void Write() { lock (gate) { Roll(); writes[(int)(bucket % WindowMinutes)]++; totalWrites++; } }
    public void List() { lock (gate) { Roll(); lists[(int)(bucket % WindowMinutes)]++; totalLists++; } }

    /// <summary>Per-minute rates over the last five minutes (fewer when the node is younger) and the totals since start.</summary>
    public OpsReport Snapshot(DateTimeOffset? startedAt = null)
    {
        lock (gate)
        {
            Roll();
            double minutes = Math.Clamp(startedAt == null ? WindowMinutes : (now() - startedAt.Value).TotalMinutes, 1, WindowMinutes);
            return new OpsReport { ReadsPerMin = Math.Round(reads.Sum() / minutes, 1), WritesPerMin = Math.Round(writes.Sum() / minutes, 1), ListsPerMin = Math.Round(lists.Sum() / minutes, 1), Reads = totalReads, Writes = totalWrites, Lists = totalLists };
        }
    }
}

public sealed class OpsReport
{
    public double ReadsPerMin { get; set; }
    public double WritesPerMin { get; set; }
    public double ListsPerMin { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    public long Lists { get; set; }
    public override string ToString() => $"{ReadsPerMin:0.#} reads/min, {WritesPerMin:0.#} writes/min, {ListsPerMin:0.#} lists/min";
}

