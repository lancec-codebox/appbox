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
}

public sealed class NodeEngine
{
    private sealed record Claim(string BlobName, string LeaseId, JobDescriptor Job, Dictionary<string, string> Tags);

    private sealed class Running
    {
        public required RunningJob Info;
        public required CancellationTokenSource Cts;
        public bool StopRequested;
        public bool LeaseLost;
        public JobContext? Ctx;
    }

    private readonly IStore store;
    private readonly Paths paths;
    private readonly EngineOptions o;
    private readonly IReadOnlyDictionary<string, IStageHandler> handlers;
    private readonly ConcurrentDictionary<Running, byte> running = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> ignoreUntil = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> stageBackoff = new();
    private readonly SemaphoreSlim registryGate = new(1, 1);
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private readonly bool[] fallbackListing = new bool[1];
    private int reserved;
    private string? lastRegistryKey;

    public ControlWatcher Control { get; }
    public ThreadBudget Budget { get; }
    public int JobsStarted => Volatile.Read(ref reserved);

    public NodeEngine(IStore store, Paths paths, EngineOptions options, IReadOnlyDictionary<string, IStageHandler> handlers)
    {
        this.store = store; this.paths = paths; o = options; this.handlers = handlers;
        Control = new ControlWatcher(store, paths);
        Budget = new ThreadBudget(options.Threads);
        Control.Changed += OnControlChanged;
    }

    // ---------------------------------------------------------------- registry

    private string NodeState()
    {
        var stages = o.Stages.Where(s => o.Jobs.GetValueOrDefault(s, 1) > 0).ToList();
        if (stages.Count > 0 && stages.All(s => Control.Effective(s) == Level.Stop)) return "stopped";
        if (stages.Count > 0 && stages.All(s => Control.Effective(s) >= Level.Drain)) return "draining";
        if (!running.IsEmpty) return "working";
        if (stageBackoff.Any(b => b.Value > DateTimeOffset.UtcNow)) return "backoff";
        return "idle";
    }

    private NodeInfo Snapshot(string? state = null) => new()
    {
        NodeId = o.NodeId, Name = o.Name, Kind = o.Kind, Version = o.Version, Platform = PlatformInfo.Platform(),
        Stages = o.Stages, Jobs = o.Jobs.Where(j => o.Stages.Contains(j.Key)).ToDictionary(j => j.Key, j => j.Value),
        Threads = o.Threads, Conditions = o.Conditions,
        State = state ?? NodeState(),
        Control = Control.Current.Mode,
        Running = running.Keys.Select(r => new RunningJob { JobId = r.Info.JobId, Stage = r.Info.Stage, Step = r.Info.Step, Progress = r.Info.Progress, Worker = r.Info.Worker }).ToList(),
        Sources = sources.IsEmpty ? null : new Dictionary<string, string>(sources),
        LastSeen = Protocol.NowUtc(), StartedAt = Protocol.Utc(startedAt),
    };

    public async Task WriteRegistryAsync(string? state = null, CancellationToken ct = default)
    {
        await registryGate.WaitAsync(ct);
        try
        {
            var info = Snapshot(state);
            lastRegistryKey = RegistryKey(info);
            await store.PutAsync(paths.Node(o.NodeId), info.ToBytes(), new Dictionary<string, string> { [Tags.Kind] = "node" }, null, false, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Log.Info($"registry: write failed ({e.Message})"); }
        finally { registryGate.Release(); }
    }

    private readonly ConcurrentDictionary<string, string> sources = new();

    /// <summary>Records the state of a source this node polls (e.g. watch-later = ok | signed-out) in the node registry.</summary>
    public void SetSource(string name, string state)
    {
        if (sources.TryGetValue(name, out var old) && old == state) return;
        sources[name] = state;
        _ = WriteRegistryAsync();
    }

    private static string RegistryKey(NodeInfo i) => i.State + "|" + i.Control + "|" + string.Join(',', (i.Sources ?? new()).Select(s => s.Key + "=" + s.Value)) + "|" + string.Join(';', i.Running.Select(r => $"{r.JobId}/{r.Stage}/{r.Step}"));

    private void RegistryChanged()
    {
        // a write on any change of state or running set; never blocks the caller
        if (RegistryKey(Snapshot()) == lastRegistryKey) return;
        _ = WriteRegistryAsync();
    }

    private async Task RegistryLoop(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(o.RegistryInterval, ct);
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
                await Task.Delay(o.ControlInterval, ct);
                await Control.RefreshAsync(ct, force: true);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnControlChanged()
    {
        foreach (var r in running.Keys)
            if (Control.Effective(r.Info.Stage) == Level.Stop) { r.StopRequested = true; r.Cts.Cancel(); }
        RegistryChanged();
    }

    // ---------------------------------------------------------------- main

    public async Task RunAsync(CancellationToken ct)
    {
        await Control.RefreshAsync(ct, force: true);
        await WriteRegistryAsync(ct: ct);
        using var loopsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loops = new[] { RegistryLoop(loopsCts.Token), ControlLoop(loopsCts.Token) };
        try
        {
            var workers = new List<Task>();
            int w = 0;
            foreach (var stage in o.Stages)
                for (int i = 0; i < o.Jobs.GetValueOrDefault(stage, 1); i++)
                {
                    int number = ++w;
                    workers.Add(Task.Run(() => WorkerLoop(stage, number, ct)));
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

    private async Task WorkerLoop(string stage, int worker, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Control.RefreshAsync(ct, force: true);
                var level = Control.Effective(stage);
                if (level != Level.Run)
                {
                    RegistryChanged();
                    if (o.Once) return;
                    await Task.Delay(o.PollInterval < o.ControlInterval ? o.PollInterval : o.ControlInterval, ct);
                    continue;
                }
                if (stageBackoff.TryGetValue(stage, out var until) && until > DateTimeOffset.UtcNow)
                {
                    RegistryChanged();
                    if (o.Once) return;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Max(0.05, (until - DateTimeOffset.UtcNow).TotalSeconds))), ct);
                    continue;
                }
                if (!TryReserve()) return;
                Claim? claim;
                try { claim = await TryClaimAsync(stage, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    Log.Info($"{stage}: looking for work failed ({e.Message})");
                    Interlocked.Decrement(ref reserved);
                    if (o.Once) return;
                    await Task.Delay(o.PollInterval, ct);
                    continue;
                }
                if (claim == null)
                {
                    Interlocked.Decrement(ref reserved);
                    if (o.Once) return;
                    RegistryChanged();
                    await Task.Delay(o.PollInterval, ct);
                    continue;
                }
                await RunJobAsync(stage, worker, claim, ct);
            }
        }
        catch (OperationCanceledException) { }
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

    private async Task<Claim?> TryClaimAsync(string stage, CancellationToken ct)
    {
        var found = await FindPendingAsync(stage, ct);
        var ordered = found.OrderBy(b => b.Tags.GetValueOrDefault(Tags.Added) ?? "", StringComparer.Ordinal).ThenBy(b => b.Name, StringComparer.Ordinal);
        foreach (var b in ordered)
        {
            if (ignoreUntil.TryGetValue(b.Name, out var until) && until > DateTimeOffset.UtcNow) continue;
            var lease = await store.AcquireLeaseAsync(b.Name, Protocol.LeaseSeconds, ct);
            if (lease == null) continue; // 409: another node has it
            try
            {
                // the tag index is eventually consistent: confirm under the lease
                var tags = await store.GetTagsAsync(b.Name, ct);
                if (tags == null || tags.GetValueOrDefault(Tags.State) != "pending" || string.CompareOrdinal(Tags.DueOf(tags), Protocol.NowUtc()) > 0) { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); continue; }
                var body = await store.GetAsync(b.Name, ct);
                if (body == null) { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); continue; }
                var job = JobDescriptor.Parse(body.Data);
                return new Claim(b.Name, lease, job, new Dictionary<string, string>(tags));
            }
            catch
            {
                try { await store.ReleaseLeaseAsync(b.Name, lease, CancellationToken.None); } catch { }
                throw;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- run one job

    private enum Outcome { Done, Permanent, Bot, Transient, Released, LeaseLost, Skipped }

    private async Task RunJobAsync(string stage, int worker, Claim claim, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = new Running { Info = new RunningJob { JobId = claim.Job.JobId, Stage = stage, Worker = worker }, Cts = cts };
        running[run] = 0;
        IDisposable? budget = stage == "process" ? Budget.Enter() : null;
        RegistryChanged();
        Log.Info($"[{stage}#{worker}] claimed {claim.Job.JobId}");

        var renewTask = RenewLoopAsync(stage, claim, run, cts.Token);
        Outcome outcome; string? error = null;
        try
        {
            var ctx = new JobContext
            {
                Job = claim.Job, Stage = stage, BlobName = claim.BlobName, LeaseId = claim.LeaseId, Worker = worker,
                Store = store, Paths = paths, NodeId = o.NodeId, Token = cts.Token, ThreadShare = () => Budget.Share,
                Report = (step, progress) => { if (step != null) run.Info.Step = step; run.Info.Progress = progress; },
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
        catch (Exception e) { outcome = run.LeaseLost ? Outcome.LeaseLost : Outcome.Transient; error = e.Message; }

        cts.Cancel();
        try { await renewTask; } catch { }
        if (run.LeaseLost) outcome = Outcome.LeaseLost;
        // a stop that arrives while the handler finishes anyway still counts as done; a cancelled one is released
        try { await ApplyOutcomeAsync(stage, claim, outcome, error, watch.Elapsed, run); }
        catch (Exception e) { Log.Info($"[{stage}#{worker}] could not record outcome for {claim.Job.JobId}: {e.Message}"); }

        budget?.Dispose();
        running.TryRemove(run, out _);
        RegistryChanged();
        Log.Info($"[{stage}#{worker}] {claim.Job.JobId}: {outcome}{(error != null ? " (" + Redact.Sas(error) + ")" : "")} in {watch.Elapsed.TotalSeconds:F1}s");
    }

    private async Task RenewLoopAsync(string stage, Claim claim, Running run, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(o.RenewInterval, token);
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

    private async Task ApplyOutcomeAsync(string stage, Claim claim, Outcome outcome, string? error, TimeSpan elapsed, Running run)
    {
        var none = CancellationToken.None;
        var job = claim.Job;
        string added = claim.Tags.GetValueOrDefault(Tags.Added) ?? job.AddedAt;
        string due = Tags.DueOf(claim.Tags);
        if (due.Length == 0) due = added;
        async Task Rewrite(string state, string? newDue = null)
        {
            await store.PutAsync(claim.BlobName, job.ToBytes(), Tags.Job(stage, state, added, newDue ?? due), claim.LeaseId, false, none);
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
                ignoreUntil[claim.BlobName] = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);
                try { await store.SetMetadataAsync(claim.BlobName, new Dictionary<string, string>(), claim.LeaseId, none); } catch { }
                break;
            case Outcome.Permanent:
                job.LastError = Redact.Sas(error); job.Permanent = true; job.NodeId = o.NodeId;
                await Rewrite("failed");
                break;
            case Outcome.Transient:
                job.Attempts++; job.LastError = Redact.Sas(error); job.NodeId = o.NodeId;
                await Rewrite(job.Attempts >= Protocol.MaxAttempts ? "failed" : "pending");
                ignoreUntil[claim.BlobName] = DateTimeOffset.UtcNow + o.TransientBackoff(job.Attempts);
                break;
            case Outcome.Bot:
                stageBackoff[stage] = DateTimeOffset.UtcNow + o.BotBackoff;
                Log.Info($"[{stage}] bot block / rate limit: backing off {o.BotBackoff.TotalMinutes:F0} min");
                try { await store.SetMetadataAsync(claim.BlobName, new Dictionary<string, string>(), claim.LeaseId, none); } catch { }
                break;
            case Outcome.Released:
                try { await store.SetMetadataAsync(claim.BlobName, new Dictionary<string, string>(), claim.LeaseId, none); } catch { }
                break;
        }
        await store.ReleaseLeaseAsync(claim.BlobName, claim.LeaseId, none);
    }
}
