using System.Text.Json;

namespace IndexNode;

/// <summary>
/// A small 5-field cron expression (minute hour day-of-month month day-of-week; *, lists, ranges, steps; 0 or 7 is Sunday), evaluated in local
/// time. When both day fields are restricted a day matches if either does, as in Vixie cron. Own code, no package.
/// </summary>
public sealed class CronExpr
{
    private readonly bool[] min = new bool[60], hour = new bool[24], dom = new bool[32], mon = new bool[13], dow = new bool[7];
    private bool domStar, dowStar;
    public string Text { get; }

    private CronExpr(string text) { Text = text; }

    public static CronExpr Parse(string text)
    {
        var f = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) throw new ArgumentException($"schedule '{text}' needs 5 fields: minute hour day-of-month month day-of-week");
        var c = new CronExpr(string.Join(' ', f));
        Fill(f[0], c.min, 0, 59, "minute");
        Fill(f[1], c.hour, 0, 23, "hour");
        Fill(f[2], c.dom, 1, 31, "day-of-month");
        Fill(f[3], c.mon, 1, 12, "month");
        var d = new bool[8];
        Fill(f[4], d, 0, 7, "day-of-week");
        for (int i = 0; i < 7; i++) c.dow[i] = d[i] || (i == 0 && d[7]);
        c.domStar = f[2].StartsWith('*'); c.dowStar = f[4].StartsWith('*');
        return c;
    }

    private static void Fill(string field, bool[] set, int lo, int hi, string name)
    {
        foreach (var part in field.Split(','))
        {
            var step = 1; var range = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0) throw new ArgumentException($"bad step in {name} field '{field}'");
                range = part[..slash];
            }
            int a, b;
            if (range == "*") { a = lo; b = hi; }
            else if (range.Contains('-'))
            {
                var ab = range.Split('-');
                if (ab.Length != 2 || !int.TryParse(ab[0], out a) || !int.TryParse(ab[1], out b)) throw new ArgumentException($"bad range in {name} field '{field}'");
            }
            else
            {
                if (!int.TryParse(range, out a)) throw new ArgumentException($"bad value in {name} field '{field}'");
                b = slash >= 0 ? hi : a;
            }
            if (a < lo || b > hi || a > b) throw new ArgumentException($"{name} field '{field}' is out of range {lo}-{hi}");
            for (int i = a; i <= b; i += step) set[i] = true;
        }
    }

    public bool Matches(DateTime t)
    {
        if (!min[t.Minute] || !hour[t.Hour] || !mon[t.Month]) return false;
        bool d1 = dom[t.Day], d2 = dow[(int)t.DayOfWeek];
        return domStar || dowStar ? d1 && d2 : d1 || d2;
    }

    /// <summary>The latest cron time at or before <paramref name="now"/> that is less than <paramref name="window"/> ago; null when now is outside every window.</summary>
    public DateTime? WindowStart(DateTime now, TimeSpan window)
    {
        var t = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        for (var k = 0; k <= window.TotalMinutes + 1; k++)
        {
            var c = t.AddMinutes(-k);
            if (now - c >= window) return null;
            if (Matches(c)) return c;
        }
        return null;
    }

    /// <summary>The first cron time strictly after <paramref name="after"/> (searches a year ahead).</summary>
    public DateTime? Next(DateTime after)
    {
        var t = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind).AddMinutes(1);
        for (var i = 0; i < 366 * 24 * 60; i++, t = t.AddMinutes(1))
            if (Matches(t)) return t;
        return null;
    }
}

/// <summary>
/// The node's run limits: <c>budget</c> (jobs it may still finish), and a <c>schedule</c> + <c>window</c> (claim new jobs only inside a window).
/// Local settings (flags, config) are overridden per field by control.json → nodes.&lt;nodeId&gt;. The budget counts down from when it was
/// set; the remaining count lives in a local state file and restarts when the effective value or the override's changedAt changes.
/// Polling a followed source (<c>watch</c>) is not a job that counts against the budget and keeps running when it is spent.
/// </summary>
public sealed class LimitState
{
    private readonly object gate = new();
    private readonly string? statePath;
    private readonly Func<DateTime> localNow;
    private readonly string nodeId;
    private readonly NodeLimits local;
    private readonly string localStamp;
    private string? key;
    private int? budget;
    private int remaining, inflight;
    private CronExpr? cron;
    private TimeSpan window = TimeSpan.FromHours(1);

    public LimitState(string nodeId, NodeLimits local, string localStamp, string? statePath, Func<DateTime>? localNow = null)
    {
        this.nodeId = nodeId; this.local = local; this.localStamp = localStamp; this.statePath = statePath;
        this.localNow = localNow ?? (() => DateTime.Now);
        Apply(null);
    }

    public int? Budget { get { lock (gate) return budget; } }
    public int Remaining { get { lock (gate) return remaining; } }

    /// <summary>Resolves the effective limits (an override in control.json wins per field) and restarts the countdown when its key changed.</summary>
    public void Apply(ControlFile? control)
    {
        lock (gate)
        {
            NodeLimits? o = null;
            control?.Nodes?.TryGetValue(nodeId, out o);
            int? b = o?.Budget ?? local.Budget;
            var newKey = b == null ? null : o?.Budget != null ? $"control|{b}|{control?.ChangedAt}" : $"local|{b}|{localStamp}";
            var sched = o?.Schedule ?? local.Schedule;
            var win = o?.Window ?? local.Window;
            try { cron = string.IsNullOrWhiteSpace(sched) ? null : CronExpr.Parse(sched); } catch (ArgumentException e) { Log.Info("limits: " + e.Message + "; ignoring the schedule"); cron = null; }
            try { window = WatchStage.ParseEvery(win ?? "1h"); } catch (ArgumentException e) { Log.Info("limits: window: " + e.Message + "; using 1h"); window = TimeSpan.FromHours(1); }
            if (newKey == key && budget == b) return;
            budget = b; key = newKey;
            if (b == null) { remaining = 0; return; }
            var saved = Load();
            remaining = saved is { } s && s.Key == newKey ? Math.Min(s.Remaining, b.Value) : b.Value;
            Save();
        }
    }

    private (string Key, int Remaining)? Load()
    {
        try
        {
            if (statePath == null || !File.Exists(statePath)) return null;
            var d = JsonDocument.Parse(File.ReadAllText(statePath)).RootElement;
            return (d.GetProperty("budgetKey").GetString() ?? "", d.GetProperty("budgetRemaining").GetInt32());
        }
        catch { return null; }
    }

    private void Save()
    {
        if (statePath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath, JsonSerializer.Serialize(new { budgetKey = key, budgetRemaining = remaining }));
        }
        catch (Exception e) { Log.Info("limits: could not save the budget state (" + e.Message + ")"); }
    }

    private bool InWindow(out DateTime? next)
    {
        next = null;
        if (cron == null) return true;
        var now = localNow();
        if (cron.WindowStart(now, window) != null) return true;
        next = cron.Next(now);
        return false;
    }

    /// <summary>True when a new job of this stage may be claimed now; counts it as in flight (give it back with <see cref="Release"/>).</summary>
    public bool TryAcquire(string stage)
    {
        lock (gate)
        {
            if (!InWindow(out _)) return false;
            if (stage == "watch" || budget == null) return true;
            if (remaining - inflight <= 0) return false;
            inflight++;
            return true;
        }
    }

    /// <summary>A claim attempt ended: <paramref name="finished"/> when the job was completed (that is what the budget counts).</summary>
    public void Release(string stage, bool finished)
    {
        lock (gate)
        {
            if (stage == "watch" || budget == null) return;
            if (inflight > 0) inflight--;
            if (finished && remaining > 0) { remaining--; Save(); }
        }
    }

    /// <summary>The registry state this limit forces on an otherwise idle node, or null.</summary>
    public string? Status()
    {
        lock (gate)
        {
            if (budget != null && remaining <= 0) return "budget spent";
            if (!InWindow(out var next)) return next is { } n ? "next window " + Protocol.Utc(new DateTimeOffset(DateTime.SpecifyKind(n, DateTimeKind.Local))) : "outside window";
            return null;
        }
    }
}
