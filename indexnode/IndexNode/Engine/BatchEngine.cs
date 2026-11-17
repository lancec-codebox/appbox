using System.Collections.Concurrent;
using System.Diagnostics;

namespace IndexNode;

/// <summary>0.9.0 stage batches: a node takes a batch of dramas and runs one stage at a time across all of them (frames, sscd, thumbs, transcribe, ocrsubs, cast, sync), --lanes dramas side by side, models loaded per stage and unloaded after it.</summary>
public sealed partial class NodeEngine
{
    private sealed class BatchDrama(string id, double seconds, LocalCacheStore local)
    {
        public string Id { get; } = id; public double Seconds { get; } = seconds; public LocalCacheStore Local { get; } = local;
        /// <summary>The pending step job of each stage this batch runs for the drama.</summary>
        public readonly Dictionary<string, BlobEntry> Steps = new();
        /// <summary>The lease on index/holds/&lt;id&gt;.json: that lease is the hold. Null once lost or released.</summary>
        public volatile string? HoldLease; public string Since = "";
        public BlobEntry? EntryFor(string stage) { lock (Steps) return HoldLease == null ? null : Steps.GetValueOrDefault(stage); }
        public bool HasStagesAfter(string stage) { lock (Steps) return StageOrder.PassOrder.SkipWhile(x => x != stage).Skip(1).Any(Steps.ContainsKey); }
    }

    private int batchNumber;
    /// <summary>Keeps this engine's work folders apart from another engine's in the same process (tests run several nodes in one process).</summary>
    private readonly string instanceTag = Guid.NewGuid().ToString("N")[..6];
    private const double UnknownSeconds = 1200;

    private async Task BatchLoop(CancellationToken ct)
    {
        var state = new WorkerState(0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (o.MaxJobs != null && Volatile.Read(ref reserved) >= o.MaxJobs) return;
                await Control.RefreshAsync(ct, force: true);
                var open = o.Stages.Where(st => IsPassStage(st) && Control.Effective(st) == Level.Run && !(state.StageBackoff.TryGetValue(st, out var u) && u > DateTimeOffset.UtcNow)).ToList();
                int ran = 0;
                Interlocked.Increment(ref busyClaims);
                try
                {
                    if (open.Count > 0)
                    {
                        List<BatchDrama> batch = [];
                        try { batch = await SelectBatchAsync(state, open, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { Log.Info($"batch: looking for work failed ({e.Message})"); }
                        if (batch.Count > 0) ran = await RunBatchAsync(state, batch, ct);
                    }
                }
                finally { Interlocked.Decrement(ref busyClaims); }
                if (ran == 0)
                {
                    RegistryChanged();
                    if (o.Once) return;
                    await Task.Delay(o.PollInterval, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<double> DurationOfAsync(string id, CancellationToken ct)
    {
        try
        {
            if (await store.GetAsync(paths.Video(id, "downloaded.json"), ct) is { } c && StoredJson.Doc(c.Data, id).RootElement.TryGetProperty("durationSeconds", out var d) && d.GetDouble() > 0) return d.GetDouble();
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return UnknownSeconds;
    }

    /// <summary>The best dramas first (priority, then the oldest pending step), up to --batch of them or --batch-hours of video; each drama's pending steps this node runs are held (idx_hold) for 10 minutes.</summary>
    private async Task<List<BatchDrama>> SelectBatchAsync(WorkerState state, List<string> open, CancellationToken ct)
    {
        var foreign = await ForeignHoldsAsync(ct);
        var picks = await StageOrder.DramasAsync(open, FindPendingAsync, (st, b, c) => EligibleAsync(state, st, b, c), ct, Math.Max(1, o.Batch));
        var root = Path.Combine(Path.GetTempPath(), "indexnode", "batch-" + Environment.ProcessId + "-" + instanceTag + "-" + (batchNumber + 1));
        var batch = new List<BatchDrama>(); double total = 0, cap = o.BatchHours * 3600;
        foreach (var pick in picks)
        {
            if (batch.Count >= Math.Max(1, o.Batch)) break;
            var seconds = await DurationOfAsync(pick.Id, ct);
            var d = new BatchDrama(pick.Id, seconds, new LocalCacheStore(store, Path.Combine(root, pick.Id)));
            foreach (var stage in StageOrder.PassOrder)
                if (open.Contains(stage) && pick.Members.TryGetValue(stage, out var b)) lock (d.Steps) d.Steps[stage] = b;
            if (d.Steps.Count == 0 || foreign.Contains(pick.Id) || !await TryHoldAsync(d, ct)) { d.Local.Dispose(); continue; }
            batch.Add(d); total += seconds;
            if (total >= cap) break;
        }
        if (batch.Count > 0) { batchNumber++; Log.Info($"[batch#{batchNumber}] {batch.Count} drama(s), {total / 3600:F1} h of video: {string.Join(", ", batch.Select(d => d.Id + "(" + string.Join('+', d.Steps.Keys) + ")"))}"); }
        return batch;
    }

    /// <summary>Video ids whose hold blob is leased by another node right now (a consistent listing with metadata, not the tag query).</summary>
    private async Task<HashSet<string>> ForeignHoldsAsync(CancellationToken ct)
    {
        var set = new HashSet<string>();
        foreach (var e in await store.ListAsync(paths.HoldsDir, false, true, ct))
            if (e.LeaseState == "leased" && HoldInfo.FromMetadata(e.Metadata)?.Node != o.NodeId) set.Add(Path.GetFileNameWithoutExtension(e.Name));
        return set;
    }

    /// <summary>Takes the drama's hold: creates index/holds/&lt;id&gt;.json if missing (If-None-Match *), then acquires its 60 s lease; the lease is the hold. A blob whose lease has expired is taken over the same way; acquire is atomic, so one node wins.</summary>
    private async Task<bool> TryHoldAsync(BatchDrama d, CancellationToken ct)
    {
        var name = paths.Hold(d.Id);
        d.Since = Protocol.NowUtc();
        var info = HoldInfo.New(o.NodeId, "batch-" + (batchNumber + 1), d.Since);
        bool created = await store.PutAsync(name, info.Body(), info.HoldTags(), null, true, ct);
        var lease = await store.AcquireLeaseAsync(name, Protocol.LeaseSeconds, ct);
        if (lease == null) return false;
        try
        {
            if (!created) await store.PutAsync(name, info.Body(), info.HoldTags(), lease, false, ct);
            await store.SetMetadataAsync(name, info.Metadata(), lease, ct);
        }
        catch { try { await store.ReleaseLeaseAsync(name, lease, CancellationToken.None); } catch { } throw; }
        d.HoldLease = lease;
        return true;
    }

    /// <summary>Deletes the hold blob under its lease (a clean stop, or the drama's last batch stage is done).</summary>
    private async Task ReleaseHoldAsync(BatchDrama d)
    {
        var lease = d.HoldLease; d.HoldLease = null;
        if (lease == null) return;
        try { await store.DeleteAsync(paths.Hold(d.Id), lease, CancellationToken.None); }
        catch (Exception e) { Log.Info($"could not delete the hold on {d.Id} ({e.Message}); the lease runs out by itself"); }
    }

    /// <summary>Renews every hold lease and re-sends its metadata and tags (a Put keeps neither unless they are re-sent); idx_until is "last renewed".</summary>
    private async Task HoldRenewLoopAsync(List<BatchDrama> batch, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Wait(o.HoldRenew, token);
                foreach (var d in batch)
                {
                    var lease = d.HoldLease;
                    if (lease == null) continue;
                    try
                    {
                        await store.RenewLeaseAsync(paths.Hold(d.Id), lease, token);
                        var info = HoldInfo.New(o.NodeId, "batch-" + batchNumber, d.Since);
                        await store.SetMetadataAsync(paths.Hold(d.Id), info.Metadata(), lease, token);
                        await store.SetTagsAsync(paths.Hold(d.Id), info.HoldTags(), lease, token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (LeaseLostException) { d.HoldLease = null; Log.Info($"hold on {d.Id} lost; its remaining steps are left to others"); }
                    catch (Exception e) { Log.Info($"hold renewal for {d.Id} failed ({e.Message}); will retry"); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void UnloadStageModels()
    {
        if (o.UnloadModels != null) { o.UnloadModels(); return; }
        int n = OrtOptions.UnloadShared() + Transcribe.UnloadRecognizers();
        if (n > 0) { GC.Collect(); Log.Info($"unloaded {n} model(s)"); }
    }

    /// <summary>Runs the batch stage by stage. Returns the number of step jobs that were started.</summary>
    private async Task<int> RunBatchAsync(WorkerState state, List<BatchDrama> batch, CancellationToken ct)
    {
        int number = batchNumber, ran = 0;
        using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewer = Task.Run(() => HoldRenewLoopAsync(batch, renewCts.Token));
        try
        {
            foreach (var stage in StageOrder.PassOrder)
            {
                if (!o.Stages.Contains(stage)) continue;
                var items = batch.Where(d => d.EntryFor(stage) != null).ToList();
                if (items.Count == 0) continue;
                if (ct.IsCancellationRequested) break;
                if (Control.Effective(stage) != Level.Run || (state.StageBackoff.TryGetValue(stage, out var bo) && bo > DateTimeOffset.UtcNow))
                {
                    foreach (var d in items) lock (d.Steps) d.Steps.Remove(stage);
                    foreach (var d in items) if (d.Steps.Count == 0) await ReleaseHoldAsync(d);
                    continue;
                }
                var sw = Stopwatch.StartNew(); int before = ran;
                var queue = new ConcurrentQueue<BatchDrama>(items);
                var lanes = Enumerable.Range(1, Math.Max(1, o.Lanes)).Select(i => new WorkerState(i) { IgnoreUntil = state.IgnoreUntil, StageBackoff = state.StageBackoff, Threads = o.ThreadsPerLane }).ToList();
                await Task.WhenAll(lanes.Select(lane => Task.Run(async () =>
                {
                    try { while (!ct.IsCancellationRequested && queue.TryDequeue(out var d)) if (await RunStepAsync(lane, d, stage, ct)) Interlocked.Increment(ref ran); }
                    catch (OperationCanceledException) { }
                })));
                foreach (var d in items) if (!d.HasStagesAfter(stage)) await ReleaseHoldAsync(d);   // the drama's last batch stage is done
                UnloadStageModels();
                Log.Info($"[batch#{number}] stage {stage} done for {ran - before}/{items.Count} drama(s) in {sw.Elapsed.TotalSeconds:F1}s");
            }
        }
        finally
        {
            renewCts.Cancel();
            try { await renewer; } catch { }
            // a clean stop (or the end of the batch): every hold still taken is deleted at once
            foreach (var d in batch) await ReleaseHoldAsync(d);
            foreach (var d in batch) d.Local.Dispose();
            if (Environment.GetEnvironmentVariable("INDEXNODE_KEEP_WORK") == null)
                try { Directory.Delete(Path.Combine(Path.GetTempPath(), "indexnode", "batch-" + Environment.ProcessId + "-" + instanceTag + "-" + number), true); } catch { }
            try { UnloadStageModels(); } catch { }
            using var me = Process.GetCurrentProcess();
            Log.Info($"[batch#{number}] finished: {ran} step(s); peak memory {me.PeakWorkingSet64 / 1048576} MB");
        }
        return ran;
    }

    /// <summary>One drama's step: the normal 60 s lease, the hold goes back to "0", the handler runs against the batch's local folder and the outcome is recorded at once.</summary>
    private async Task<bool> RunStepAsync(WorkerState lane, BatchDrama d, string stage, CancellationToken ct)
    {
        var entry = d.EntryFor(stage);
        if (entry == null) return false;
        if (o.Limits != null && !o.Limits.TryAcquire(stage)) return false;
        if (!TryReserve()) { o.Limits?.Release(stage, false); return false; }
        Claim? claim;
        try { claim = await TryClaimOneAsync(lane, stage, entry, ct, null); }
        catch (OperationCanceledException) { Interlocked.Decrement(ref reserved); o.Limits?.Release(stage, false); throw; }
        catch (Exception e) { Log.Info($"[{stage}] {d.Id}: claim failed ({e.Message})"); claim = null; }
        if (claim == null) { Interlocked.Decrement(ref reserved); o.Limits?.Release(stage, false); return false; }
        lock (d.Steps) d.Steps.Remove(stage);
        var finished = await RunJobAsync(lane, stage, claim, d.Local, ct);
        o.Limits?.Release(stage, finished);
        return true;
    }
}
