using System.Collections.Concurrent;

namespace IndexNode;

/// <summary>0.13.0: the node's work loop on the queue (PROTOCOL "Job queue: one log"): look (one tail read, one active/ listing), claim a subject, run its stages, record each step, finish with one append and delete the active file.</summary>
public sealed partial class NodeEngine
{
    /// <summary>The standard (non-AI) steps of a process claim, in order.</summary>
    public static readonly string[] StandardSteps = ["frames", "sscd", "centers", "thumbs", "transcribe", "ocrsubs", "cast", "sync"];
    public static readonly string[] AiStageNames = ["describe", "translate"];

    private JobQueue queue = null!;
    public JobQueue Queue => queue;
    private readonly ConcurrentDictionary<string, DateTimeOffset> ignoreSubject = new();
    private readonly ConcurrentDictionary<string, byte> shadowTaken = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> stageBackoff = new();
    private readonly string instanceTag = Guid.NewGuid().ToString("N")[..6];
    private OpCounter ops => o.Ops ?? OpCounter.Current;

    /// <summary>Whether this node runs a queue stage at all (its --stages, the AI opt-in, control.json, a bot back-off).</summary>
    private bool StageEnabled(string stage)
    {
        bool listed = stage switch
        {
            "process" => o.Stages.Contains("process") || StandardSteps.Any(s => s != "sync" && o.Stages.Contains(s)),
            "search-index" => handlers.ContainsKey("search-index") && (o.Stages.Contains("search-index") || o.Stages.Contains("process") || StandardSteps.Any(s => s != "sync" && o.Stages.Contains(s))),   // 0.13.1: standard (non-AI) work of any processor node
            "actors-index" => handlers.ContainsKey("actors-index") && (o.Stages.Contains("actors-index") || o.Stages.Contains("actors") || o.Stages.Contains("process") || StandardSteps.Any(s => s != "sync" && o.Stages.Contains(s))),   // 0.13.23: standard work of any process node, the single writer of the actor index
            "group" => handlers.ContainsKey("group") && (o.Stages.Contains("group") || o.Stages.Contains("process") || StandardSteps.Any(s => s != "sync" && o.Stages.Contains(s))),   // 0.13.10: standard work of any process node
            "actors" => handlers.ContainsKey("actors") && (o.Stages.Contains("actors") || o.Stages.Contains("process") || StandardSteps.Any(s => s != "sync" && o.Stages.Contains(s))),   // standard work of any process node (needs the cast step's files and the r50 model)
            "sample" => handlers.ContainsKey("sample") && (o.Stages.Contains("sample") || o.Stages.Contains("download")),   // 0.13.16: downloader nodes only (it needs YouTube)
            "check" => handlers.ContainsKey("check") && (o.Stages.Contains("check") || o.Stages.Contains("process") || StandardSteps.Any(s => s != "sync" && o.Stages.Contains(s))),   // 0.13.16: any processor, no YouTube
            "describe" or "translate" => o.AiStages,   // opt-in: only a node started with the AI stages and an endpoint
            _ => o.Stages.Contains(stage),
        };
        if (!listed) return false;
        if (Control.Effective(stage) != Level.Run) return false;
        if (stage == "classify" && !Control.Current.ClassifyOn) return false;   // 0.13.26: classify is paused unless control classify=on
        return !(stageBackoff.TryGetValue(stage, out var u) && u > DateTimeOffset.UtcNow);
    }

    /// <summary>The steps of a process entry this node would run, in order: standard steps, listed for the node (or all of them for a node with no step list), supported, and not done yet.</summary>
    internal string[] PlanSteps(QEntry e) => PlanSteps(e.StepsDone ?? []);

    /// <summary>0.13.2: the steps whose output zip is not on the store (one HEAD each, never a hydrate). sync is not checked: it has no output when a video has no transcript or no text lines.</summary>
    internal static readonly string[] CheckedSteps = StandardSteps.Where(s => s != "sync").ToArray();

    internal string[] PlanSteps(IEnumerable<string> doneSteps)
    {
        var done = doneSteps.ToList();
        return StandardSteps.Where(s => !done.Contains(s) && (o.Stages.Contains("process") || o.Stages.Contains(s)) && (o.Steps == null || o.Steps.Contains(s)) && Control.Effective(s) == Level.Run).ToArray();
    }

    // 0.13.12: the node-wide group gate (frames listing + L), refreshed before each look for work
    private GroupSequencing.Gate? gate; private volatile GroupSequencing.Gate.View? gateView;

    private bool CanRunEntry(QSubject s, QEntry e) =>
        StageDeps.Held(e) ? false   // 0.13.24: an actors entry with a hold (AI) is never ready; a manual "actors add" replaces it
        : e.Hold == "ai" && !Control.Current.AiDramasOn ? false   // 0.13.23: an AI-held entry of any stage (a download too) waits for control aiDramas=on
        : e.Stage == "process" && s.Entries.TryGetValue("check", out var ck0) && !ck0.Final ? false   // 0.13.16: the check comes first (a process entry never runs the pre-check itself while a check is pending)
        : e.Stage == "group" ? gateView is not { } gv || gv.Runnable_(s)   // a blocked group step is never claimed and nothing is appended for it (the claim-time BlockersAsync stays as the race safety net)
        : e.Stage != "process" || (PlanSteps(e).Length > 0 && !(e.Hold == "ai" && !Control.Current.AiDramasOn));   // an AI-held drama waits for control aiDramas=on (it stays pending, priority unchanged)
   // an AI-held drama waits for control aiDramas=on (it stays pending, priority unchanged)

    // ---------------------------------------------------------------- the lanes

    private async Task LaneLoop(int lane, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (o.MaxJobs != null && Volatile.Read(ref reserved) >= o.MaxJobs) return;
                if (diskFull is { } full) { if (Interlocked.Exchange(ref diskLogged, 1) == 0) Log.Info($"node stopped: {full}"); runStop?.Cancel(); return; }
                bool worked = false; IdleEnd();
                if (QueueStages().Any(StageEnabled))
                {
                    QueueLook? look = null;
                    Interlocked.Increment(ref busyClaims);
                    try
                    {
                        try
                        {
                            if (StageEnabled("group"))
                            {
                                gate ??= new GroupSequencing.Gate(store, paths);
                                var gv = await gate.RefreshAsync(queue, queue.Now, ct); gateView = gv;
                                if (gate.Summary(gv, queue.Now) is { } gsum) Log.Info(gsum);
                            }
                            look = await queue.LookAsync(StageEnabled, CanRunEntry, ct, ReadyNow);
                            await Control.RefreshIfAnnouncedAsync(queue.Read(st => st.ControlETag), ct);   // 0.13.28: control.json is read only when a "ctl" line says it changed
                        }

                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { Log.Info($"queue: looking for work failed ({e.Message})"); }
                        if (look != null)
                        {
                            if (look.Stale.Count > 0) try { await queue.CleanStaleAsync(look.Stale, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
                            foreach (var (subject, runnable) in look.Candidates)
                            {
                                bool ran = false;
                                try { ran = await TryClaimAndRunAsync(lane, subject, runnable, ct); }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception e) { Log.Info($"[claim#{lane}] {subject.Id}: the claim failed outside a stage ({DiskGuard.Root(e).GetType().Name}: {Redact.Sas(DiskGuard.Root(e).Message)}); the node goes on"); ignoreSubject[subject.Id] = DateTimeOffset.UtcNow.AddMinutes(2); if (DiskGuard.IsDiskError(e) && o.FreeSpace((StageZipStore.Find(store) as StageZipStore)?.CacheDir ?? Path.GetTempPath()) is { } fr && fr < o.MinFreeBytes) diskFull ??= $"local disk full ({(StageZipStore.Find(store) as StageZipStore)?.CacheDir ?? Path.GetTempPath()}, {DiskGuard.Fmt(fr)} free)"; }
                                if (ran) { worked = true; break; }
                            }
                        }
                    }
                    finally { Interlocked.Decrement(ref busyClaims); }
                }
                if (!worked)
                {
                    if (await TryFetchFromServiceAsync(lane, ct)) continue;
                    await RetryFailedSearchIndexAsync(ct);
                    if (await TryPullForwardSearchIndexAsync(lane, ct)) continue;
                    RegistryChanged();
                    if (o.Once) return;
                    IdleBegin(); try { await Wait(NextTailWait(), ct); } finally { IdleEnd(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>0.13.1 catch-up: a node that runs the search-index stage adds one at start when none is pending (so the dramas processed since the last build get in).</summary>
    private async Task EnsureSearchIndexAsync(CancellationToken ct)
    {
        if (!o.SearchIndexCatchUp || !handlers.ContainsKey("search-index") || !StageEnabled("search-index")) return;
        try
        {
            if (o.HeadBuiltAt != null)
            {
                var builtAt = await o.HeadBuiltAt(ct);
                if (!queue.Read(st => SearchIndexJobs.IndexBehind(st, builtAt))) { Log.Info("search-index: the published index is up to date, no catch-up build"); return; }
            }
            await queue.AppendLinesAsync(st => SearchIndexJobs.AddLine(st, NowUtc, TimeSpan.Zero) is { } l ? [l] : null, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"search-index: catch-up add failed ({e.Message})"); }
    }

    /// <summary>0.13.17: true when the only work this node could ever run is a search-index entry still waiting out its debounce: it is enabled here, pending, not due, and the queue holds no claim by any node and no runnable entry of a stage this node runs.</summary>
    internal bool SearchIndexPullableForTest() => SearchIndexPullable();
    internal bool SearchIndexPullable()
    {
        if (!handlers.ContainsKey("search-index") || !StageEnabled("search-index")) return false;
        return queue.Read(st =>
        {
            var e = st.Entry(SearchIndexJobs.Subject, SearchIndexJobs.Stage);
            if (e is not { State: "pending" }) return false;
            var now = Protocol.Utc(NowUtc);
            if (e.Runnable(now)) return false;   // already runnable: the normal path takes it
            if (st.Subjects.Values.Any(s => s.ClaimBy != null)) return false;
            return st.Candidates(now, StageEnabled, CanRunEntry).Count == 0;
        });
    }

    private long haltLog;
    /// <summary>0.13.18: true while this node must not claim: the storage breaker is open, or control.json has storage: stop. One log line per minute.</summary>
    private bool StorageHalted()
    {
        bool why = o.Meter.Tripped, kill = Control.Current.StorageStopped;
        if (!why && !kill) return false;
        var m = Environment.TickCount64 / 60000; if (Interlocked.Exchange(ref haltLog, m) != m) Log.Info(kill ? "storage: control.json says storage: stop, not claiming" : "storage: the breaker is open, not claiming (" + o.Meter.TripMessage + ")");
        return true;
    }

    private async Task<bool> TryPullForwardSearchIndexAsync(int lane, CancellationToken ct)
    {
        if (StorageHalted()) return false;
        if (!SearchIndexPullable()) return false;
        if (!TryReserve()) return false;
        ClaimHandle? h;
        Interlocked.Increment(ref busyClaims);
        try
        {
            try { h = await queue.TryClaimAsync(SearchIndexJobs.Subject, s => s.ClaimBy == null && s.Entries.TryGetValue(SearchIndexJobs.Stage, out var e) && e.State == "pending" ? [SearchIndexJobs.Stage] : null, ct); }
            catch (OperationCanceledException) { Interlocked.Decrement(ref reserved); throw; }
            catch (Exception e) { Log.Info($"[search-index#{lane}] claim failed ({e.Message})"); h = null; }
            if (h == null) { Interlocked.Decrement(ref reserved); return false; }
            Log.Info("search-index: nothing else to run, building now (not waiting for the debounce)");
            await RunClaimAsync(lane, h, ct, pulled: true);
            return true;
        }
        finally { Interlocked.Decrement(ref busyClaims); }
    }

    private DateTimeOffset nextFetch;
    /// <summary>0.13.26: a downloader with nothing ready in its work queue asks the download service for one item (already claimed for it). The service answers none when it has nothing: the node waits a minute. A service that is down only means this stays empty.</summary>
    private async Task<bool> TryFetchFromServiceAsync(int lane, CancellationToken ct)
    {
        if (o.FetchNext == null || !o.DownloaderOnly || DateTimeOffset.UtcNow < nextFetch || StorageHalted()) return false;
        if (!TryReserve()) return false;
        ClaimHandle? h = null;
        try
        {
            NextClaim? got;
            try { got = await o.FetchNext(o.Stages.Where(StageEnabled).ToArray(), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Log.Info($"download: the service did not answer ({e.Message}); working from the work queue only"); got = null; nextFetch = DateTimeOffset.UtcNow.AddMinutes(1); }
            if (got == null) { nextFetch = DateTimeOffset.UtcNow.AddMinutes(1); return false; }
            await queue.PollAsync(ct);
            h = queue.AdoptClaim(got.Subject, got.Epoch, got.Lease);
        }
        catch { Interlocked.Decrement(ref reserved); throw; }
        if (h == null) { Interlocked.Decrement(ref reserved); return false; }
        Interlocked.Increment(ref busyClaims);
        try { Log.Info($"download: the service handed over {h.Subject}"); await RunClaimAsync(lane, h, ct); return true; }
        finally { Interlocked.Decrement(ref busyClaims); }
    }

    private DateTimeOffset nextSearchIndexRetry;
    /// <summary>0.13.3: a search-index entry that ended failed (attempts used up) is added again by a node that runs the stage, at most every 15 minutes: no operator step (the manual one is: indexnode jobs retry --stage search-index).</summary>
    private async Task RetryFailedSearchIndexAsync(CancellationToken ct)
    {
        if (!handlers.ContainsKey("search-index") || !StageEnabled("search-index") || DateTimeOffset.UtcNow < nextSearchIndexRetry) return;
        nextSearchIndexRetry = DateTimeOffset.UtcNow + SearchIndexJobs.Delay;
        try
        {
            if (!queue.Read(st => st.Entry(SearchIndexJobs.Subject, SearchIndexJobs.Stage) is { State: "failed" })) return;
            await queue.AppendLinesAsync(st => st.Entry(SearchIndexJobs.Subject, SearchIndexJobs.Stage) is { State: "failed" } && SearchIndexJobs.AddLine(st, NowUtc, TimeSpan.Zero) is { } l ? [l] : null, ct);
            Log.Info("search-index: the failed entry is added again (automatic retry)");
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"search-index: automatic retry failed ({e.Message})"); }
    }

    private static IEnumerable<string> QueueStages() => new[] { "sample", "check", "download", "process", "group", "actors", "describe", "translate", "refresh", "classify", "availability", "search-index", "actors-index" };

    /// <summary>Claims one candidate subject (limits, per-IP cap, then the protocol's claim) and runs it. False when nothing was claimed.</summary>
    private async Task<bool> TryClaimAndRunAsync(int lane, QSubject subject, List<QEntry> runnable, CancellationToken ct)
    {
        var id = subject.Id;
        if (StorageHalted()) return false;   // 0.13.18: the breaker is open or control.json says storage: stop
        if (ignoreSubject.TryGetValue(id, out var until) && until > DateTimeOffset.UtcNow) return false;
        if (IsAbandoned(subject)) return false;   // 0.13.36: attempted and released with nothing run: not again until a queue line for it, or a control change
        if (o.Shadow && shadowTaken.ContainsKey(id)) return false;   // 0.13.30: shadow claims are in-process: a subject is taken once per run, by one lane
        var first = runnable[0].Stage;
        if (o.Limits != null && !o.Limits.TryAcquire(first)) return false;
        if (runnable.Any(e => e.Stage is "download" or "sample"))
        {
            bool allowed;
            try { await EnsureFreshNetworkAsync(ct); allowed = await UnderIpCapAsync(ct); }
            catch (OperationCanceledException) { o.Limits?.Release(first, false); throw; }
            catch (Exception e) { Log.Info($"download: the per-IP cap check failed ({e.Message}); not capping this time"); allowed = true; }
            if (!allowed) { o.Limits?.Release(first, false); return false; }
        }
        if (!TryReserve()) { o.Limits?.Release(first, false); return false; }
        if (o.Shadow && !shadowTaken.TryAdd(id, 0)) { Interlocked.Decrement(ref reserved); o.Limits?.Release(first, false); return false; }
        ClaimHandle? h;
        try { h = await queue.TryClaimAsync(id, (st, s) => ClaimableStages(st, s), ct, null, ReadyNow); }
        catch (OperationCanceledException) { Interlocked.Decrement(ref reserved); o.Limits?.Release(first, false); throw; }
        catch (Exception e) { Log.Info($"[{first}#{lane}] {id}: claim failed ({e.Message})"); h = null; }
        if (h == null) { Interlocked.Decrement(ref reserved); o.Limits?.Release(first, false); MarkAbandoned(subject); return false; }   // 0.13.36: lost the lease race, or the claim found nothing runnable
        bool finished = await RunClaimAsync(lane, h, ct);
        o.Limits?.Release(first, finished);
        return true;
    }

    // 0.13.36: a subject attempted and abandoned (the claim found nothing runnable, or this node ran none of its steps) is not attempted again until a change signal: a log line that touched it (its Rev moves) or a control.json change. Not a timer.
    private readonly ConcurrentDictionary<string, (long Rev, int Ctl)> abandoned = new(StringComparer.Ordinal);
    private void MarkAbandoned(QSubject s) => abandoned[s.Id] = (s.Rev, Control.Changes);
    private bool IsAbandoned(QSubject s) => abandoned.TryGetValue(s.Id, out var a) && (a.Rev == s.Rev && a.Ctl == Control.Changes ? true : abandoned.TryRemove(s.Id, out _) && false);
    /// <summary>How many subjects this node is holding back until a change signal (for the status line and the tests).</summary>
    internal int AbandonedCount => abandoned.Count;

    /// <summary>0.13.32: the readiness options with the versions control.json requires now.</summary>
    private ReadyOptions ReadyNow => o.Ready with { Required = s => Control.Current.RequiredVersion(s) };

    /// <summary>What this node would run of the subject right now (also the check, after the active file is ours, that the work is not done already).</summary>
    private string[]? ClaimableStages(QueueState state, QSubject s) => StageDeps.ClaimStages(state, s, Protocol.Utc(NowUtc), StageEnabled, CanRunEntry, ReadyNow);

    // ---------------------------------------------------------------- one claim

    private enum Outcome { Done, Permanent, Bot, Transient, Released, LeaseLost, Skipped, Duplicate }

    private sealed class ClaimWork(ClaimHandle h, string id, Running run, CancellationTokenSource cts)
    {
        public ClaimHandle Handle = h; public string Id = id; public Running Run = run; public CancellationTokenSource Cts = cts;
        public LocalCacheStore? Local;
    }

    private async Task<bool> RunClaimAsync(int lane, ClaimHandle h, CancellationToken ct, bool pulled = false)
    {
        var id = h.Subject; var watch = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, h.Token);
        var run = new Running { Info = new RunningJob { JobId = id, Stage = "claim", Worker = lane, StartedAt = Protocol.NowUtc() }, Cts = cts };
        running[run] = 0; RegistryChanged();
        h.StartHeartbeat(o.RenewInterval, o.LeaseLimit, Wait);
        var work = new ClaimWork(h, id, run, cts);
        bool anyDone = false; var timing = run.Timer;
        Log.Info($"[claim#{lane}] claimed {id} (epoch {h.Epoch})");
        try
        {
            // the stages of the subject this node runs, in order; follow-ups the stages add (process -> describe) join the same claim when this node runs them
            for (int guard = 0; guard < 16 && !cts.IsCancellationRequested; guard++)
            {
                var entry = queue.Read(s => s.Get(id)?.Entries.Values.Where(e => (e.Runnable(Protocol.Utc(NowUtc)) || (pulled && e.Stage == SearchIndexJobs.Stage && e.State == "pending")) && StageEnabled(e.Stage) && CanRunEntry(s.Get(id)!, e)).OrderBy(e => QueueState.Rank(e.Stage)).FirstOrDefault());
                if (entry == null) break;
                var (outcome, error, last, ctx) = await RunStageAsync(lane, work, entry, cts.Token);
                if (outcome == Outcome.Done) { anyDone = true; if (last) break; continue; }
                if (outcome == Outcome.LeaseLost) { Log.Info($"[claim#{lane}] {id}: lease lost, dropping the result"); break; }
                await RecordFailureAsync(lane, work, entry, outcome, error, ctx);
                return false;
            }
            // nothing left this node runs: the claim ends (the final done was appended with the last stage; this closes a claim that had none)
            if (!h.Lost) await EndClaimAsync(work);
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { await h.FinishAsync(); } catch { }
            work.Local?.Dispose();
            try { StageZipStore.ReleaseAll(store, id); } catch (Exception e) { Log.Info($"[claim#{lane}] {id}: could not remove the downloaded zips ({e.Message})"); }   // 0.13.34: done, failed, Transient or released: the zips of this claim go
            running.TryRemove(run, out _);
            cts.Cancel();
            if (timing.Line(o.Device?.Invoke() ?? "cpu") is { Length: > 0 } t) Log.Info($"[claim#{lane}] {id}: time by step: {t}");
            RegistryChanged();
            Log.Info($"[claim#{lane}] {id}: {(anyDone ? "done" : "ended")} in {watch.Elapsed.TotalSeconds:F1}s");
        }
        return anyDone;
    }

    /// <summary>Closes a claim whose last stage did not carry the final flag (e.g. a node that ran only some steps): one line that ends it without finishing anything.</summary>
    private async Task EndClaimAsync(ClaimWork w)
    {
        bool open = queue.Read(s => s.Get(w.Id) is { } sub && sub.ClaimBy == o.NodeId && sub.Epoch == w.Handle.Epoch);
        if (!open) return;
        try { await w.Handle.AppendAsync([new QLine { T = "done", Fin = true }], true, CancellationToken.None); } catch (LeaseLostException) { }
    }

    private static string StepName(string stage) => stage;

    private JobContext NewContext(int lane, ClaimWork w, string stage, JobDescriptor job, IStore jobStore, string[] stepsDone, CancellationToken token) => new()
    {
        Job = job, Stage = stage, BlobName = w.Id, LeaseId = w.Handle.Lease, Worker = lane, Store = jobStore, Paths = paths, NodeId = o.NodeId, Token = token, Queue = queue,
        StepsDone = stepsDone, ThreadShare = () => o.ThreadsPerWorker, Frames = gateView?.Frames, AiDramasOn = () => Control.Current.AiDramasOn,
        Report = (step, progress) => { if (step != null && step != w.Run.Info.Step) { w.Run.Info.Step = step; w.Run.Info.StartedAt = Protocol.NowUtc(); w.Run.Timer.Enter(step); } w.Run.Info.Progress = progress; },
    };

    private static JobDescriptor BodyOf(QEntry e, string id)
    {
        var j = e.Job ?? new JobDescriptor();
        j.JobId = id; j.Stage = e.Stage; j.Priority = e.Priority; j.Attempts = e.Attempts; j.LastError = e.LastError; if (j.AddedAt.Length == 0) j.AddedAt = e.Added;
        return j;
    }

    private async Task<(Outcome Outcome, string? Error, bool Last, JobContext? Ctx)> RunStageAsync(int lane, ClaimWork w, QEntry entry, CancellationToken token)
    {
        var id = w.Id; var stage = entry.Stage; var h = w.Handle;
        var job = BodyOf(entry, id);
        JobContext? ctx = null;
        using var budget = o.Meter.BeginStep(o.Meter.Limits.StepLimitFor(stage), $"[{stage}#{lane}] {id}");   // 0.13.18: this step's own store budget (max-per-step) and its own site table
        try
        {
            w.Run.Info.Stage = stage; w.Run.Info.Step = null; w.Run.Info.StartedAt = Protocol.NowUtc(); RegistryChanged();
            switch (stage)
            {
                case "process": return await RunProcessAsync(lane, w, entry, job, token);
                case "group":
                    {
                        // 0.13.10: sequence order. A drama waits (the entry goes back to pending, due in two minutes, nothing else written) while an earlier one is still in flight.
                        // a group entry of a drama without frames is not runnable (the gate keeps it from being claimed; this is the race safety net): a number is given only to a drama that has frames
                        if (queue.Read(st => st.Get(id)?.Seq) == null && (gate?.Frames.Contains(id) == true || gate == null && await store.HeadAsync(paths.FramesFile(id), token) != null)) await h.AssignSeqAsync(token);
                        var blockers = await GroupSequencing.BlockersAsync(queue, store, paths, id, token, gate?.Frames);
                        if (blockers.Count == 0 && queue.Read(st => st.Get(id)?.Seq) == null) blockers = [id];   // no frames, so no number: not runnable yet
                        if (blockers.Count > 0)
                        {
                            Log.Info($"group {id}: waiting for {blockers.Count} earlier drama(s) ({string.Join(", ", blockers.Take(3))}{(blockers.Count > 3 ? ", ..." : "")})");
                            await h.AppendAsync([new QLine { T = "done", Stage = "group", State = "pending", Due = Protocol.Utc(queue.Now + GroupSequencing.Wait) }], true, CancellationToken.None);
                            return (Outcome.Done, null, true, null);
                        }
                        var gj = JobDescriptor.NewStep("group", id); gj.Priority = entry.Priority;
                        ctx = NewContext(lane, w, "group", gj, store, [], token);
                        await handlers["group"].RunAsync(ctx);
                        var gl = new List<QLine> { new() { T = "done", Stage = "group", Join = ctx.GroupJoin, JoinKind = ctx.GroupJoinKind } };
                        if (handlers.ContainsKey("search-index") && queue.Read(st => SearchIndexJobs.AddLine(st, NowUtc, SearchIndexJobs.Delay)) is { } gsi) gl.Add(gsi);
                        // 0.13.24: actors becomes ready on its own once this group step is done (nothing is added for it); an AI-flagged upload gets the hold on actors here, where the flag is known (the stage checks the flag again when it runs)
                        if (queue.Read(st => st.Entry(id, "actors") == null) && AiHold.Flagged((await VideoJson.ReadAsync(store, paths, id, token))?.Root)) gl.Add(new QLine { T = "add", S = id, Stage = "actors", Job = NewFollow("actors", id, entry.Priority), At = queue.NowUtc(), Pri = entry.Priority, Hold = "ai" });
                        await h.AppendAsync(gl, !WillContinue(id, "group", gl), CancellationToken.None);
                        return (Outcome.Done, null, ClaimEnded(id), ctx);
                    }
                case "describe" or "translate":
                    {
                        var sj = JobDescriptor.NewStep(stage, id); sj.Priority = entry.Priority;
                        ctx = NewContext(lane, w, stage, sj, store, [], token);
                        await handlers[stage].RunAsync(ctx);
                        var lines = new List<QLine> { new() { T = "done", Stage = stage } };
                        if (stage == "describe") lines.Add(QLine.Add(id, "translate", NewFollow("translate", id, entry.Priority), queue.NowUtc()));
                        await h.AppendAsync(lines, !WillContinue(id, stage, lines), CancellationToken.None);
                        return (Outcome.Done, null, ClaimEnded(id), ctx);
                    }
                default:
                    {
                        if (stage is "download" or "sample") job.Network = JobNetwork.From(network);
                        ctx = NewContext(lane, w, stage, job, store, [], token);
                        await handlers[stage].RunAsync(ctx);
                        var lines = new List<QLine>();
                        var done = new QLine { T = "done", Stage = stage, Job = ctx.Job, Ver = ctx.PublishedVersion, StepVer = Array.IndexOf(StepVersions.Stages, stage) >= 0 ? StepVersions.Code(stage) : null };
                        job.FinishedAt = Protocol.NowUtc(); job.NodeId = o.NodeId;
                        if (ctx.NextDue is { } nd) { done.State = "pending"; done.Due = nd; }   // a source, the classifier, the sweep: pending again, due later
                        lines.Add(done); lines.AddRange(ctx.FollowUps);
                        await h.AppendAsync(lines, true, CancellationToken.None);
                        return (Outcome.Done, null, true, ctx);
                    }
            }
        }
        catch (PermanentFailure e) { return (Outcome.Permanent, e.Message, false, ctx); }
        catch (JobSkipped e) { return (Outcome.Skipped, e.Message, false, ctx); }
        catch (DuplicateFound e) { w.Run.Duplicate = e; return (Outcome.Duplicate, e.Message, false, ctx); }
        catch (BotBlocked e) { return (Outcome.Bot, e.Message, false, ctx); }
        catch (LeaseLostException e) { return (Outcome.LeaseLost, e.Message, false, ctx); }
        catch (OperationCanceledException) { return (h.Lost ? Outcome.LeaseLost : Outcome.Released, null, false, ctx); }
        catch (StorageBudgetExceeded e) { Log.Info($"[{stage}#{lane}] {id}: {e.Message}"); return (h.Lost ? Outcome.LeaseLost : Outcome.Transient, e.Message, false, ctx); }
        catch (Exception e) when (DiskGuard.IsDiskError(e))
        {   // 0.13.34: the local disk failed (full, or a write error): this claim fails as Transient, the node goes on; it stops only if the disk is still too full for the next download
            var dir = (StageZipStore.Find(store) as StageZipStore)?.CacheDir ?? Path.GetTempPath(); var free = o.FreeSpace(dir);
            Log.Info($"[{stage}#{lane}] {id}: LOCAL DISK ERROR writing under {dir} ({e.GetType().Name}: {DiskGuard.Root(e).Message}); free {DiskGuard.Fmt(free)}; this claim fails as Transient");
            if (free != null && free < o.MinFreeBytes) diskFull ??= $"local disk full ({dir}, {DiskGuard.Fmt(free)} free)";
            return (h.Lost ? Outcome.LeaseLost : Outcome.Transient, "local disk error: " + DiskGuard.Root(e).Message, false, ctx);
        }
        catch (Exception e) { Console.Error.WriteLine($"[{stage}#{lane}] {id}: {Redact.Trace(e)}"); return (h.Lost ? Outcome.LeaseLost : Outcome.Transient, e.Message, false, ctx); }
    }
    private volatile string? diskFull; private int diskLogged; private CancellationTokenSource? runStop;
    /// <summary>0.13.34: why the node stopped for the local disk (null = it did not).</summary>
    public string? DiskStop => diskFull;

    private static JobDescriptor NewFollow(string stage, string id, int priority) => new() { Stage = stage, JobId = id, Priority = priority, AddedAt = Protocol.NowUtc() };

    /// <summary>True when this node runs another stage of the subject after the lines are applied (then the claim stays open).</summary>
    private bool WillContinue(string id, string stage, List<QLine> lines)
    {
        var next = lines.Where(l => l.T == "add" && l.Stage != null && l.Hold == null).Select(l => l.Stage!).Where(StageEnabled);
        return next.Any();
    }

    private bool ClaimEnded(string id) => queue.Read(s => s.Get(id) is not { } sub || sub.ClaimBy != o.NodeId);

    // ---------------------------------------------------------------- the process claim: every standard step this node supports, in order, from local files

    private async Task<(Outcome, string?, bool, JobContext?)> RunProcessAsync(int lane, ClaimWork w, QEntry entry, JobDescriptor job, CancellationToken token)
    {
        var id = w.Id; var h = w.Handle;
        // 0.13.2: a step counts as done only if its output is on the store (the log can say done for a step that never ran: migration did); a missing output is run, not skipped
        // 0.13.18: ONE LIST of the video's folder answers every "is this output stored" below (it was a HEAD per step, per need and per check)
        var have = await VideoFilesAsync(id, token);
        var done = new List<string>(entry.StepsDone ?? []);
        foreach (var s0 in CheckedSteps.Where(done.Contains).ToList())
            if (!have.Contains(s0 + ".zip")) { done.Remove(s0); Log.Info($"process {id}: {s0} is in the done list but {s0}.zip is not on the store: it runs"); }
        var plan = PlanSteps(done);
        // the video must be downloaded: checked here, when we run, never while finding work (a HEAD never hydrates)
        if (!have.Contains("download.zip") && !have.Contains("downloaded.json")) return (Outcome.Permanent, "invalid: " + paths.Video(id, "download.zip") + " is missing (nothing was downloaded for this video)", false, null);
        // 0.13.3: the claim holds the video's lease, so it builds a missing video.json itself (the backfill's builder), then goes on
        if (o.EnsureVideoJson != null && !have.Contains("video.json"))
        {
            Log.Info($"process {id}: video.json is missing: building it from what is stored");
            await o.EnsureVideoJson(store, paths, id, token); have.Add("video.json");
        }
        w.Local ??= new LocalCacheStore(store, Path.Combine(Path.GetTempPath(), "indexnode", "c-" + instanceTag + "-" + id));
        // 0.13.0: the pre-check is the first step of a process claim, only for a drama with no processing done yet (a part-processed one resumes from its first unfinished step)
        bool ranPre = false;
        // 0.13.10: the sequence number is assigned at the first pre-check line, under the claim (and so under the drama's lease)
        if (queue.Read(s => s.Get(id)?.Seq) == null && handlers.ContainsKey("precheck")) await h.AssignSeqAsync(token);
        // 0.13.2: decided from the store only (no processing zip besides download/comments), never from the done set; the stage itself returns at once for a video.json that already has the record
        bool noProcessing = !HasProcessing(have);
        var vjPre = handlers.ContainsKey("precheck") && noProcessing ? (await VideoJson.RequireAsync(store, paths, id, token)).Root["precheck"] as System.Text.Json.Nodes.JsonObject : null;
        if (handlers.TryGetValue("precheck", out var pre) && noProcessing && (vjPre == null || PreCheckDecision.IsSkip((string?)vjPre["decision"])))
        {
            ranPre = true;
            w.Run.Info.Stage = "precheck"; w.Run.Info.Step = null; RegistryChanged();
            var pj = JobDescriptor.NewStep("precheck", id); pj.Priority = entry.Priority;
            var pctx = NewContext(lane, w, "precheck", pj, w.Local, done.ToArray(), token);
            await pre.RunAsync(pctx);   // throws DuplicateFound for a skip (nothing was deleted or rewritten: the done line marks the video for `indexnode trash empty`)
        }
        // the pre-check's decision (PASS included) goes on the final done line, read from the record the stage wrote
        string? pcDecision = null; System.Text.Json.Nodes.JsonObject? pcRec = null;
        if (handlers.ContainsKey("precheck")) { pcRec = (await VideoJson.RequireAsync(store, paths, id, token)).Root["precheck"] as System.Text.Json.Nodes.JsonObject; pcDecision = (string?)pcRec?["decision"]; }   // 0.13.2: video.json holds the record; unreadable or missing fails the claim (retried), never a quiet null
        bool pcAi = (bool?)pcRec?["ai"]?["flag"] == true; double pcScore = (double?)pcRec?["ai"]?["score"] ?? 0;
        // 0.13.1: an AI-flagged drama that is not a duplicate (that was decided first) is held, not skipped: the pre-check's done step goes on the log and the claim is released; with aiDramas on it just goes on
        if (noProcessing && pcAi && !PreCheckDecision.IsSkip(pcDecision) && !Control.Current.AiDramasOn)
        {
            Log.Info($"process {id}: pre-check says AI (score {pcScore:0.000}): held, aiDramas is off");
            await h.AppendAsync([new QLine { T = "done", Stage = "process", Step = "precheck", PreCheck = pcDecision, Hold = "ai", AiScore = pcScore, Why = $"ai: score {pcScore:0.000}", Pre = pcRec }], true, CancellationToken.None);
            return (Outcome.Done, null, ClaimEnded(id), null);
        }
        var avail = new HashSet<string>(StringComparer.Ordinal);
        JobContext? ctx = null; var ran = new List<string>();
        foreach (var step in plan)
        {
            if (token.IsCancellationRequested) return (h.Lost ? Outcome.LeaseLost : Outcome.Released, null, false, ctx);
            if (Control.Effective(step) != Level.Run) continue;
            // inputs: made earlier in this claim, or already stored; a step whose producer is not ours and is not done yet is not ready for this node
            foreach (var need in Protocol.NormalizeNeeds(Protocol.NeedsOf(step)))
            {
                if (avail.Contains(need) || done.Contains(ProducerOf(need) ?? "")) { if (!avail.Contains(need) && !have.Contains(need)) return (Outcome.Permanent, $"invalid: {step} needs {need}, which is missing", false, ctx); continue; }
                if (have.Contains(need)) { avail.Add(need); continue; }
                goto notReady;
            }
            w.Run.Info.Stage = step; w.Run.Info.Step = null; RegistryChanged();
            var sj = JobDescriptor.NewStep(step, id); sj.Priority = entry.Priority;
            ctx = NewContext(lane, w, step, sj, w.Local, done.ToArray(), token);
            using (o.Meter.BeginStep(o.Meter.Limits.StepLimitFor("process:" + step), $"[process:{step}#{lane}] {id}")) await handlers[step].RunAsync(ctx);   // 0.13.32: each step has its own named budget
            done.Add(step); ran.Add(step); avail.Add(step + ".zip");
            var stepLines = new List<QLine> { new() { T = "done", Stage = "process", Step = step, StepVer = Array.IndexOf(StepVersions.Steps, step) >= 0 ? ctx.StepVersion ?? StepVersions.Code(step) : null } };
            // 0.13.1: a new centers.zip means the search index is behind: add the singleton, due in 15 minutes (nothing when one is pending)
            if (step == "centers" && handlers.ContainsKey("search-index") && queue.Read(st => SearchIndexJobs.AddLine(st, NowUtc, SearchIndexJobs.Delay)) is { } si) stepLines.Add(si);
            await h.AppendAsync(stepLines, false, CancellationToken.None);
            continue;
            notReady:;
        }
        bool complete = StandardSteps.All(done.Contains);
        if (complete)
        {
            // 0.13.2: never a final done with an output missing (one HEAD each, never a hydrate): the claim fails (transient) and the next one runs what is missing
            var missing = new List<string>();
            var now = await VideoFilesAsync(id, token);   // one LIST, after the steps ran
            foreach (var s1 in CheckedSteps) if (!now.Contains(s1 + ".zip")) missing.Add(s1);
            if (missing.Count > 0) throw new InvalidOperationException($"process {id}: every step is done but {string.Join(", ", missing.Select(m => m + ".zip"))} is not on the store; not finishing (the missing step runs on the next claim)");
            var lines = new List<QLine> { new() { T = "done", Stage = "process", PreCheck = pcDecision, Group = (string?)pcRec?["groupId"], GroupOffset = (double?)pcRec?["groupOffset"], MatchShare = (double?)pcRec?["matchShare"], DupShare = (double?)pcRec?["dupShare"], Mixed = (bool?)(pcRec?["group"] as System.Text.Json.Nodes.JsonObject)?["mixed"] is true ? true : null } };
            if (handlers.ContainsKey("group") && queue.Read(st => st.Entry(id, "group")) == null) lines.Add(QLine.Add(id, "group", GroupSequencing.NewJob(id, entry.Priority), queue.NowUtc()));
            lines.Add(QLine.Add(id, "describe", NewFollow("describe", id, entry.Priority), queue.NowUtc()));
            await h.AppendAsync(lines, !WillContinue(id, "process", lines), CancellationToken.None);
            return (Outcome.Done, null, ClaimEnded(id), ctx);
        }
        if (ran.Count == 0) { return (Outcome.Skipped, "no step of this video is ready for this node", false, ctx); }
        return (Outcome.Done, null, false, ctx);   // some steps done; the rest are for another node: the claim ends without finishing the entry
    }

    /// <summary>Any step output (a stage zip other than download/comments) (a recorded pre-check is handled by the pre-check itself): processing has started on this video.</summary>
    /// <summary>0.13.18: the file names (not paths) in the video's folder, from one LIST.</summary>
    private async Task<HashSet<string>> VideoFilesAsync(string id, CancellationToken ct)
    {
        var list = await StageZipStore.ListRawAsync(store, paths.VideoDir(id), ct);   // names only: no stage zip is opened for the question "is this output stored"
        return list.Select(b => b.Name[(b.Name.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.Ordinal);
    }
    private static bool HasProcessing(HashSet<string> have) => have.Any(n => n == "processed.json" || (n.EndsWith(".zip", StringComparison.Ordinal) && n is not ("download.zip" or "comments.zip")));

    private static string? ProducerOf(string zip) => zip.EndsWith(".zip", StringComparison.Ordinal) ? zip[..^4] : null;

    // ---------------------------------------------------------------- failures

    private async Task RecordFailureAsync(int lane, ClaimWork w, QEntry entry, Outcome outcome, string? error, JobContext? ctx)
    {
        var id = w.Id; var h = w.Handle; var stage = entry.Stage; var none = CancellationToken.None;
        var job = BodyOf(entry, id);
        if (ctx?.Job != null && stage is not ("process" or "describe" or "translate")) job = ctx.Job;
        if (stage is "download" or "sample") job.Network ??= JobNetwork.From(network);
        var steps = queue.Read(s => s.Entry(id, stage)?.StepsDone)?.ToArray();
        var at = queue.NowUtc();
        QLine L(string state) => new() { T = "failed", Stage = stage, State = state, Error = Redact.Sas(error), Attempt = entry.Attempts, Done = steps, Step = ctx?.Stage is { } cs && stage == "process" ? cs : null };
        try
        {
            switch (outcome)
            {
                case Outcome.Duplicate:
                    {
                        var d = w.Run.Duplicate;
                        job.FinishedAt = Protocol.NowUtc(); job.NodeId = o.NodeId; job.DuplicateOf = d is { Ai: true } ? null : d?.Upload; job.DuplicateShare = d is { Ai: true } ? null : d?.Share; job.LastError = null;
                        job.DuplicateSpeechLanguage = d?.SpeechLanguage; job.DuplicateDubbed = d?.Dubbed; job.DuplicateLanguageSource = d?.LanguageSource; job.DuplicateMetadataLanguage = d?.MetadataLanguage; job.DuplicateUrl = d?.Url;
                        // 0.13.16: a check on a sample marks nothing (no video stored); a check on a stored video marks it as a process skip does, and ends the waiting process entry the same way
                        bool stored = stage != "check" || await store.HeadAsync(paths.Video(id, "video.mp4"), none) != null;
                        var dl = new List<QLine> { new QLine { T = "done", Stage = stage, State = "duplicate", Job = job, Trash = stored ? "download.zip/video" : null, Why = d?.Why, Group = d?.Group, GroupOffset = d?.GroupOffset, MatchShare = d?.MatchShare, DupShare = d?.DupShare, Mixed = d?.Mixed } };
                        if (stage == "check" && queue.Read(st => st.Entry(id, "process") is { Final: false })) dl.Add(new QLine { T = "done", Stage = "process", State = "duplicate", Job = BodyOf(queue.Read(st => st.Entry(id, "process"))!, id), Why = d?.Why, Group = d?.Group, GroupOffset = d?.GroupOffset, MatchShare = d?.MatchShare, DupShare = d?.DupShare, Mixed = d?.Mixed });
                        await h.AppendAsync(dl, true, none);   // the pre-check skipped it (process entry ends in 'duplicate': metadata only, no frames or later artifacts, no describe)
                        break;
                    }
                case Outcome.Permanent:
                    { var l = L("failed"); l.Permanent = true; if (stage == "download") l.Job = job; await h.AppendAsync([l], true, none); break; }
                case Outcome.Transient:
                    {
                        var l = L("pending"); l.Attempt = entry.Attempts + 1; l.Job = stage is "download" or "refresh" or "classify" or "availability" ? job : null; job.NodeId = o.NodeId;   // the body carries the network of the attempt
                        if (l.Attempt >= Protocol.MaxAttempts) l.State = "failed"; else l.Due = Protocol.Utc(NowUtc + o.TransientBackoff(l.Attempt.Value));
                        await h.AppendAsync([l], true, none);
                        ignoreSubject[id] = DateTimeOffset.UtcNow + o.TransientBackoff(l.Attempt.Value);
                        break;
                    }
                case Outcome.Bot:
                    {
                        var l = L("pending"); l.Job = job; job.NodeId = o.NodeId; await h.AppendAsync([l], true, none);   // no attempt counted; the error and the network stay on the job
                        stageBackoff[stage] = DateTimeOffset.UtcNow + o.BotBackoff; if (stage == "sample") stageBackoff["download"] = stageBackoff[stage];
                        Log.Info($"[{stage}] bot block / rate limit: backing off {o.BotBackoff.TotalMinutes:F0} min");
                        break;
                    }
                case Outcome.Skipped:
                    await h.AbandonAsync(); if (queue.Read(s => s.Get(id)) is { } sk) MarkAbandoned(sk); break;   // this node cannot do it: left to another until a line for it or a control change; no line, no attempt
                case Outcome.Released:
                    await h.AbandonAsync(); break;
            }
        }
        catch (LeaseLostException) { Log.Info($"[claim#{lane}] {id}: lease lost before the outcome could be recorded; dropped"); }
        catch (Exception e) { Log.Info($"[claim#{lane}] could not record the outcome for {id}: {e.Message}"); }
        if (stage is "process" or "describe" or "translate" || true) StageZipStore.Stage(store)?.DiscardStaged(id, Protocol.IsStep(stage) || stage is "download" ? stage : null);
        Log.Info($"[claim#{lane}] {id}: {stage} {outcome}{(error != null ? " (" + Redact.Sas(error) + ")" : "")}");
    }
}
