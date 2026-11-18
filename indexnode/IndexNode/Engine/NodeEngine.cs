using System.Collections.Concurrent;

namespace IndexNode;

public sealed class PermanentFailure(string message) : Exception(message);
public sealed class BotBlocked(string message) : Exception(message);

public sealed class JobContext
{
    public required JobDescriptor Job { get; init; }
    public required string Stage { get; init; }
    public required string BlobName { get; init; }
    public required string LeaseId { get; init; }
    public required int Worker { get; init; }
    public required IStore Store { get; init; }
    public required Paths Paths { get; init; }
    public required string NodeId { get; init; }
    /// <summary>Cancelled on stop, on Ctrl+C and when the lease is lost.</summary>
    public required CancellationToken Token { get; init; }
    public required Func<int> ThreadShare { get; init; }
    public required Action<string?, string?> Report { get; init; }
    /// <summary>Set by a handler that wants the job back as pending, due at this time (comments refresh); null: the job is done.</summary>
    public string? NextDue { get; set; }
}

/// <summary>The node cannot do this job (e.g. a step it does not know): it is released without counting an attempt.</summary>
public sealed class JobSkipped(string message) : Exception(message);

public interface IStageHandler
{
    Task RunAsync(JobContext ctx);
}

public sealed class EngineOptions
{
    public required string NodeId { get; init; }
    public string Name { get; init; } = "node";
    public string Kind { get; init; } = "desktop";
    public string Version { get; init; } = "0.1.0";
    public string[] Stages { get; init; } = ["download", "process", "comments"];
    public Dictionary<string, int> Jobs { get; init; } = new() { ["download"] = 1, ["process"] = 1, ["comments"] = 1 };
    public int Threads { get; init; } = 1;
    public bool Once { get; init; }
    public int? MaxJobs { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RenewInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ControlInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RegistryInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan BotBackoff { get; init; } = TimeSpan.FromHours(1);
    public Func<int, TimeSpan> TransientBackoff { get; init; } = attempts => TimeSpan.FromMinutes(Math.Pow(2, attempts));
    public string? Conditions { get; init; }
    /// <summary>The process steps this node can run (advertised in the registry).</summary>
    public string[]? Steps { get; init; }
    /// <summary>Claim filter: a job this returns false for is left to another node.</summary>
    public Func<JobDescriptor, bool>? CanRun { get; init; }
    /// <summary>Budget, schedule and window; null: no limits.</summary>
    public LimitState? Limits { get; init; }
    /// <summary>Tests replace the delay of the renewal and control loops with a fake timer (default Task.Delay), so no test depends on real time.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    /// <summary>The byte counters this node reports (default: the process-wide Traffic.Current).</summary>
    public Traffic? Traffic { get; init; }
    /// <summary>A GPU node: its stages are the GPU stages and it claims a marker-stage job at once; a CPU node waits <see cref="GpuFallback"/> before taking one.</summary>
    public bool Gpu { get; init; }
    public TimeSpan? GpuFallback { get; init; }
    /// <summary>Independent workers on this node (--workers, default 2). Each takes the best waiting job across all the node's stages, runs it and repeats; nothing is shared between them.</summary>
    public int Workers { get; init; } = 2;
    /// <summary>Threads each worker's steps may use (--worker-threads); null = <see cref="Threads"/> divided evenly by <see cref="Workers"/>.</summary>
    public int? WorkerThreads { get; init; }
    /// <summary>Dramas processed side by side within a stage of a batch (--lanes, default 2); they share the stage's one loaded model.</summary>
    public int Lanes { get; init; } = 2;
    /// <summary>A batch holds up to this many dramas (--batch) or about <see cref="BatchHours"/> of video (--batch-hours), best first.</summary>
    public int Batch { get; init; } = 6;
    public double BatchHours { get; init; } = 2;
    /// <summary>How often a batch renews its hold-blob leases (60 s leases).</summary>
    public TimeSpan HoldRenew { get; init; } = TimeSpan.FromSeconds(40);
    /// <summary>Tests replace the unloading of the shared models after a stage.</summary>
    public Action? UnloadModels { get; init; }
    public int ThreadsPerLane => ThreadSplit.PerWorker(Threads, Lanes, WorkerThreads);
    public int ThreadsPerWorker => ThreadSplit.PerWorker(Threads, Workers, WorkerThreads);
    /// <summary>Stop after this long with no job running (a container job that was started for a marker and has no more work); null: never.</summary>
    public TimeSpan? ExitWhenIdle { get; init; }
    public TimeSpan IdleCheck { get; init; } = TimeSpan.FromSeconds(10);
    public string? StorageHost { get; init; }
    public string StorageRegion { get; init; } = "westus2";
    /// <summary>Tests replace the placement probe (IMDS and the storage connect time).</summary>
    public Func<CancellationToken, Task<Placement>>? PlacementProbe { get; init; }
    /// <summary>Tests replace the trace fetch (Cloudflare's /cdn-cgi/trace); null: a real HTTP GET.</summary>
    public Func<string, CancellationToken, Task<string?>>? NetworkFetch { get; init; }
    /// <summary>Tests replace the clock used for the network check's age.</summary>
    public Func<DateTimeOffset>? Now { get; init; }
}

public sealed partial class NodeEngine
{
    private sealed record Claim(string BlobName, string LeaseId, JobDescriptor Job, Dictionary<string, string> Tags);

    private sealed class Running
    {
        public required RunningJob Info;
        public required CancellationTokenSource Cts;
        public bool StopRequested;
        public bool LeaseLost;
        public JobContext? Ctx;
        public readonly StepTimer Timer = new();
    }

    /// <summary>Wall time per step of one job (fetch, frames, sscd, ...): one line when the job ends, so a slow phase shows without a profiler.</summary>
    internal sealed class StepTimer
    {
        private readonly List<(string Step, TimeSpan Time)> done = new();
        private string? current; private readonly System.Diagnostics.Stopwatch sw = new();
        public void Enter(string step) { lock (done) { Close(); current = step; sw.Restart(); } }
        private void Close() { if (current != null) { done.Add((current, sw.Elapsed)); current = null; } }
        public string Line(string device) { lock (done) { Close(); return done.Count == 0 ? "" : string.Join(", ", done.GroupBy(d => d.Step).Select(g => $"{g.Key} {g.Sum(x => x.Time.TotalSeconds):F1}s")) + $" [{device}]"; } }
    }

    private readonly IStore store;
    private readonly Paths paths;
    private readonly EngineOptions o;
    private readonly IReadOnlyDictionary<string, IStageHandler> handlers;
    private readonly ConcurrentDictionary<Running, byte> running = new();
    /// <summary>Everything one worker owns: its back-off state and its work folder. Workers share nothing but the store client, the config and the node record.</summary>
    private sealed class WorkerState(int number)
    {
        public int Number { get; } = number;
        public ConcurrentDictionary<string, DateTimeOffset> IgnoreUntil { get; init; } = new();
        public ConcurrentDictionary<string, DateTimeOffset> StageBackoff { get; init; } = new();
        /// <summary>Threads this lane's steps may use; null = the node's per-worker share.</summary>
        public int? Threads { get; init; }
        public string WorkRoot => Path.Combine(Path.GetTempPath(), "indexnode", "w" + Number + "-" + Environment.ProcessId);
    }
    private readonly List<WorkerState> workerStates = new();
    private readonly SemaphoreSlim registryGate = new(1, 1);
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private readonly bool[] fallbackListing = new bool[1];
    private int reserved;
    private long lastBusyTicks = DateTime.UtcNow.Ticks;
    public bool ExitedIdle { get; private set; }
    private string? lastRegistryKey;
    private Placement? placement;
    private NetworkInfo? network;
    private readonly SemaphoreSlim networkGate = new(1, 1);
    private string? firstSeen;
    private Traffic traffic => o.Traffic ?? Traffic.Current;
    private Task Wait(TimeSpan t, CancellationToken ct) => (o.Delay ?? Task.Delay)(t, ct);
    private volatile bool recordLoaded; // no registry write before the previous record's traffic is loaded, or it would be counted twice
    private readonly string instance = Guid.NewGuid().ToString("N");
    public static readonly TimeSpan InstanceWindow = TimeSpan.FromMinutes(2);
    private Dictionary<string, string> peers = new();

    public ControlWatcher Control { get; }
    public int JobsStarted => Volatile.Read(ref reserved);

    public NodeEngine(IStore store, Paths paths, EngineOptions options, IReadOnlyDictionary<string, IStageHandler> handlers)
    {
        this.store = store; this.paths = paths; o = options; this.handlers = handlers;
        Control = new ControlWatcher(store, paths);
        Control.Changed += OnControlChanged;
        o.Limits?.Apply(Control.Current);
    }

    // ---------------------------------------------------------------- registry

    private string NodeState()
    {
        var stages = o.Stages.ToList();
        if (stages.Count > 0 && stages.All(s => Control.Effective(s) == Level.Stop)) return "stopped";
        if (stages.Count > 0 && stages.All(s => Control.Effective(s) >= Level.Drain)) return "draining";
        if (!running.IsEmpty) return "working";
        if (o.Limits?.Status() is { } limited) return limited;
        if (workerStates.ToArray().Any(w => w.StageBackoff.Any(b => b.Value > DateTimeOffset.UtcNow))) return "backoff";
        return "idle";
    }

    private NodeInfo Snapshot(string? state = null) => new()
    {
        NodeId = o.NodeId, Name = o.Name, Kind = o.Kind, Version = o.Version, Platform = PlatformInfo.Platform(),
        Stages = o.Stages, Workers = o.Workers,
        Threads = o.Threads, WorkerThreads = o.ThreadsPerWorker, Conditions = o.Conditions, Steps = o.Steps,
        State = state ?? NodeState(),
        Control = Control.Current.Mode,
        Running = running.Keys.Select(r => new RunningJob { JobId = r.Info.JobId, Stage = r.Info.Stage, Step = r.Info.Step, Progress = r.Info.Progress, Worker = r.Info.Worker, StartedAt = r.Info.StartedAt }).ToList(),
        Traffic = traffic.Snapshot(), Placement = placement, Network = network, FirstSeen = firstSeen ?? Protocol.Utc(startedAt), Instance = instance, Instances = peers.Count == 0 ? null : new(peers),
        LastSeen = Protocol.NowUtc(), StartedAt = Protocol.Utc(startedAt),
    };

    public async Task WriteRegistryAsync(string? state = null, CancellationToken ct = default)
    {
        if (!recordLoaded) return;
        await registryGate.WaitAsync(ct);
        try
        {
            try { peers = LivePeers(NodeInfo.Parse((await store.GetAsync(paths.Node(o.NodeId), ct))?.Data ?? []), instance, DateTimeOffset.UtcNow); } catch (Exception ex) when (ex is not OperationCanceledException) { }
            var info = Snapshot(state);
            // every live process under this name, this one included, while there is more than one
            info.Instances = peers.Count > 0 && state != "offline" ? new Dictionary<string, string>(peers) { [instance] = info.LastSeen } : null;
            lastRegistryKey = RegistryKey(info);
            await store.PutAsync(paths.Node(o.NodeId), info.ToBytes(), new Dictionary<string, string> { [Tags.Kind] = "node" }, null, false, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Log.Info($"registry: write failed ({e.Message})"); }
        finally { registryGate.Release(); }
    }

    private static string RegistryKey(NodeInfo i) => i.State + "|" + i.Control + "|" + string.Join(';', i.Running.Select(r => $"{r.JobId}/{r.Stage}/{r.Step}"));

    private void RegistryChanged()
    {
        // a write on any change of state or running set; never blocks the caller
        if (RegistryKey(Snapshot()) == lastRegistryKey) return;
        _ = WriteRegistryAsync();
    }

    /// <summary>The instances in a record that wrote within the last 2 minutes, other than this process.</summary>
    public static Dictionary<string, string> LivePeers(NodeInfo? rec, string self, DateTimeOffset now)
    {
        var live = new Dictionary<string, string>();
        if (rec == null) return live;
        var all = new Dictionary<string, string>(rec.Instances ?? new());
        if (rec.Instance != null && !all.ContainsKey(rec.Instance)) all[rec.Instance] = rec.LastSeen;
        foreach (var (id, seen) in all)
            if (id != self && rec.State != "offline" && DateTimeOffset.TryParse(seen, out var at) && now - at < InstanceWindow) live[id] = seen;
        return live;
    }

    /// <summary>The node's record is never deleted or reset: its traffic and firstSeen carry over from the last run.</summary>
    private async Task LoadPreviousRecordAsync(CancellationToken ct)
    {
        try
        {
            if (await store.GetAsync(paths.Node(o.NodeId), ct) is { } c && NodeInfo.Parse(c.Data) is { } prev)
            {
                traffic.Load(prev.Traffic);
                firstSeen = prev.FirstSeen ?? prev.StartedAt;
                placement ??= prev.Placement;
                network ??= prev.Network; // keeps "since" when the same address is seen again
                var others = LivePeers(prev, instance, DateTimeOffset.UtcNow);
                if (others.Count > 0) Log.Info($"WARNING: another process is already reporting as node '{o.NodeId}' (seen {string.Join(", ", others.Values)}); running anyway, leases keep jobs safe");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Log.Info($"registry: could not read the previous record ({e.Message})"); }
        finally { recordLoaded = true; }
    }

    private DateTimeOffset NowUtc => o.Now?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>Looks up the public IPv4/IPv6 (5 s per family). Called at start, every 30 minutes, and before a download claim when the last check is older than 10 minutes.</summary>
    public async Task RefreshNetworkAsync(CancellationToken ct)
    {
        await networkGate.WaitAsync(ct);
        try
        {
            var prev = network;
            var n = await NetworkProbe.DetectAsync(o.NetworkFetch ?? NetworkProbe.HttpFetchAsync, ct, prev);
            n.CheckedAt = Protocol.Utc(NowUtc);
            n.Since = prev != null && n.SamePair(prev) && prev.Since.Length > 0 ? prev.Since : n.CheckedAt;
            if (!n.SamePair(prev)) Log.Info($"network: ipv4={n.Ipv4 ?? "-"} ipv6={n.Ipv6Prefix ?? n.Ipv6 ?? "-"} {n.Country} {n.Colo}");
            network = n;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Log.Info($"network: lookup failed ({e.Message})"); }
        finally { networkGate.Release(); }
    }

    private async Task EnsureFreshNetworkAsync(CancellationToken ct)
    {
        var n = network;
        if (n != null && DateTimeOffset.TryParse(n.CheckedAt, out var at) && NowUtc - at < NetworkProbe.ClaimStale) return;
        await RefreshNetworkAsync(ct);
    }

    /// <summary>The cap check before a download claim: true = may claim. Off (the default) = no storage reads at all.</summary>
    private async Task<bool> UnderIpCapAsync(CancellationToken ct)
    {
        var cap = Control.Current.Downloads?.MaxPerIp;
        if (cap is not > 0) return true;
        var nodes = new List<NodeInfo>();
        foreach (var b in await store.ListAsync(paths.NodesDir, false, false, ct))
            if (b.Name != paths.Node(o.NodeId) && await store.GetAsync(b.Name, ct) is { } c && NodeInfo.Parse(c.Data) is { } n) nodes.Add(n);
        int others = NetworkCap.CountOthers(network, o.NodeId, nodes, NowUtc);
        int own = running.Keys.Count(r => r.Info.Stage == "download");
        if (NetworkCap.MayClaim(cap, others, own)) return true;
        Log.Info($"download: {NetworkCap.WaitText(others + own)} (cap {cap})");
        return false;
    }

    private async Task RefreshPlacementAsync(CancellationToken ct)
    {
        try
        {
            placement = o.PlacementProbe != null ? await o.PlacementProbe(ct)
                : await Placement.DetectAsync(o.StorageHost, o.StorageRegion, null, Environment.GetEnvironmentVariable("INDEXNODE_REGION"), null, ct);
            Log.Info($"placement: {placement.Provider} {placement.Region ?? "(region unknown" + (placement.Reason != null ? ": " + placement.Reason : "") + ")"} storageRtt={placement.StorageRttMs?.ToString() ?? "n/a"} ms");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Log.Info($"placement: detection failed ({e.Message})"); }
    }

    private async Task RegistryLoop(CancellationToken ct)

    {
        try
        {
            while (true)
            {
                await Task.Delay(o.RegistryInterval, ct);
                if (!DateTimeOffset.TryParse(placement?.CheckedAt, out var chk) || DateTimeOffset.UtcNow - chk > Placement.Refresh) await RefreshPlacementAsync(ct);
                if (!DateTimeOffset.TryParse(network?.CheckedAt, out var nchk) || NowUtc - nchk >= NetworkProbe.Refresh) await RefreshNetworkAsync(ct);
                await WriteRegistryAsync(ct: ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ControlLoop(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Wait(o.ControlInterval, ct);
                await Control.RefreshAsync(ct, force: true);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnControlChanged()
    {
        o.Limits?.Apply(Control.Current);
        foreach (var r in running.Keys)
            if (Control.Effective(r.Info.Stage) == Level.Stop) { r.StopRequested = true; r.Cts.Cancel(); }
        RegistryChanged();
    }

    // ---------------------------------------------------------------- main

    public async Task RunAsync(CancellationToken ct)
    {
        await Control.RefreshAsync(ct, force: true);
        await LoadPreviousRecordAsync(ct);
        await RefreshPlacementAsync(ct);
        await RefreshNetworkAsync(ct);
        await WriteRegistryAsync(ct: ct);

        using var loopsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loops = new[] { RegistryLoop(loopsCts.Token), ControlLoop(loopsCts.Token), IdleLoop(runCts, loopsCts.Token) };
        try
        {
            var rt = runCts.Token;
            var workers = new List<Task>();
            int w = 0;
            var sharedIgnore = new ConcurrentDictionary<string, DateTimeOffset>(); var sharedBackoff = new ConcurrentDictionary<string, DateTimeOffset>();
            // N independent workers: each loops on its own over all the node's stages, with no limit shared between them
            if (o.Stages.Any(IsPassStage)) workers.Add(Task.Run(() => BatchLoop(rt)));
            for (int i = 0; i < (o.Stages.Any(s => !IsPassStage(s)) ? Math.Max(1, o.Workers) : 0); i++)
            {
                var ws = new WorkerState(++w) { IgnoreUntil = sharedIgnore, StageBackoff = sharedBackoff }; workerStates.Add(ws);   // workers share their back-offs: a job one worker just failed is not retried at once by the other
                workers.Add(Task.Run(() => WorkerLoop(ws, rt)));
            }
            await Task.WhenAll(workers);
        }
        finally
        {
            loopsCts.Cancel();
            await Task.WhenAll(loops);
            await WriteRegistryAsync("offline");
        }
    }

    /// <summary>--exit-when-idle: stops the node once no job has been running for the window (a GPU container started for a marker leaves when the work is gone, and costs nothing more).</summary>
    private async Task IdleLoop(CancellationTokenSource runCts, CancellationToken token)
    {
        if (o.ExitWhenIdle is not { } window) return;
        try
        {
            while (true)
            {
                await Task.Delay(o.IdleCheck, token);
                if (!running.IsEmpty || Volatile.Read(ref busyClaims) > 0) { Interlocked.Exchange(ref lastBusyTicks, DateTime.UtcNow.Ticks); continue; }
                if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastBusyTicks), DateTimeKind.Utc) >= window)
                {
                    Log.Info($"idle for {window.TotalMinutes:F1} min: exiting (--exit-when-idle)");
                    ExitedIdle = true; runCts.Cancel(); return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private int busyClaims;

    private bool TryReserve()
    {
        if (o.MaxJobs == null) { Interlocked.Increment(ref reserved); return true; }
        while (true)
        {
            var cur = Volatile.Read(ref reserved);
            if (cur >= o.MaxJobs) return false;
            if (Interlocked.CompareExchange(ref reserved, cur + 1, cur) == cur) return true;
        }
    }

    private bool IsPassStage(string stage) => StageOrder.PassOrder.Contains(stage);

    /// <summary>One worker, an engine of its own: take the best waiting work across all the node's stages, run it, repeat. A CPU step stage is taken a whole drama at a time (see <see cref="ClaimDramaAsync"/>).</summary>
    private async Task WorkerLoop(WorkerState w, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (o.MaxJobs != null && Volatile.Read(ref reserved) >= o.MaxJobs) return;
                await Control.RefreshAsync(ct, force: true);
                var open = o.Stages.Where(st => !IsPassStage(st) && Control.Effective(st) == Level.Run && !(w.StageBackoff.TryGetValue(st, out var u) && u > DateTimeOffset.UtcNow)).ToList();
                if (open.Count == 0)
                {
                    RegistryChanged();
                    if (o.Once) return;
                    await Task.Delay(o.PollInterval < o.ControlInterval ? o.PollInterval : o.ControlInterval, ct);
                    continue;
                }
                Task<bool> Eligible(string st, BlobEntry b, CancellationToken c) => EligibleAsync(w, st, b, c);
                var heads = await StageOrder.HeadsAsync(open, FindPendingAsync, Eligible, ct);
                bool worked = false;
                foreach (var (stage, _) in heads)
                    if (await ClaimAndRunAsync(w, stage, ct)) { worked = true; break; }
                if (!worked)
                {
                    RegistryChanged();
                    if (o.Once) return;
                    await Task.Delay(o.PollInterval, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }


    /// <summary>Claims and runs one job of a non-pass stage (download, comments, watch, ...). False when nothing could be claimed.</summary>
    private async Task<bool> ClaimAndRunAsync(WorkerState w, string stage, CancellationToken ct)
    {
        if (o.Limits != null && !o.Limits.TryAcquire(stage)) return false;
        if (stage == "download")
        {
            bool allowed;
            try { await EnsureFreshNetworkAsync(ct); allowed = await UnderIpCapAsync(ct); }
            catch (OperationCanceledException) { o.Limits?.Release(stage, false); throw; }
            catch (Exception e) { Log.Info($"download: the per-IP cap check failed ({e.Message}); not capping this time"); allowed = true; }
            if (!allowed) { o.Limits?.Release(stage, false); return false; } // waiting, not failing: no attempt counted, no back-off
        }
        if (!TryReserve()) { o.Limits?.Release(stage, false); return false; }
        Claim? claim;
        Interlocked.Increment(ref busyClaims);
        try { claim = await TryClaimAsync(w, stage, ct); }
        catch (OperationCanceledException) { Interlocked.Decrement(ref reserved); o.Limits?.Release(stage, false); throw; }
        catch (Exception e) { Log.Info($"{stage}: looking for work failed ({e.Message})"); claim = null; }
        finally { Interlocked.Decrement(ref busyClaims); }
        if (claim == null) { Interlocked.Decrement(ref reserved); o.Limits?.Release(stage, false); return false; }
        var finished = await RunJobAsync(w, stage, claim, store, ct);
        o.Limits?.Release(stage, finished);
        return true;
    }

    // ---------------------------------------------------------------- claim

    private async Task<IReadOnlyList<BlobEntry>> FindPendingAsync(string stage, CancellationToken ct)
    {
        if (!fallbackListing[0])
        {
            try
            {
                // Find Blobs by Tags searches the whole container: keep only this prefix's jobs
                var dir = paths.JobsDir(stage);
                var found = (await store.FindByTagsAsync(Tags.PendingWhere(stage, Protocol.NowUtc()), ct)).Where(b => b.Name.StartsWith(dir, StringComparison.Ordinal)).ToList();
                if (found.Count > 0) return found;
                // the tag index lags behind writes by a few seconds, so an empty answer is double-checked with a (consistent) listing
            }
            catch (ForbiddenException) { Log.Info("storage: the SAS cannot Find Blobs by Tags (needs permission f); listing jobs with tags instead"); fallbackListing[0] = true; }
        }
        var all = await store.ListAsync(paths.JobsDir(stage), true, false, ct);
        var now = Protocol.NowUtc();
        return all.Where(b => b.Tags.GetValueOrDefault(Tags.Stage) == stage && b.Tags.GetValueOrDefault(Tags.State) == "pending"
            && string.CompareOrdinal(Tags.DueOf(b.Tags), now) <= 0).ToList();
    }

    /// <summary>Whether a pending job could be claimed right now (for the workers' stage ranking): not ignored, its needs exist, this node can run it and no GPU hold applies. Reads only.</summary>
    private async Task<bool> EligibleAsync(WorkerState w, string stage, BlobEntry b, CancellationToken ct)
    {
        if (w.IgnoreUntil.TryGetValue(b.Name, out var until) && until > DateTimeOffset.UtcNow) return false;
        if (!o.Gpu && GpuRouting.IsMarkerStage(stage) && GpuRouting.EffectiveHold(o.GpuFallback, Control.Current) is { } hold && !GpuRouting.MayClaim(false, stage, await ReadySinceAsync(stage, b, ct), DateTimeOffset.UtcNow, hold)) return false;
        var body = await store.GetAsync(b.Name, ct);
        if (body == null) return false;
        var job = JobDescriptor.Parse(body.Data);
        if (job.Needs is { Length: > 0 } needs && await MissingNeedAsync(job, needs, ct) != null && !(IsPassStage(stage) && NeedsFromPass(job, needs))) return false;
        if (NeedsLayout(stage) && await LooseAsync(job.JobId, ct)) return false;
        if (o.CanRun != null && !o.CanRun(job)) return false;
        return true;
    }

    /// <summary>True when every input still missing for a pass-stage job is an output of an earlier step of the pass.</summary>
    private static bool NeedsFromPass(JobDescriptor job, string[] needs)
    {
        var earlier = StageOrder.PassOrder.TakeWhile(x => x != job.Stage).Select(x => x + ".zip").ToHashSet(StringComparer.Ordinal);
        return needs.Any(n => earlier.Contains(n));
    }

    private async Task<Claim?> TryClaimAsync(WorkerState w, string stage, CancellationToken ct)
    {
        var found = await FindPendingAsync(stage, ct);
        foreach (var b in Tags.ClaimOrder(found))
            if (await TryClaimOneAsync(w, stage, b, ct, null) is { } c) return c;
        return null;
    }

    /// <summary>True while another node holds the drama (its hold blob is leased and not ours).</summary>
    private async Task<bool> HeldByOtherAsync(string videoId, CancellationToken ct)
    {
        foreach (var e in await store.ListAsync(paths.Hold(videoId), false, true, ct))
            if (e.Name == paths.Hold(videoId) && e.LeaseState == "leased" && HoldInfo.FromMetadata(e.Metadata)?.Node != o.NodeId) return true;
        return false;
    }

    /// <summary>Leases one pending job and confirms it under the lease. <paramref name="produced"/>: outputs earlier steps of the same drama pass will write, so those needs do not block the claim.</summary>
    private async Task<Claim?> TryClaimOneAsync(WorkerState w, string stage, BlobEntry b, CancellationToken ct, HashSet<string>? produced)
    {
        if (w.IgnoreUntil.TryGetValue(b.Name, out var until) && until > DateTimeOffset.UtcNow) return null;
        if (!o.Gpu && GpuRouting.IsMarkerStage(stage) && GpuRouting.EffectiveHold(o.GpuFallback, Control.Current) is { } hold && !GpuRouting.MayClaim(false, stage, await ReadySinceAsync(stage, b, ct), DateTimeOffset.UtcNow, hold))
        {
            w.IgnoreUntil[b.Name] = DateTimeOffset.UtcNow.AddMinutes(1); // held for the GPU node; look again in a minute
            return null;
        }
        var lease = await store.AcquireLeaseAsync(b.Name, Protocol.LeaseSeconds, ct);
        if (lease == null) return null; // 409: another worker or node has it
        try
        {
            // the tag index is eventually consistent: confirm under the lease
            var tags = await store.GetTagsAsync(b.Name, ct);
            if (tags == null || tags.GetValueOrDefault(Tags.State) != "pending" || string.CompareOrdinal(Tags.DueOf(tags), Protocol.NowUtc()) > 0) { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); return null; }
            // the drama may be held (index/holds/<id>.json under a live lease) by another node's batch, or this step may already be done (the body says so, unlike a lagging tag)
            if (await HeldByOtherAsync(Path.GetFileNameWithoutExtension(b.Name), ct)) { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); return null; }
            var body = await store.GetAsync(b.Name, ct);
            if (body == null) { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); return null; }
            var job = JobDescriptor.Parse(body.Data);
            if (Protocol.IsStep(stage) && job.FinishedAt != null) { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); return null; }
            if (job.Needs is { Length: > 0 } needs && await MissingNeedAsync(job, needs, ct, produced) is { } missing)
            {
                // an earlier step has not finished: leave it pending, due a little later
                var later = Protocol.Utc(DateTimeOffset.UtcNow + Protocol.NeedsRetry);
                await store.SetTagsAsync(b.Name, Tags.Job(stage, "pending", tags.GetValueOrDefault(Tags.Added) ?? job.AddedAt, later, Tags.PriorityOf(tags)), lease, ct);
                await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None);
                Log.Info($"[{stage}] {job.JobId} waits for {missing}");
                return null;
            }
            if (NeedsLayout(stage) && await LooseAsync(job.JobId, ct))
            {
                await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None);
                w.IgnoreUntil[b.Name] = DateTimeOffset.UtcNow.AddMinutes(10);
                return null;
            }
            if (o.CanRun != null && !o.CanRun(job))
            {
                // this node lacks a step the job needs: leave it to another node, and look again later
                await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None);
                w.IgnoreUntil[b.Name] = DateTimeOffset.UtcNow.AddMinutes(10);
                return null;
            }
            return new Claim(b.Name, lease, job, new Dictionary<string, string>(tags));
        }
        catch
        {
            try { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); } catch { }
            throw;
        }
    }

    /// <summary>When a GPU-stage job became ready: its marker's readyAt, else (no marker: frames made before 0.6.0) the later of idx_added and idx_due.</summary>
    private async Task<DateTimeOffset?> ReadySinceAsync(string stage, BlobEntry b, CancellationToken ct)
    {
        var jobId = Path.GetFileNameWithoutExtension(b.Name);
        try { if (await store.GetAsync(GpuRouting.MarkerName(paths, stage, jobId), ct) is { } m && GpuRouting.ReadyAtOf(m.Data) is { } at) return at; }
        catch (OperationCanceledException) { throw; }
        catch { }
        string added = b.Tags.GetValueOrDefault(Tags.Added) ?? "", due = Tags.DueOf(b.Tags);
        DateTimeOffset? A = DateTimeOffset.TryParse(added, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var x) ? x : null;
        DateTimeOffset? Dd = DateTimeOffset.TryParse(due, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var y) ? y : null;
        return A == null ? Dd : Dd == null ? A : (A > Dd ? A : Dd);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, bool Loose)> looseSeen = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> looseLogged = new();
    /// <summary>0.10.0 refuses to process a drama that still has loose 0.9 files (it has to be repacked first: `indexnode repack`): its jobs stay pending, the log says so and `indexnode status` counts it.</summary>
    private async Task<bool> LooseAsync(string id, CancellationToken ct)
    {
        if (StageZipStore.Find(store) is not StageZipStore z) return false;   // a plain store (tests, --local runs) has no layout to refuse
        if (looseSeen.TryGetValue(id, out var c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(5)) return c.Loose;
        var loose = (await z.LooseFilesAsync(paths.Prefix, id, ct)).Count > 0;
        looseSeen[id] = (DateTimeOffset.UtcNow, loose);
        if (loose && looseLogged.TryAdd(id, 0)) Log.Info($"{id} is still in the loose 0.9 layout: not processing it until `indexnode repack` has run (its jobs stay pending)");
        return loose;
    }
    private static bool NeedsLayout(string stage) => stage == "process" || Protocol.IsStep(stage);

    private async Task<string?> MissingNeedAsync(JobDescriptor job, string[] needs, CancellationToken ct, HashSet<string>? produced = null)
    {
        foreach (var n in needs) if (!(produced?.Contains(n) ?? false) && await store.GetTagsAsync(paths.Video(job.JobId, n), ct) == null) return n;
        return null;
    }

    // ---------------------------------------------------------------- run one job

    private enum Outcome { Done, Permanent, Bot, Transient, Released, LeaseLost, Skipped }

    private async Task<bool> RunJobAsync(WorkerState w, string stage, Claim claim, IStore jobStore, CancellationToken ct)
    {
        int worker = w.Number;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = new Running { Info = new RunningJob { JobId = claim.Job.JobId, Stage = stage, Worker = worker, StartedAt = Protocol.NowUtc() }, Cts = cts };
        running[run] = 0;
        RegistryChanged();
        Log.Info($"[{stage}#{worker}] claimed {claim.Job.JobId}");

        if (stage == "download") claim.Job.Network = JobNetwork.From(network); // the network this attempt starts on
        var renewTask = RenewLoopAsync(stage, claim, run, cts.Token);
        Outcome outcome; string? error = null;
        try
        {
            var ctx = new JobContext
            {
                Job = claim.Job, Stage = stage, BlobName = claim.BlobName, LeaseId = claim.LeaseId, Worker = worker,
                Store = jobStore, Paths = paths, NodeId = o.NodeId, Token = cts.Token, ThreadShare = () => w.Threads ?? o.ThreadsPerWorker,
                Report = (step, progress) => { if (step != null && step != run.Info.Step) { run.Info.Step = step; run.Info.StartedAt = Protocol.NowUtc(); run.Timer.Enter(step); } run.Info.Progress = progress; },
            };
            run.Ctx = ctx;
            await handlers[stage].RunAsync(ctx);
            outcome = Outcome.Done;
        }
        catch (PermanentFailure e) { outcome = Outcome.Permanent; error = e.Message; }
        catch (JobSkipped e) { outcome = Outcome.Skipped; error = e.Message; }
        catch (BotBlocked e) { outcome = Outcome.Bot; error = e.Message; }
        catch (LeaseLostException e) { outcome = Outcome.LeaseLost; error = e.Message; }
        catch (OperationCanceledException) { outcome = run.LeaseLost ? Outcome.LeaseLost : Outcome.Released; }
        catch (Exception e) { outcome = run.LeaseLost ? Outcome.LeaseLost : Outcome.Transient; error = e.Message; Console.Error.WriteLine($"[{stage}#{worker}] {claim.Job.JobId}: {Redact.Trace(e)}"); }

        cts.Cancel();
        try { await renewTask; } catch { }
        if (outcome != Outcome.Done) StageZipStore.Stage(jobStore)?.DiscardStaged(claim.Job.JobId, Protocol.IsStep(stage) || stage is "download" or "comments" ? stage : null);   // a failed or released run uploads no zip
        if (run.LeaseLost) outcome = Outcome.LeaseLost;
        // a stop that arrives while the handler finishes anyway still counts as done; a cancelled one is released
        try { await ApplyOutcomeAsync(w, stage, claim, outcome, error, watch.Elapsed, run); }
        catch (Exception e) { Log.Info($"[{stage}#{worker}] could not record outcome for {claim.Job.JobId}: {e.Message}"); }

        running.TryRemove(run, out _);
        if (run.Timer.Line(OrtOptions.Device) is { Length: > 0 } timing) Log.Info($"[{stage}#{worker}] {claim.Job.JobId}: time by step: {timing}");
        RegistryChanged();
        Log.Info($"[{stage}#{worker}] {claim.Job.JobId}: {outcome}{(error != null ? " (" + Redact.Sas(error) + ")" : "")} in {watch.Elapsed.TotalSeconds:F1}s");
        return outcome == Outcome.Done;
    }

    private async Task RenewLoopAsync(string stage, Claim claim, Running run, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Wait(o.RenewInterval, token);
                try
                {
                    await store.RenewLeaseAsync(claim.BlobName, claim.LeaseId, token);
                    await Control.RefreshAsync(token, force: true);
                    if (Control.Effective(stage) == Level.Stop) { run.StopRequested = true; run.Cts.Cancel(); return; }
                    await store.SetMetadataAsync(claim.BlobName, new Dictionary<string, string>
                    {
                        ["node"] = o.NodeId,
                        ["progress"] = AsciiMeta(run.Info.Step == null ? run.Info.Progress : $"{run.Info.Step} {run.Info.Progress}"),
                        ["heartbeat"] = Protocol.NowUtc(),
                    }, claim.LeaseId, token);
                }
                catch (LeaseLostException) { run.LeaseLost = true; run.Cts.Cancel(); return; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Log.Info($"lease renewal for {claim.Job.JobId} failed ({e.Message}); will retry"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string AsciiMeta(string? s) => new string((s ?? "").Where(c => c is >= ' ' and <= '~').Take(200).ToArray());

    private async Task ApplyOutcomeAsync(WorkerState w, string stage, Claim claim, Outcome outcome, string? error, TimeSpan elapsed, Running run)
    {
        var none = CancellationToken.None;
        var job = claim.Job;
        string added = claim.Tags.GetValueOrDefault(Tags.Added) ?? job.AddedAt;
        string due = Tags.DueOf(claim.Tags);
        if (due.Length == 0) due = added;
        async Task Rewrite(string state, string? newDue = null)
        {
            await store.PutAsync(claim.BlobName, job.ToBytes(), Tags.Job(stage, state, added, newDue ?? due, Tags.PriorityOf(claim.Tags)), claim.LeaseId, false, none);
        }
        switch (outcome)
        {
            case Outcome.LeaseLost:
                return;
            case Outcome.Done:
                job.FinishedAt = Protocol.NowUtc(); job.NodeId = o.NodeId; job.ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1);
                if (run.Ctx?.NextDue is { } nextDue) await Rewrite("pending", nextDue); // a comments refresh: back to pending, due later
                else await Rewrite("done");
                break;
            case Outcome.Skipped:
                w.IgnoreUntil[claim.BlobName] = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);
                try { await store.SetMetadataAsync(claim.BlobName, new Dictionary<string, string>(), claim.LeaseId, none); } catch { }
                break;
            case Outcome.Permanent:
                job.LastError = Redact.Sas(error); job.Permanent = true; job.NodeId = o.NodeId;
                await Rewrite("failed");
                break;
            case Outcome.Transient:
                job.Attempts++; job.LastError = Redact.Sas(error); job.NodeId = o.NodeId;
                await Rewrite(job.Attempts >= Protocol.MaxAttempts ? "failed" : "pending");
                w.IgnoreUntil[claim.BlobName] = DateTimeOffset.UtcNow + o.TransientBackoff(job.Attempts);
                break;
            case Outcome.Bot:
                // no attempt counted; the error and the network are written to the job so a block can be matched to home, VPN or mobile data
                job.LastError = Redact.Sas(error); job.NodeId = o.NodeId;
                try { await Rewrite("pending"); } catch (Exception e) { Log.Info($"could not record the bot block on {job.JobId}: {e.Message}"); }
                w.StageBackoff[stage] = DateTimeOffset.UtcNow + o.BotBackoff;
                Log.Info($"[{stage}] bot block / rate limit: backing off {o.BotBackoff.TotalMinutes:F0} min");
                try { await store.SetMetadataAsync(claim.BlobName, new Dictionary<string, string>(), claim.LeaseId, none); } catch { }
                break;
            case Outcome.Released:
                try { await store.SetMetadataAsync(claim.BlobName, new Dictionary<string, string>(), claim.LeaseId, none); } catch { }
                break;
        }
        bool final = outcome == Outcome.Done && run.Ctx?.NextDue == null || outcome == Outcome.Permanent || outcome == Outcome.Transient && job.Attempts >= Protocol.MaxAttempts;
        if (final) await GpuRouting.DeleteMarkerAsync(store, paths, stage, job.JobId);
        await store.ReleaseLeaseAsync(claim.BlobName, claim.LeaseId, none);
    }
}
