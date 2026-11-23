using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace IndexNode;

/// <summary>0.13.18: thrown by a store call when the storage budget is used up (the node's breaker is open, or the step's own budget is spent). The step fails as a normal failure and is retried later.</summary>
public sealed class StorageBudgetExceeded(string message) : InvalidOperationException(message);

/// <summary>0.13.18: one line of the per-site table.</summary>
/// <summary>One line of the per-site table. <c>Failed</c> counts real failures (404, 409, 412 ...); <c>NotModified</c> counts 304s of conditional reads, which are successes (not in <c>Failed</c>, not in the breaker). <c>Blobs</c>: distinct blobs behind the row in the breaker's one-minute window (0 = not tracked, e.g. the all-time table).</summary>
public sealed record SiteRow(string Site, string Op, long Count, long Failed, string? Example, long NotModified = 0, int Blobs = 0);

/// <summary>0.13.18 settings (config storage.*). Defaults (0.13.26, the normal budget is low: a 2-lane processor measured a median of ~41 ops/min): warn 150 / max 200 per minute, 1,000 per step, 100 failures per minute, 5 minutes cooldown. One-off commands widen it with --storage-max N.</summary>
public sealed class StorageLimits
{
    /// <summary>0.13.30: the per-minute cap is per lane (config storage.max-per-min-per-lane); the node's cap is this times its lanes. A 2-lane node may do 400 a minute.</summary>
    public int MaxPerMinPerLane { get; set; } = 200;
    /// <summary>The lanes of the node (set by the engine): the node-wide cap is MaxPerMinPerLane x Lanes.</summary>
    public int Lanes { get; set; } = 1;
    public int MaxPerMin { get => MaxPerMinPerLane * Math.Max(1, Lanes); set => MaxPerMinPerLane = Math.Max(1, (value + Math.Max(1, Lanes) - 1) / Math.Max(1, Lanes)); }
    int? warn; /// <summary>Warn at 75% of the node-wide cap unless set.</summary>
    public int WarnPerMin { get => warn ?? MaxPerMin * 3 / 4; set => warn = value; }
    /// <summary>0.13.30: a hard limit on the store ops of ONE job step (storage.max-per-step): the runaway signal. Over it the step fails Transient with the per-site report and the node stops claiming.</summary>
    public int MaxPerStep { get; set; } = 25;
    /// <summary>Named larger limits for singleton builds that legitimately read many files (stage name -> ops).</summary>
    public Dictionary<string, int> StepLimits { get; set; } = new() { ["actors-index"] = 250, ["search-index"] = 400, ["process"] = 150, ["process:frames"] = 80, ["process:sscd"] = 60, ["process:centers"] = 50, ["process:thumbs"] = 60, ["process:transcribe"] = 60, ["process:ocrsubs"] = 60, ["process:cast"] = 60, ["process:sync"] = 40, ["process:describe"] = 40, ["process:translate"] = 40 };   // 0.13.32: "process" = the claim outside its steps (pre-check, listings, queue lines); "process:<step>" = each step's own budget (sum of the standard steps 410, a measured full job was about 221 live)
    public int StepLimitFor(string? stage) => stage != null && StepLimits.TryGetValue(stage, out var n) ? n : MaxPerStep;
    public int MaxFailuresPerMin { get; set; } = 100;
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
    DateTimeOffset? stepHaltAt; string? stepHaltMessage;
    int winOps, winFails; long lastWarnMin = -1; DateTimeOffset? trippedAt; string? tripMessage;
    public StoreMeter(Func<DateTimeOffset>? clock = null) { now = clock ?? (() => DateTimeOffset.UtcNow); }

    sealed class Cell { public long Count, Failed, NotModified; public string? Example; readonly HashSet<string> names = new(); public void Seen(string? n) { if (n == null) return; lock (names) if (names.Count < 1000) names.Add(n); } public int Blobs { get { lock (names) return names.Count; } } }
    /// <summary>One full stack sample per call site, refreshed at most once a minute (what the breaker logs on a warn or a trip).</summary>
    readonly ConcurrentDictionary<string, (DateTimeOffset At, string Stack)> samples = new();
    public static readonly TimeSpan SampleEvery = TimeSpan.FromMinutes(1);

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
    /// <summary>Type names of helpers that many callers share: a site that starts in one of them also names its callers.</summary>
    static readonly HashSet<string> SharedHelpers = new(StringComparer.Ordinal) { "VideoJson", "FramesReader", "CentersBlob", "CentersFile", "Catalog", "CatalogBuild", "ZipOpen", "FramesFile" };
    /// <summary>0.13.22: the call chain outside the storage layer, "A.m (A.cs:1) <- B.n (B.cs:2) <- ...": the first frame, and while the frame just taken is a shared helper (<see cref="SharedHelpers"/>) its caller too, up to 3 frames. An explicit tag (<see cref="Tag"/>) wins.</summary>
    public static string SiteOf([CallerMemberName] string? _ = null) => tag.Value is { } t ? t : SiteFrom(new System.Diagnostics.StackTrace(2, true));
    public static string SiteFrom(System.Diagnostics.StackTrace st)
    {
        var labels = new List<string>(3); string? curTop = null;
        for (int i = 0; i < st.FrameCount; i++)
        {
            var f = st.GetFrame(i); var m = f?.GetMethod(); var type = m?.DeclaringType; if (m == null || type == null) continue;
            var ns = type.FullName ?? "";
            if (type == typeof(StoreMeter) || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal)) continue;
            // the storage plumbing is not a call site: the caller of the store is
            if (IsPlumbing(type)) continue;
            var top = (type.DeclaringType ?? type).Name;
            if (top == curTop) continue;   // another frame of the helper already taken (an overload, its async state machine): the chain goes on with ITS caller
            var file = f!.GetFileName(); var name = type.DeclaringType != null && type.Name.StartsWith('<') ? type.DeclaringType.Name : type.Name;
            var member = m.Name; if (type.Name.StartsWith('<')) { var s = type.Name; var a = s.IndexOf('<') + 1; var b = s.IndexOf('>'); if (b > a) member = s[a..b]; }
            labels.Add($"{name}.{member}" + (file != null ? $" ({Path.GetFileName(file)}:{f.GetFileLineNumber()})" : ""));
            curTop = top;
            if (labels.Count >= 3 || !SharedHelpers.Contains(top)) break;
        }
        return labels.Count == 0 ? "(unknown)" : string.Join(" <- ", labels);
    }
    static bool IsPlumbing(Type t) { var n = t.FullName ?? ""; var top = t.DeclaringType ?? t; var tn = top.Name; return tn is "RestStore" or "LocalCacheStore" or "StageZipStore" or "RecordingStore" or "RedirectStore" or "SplitStore" or "MeteredStore" or "GhostSafeRead" or "NexisAccess" or "RestGhostBlob" or "ZipOpen" && n.Length > 0; }
    static readonly AsyncLocal<string?> tag = new();
    /// <summary>An explicit call-site tag for everything inside the scope (for a helper that many callers share).</summary>
    public static IDisposable Tag(string site) { var prev = tag.Value; tag.Value = site; return new TagEnd(prev); }
    sealed class TagEnd(string? prev) : IDisposable { public void Dispose() => tag.Value = prev; }

    // ------------------------------------------------------------ counting and the breaker
    long Minute() => now().ToUnixTimeSeconds();
    void Roll(long nowSec) { while (window.Count > 0 && window.Peek().Tick <= nowSec - 60) { var o = window.Dequeue(); if (o.Failed) winFails--; else winOps--; } }

    /// <summary>Called before a store call: throws when the breaker is open or the step's budget is spent; otherwise counts the op and returns the site. <paramref name="name"/> is only for the example.</summary>
    public string Before(string op, string? name) => Before(op, name, null);
    public string Before(string op, string? name, string? site)
    {
        bool computed = site == null; site ??= SiteOf();
        if (computed) Sample(site);
        var sc = step.Value;
        lock (gate)
        {
            var sec = Minute(); Roll(sec);
            if (trippedAt is { } t)
            {
                if (now() - t >= Limits.Cooldown) { trippedAt = null; window.Clear(); winOps = winFails = 0; Log.Info($"storage: the breaker closed after {Limits.Cooldown.TotalMinutes:0.#} min"); tripMessage = null; }
                else throw new StorageBudgetExceeded(tripMessage ?? "storage breaker open");
            }
            if (CliMax > 0 && Total >= CliMax) throw new StorageBudgetExceeded($"storage: --storage-max {CliMax} reached. Top sites: {Top(Table(), 5)}");
            if (sc != null && sc.Tripped != null) throw new StorageBudgetExceeded(sc.Tripped);
            if (sc != null && sc.Ops >= sc.Max)
            {
                sc.Tripped = $"storage: this step made {sc.Ops} store ops (max-per-step {sc.Max}). Top sites: {Top(Rows(sc.Cells), 5, true)}";
                stepHaltAt = now(); stepHaltMessage = $"storage: a step made {sc.Ops} store ops (max-per-step {sc.Max}); the node stops claiming for {Limits.Cooldown.TotalMinutes:0.#} min. Top sites: {Top(Rows(sc.Cells), 5, true)}";   // 0.13.30: the node stops claiming (store calls stay open, so the step's failure line can still be written)
                Log.Info(stepHaltMessage);
                throw new StorageBudgetExceeded(sc.Tripped);
            }
            Count(total, site, op, name, false); if (sc != null) { sc.Ops++; Count(sc.Cells, site, op, name, false); }
            window.Enqueue((sec, false, site, op, name)); winOps++;
            if (winOps >= Limits.MaxPerMin) Trip($"{winOps} store ops in the last minute (max-per-min {Limits.MaxPerMin})");
            else if (winOps >= Limits.WarnPerMin && sec / 60 != lastWarnMin) { lastWarnMin = sec / 60; { var wr = WindowRows(); Log.Info($"storage: {winOps} ops in the last minute (warn {Limits.WarnPerMin}). Top sites: {Top(wr, 5, true)}\n{Stacks(wr, 3)}"); } }
        }
        return site;
    }

    /// <summary>
    /// Records an answer that was not a plain success, with the blob name as an example. 0.13.22: a 304 (a conditional read answered "not modified") is a SUCCESS: it is kept in its own column (<c>NotModified</c>, row op "get!304"),
    /// is already in the op totals (the read was counted when it was sent) and never counts toward the failure breaker. The other statuses stay failures, each under its own "op!status" row: 404 (a probe of a name that may be absent: get, head, tags, lease, range),
    /// 409 (lease held, append blob sealed) and 412 (a conditional write or an append position lost a race and its caller retries). They are not excluded from the breaker: a burst of them is a loop worth tripping on.
    /// </summary>
    public void Failed(string site, string op, string? name, int status)
    {
        var sc = step.Value;
        lock (gate)
        {
            if (status == 304) { CountNotModified(total, site, op, name); if (sc != null) CountNotModified(sc.Cells, site, op, name); return; }
            var sec = Minute(); Roll(sec);
            Count(total, site, op + "!" + status, name, true); if (sc != null) Count(sc.Cells, site, op + "!" + status, name, true);
            window.Enqueue((sec, true, site, op + "!" + status, name)); winFails++;
            if (trippedAt == null && winFails >= Limits.MaxFailuresPerMin) { var wr = WindowRows(); Trip($"{winFails} failed store ops in the last minute (max-failures-per-min {Limits.MaxFailuresPerMin}; 304 not-modified answers are not failures); failing sites: {Top(wr.Where(r => r.Op.Contains('!')).ToList(), 5, true)}", wr.Where(r => r.Op.Contains('!')).ToList()); }
        }
    }

    void Sample(string site)
    {
        var n = now();
        if (samples.TryGetValue(site, out var s) && n - s.At < SampleEvery) return;
        var lines = new System.Diagnostics.StackTrace(true).ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.Contains(" IndexNode.StoreMeter.") && !l.TrimStart().StartsWith("at System.", StringComparison.Ordinal)).Take(25);
        samples[site] = (n, string.Join("\n", lines));
    }
    /// <summary>The sampled stacks of the top <paramref name="n"/> sites (at most one sample a minute each).</summary>
    string Stacks(IReadOnlyList<SiteRow> rows, int n) => string.Join("\n", rows.Select(r => r.Site).Distinct().Take(n).Select(s => samples.TryGetValue(s, out var x) ? $"  stack of {s} (sampled {x.At:HH:mm:ss}):\n{string.Join("\n", x.Stack.Split('\n').Select(l => "    " + l.TrimStart()))}" : $"  {s}: no stack sampled"));

    void Trip(string why, IReadOnlyList<SiteRow>? rows = null)
    {
        if (trippedAt != null) return;
        var wr = rows ?? WindowRows();
        trippedAt = now(); tripMessage = $"storage breaker OPEN: {why}. Top sites: {Top(wr, 5, true)}. It closes after {Limits.Cooldown.TotalMinutes:0.#} min.";
        Log.Info(tripMessage + "\n" + Stacks(wr, 3));
    }

    static void Count(ConcurrentDictionary<(string, string), Cell> d, string site, string op, string? name, bool failed)
    {
        var c = d.GetOrAdd((site, op), _ => new Cell()); Interlocked.Increment(ref c.Count); if (failed) Interlocked.Increment(ref c.Failed); c.Example ??= name; c.Seen(name);
    }
    static void CountNotModified(ConcurrentDictionary<(string, string), Cell> d, string site, string op, string? name)
    {
        var c = d.GetOrAdd((site, op + "!304"), _ => new Cell()); Interlocked.Increment(ref c.Count); Interlocked.Increment(ref c.NotModified); c.Example ??= name; c.Seen(name);
    }

    static IReadOnlyList<SiteRow> Rows(ConcurrentDictionary<(string, string), Cell>? d) => d == null ? [] : d.Select(kv => new SiteRow(kv.Key.Item1, kv.Key.Item2, kv.Value.Count, kv.Value.Failed, kv.Value.Example, kv.Value.NotModified, kv.Value.Blobs)).OrderByDescending(r => r.Count).ToList();
    List<SiteRow> WindowRows() => window.GroupBy(o => (o.Site, o.Op)).Select(g => new SiteRow(g.Key.Site, g.Key.Op, g.Count(), g.Count(x => x.Failed), g.Select(x => x.Name).FirstOrDefault(n => n != null), 0, g.Select(x => x.Name).Where(n => n != null).Distinct().Count())).OrderByDescending(r => r.Count).ToList();
    /// <summary>"site op xN" per row; with examples: how many distinct blobs and one of them ("295 blobs, e.g. ..."), or "all on one blob" when it really is one: never a lone path that reads like a loop on one file.</summary>
    static string Top(IReadOnlyList<SiteRow> rows, int n, bool withExample = false) => string.Join("; ", rows.Take(n).Select(r => $"{r.Site} {r.Op} x{r.Count}" + (!withExample || r.Example == null ? "" : r.Blobs > 1 ? $" ({r.Blobs} blobs, e.g. {r.Example})" : r.Blobs == 1 && r.Count > 1 ? $" (all on one blob: {r.Example})" : $" (e.g. {r.Example})")));

    // ------------------------------------------------------------ reports
    public bool Tripped { get { lock (gate) { Roll(Minute()); if (stepHaltAt is { } sh && now() - sh >= Limits.Cooldown) { stepHaltAt = null; stepHaltMessage = null; } if (stepHaltAt != null) return true; if (trippedAt is { } t && now() - t >= Limits.Cooldown) { trippedAt = null; tripMessage = null; window.Clear(); winOps = winFails = 0; Log.Info($"storage: the breaker closed after {Limits.Cooldown.TotalMinutes:0.#} min"); } return trippedAt != null; } } }
    public string? TripMessage { get { lock (gate) return tripMessage ?? stepHaltMessage; } }
    public long OpsLastMinute { get { lock (gate) { Roll(Minute()); return winOps; } } }
    public long Total => total.Where(kv => !kv.Key.Op.Contains('!')).Sum(kv => kv.Value.Count);   // the "op!404" rows are failures of ops already counted
    public long TotalFailed => total.Values.Sum(c => c.Failed);
    /// <summary>Conditional reads answered 304: successes, kept out of <see cref="TotalFailed"/> and the failure breaker.</summary>
    public long TotalNotModified => total.Values.Sum(c => c.NotModified);
    public IReadOnlyList<SiteRow> Table() => Rows(total);
    /// <summary>"top 5: Site op xN, ..." for the time split and status.</summary>
    public string TopLine(int n = 5) => Total == 0 ? "no store ops" : Top(Table(), n);

    /// <summary>The per-site table for `indexnode status --ops` and the end of a write-free command.</summary>
    public string TableText(int max = 40)
    {
        var rows = Table(); var sb = new System.Text.StringBuilder();
        sb.AppendLine($"storage ops: {Total} total, {TotalFailed} failed (404/409/412), {TotalNotModified} not modified (304, a success); {OpsLastMinute} in the last minute{(Tripped ? "; BREAKER OPEN" : "")}");
        sb.AppendLine($"{"count",8} {"failed",7} {"304",7}  {"op",-10} site{"",-0}");
        foreach (var r in rows.Take(max)) sb.AppendLine($"{r.Count,8} {r.Failed,7} {r.NotModified,7}  {r.Op,-10} {r.Site}{(r.Example != null ? "  e.g. " + r.Example : "")}");
        if (rows.Count > max) sb.AppendLine($"  ... {rows.Count - max} more sites");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Clears the counters and the breaker (tests; a new write-free command).</summary>
    public void Reset() { lock (gate) { total.Clear(); samples.Clear(); window.Clear(); winOps = winFails = 0; trippedAt = null; tripMessage = null; stepHaltAt = null; stepHaltMessage = null; lastWarnMin = -1; } }

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
