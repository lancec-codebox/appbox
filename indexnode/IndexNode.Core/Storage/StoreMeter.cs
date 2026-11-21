using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace IndexNode;

/// <summary>0.13.18: thrown by a store call when the storage budget is used up (the node's breaker is open, or the step's own budget is spent). The step fails as a normal failure and is retried later.</summary>
public sealed class StorageBudgetExceeded(string message) : InvalidOperationException(message);

/// <summary>0.13.18: one line of the per-site table.</summary>
public sealed record SiteRow(string Site, string Op, long Count, long Failed, string? Example);

/// <summary>0.13.18 settings (config storage.*). Defaults: warn 3,000 / max 10,000 per minute, 20,000 per step, 300 failures per minute, 5 minutes cooldown.</summary>
public sealed class StorageLimits
{
    public int WarnPerMin { get; set; } = 3000;
    public int MaxPerMin { get; set; } = 10000;
    public int MaxPerStep { get; set; } = 20000;
    public int MaxFailuresPerMin { get; set; } = 300;
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>0.13.18: the store-layer meter and circuit breaker. Every store call is counted per call site (the first frame outside the storage layer: member, file, line) and per op type
/// (get, range, head, list, put, append, delete, lease, meta), with its failures (404, 409, 412, 304) counted apart. A sliding one-minute window trips the breaker at max-per-min total ops or
/// max-failures-per-min failures: new claims stop and the next store call throws <see cref="StorageBudgetExceeded"/> with the top sites; it closes after the cooldown. A step scope (see <see cref="BeginStep"/>)
/// has its own budget and its own table. A 404 is remembered (<see cref="NegativeCache"/>).</summary>
public sealed class StoreMeter
{
    public static StoreMeter Current { get; set; } = new();
    public StorageLimits Limits { get; set; } = new();
    /// <summary>--storage-max N of a write-free command: the whole command may make at most N store ops (0 = no limit).</summary>
    public int CliMax { get; set; }
    readonly Func<DateTimeOffset> now;
    readonly object gate = new();
    readonly ConcurrentDictionary<(string Site, string Op), Cell> total = new();
    readonly Queue<(long Tick, bool Failed, string Site, string Op, string? Name)> window = new();
    int winOps, winFails; long lastWarnMin = -1; DateTimeOffset? trippedAt; string? tripMessage;
    public StoreMeter(Func<DateTimeOffset>? clock = null) { now = clock ?? (() => DateTimeOffset.UtcNow); }

    sealed class Cell { public long Count, Failed; public string? Example; }

    // ------------------------------------------------------------ the step scope
    sealed class StepScope { public long Ops; public int Max; public ConcurrentDictionary<(string, string), Cell> Cells = new(); public string? Tripped; }
    static readonly AsyncLocal<StepScope?> step = new();
    /// <summary>Starts a per-step budget for everything this async flow does (a claimed step). Dispose ends it. maxOps 0 = the limit from <see cref="Limits"/>.</summary>
    public IDisposable BeginStep(int maxOps = 0, string? label = null) { var prev = step.Value; var sc = new StepScope { Max = maxOps > 0 ? maxOps : Limits.MaxPerStep }; step.Value = sc; return new End(prev, sc, label); }
    sealed class End(StepScope? prev, StepScope sc, string? label) : IDisposable
    {
        public void Dispose()
        {
            step.Value = prev;
            if (label != null && sc.Ops > 0) Log.Info($"{label}: {sc.Ops} store ops ({sc.Cells.Values.Sum(x => x.Failed)} failed); top sites: {Top(Rows(sc.Cells), 5)}");
        }
    }
    /// <summary>The ops of the current step so far (0 outside a step scope).</summary>
    public long StepOps => step.Value?.Ops ?? 0;
    public IReadOnlyList<SiteRow> StepTable() => Rows(step.Value?.Cells);

    // ------------------------------------------------------------ the call site
    /// <summary>The first frame outside the storage layer, "Member (File.cs:line)"; an explicit tag (<see cref="Tag"/>) wins.</summary>
    public static string SiteOf([CallerMemberName] string? _ = null)
    {
        if (tag.Value is { } t) return t;
        var st = new System.Diagnostics.StackTrace(2, true);
        for (int i = 0; i < st.FrameCount; i++)
        {
            var f = st.GetFrame(i); var m = f?.GetMethod(); var type = m?.DeclaringType; if (m == null || type == null) continue;
            var ns = type.FullName ?? "";
            if (type == typeof(StoreMeter) || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal)) continue;
            // the storage plumbing is not a call site: the caller of the store is
            if (IsPlumbing(type)) continue;
            var file = f!.GetFileName(); var name = type.DeclaringType != null && type.Name.StartsWith('<') ? type.DeclaringType.Name : type.Name;
            var member = m.Name; if (type.Name.StartsWith('<')) { var s = type.Name; var a = s.IndexOf('<') + 1; var b = s.IndexOf('>'); if (b > a) member = s[a..b]; }
            return $"{name}.{member}" + (file != null ? $" ({Path.GetFileName(file)}:{f.GetFileLineNumber()})" : "");
        }
        return "(unknown)";
    }
    static bool IsPlumbing(Type t) { var n = t.FullName ?? ""; var top = t.DeclaringType ?? t; var tn = top.Name; return tn is "RestStore" or "LocalCacheStore" or "StageZipStore" or "RecordingStore" or "RedirectStore" or "SplitStore" or "MeteredStore" or "GhostSafeRead" or "NexisAccess" or "RestGhostBlob" or "ZipOpen" && n.Length > 0; }
    static readonly AsyncLocal<string?> tag = new();
    /// <summary>An explicit call-site tag for everything inside the scope (for a helper that many callers share).</summary>
    public static IDisposable Tag(string site) { var prev = tag.Value; tag.Value = site; return new TagEnd(prev); }
    sealed class TagEnd(string? prev) : IDisposable { public void Dispose() => tag.Value = prev; }

    // ------------------------------------------------------------ counting and the breaker
    long Minute() => now().ToUnixTimeSeconds();
    void Roll(long nowSec) { while (window.Count > 0 && window.Peek().Tick <= nowSec - 60) { var o = window.Dequeue(); winOps--; if (o.Failed) winFails--; } }

    /// <summary>Called before a store call: throws when the breaker is open or the step's budget is spent; otherwise counts the op and returns the site. <paramref name="name"/> is only for the example.</summary>
    public string Before(string op, string? name) => Before(op, name, null);
    public string Before(string op, string? name, string? site)
    {
        site ??= SiteOf();
        var sc = step.Value;
        lock (gate)
        {
            var sec = Minute(); Roll(sec);
            if (trippedAt is { } t)
            {
                if (now() - t >= Limits.Cooldown) { trippedAt = null; Log.Info($"storage: the breaker closed after {Limits.Cooldown.TotalMinutes:0.#} min"); tripMessage = null; }
                else throw new StorageBudgetExceeded(tripMessage ?? "storage breaker open");
            }
            if (CliMax > 0 && Total >= CliMax) throw new StorageBudgetExceeded($"storage: --storage-max {CliMax} reached. Top sites: {Top(Table(), 5)}");
            if (sc != null && sc.Tripped != null) throw new StorageBudgetExceeded(sc.Tripped);
            if (sc != null && sc.Ops >= sc.Max)
            {
                sc.Tripped = $"storage: this step made {sc.Ops} store ops (max-per-step {sc.Max}). Top sites: {Top(Rows(sc.Cells), 5)}";
                throw new StorageBudgetExceeded(sc.Tripped);
            }
            Count(total, site, op, name, false); if (sc != null) { sc.Ops++; Count(sc.Cells, site, op, name, false); }
            window.Enqueue((sec, false, site, op, name)); winOps++;
            if (winOps >= Limits.MaxPerMin) Trip($"{winOps} store ops in the last minute (max-per-min {Limits.MaxPerMin})");
            else if (winOps >= Limits.WarnPerMin && sec / 60 != lastWarnMin) { lastWarnMin = sec / 60; Log.Info($"storage: {winOps} ops in the last minute (warn {Limits.WarnPerMin}). Top sites: {Top(WindowRows(), 5)}"); }
        }
        return site;
    }

    /// <summary>Records that the call at <paramref name="site"/> failed (404, 409, 412, 304 and so on), with the blob name as an example.</summary>
    public void Failed(string site, string op, string? name, int status)
    {
        var sc = step.Value;
        lock (gate)
        {
            var sec = Minute(); Roll(sec);
            Count(total, site, op + "!" + status, name, true); if (sc != null) Count(sc.Cells, site, op + "!" + status, name, true);
            window.Enqueue((sec, true, site, op + "!" + status, name)); winFails++;
            if (trippedAt == null && winFails >= Limits.MaxFailuresPerMin) Trip($"{winFails} failed store ops in the last minute (max-failures-per-min {Limits.MaxFailuresPerMin}); failing sites: {Top(WindowRows().Where(r => r.Op.Contains('!')).ToList(), 5, true)}");
        }
    }

    void Trip(string why)
    {
        if (trippedAt != null) return;
        trippedAt = now(); tripMessage = $"storage breaker OPEN: {why}. Top sites: {Top(WindowRows(), 5)}. It closes after {Limits.Cooldown.TotalMinutes:0.#} min.";
        Log.Info(tripMessage);
    }

    static void Count(ConcurrentDictionary<(string, string), Cell> d, string site, string op, string? name, bool failed)
    {
        var c = d.GetOrAdd((site, op), _ => new Cell()); Interlocked.Increment(ref c.Count); if (failed) { Interlocked.Increment(ref c.Failed); c.Example ??= name; }
    }

    static IReadOnlyList<SiteRow> Rows(ConcurrentDictionary<(string, string), Cell>? d) => d == null ? [] : d.Select(kv => new SiteRow(kv.Key.Item1, kv.Key.Item2, kv.Value.Count, kv.Value.Failed, kv.Value.Example)).OrderByDescending(r => r.Count).ToList();
    List<SiteRow> WindowRows() => window.GroupBy(o => (o.Site, o.Op)).Select(g => new SiteRow(g.Key.Site, g.Key.Op, g.Count(), g.Count(x => x.Failed), g.Select(x => x.Name).FirstOrDefault(n => n != null))).OrderByDescending(r => r.Count).ToList();
    static string Top(IReadOnlyList<SiteRow> rows, int n, bool withExample = false) => string.Join("; ", rows.Take(n).Select(r => $"{r.Site} {r.Op} x{r.Count}" + (withExample && r.Example != null ? $" e.g. {r.Example}" : "")));

    // ------------------------------------------------------------ reports
    public bool Tripped { get { lock (gate) { Roll(Minute()); if (trippedAt is { } t && now() - t >= Limits.Cooldown) { trippedAt = null; tripMessage = null; Log.Info($"storage: the breaker closed after {Limits.Cooldown.TotalMinutes:0.#} min"); } return trippedAt != null; } } }
    public string? TripMessage { get { lock (gate) return tripMessage; } }
    public long OpsLastMinute { get { lock (gate) { Roll(Minute()); return winOps; } } }
    public long Total => total.Where(kv => !kv.Key.Op.Contains('!')).Sum(kv => kv.Value.Count);   // the "op!404" rows are failures of ops already counted
    public long TotalFailed => total.Values.Sum(c => c.Failed);
    public IReadOnlyList<SiteRow> Table() => Rows(total);
    /// <summary>"top 5: Site op xN, ..." for the time split and status.</summary>
    public string TopLine(int n = 5) => Total == 0 ? "no store ops" : Top(Table(), n);

    /// <summary>The per-site table for `indexnode status --ops` and the end of a write-free command.</summary>
    public string TableText(int max = 40)
    {
        var rows = Table(); var sb = new System.Text.StringBuilder();
        sb.AppendLine($"storage ops: {Total} total, {TotalFailed} failed (404/409/412/304); {OpsLastMinute} in the last minute{(Tripped ? "; BREAKER OPEN" : "")}");
        sb.AppendLine($"{"count",8} {"failed",7}  {"op",-10} site{"",-0}");
        foreach (var r in rows.Take(max)) sb.AppendLine($"{r.Count,8} {r.Failed,7}  {r.Op,-10} {r.Site}{(r.Example != null ? "  e.g. " + r.Example : "")}");
        if (rows.Count > max) sb.AppendLine($"  ... {rows.Count - max} more sites");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Clears the counters and the breaker (tests; a new write-free command).</summary>
    public void Reset() { lock (gate) { total.Clear(); window.Clear(); winOps = winFails = 0; trippedAt = null; tripMessage = null; lastWarnMin = -1; } }

    // ------------------------------------------------------------ the negative cache
    /// <summary>A 404 for a name is remembered until something says the name exists (a listing, a put, a queue line); asking again within <see cref="NegativeTtl"/> is answered "missing" with no request.</summary>
    public NegativeCache Negative { get; } = new();
}

/// <summary>0.13.18: names a store answered 404 for. Never ask for the same missing name twice in a step, and no more than once a minute per node, until something says it exists.</summary>
public sealed class NegativeCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);
    readonly ConcurrentDictionary<string, DateTimeOffset> missing = new(StringComparer.Ordinal);
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    public long Saved;
    public bool IsMissing(string name) { if (missing.TryGetValue(name, out var at)) { if (Now() - at < Ttl) { Interlocked.Increment(ref Saved); return true; } missing.TryRemove(name, out _); } return false; }
    public void Missing(string name) => missing[name] = Now();
    public void Exists(string name) => missing.TryRemove(name, out _);
    /// <summary>A listing saw these names: they exist now.</summary>
    public void ExistAll(IEnumerable<string> names) { foreach (var n in names) missing.TryRemove(n, out _); }
    public void Clear() => missing.Clear();
}
