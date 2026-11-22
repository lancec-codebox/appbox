using System.Collections.Concurrent;

namespace IndexNode;

public sealed class PermanentFailure(string message) : Exception(message);
public sealed class BotBlocked(string message) : Exception(message);

public sealed class JobContext
{
    public required JobDescriptor Job { get; init; }
    public required string Stage { get; init; }
    /// <summary>The queue subject the claim is on (the video id, ch-/pl-..., classify); the pre-0.13 blob name and lease id are gone.</summary>
    public string BlobName { get; init; } = "";
    public string LeaseId { get; init; } = "";
    public int Worker { get; init; }
    /// <summary>The queue, for handlers that look at it (never to find work).</summary>
    public JobQueue? Queue { get; init; }
    /// <summary>What the stage unlocks (add describe, add process ...): appended together with the final done, in one append.</summary>
    public List<QLine> FollowUps { get; } = new();
    /// <summary>The steps this claim already finished (a process claim resumes from the first unfinished step).</summary>
    public string[] StepsDone { get; init; } = [];
    public required IStore Store { get; init; }
    public required Paths Paths { get; init; }
    public required string NodeId { get; init; }
    /// <summary>Cancelled on stop, on Ctrl+C and when the lease is lost.</summary>
    public required CancellationToken Token { get; init; }
    public required Func<int> ThreadShare { get; init; }
    public required Action<string?, string?> Report { get; init; }
    /// <summary>Set by a handler that wants the job back as pending, due at this time (comments refresh); null: the job is done.</summary>
    public string? NextDue { get; set; }
    /// <summary>0.13.14 (group step): the join its grouping record holds, put on the step's done line.</summary>
    public string? GroupJoin { get; set; }
    /// <summary>0.13.15 (group step): the node's frames-file set (the gate's), so membership never lists the store.</summary>
    public IReadOnlySet<string>? Frames { get; init; }
    public string? GroupJoinKind { get; set; }
    /// <summary>0.13.23: control aiDramas (off unless the engine says otherwise): a sample check that finds AI adds the full download held while it is off.</summary>
    public Func<bool> AiDramasOn { get; init; } = () => false;
}

/// <summary>The node cannot do this job (e.g. a step it does not know): it is released without counting an attempt.</summary>
public sealed class JobSkipped(string message) : Exception(message);

/// <summary>0.12.0/0.13.0: thrown by the pre-check (the first step of a process claim) when the upload is a copy / AI / a non-canonical dub of an upload we already have; the job ends in state "duplicate".</summary>
public sealed class DuplicateFound(string upload, double share, string? speechLanguage = null, bool? dubbed = null, string? languageSource = null, string? metadataLanguage = null, string? url = null) : Exception($"duplicate of {upload} (share {share:0.00})")
{ public string Upload { get; } = upload; public double Share { get; } = share; public string? SpeechLanguage { get; } = speechLanguage; public bool? Dubbed { get; } = dubbed; public string? LanguageSource { get; } = languageSource; public string? MetadataLanguage { get; } = metadataLanguage; public string? Url { get; } = url; public string? Why { get; init; } public double? AiScore { get; init; } public string? Group { get; init; } public double? GroupOffset { get; init; } public double? MatchShare { get; init; } public double? DupShare { get; init; } public bool? Mixed { get; init; } public bool Ai { get; init; } }

public interface IStageHandler
{
    Task RunAsync(JobContext ctx);
}

public sealed class EngineOptions
{
    /// <summary>0.13.1: at start, add the singleton search-index entry when none is pending (catch-up; tests of the follow-up turn it off).</summary>
    public bool SearchIndexCatchUp { get; init; } = true;
    /// <summary>0.13.17: the head's build time (null: no published index). When set, the start-up catch-up adds a build only if the index is behind the queue; null function = always add (old behaviour).</summary>
    public Func<CancellationToken, Task<DateTimeOffset?>>? HeadBuiltAt { get; init; }
    public required string NodeId { get; init; }
    public string Name { get; init; } = "node";
    public string Kind { get; init; } = "desktop";
    public string Version { get; init; } = "0.1.0";
    /// <summary>Stable id of this process's instance on the node record; null: a new random one per engine (desktop). The phone passes one per device so a restart is not reported as a second process.</summary>
    public string? InstanceId { get; init; }
    public string[] Stages { get; init; } = ["download", "process"];
    public int Threads { get; init; } = 1;
    public bool Once { get; init; }
    public int? MaxJobs { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RenewInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How long renewals may keep failing before the job counts as lost and is cancelled (the lease itself lasts this long).</summary>
    public TimeSpan LeaseLimit { get; init; } = TimeSpan.FromSeconds(Protocol.LeaseSeconds);
    public TimeSpan ControlInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RegistryInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan BotBackoff { get; init; } = TimeSpan.FromHours(1);
    public Func<int, TimeSpan> TransientBackoff { get; init; } = attempts => TimeSpan.FromMinutes(Math.Pow(2, attempts));
    public string? Conditions { get; init; }
    /// <summary>The process steps this node can run (advertised in the registry).</summary>
    public string[]? Steps { get; init; }
    /// <summary>Claim filter: a job this returns false for is left to another node.</summary>
    public Func<JobDescriptor, bool>? CanRun { get; init; }
    /// <summary>0.13.3: builds a missing video.json for a video whose lease the claim holds (the backfill's builder); null: a missing descriptor fails the claim as before.</summary>
    public Func<IStore, Paths, string, CancellationToken, Task>? EnsureVideoJson { get; init; }
    /// <summary>Budget, schedule and window; null: no limits.</summary>
    public LimitState? Limits { get; init; }
    /// <summary>Tests replace the delay of the renewal and control loops with a fake timer (default Task.Delay), so no test depends on real time.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    /// <summary>The byte counters this node reports (default: the process-wide Traffic.Current).</summary>
    public Traffic? Traffic { get; init; }
    /// <summary>A GPU node: its stages are the GPU stages and it claims a marker-stage job at once; a CPU node waits <see cref="GpuFallback"/> before taking one.</summary>
    public bool Gpu { get; init; }
    public TimeSpan? GpuFallback { get; init; }
    /// <summary>Concurrent claims on this node (--lanes, default 1: one lane per 8-core sandbox). Each lane looks at the queue, claims the best subject it can run and runs it; the models are loaded once per node and shared.</summary>
    public int Workers { get; init; } = 1;
    /// <summary>Threads each worker's steps may use (--worker-threads); null = <see cref="Threads"/> divided evenly by <see cref="Workers"/>.</summary>
    public int? WorkerThreads { get; init; }
    /// <summary>The queue this node uses; default one over the store.</summary>
    public JobQueue? Queue { get; init; }
    /// <summary>The storage operations this node counts (reads, writes, lists); default: the process-wide counter.</summary>
    public OpCounter? Ops { get; init; }
    /// <summary>0.13.18: the store meter and breaker (default: the process-wide one).</summary>
    public StoreMeter Meter { get; init; } = StoreMeter.Current;
    // ---- accepted and ignored since 0.13.0 (old callers and tests set them)
    [Obsolete("0.13.0: a node runs one claim per lane; there are no per-stage job counts")] public Dictionary<string, int>? Jobs { get; init; }
    [Obsolete("0.13.0: batches are gone; --lanes is the number of concurrent claims")] public int Lanes { get => Workers; init => Workers = value; }
    [Obsolete("0.13.0: batches are gone")] public int Batch { get; init; }
    [Obsolete("0.13.0: batches are gone")] public double BatchHours { get; init; }
    [Obsolete("0.13.0: batches are gone")] public TimeSpan HoldRenew { get; init; }
    [Obsolete("0.13.0: models load once per node lifetime and are never unloaded between stages")] public Action? UnloadModels { get; init; }
    /// <summary>The node runs the AI stages (describe, translate): an AI endpoint is configured and the flag is on. Default processors never do.</summary>
    public bool AiStages { get; init; }
    /// <summary>The compute device named in the timing line of a job ("cpu", "cuda"); the desktop sets it from its ONNX settings.</summary>
    public Func<string>? Device { get; init; }
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
    /// <summary>Called on every fresh network lookup before it is recorded: a phone node adds the transport and VPN flag here (registry field "network").</summary>
    public Action<NetworkInfo>? NetworkExtra { get; init; }
}

public sealed partial class NodeEngine
{
    private sealed class Running
    {
        public required RunningJob Info;
        public required CancellationTokenSource Cts;
        public bool StopRequested;
        public bool LeaseLost;
        public JobContext? Ctx;
        public DuplicateFound? Duplicate;
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

    private OpsReport WithTop(OpsReport r) { var m = o_Meter; r.TopSites = m.Table().Take(30).Select(x => $"{x.Site} {x.Op} x{x.Count}" + (x.Failed > 0 ? $" ({x.Failed} failed)" : "") + (x.NotModified > 0 ? $" ({x.NotModified} not modified)" : "")).ToList(); r.Failed = m.TotalFailed; r.NotModified = m.TotalNotModified; r.Tripped = m.Tripped; return r; }
    private StoreMeter o_Meter => o.Meter;

    private readonly IStore store;
    private readonly Paths paths;
    private readonly EngineOptions o;
    private readonly IReadOnlyDictionary<string, IStageHandler> handlers;
    private readonly ConcurrentDictionary<Running, byte> running = new();
    private readonly SemaphoreSlim registryGate = new(1, 1);
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
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
    private readonly string instance;
    public static readonly TimeSpan InstanceWindow = TimeSpan.FromMinutes(2);
    private Dictionary<string, string> peers = new();

    public ControlWatcher Control { get; }
    public int JobsStarted => Volatile.Read(ref reserved);

    public NodeEngine(IStore store, Paths paths, EngineOptions options, IReadOnlyDictionary<string, IStageHandler> handlers)
    {
        this.store = store; this.paths = paths; o = options; this.handlers = handlers;
        instance = string.IsNullOrEmpty(o.InstanceId) ? Guid.NewGuid().ToString("N") : o.InstanceId;   // a host that is one instance per device (the phone) passes a stable id, so its own earlier heartbeat is "me", not a peer
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
        if (stageBackoff.Any(b => b.Value > DateTimeOffset.UtcNow)) return "backoff";
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
        Traffic = traffic.Snapshot(), Ops = WithTop(ops.Snapshot(startedAt)), Placement = placement, Network = network, FirstSeen = firstSeen ?? Protocol.Utc(startedAt), Instance = instance, Instances = peers.Count == 0 ? null : new(peers),
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
            o.NetworkExtra?.Invoke(n);
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
            queue = o.Queue ?? new JobQueue(store, paths, o.NodeId, o.Now);
            await queue.LoadAsync(rt);
            await EnsureSearchIndexAsync(rt);
            var workers = new List<Task>();
            for (int i = 0; i < Math.Max(1, o.Workers); i++) { int lane = i + 1; workers.Add(Task.Run(() => LaneLoop(lane, rt))); }
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
                if (SearchIndexPullable()) { Interlocked.Exchange(ref lastBusyTicks, DateTime.UtcNow.Ticks); continue; }
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
}
