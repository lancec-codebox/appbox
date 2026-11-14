using System.Text.Json.Serialization;

namespace IndexNode;

/// <summary>Bytes moved, by kind.</summary>
public sealed class TrafficCounts
{
    public long YoutubeIn { get; set; }
    public long StorageRead { get; set; }
    public long StorageWrite { get; set; }
    public long AiOut { get; set; }
    public long AiIn { get; set; }
    public long OtherIn { get; set; }

    public void Add(string kind, long n)
    {
        switch (kind)
        {
            case Traffic.YoutubeIn: YoutubeIn += n; break;
            case Traffic.StorageRead: StorageRead += n; break;
            case Traffic.StorageWrite: StorageWrite += n; break;
            case Traffic.AiOut: AiOut += n; break;
            case Traffic.AiIn: AiIn += n; break;
            case Traffic.OtherIn: OtherIn += n; break;
            default: throw new ArgumentException("unknown traffic kind " + kind);
        }
    }

    public void Add(TrafficCounts o) { YoutubeIn += o.YoutubeIn; StorageRead += o.StorageRead; StorageWrite += o.StorageWrite; AiOut += o.AiOut; AiIn += o.AiIn; OtherIn += o.OtherIn; }
    public TrafficCounts Clone() { var c = new TrafficCounts(); c.Add(this); return c; }
}

public sealed class TrafficHour
{
    public string Hour { get; set; } = "";
    public long YoutubeIn { get; set; }
    public long StorageRead { get; set; }
    public long StorageWrite { get; set; }
    public long AiOut { get; set; }
    public long AiIn { get; set; }
    public long OtherIn { get; set; }

    public TrafficCounts Counts() => new() { YoutubeIn = YoutubeIn, StorageRead = StorageRead, StorageWrite = StorageWrite, AiOut = AiOut, AiIn = AiIn, OtherIn = OtherIn };
}

/// <summary>The traffic block of a node record: totals since the node started plus the last 48 hourly buckets.</summary>
public sealed class TrafficReport
{
    public TrafficCounts Totals { get; set; } = new();
    public List<TrafficHour> Hours { get; set; } = new();

    /// <summary>Sum of the buckets that started after now - span (the current partial hour counts).</summary>
    public TrafficCounts Sum(DateTimeOffset now, TimeSpan span)
    {
        var sum = new TrafficCounts();
        foreach (var h in Hours)
            if (DateTimeOffset.TryParse(h.Hour, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) && at > now - span) sum.Add(h.Counts());
        return sum;
    }
}

/// <summary>In-process byte counters for the node's own transfers. Thread safe; the hourly buckets roll over with the clock.</summary>
public sealed class Traffic
{
    public const string YoutubeIn = "youtubeIn", StorageRead = "storageRead", StorageWrite = "storageWrite", AiOut = "aiOut", AiIn = "aiIn", OtherIn = "otherIn";
    public const int KeepHours = 48;

    /// <summary>The counters the node's HTTP clients and stages feed.</summary>
    public static Traffic Current { get; set; } = new();

    private readonly object gate = new();
    private readonly TrafficCounts totals = new();
    private readonly SortedDictionary<DateTimeOffset, TrafficCounts> hours = new();
    private readonly Func<DateTimeOffset> clock;

    public Traffic(Func<DateTimeOffset>? clock = null) { this.clock = clock ?? (() => DateTimeOffset.UtcNow); }

    public static string HourKey(DateTimeOffset t) { var u = t.UtcDateTime; return new DateTime(u.Year, u.Month, u.Day, u.Hour, 0, 0, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH':00Z'"); }

    /// <summary>Seeds the counters from the node's previous registry record, so totals and the hourly buckets keep accumulating across restarts.</summary>
    public void Load(TrafficReport? prev)
    {
        if (prev == null) return;
        lock (gate)
        {
            totals.Add(prev.Totals);
            foreach (var h in prev.Hours)
                if (DateTimeOffset.TryParse(h.Hour, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                {
                    if (!hours.TryGetValue(at, out var b)) hours[at] = b = new TrafficCounts();
                    b.Add(h.Counts());
                }
            Trim(new DateTimeOffset(new DateTime(clock().UtcDateTime.Year, clock().UtcDateTime.Month, clock().UtcDateTime.Day, clock().UtcDateTime.Hour, 0, 0, DateTimeKind.Utc)));
        }
    }

    public void Add(string kind, long bytes)

    {
        if (bytes <= 0) return;
        lock (gate)
        {
            var now = clock();
            var start = new DateTimeOffset(new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, 0, 0, DateTimeKind.Utc));
            totals.Add(kind, bytes);
            if (!hours.TryGetValue(start, out var b)) hours[start] = b = new TrafficCounts();
            b.Add(kind, bytes);
            Trim(start);
        }
    }

    private void Trim(DateTimeOffset current)
    {
        foreach (var k in hours.Keys.Where(k => k <= current.AddHours(-KeepHours)).ToList()) hours.Remove(k);
    }

    public TrafficReport Snapshot()
    {
        lock (gate)
        {
            var now = clock();
            Trim(new DateTimeOffset(new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, 0, 0, DateTimeKind.Utc)));
            return new TrafficReport
            {
                Totals = totals.Clone(),
                Hours = hours.Select(h => { var c = h.Value; return new TrafficHour { Hour = HourKey(h.Key), YoutubeIn = c.YoutubeIn, StorageRead = c.StorageRead, StorageWrite = c.StorageWrite, AiOut = c.AiOut, AiIn = c.AiIn, OtherIn = c.OtherIn }; }).ToList(),
            };
        }
    }
}
