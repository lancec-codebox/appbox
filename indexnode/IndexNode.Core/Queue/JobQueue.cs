using System.Text;

namespace IndexNode;

/// <summary>The lease on jobs/active/&lt;subject&gt;.json and the epoch of the claim line that goes with it. A worker appends only through its handle: every append is fenced.</summary>
public sealed class ClaimHandle
{
    private readonly JobQueue queue; private readonly IStore store; private readonly string activeName;
    internal ClaimHandle(JobQueue q, IStore store, string subject, int epoch, string lease, string activeName)
    { queue = q; this.store = store; Subject = subject; Epoch = epoch; Lease = lease; this.activeName = activeName; Cts = new(); }
    public string Subject { get; }
    public int Epoch { get; }
    public string Lease { get; }
    /// <summary>Cancelled when the lease is lost: the worker stops (and appends nothing more).</summary>
    public CancellationTokenSource Cts { get; }
    public CancellationToken Token => Cts.Token;
    public volatile bool Lost;
    private CancellationTokenSource? beat;
    private Task? beatTask;

    /// <summary>The lease is renewed every <paramref name="every"/> (the heartbeat; it never goes in the log). Renewals that keep failing for <paramref name="limit"/> count as lost.</summary>
    public void StartHeartbeat(TimeSpan every, TimeSpan limit, Func<TimeSpan, CancellationToken, Task> delay)
    {
        beat = new CancellationTokenSource();
        var t = beat.Token;
        beatTask = Task.Run(async () =>
        {
            var lastOk = DateTimeOffset.UtcNow;
            try
            {
                while (true)
                {
                    await delay(every, t);
                    try
                    {
                        using var rc = CancellationTokenSource.CreateLinkedTokenSource(t); rc.CancelAfter(limit);   // a renewal that never answers counts as a failure after the limit
                        await store.RenewLeaseAsync(activeName, Lease, rc.Token); lastOk = DateTimeOffset.UtcNow;
                    }
                    catch (LeaseLostException) { MarkLost(); return; }
                    catch (OperationCanceledException) when (t.IsCancellationRequested) { throw; }
                    catch (Exception e)
                    {
                        if (DateTimeOffset.UtcNow - lastOk >= limit) { Log.Info($"lease on {Subject} not renewed for {(DateTimeOffset.UtcNow - lastOk).TotalSeconds:0} s ({e.Message}): treating it as lost"); MarkLost(); return; }
                        Log.Info($"lease renewal for {Subject} failed ({e.Message}); will retry");
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    internal void MarkLost() { Lost = true; try { Cts.Cancel(); } catch (ObjectDisposedException) { } }

    private async Task StopHeartbeatAsync()
    {
        if (beat == null) return;
        beat.Cancel();
        try { await beatTask!; } catch { }
        beat.Dispose(); beat = null;
    }

    /// <summary>Appends lines for this claim. A worker whose lease is gone, or that sees a newer claim of the subject after reading the log, throws <see cref="LeaseLostException"/> and appends nothing. <paramref name="final"/> marks the claim as ended by the last done/failed line.</summary>
    public async Task AppendAsync(IReadOnlyList<QLine> lines, bool final, CancellationToken ct)
    {
        if (Lost) throw new LeaseLostException($"lease on {Subject} is lost");
        var ok = await queue.AppendAsync(state =>
        {
            var s = state.Get(Subject);
            if (s == null || s.Epoch != Epoch || s.ClaimBy != queue.NodeId) { MarkLost(); return null; }
            var at = queue.NowUtc();
            foreach (var l in lines) { if (l.S.Length == 0) l.S = Subject; l.By = queue.NodeId; l.At ??= at; if (l.T is "done" or "failed" or "claim") l.Epoch = Epoch; }
            if (final) { var last = lines.LastOrDefault(l => l.T is "done" or "failed"); if (last != null) last.Fin = true; }
            return lines.ToList();
        }, ct);
        if (!ok) throw new LeaseLostException($"{Subject}: claim epoch {Epoch} was superseded");
    }

    /// <summary>0.13.10: gives this subject its sequence number if it has none, inside the fenced conditional append (the number is max + 1 of the state the append is conditional on). Returns the number.</summary>
    public async Task<long> AssignSeqAsync(CancellationToken ct)
    {
        if (Lost) throw new LeaseLostException($"lease on {Subject} is lost");
        long seq = 0;
        var ok = await queue.AppendAsync(state =>
        {
            var s = state.Get(Subject);
            if (s == null || s.Epoch != Epoch || s.ClaimBy != queue.NodeId) { MarkLost(); return null; }
            if (s.Seq is { } have) { seq = have; return []; }
            seq = state.NextSeq();
            return [new QLine { T = "seq", S = Subject, Seq = seq, By = queue.NodeId, At = queue.NowUtc() }];
        }, ct);
        if (!ok) throw new LeaseLostException($"{Subject}: claim epoch {Epoch} was superseded");
        return seq;
    }

    /// <summary>After the final append: stops the heartbeat and deletes the active file (a crash before this leaves the work recorded as done; the lease lapses and the next node deletes the file).</summary>
    public async Task FinishAsync()
    {
        await StopHeartbeatAsync();
        try { await store.DeleteAsync(activeName, Lease, CancellationToken.None); }
        catch (Exception e) { Log.Info($"could not delete {activeName} ({e.Message}); its lease runs out by itself"); }
    }

    /// <summary>Gives the subject back without a log line (a step this node cannot run after all): the active file goes, so the subject is claimable at once.</summary>
    public Task AbandonAsync() => FinishAsync();
}

/// <summary>What one look at the queue found.</summary>
public sealed record QueueLook(List<(QSubject Subject, List<QEntry> Runnable)> Candidates, List<string> Stale, int ActiveFiles);

/// <summary>
/// 0.13.0: the job queue as one log (PROTOCOL "Job queue: one log"). jobs/queue.log (append-blob segments, sealed on rollover), jobs/queue.snapshot.json and one lease file per running subject
/// (jobs/active/&lt;subject&gt;.json). The in-memory state is the snapshot plus the log lines after it. Appends are read-modify-write: conditional on the length read (412 = read and decide again).
/// </summary>
public sealed class QueueNotMigratedException(string m) : Exception(m);
/// <summary>The queue moved (`queue split`) and this node was pinned to the old place.</summary>
public sealed class QueueMovedException(string m) : Exception(m);
/// <summary>A snapshot or marker with a format version this binary does not know.</summary>
public sealed class QueueFormatException(string m) : Exception(m);

public sealed class JobQueue
{
    /// <summary>A segment rolls over at this many appends (Azure's limit is 50,000) ...</summary>
    public const int RolloverBlocks = 10_000;
    /// <summary>... or this many bytes (kept well under 2 MB so a node that starts reads at most this much after the snapshot).</summary>
    public const long RolloverBytes = 1L << 20;
    public const int MaxLineBytes = 16 * 1024;
    /// <summary>One AppendBlock holds at most 4 MiB (Azure); a batch of lines is one block, so callers split a bigger batch (<see cref="BatchChunk"/>).</summary>
    public const int MaxAppendBytes = 4 * 1024 * 1024;
    /// <summary>Entries per append for the bulk adds (a lookup of a whole channel is a few hundred; 1000 lines of ~600 bytes stay far under <see cref="MaxAppendBytes"/>).</summary>
    public const int BatchChunk = 1000;
    /// <summary>Splits a batch into chunks of at most <see cref="BatchChunk"/> items AND about 3 MiB of serialized size (<paramref name="size"/> = bytes of one item), so one chunk is one append block under <see cref="MaxAppendBytes"/>.</summary>
    public static List<List<T>> Chunks<T>(IReadOnlyList<T> items, Func<T, int> size)
    {
        var res = new List<List<T>>(); var cur = new List<T>(); long bytes = 0;
        foreach (var it in items)
        {
            int s = size(it) + 400;
            if (cur.Count > 0 && (cur.Count >= BatchChunk || bytes + s > 3L * 1024 * 1024)) { res.Add(cur); cur = new(); bytes = 0; }
            cur.Add(it); bytes += s;
        }
        if (cur.Count > 0) res.Add(cur);
        return res;
    }
    /// <summary>A just-created active file has no lease for a moment; without a lease it only counts as abandoned once it is this old.</summary>
    public static readonly TimeSpan NewActiveGrace = TimeSpan.FromMinutes(2);
    private const int ReadChunk = 1 << 20;

    private readonly IStore store; private Paths paths; private readonly Func<DateTimeOffset> clock;
    private bool layoutResolved;
    /// <summary>The paths of the queue this object reads (after LoadAsync: the split layout's processing queue when jobs/moved.json exists).</summary>
    public Paths Paths => paths;
    public const string MovedHint = "indexnode: the job queue moved: it is now index/jobs/process/ (processing) and index/jobs/download/ (download). This binary predates the split: update indexnode.";

    /// <summary>0.13.26: the queue this node works in. With index/jobs/moved.json (written by `queue split`) it is the processing queue (jobs/process/); without it, the old jobs/ layout. A marker with a version newer than this binary knows refuses.</summary>
    public static async Task<Paths> ResolveAsync(IStore store, Paths paths, CancellationToken ct)
    {
        if (!paths.IsLegacyLayout) return paths;
        var m = await store.GetAsync(paths.Moved, ct);
        if (m == null) return paths;
        int v = 0; try { v = System.Text.Json.JsonDocument.Parse(m.Data).RootElement.GetProperty("v").GetInt32(); } catch { }
        if (v > MovedVersion) throw new QueueFormatException($"indexnode: the queue layout has version {v}; this binary reads up to {MovedVersion}. Update indexnode.");
        return paths.ProcessQueue;
    }
    public const int MovedVersion = 2;

    /// <summary>0.13.26: opens (loads) the queue a command works on. Processing is what <c>new JobQueue(store, paths)</c> follows to after the split; the FULL download queue (host one-offs only: sources, priority, classify, enqueue; the breaker's --storage-max applies) is jobs/download/. Before the split both are the one queue.</summary>
    public static async Task<JobQueue> OpenAsync(IStore store, Paths paths, string node, bool download, CancellationToken ct)
    {
        var root = await ResolveAsync(store, paths, ct);
        var q = new JobQueue(store, download && !root.IsLegacyLayout ? paths.DownloadQueue : paths, node); await q.LoadAsync(ct); return q;
    }

    /// <summary>Commands that take a video id: the processing queue first, then the full download queue; the processing queue when neither knows it.</summary>
    public static async Task<JobQueue> OpenForVideoAsync(IStore store, Paths paths, string node, string id, CancellationToken ct)
    {
        var pq = await OpenAsync(store, paths, node, false, ct);
        if (pq.Read(s => s.Get(id)) != null || (await ResolveAsync(store, paths, ct)).IsLegacyLayout) return pq;
        var dq = await OpenAsync(store, paths, node, true, ct);
        return dq.Read(s => s.Get(id)) != null ? dq : pq;
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateLock = new();
    private QueueState state = new();
    private int seg = 1; private long off; private int blocks; private int snapSeg = 1; private string? snapEtag;
    private bool loaded;
    public string NodeId { get; }

    private readonly bool downloader;
    /// <summary><paramref name="downloader"/>: a node that only downloads reads the work queue (jobs/download/work/) after the split, never the processing queue or the full download queue.</summary>
    public JobQueue(IStore store, Paths paths, string nodeId, Func<DateTimeOffset>? clock = null, bool downloader = false)
    { this.store = store; this.paths = paths; NodeId = nodeId; this.clock = clock ?? (() => DateTimeOffset.UtcNow); this.downloader = downloader; }

    public string NowUtc() => Protocol.Utc(clock());
    public DateTimeOffset Now => clock();
    public IStore Store => store;
    public int Segment => seg;
    public long Offset => off;
    public int Blocks => blocks;
    public T Read<T>(Func<QueueState, T> f) { lock (stateLock) return f(state); }

    // ---------------------------------------------------------------- load and poll

    public const string MigrateHint = "indexnode: the job queue has not been created (index/jobs/queue.snapshot.json.gz is missing). Run: indexnode jobs migrate --to log   (dry run first: add --dry-run; see docs/cutover.md)";
    /// <summary>0.13.0 cutover: a node command that needs the queue refuses to start without the snapshot (it would otherwise start an empty queue beside the old store). Throws with one clear line.</summary>
    public static async Task RequireSnapshotAsync(IStore store, Paths paths, CancellationToken ct)
    {
        paths = await ResolveAsync(store, paths, ct);
        if (await store.HeadAsync(paths.QueueSnapshotGz, ct) == null && await store.HeadAsync(paths.QueueSnapshot, ct) == null) throw new QueueNotMigratedException(MigrateHint);
    }

    /// <summary>Reads the snapshot once, then the log after it. With no snapshot and no log the first (empty) segment is created.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!layoutResolved) { paths = await ResolveAsync(store, paths, ct); if (downloader && !ReferenceEquals(paths, null) && !paths.IsLegacyLayout) paths = paths.DownloadWork; layoutResolved = true; }
            var snap = await store.GetAsync(paths.QueueSnapshotGz, ct);
            var legacy = snap == null ? await store.GetAsync(paths.QueueSnapshot, ct) : null;   // the plain file of the first 0.13.0 migration, until the first rollover writes the .gz
            if (snap != null && snap.Data.Length > 0 || legacy != null && legacy.Data.Length > 0)
            {
                var from = snap ?? legacy!;
                var (st, doc) = QueueState.FromSnapshot(from.Data);
                lock (stateLock) state = st;
                seg = doc.Seg; off = doc.Off; blocks = doc.Blocks; snapSeg = doc.Seg; snapEtag = snap?.ETag;   // no .gz yet: the next write creates it (If-None-Match: *)
                if (snap == null) snapSeg = 0;                                                                   // write the .gz at once, so every node reads the small file from now on
            }
            else
            {
                lock (stateLock) state = new();
                seg = 1; off = 0; blocks = 0; snapSeg = 1; snapEtag = null;
                await store.CreateAppendBlobAsync(paths.QueueSegment(1), true, ct);
            }
            loaded = true;
            await PollCoreAsync(ct);
            await EnsureSnapshotAsync(ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>One ranged read of the log tail (more of them only when there is more than a chunk, or the segment was sealed). Returns the number of lines applied.</summary>
    public async Task<int> PollAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var n = await PollCoreAsync(ct); await EnsureSnapshotAsync(ct); return n; }
        finally { gate.Release(); }
    }

    private async Task<int> PollCoreAsync(CancellationToken ct)
    {
        int applied = 0;
        while (true)
        {
            var r = await store.ReadAppendAsync(paths.QueueSegment(seg), off, ReadChunk, ct);
            if (r.NotFound)
            {
                // step (2) of a rollover: the segment after a sealed one is created with If-None-Match: * (whoever gets here first; the others see it exist)
                if (off == 0)
                {
                    bool made = await store.CreateAppendBlobAsync(paths.QueueSegment(seg), true, ct); if (seg == 1) return applied;
                    // 0.13.26: the name is taken but is no append blob (the split's marker where a pre-split node would continue): this node must not go on writing there
                    if (!made && (await store.ReadAppendAsync(paths.QueueSegment(seg), 0, 1, ct)).NotFound) throw new InvalidDataException($"queue segment {paths.QueueSegment(seg)} is not an append blob: the queue moved (see jobs/moved.json); update indexnode");
                    continue;
                }
                throw new InvalidDataException($"queue segment {paths.QueueSegment(seg)} is missing");
            }
            int used = 0;
            if (r.Data.Length > 0)
            {
                int end = Array.LastIndexOf(r.Data, (byte)'\n') + 1;
                if (end > 0)
                {
                    var text = Encoding.UTF8.GetString(r.Data, 0, end);
                    lock (stateLock)
                        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            if (QLine.Parse(raw) is { } l) { state.Apply(l); applied++; }
                            else Log.Info("queue: skipping an unreadable log line");
                    used = end;
                    off += end;
                }
            }
            blocks = r.Blocks > 0 ? r.Blocks : blocks;
            if (r.Sealed && off >= r.Length) { seg++; off = 0; blocks = 0; continue; }   // sealed and read to its end: the live segment is the next one
            if (used == 0 || used < ReadChunk) return applied;
        }
    }

    // ---------------------------------------------------------------- append (read-modify-write)

    /// <summary>
    /// Appends what <paramref name="decide"/> returns for the state as it is, conditional on the log length this node has read. A 412 (someone appended first) reads the new lines and asks <paramref name="decide"/> again with the new state;
    /// a sealed segment moves on to the next one. <paramref name="decide"/> returns null to give up (false). Runs under the queue's gate: keep it quick and free of I/O.
    /// </summary>
    public async Task<bool> AppendAsync(Func<QueueState, List<QLine>?> decide, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!loaded) throw new InvalidOperationException("the queue is not loaded");
            for (int attempt = 0; attempt < 50; attempt++)
            {
                List<QLine>? lines; lock (stateLock) lines = decide(state);
                if (lines == null) return false;
                if (lines.Count == 0) return true;
                var sb = new StringBuilder();
                foreach (var l in lines)
                {
                    var j = l.ToJson();
                    if (Encoding.UTF8.GetByteCount(j) > MaxLineBytes) throw new InvalidOperationException($"queue line for {l.S} is {j.Length} bytes: too big for the log (keep lines small)");
                    sb.Append(j).Append('\n');
                }
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                if (bytes.Length > MaxAppendBytes) throw new InvalidOperationException($"queue: {lines.Count} lines are {bytes.Length} bytes: over the {MaxAppendBytes}-byte append block limit (split the batch)");
                var res = await store.AppendBlockAsync(paths.QueueSegment(seg), bytes, off, ct);
                switch (res.Outcome)
                {
                    case AppendOutcome.Ok:
                        lock (stateLock) foreach (var l in lines) state.Apply(l);
                        off = res.Length; blocks = res.Blocks > 0 ? res.Blocks : blocks + 1;
                        await MaintainAsync(ct);
                        return true;
                    case AppendOutcome.PositionMismatch:
                        await PollCoreAsync(ct); break;
                    case AppendOutcome.Sealed:
                        await PollCoreAsync(ct); break;   // reads the rest of the sealed segment and moves to the live one
                    case AppendOutcome.NotFound:
                        if (seg == 1 && off == 0) await store.CreateAppendBlobAsync(paths.QueueSegment(1), true, ct); else await PollCoreAsync(ct);
                        break;
                }
            }
            throw new IOException("queue: could not append after 50 tries (the log keeps changing)");
        }
        finally { gate.Release(); }
    }

    // ---------------------------------------------------------------- snapshot and rollover

    private async Task MaintainAsync(CancellationToken ct)
    {
        try { if (blocks >= RolloverBlocks || off >= RolloverBytes) await RollOverCoreAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Log.Info($"queue: rollover failed ({e.Message}); the log goes on"); }
    }

    /// <summary>
    /// Rollover, in the protocol's order: (1) seal the live segment at the length this node has read; (2) create the next segment (If-None-Match: *); (3) write queue.snapshot.json (everything through the sealed segment, pointing at the next one) conditional on the ETag of the snapshot read.
    /// Any node may do it: every node holds the full state, and a lost race at any step just means someone else did it. A node that meets a sealed segment while the snapshot still points at it finishes (2) and (3) itself (see <see cref="EnsureSnapshotAsync"/>).
    /// </summary>
    public async Task RollOverAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await RollOverCoreAsync(ct); }
        finally { gate.Release(); }
    }

    private async Task RollOverCoreAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int old = seg;
            var sealedOk = await store.SealAppendBlobAsync(paths.QueueSegment(old), off, ct);
            if (sealedOk == null) throw new InvalidDataException("the segment to seal is gone");
            if (sealedOk == true) { await PollCoreAsync(ct); Log.Info($"queue: segment {old} sealed; live segment is {seg}"); await EnsureSnapshotAsync(ct); return; }
            await PollCoreAsync(ct);   // someone appended before the seal: read it and look again
            if (seg != old) { await EnsureSnapshotAsync(ct); return; }   // or sealed it for us
        }
        throw new IOException("queue: the segment kept changing during rollover");
    }

    /// <summary>
    /// The snapshot is written only when the log has moved to a new segment that the snapshot does not point at yet: it holds the state through the sealed segment (plus the lines read so far of the new one, at the offset it names),
    /// conditional on the ETag of the snapshot this node last saw (If-None-Match: * when there was none). One lands; a node that lost reads the winner's and goes on. Idempotent.
    /// </summary>
    private async Task EnsureSnapshotAsync(CancellationToken ct)
    {
        if (!loaded || snapSeg >= seg) return;
        try
        {
            byte[] data; lock (stateLock) data = QueueState.Pack(state.ToSnapshot(seg, off, blocks, NowUtc(), clock(), paths.IsLegacyLayout ? 1 : 2));
            if (await store.PutIfMatchAsync(paths.QueueSnapshotGz, data, snapEtag, ct))
            {
                snapSeg = seg; snapEtag = (await store.HeadAsync(paths.QueueSnapshotGz, ct))?.ETag;
                Log.Info($"queue: snapshot written for segment {seg}");
                return;
            }
            var cur = await store.GetAsync(paths.QueueSnapshotGz, ct);   // lost the race: whoever won wrote the same thing for this rollover
            if (cur != null && cur.Data.Length > 0) { snapEtag = cur.ETag; snapSeg = QueueState.FromSnapshot(cur.Data).Doc.Seg; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Log.Info($"queue: snapshot not written ({e.Message}); the next poll tries again"); }
    }

    // ---------------------------------------------------------------- finding work and claiming

    /// <summary>One look: poll the log tail (one ranged read), list jobs/active/ once, and name what this node could claim now. No job bodies are read and no inputs are checked.</summary>
    public async Task<QueueLook> LookAsync(Func<string, bool> stageOk, Func<QSubject, QEntry, bool>? canRun, CancellationToken ct, ReadyOptions? ready = null)
    {
        await PollAsync(ct);
        var active = await store.ListAsync(paths.ActiveDir, false, false, ct);
        var now = clock(); var blocked = new HashSet<string>(StringComparer.Ordinal); var stale = new List<string>();
        foreach (var b in active)
        {
            var subject = Path.GetFileNameWithoutExtension(b.Name);
            bool leased = b.LeaseState == "leased";
            bool young = b.Created is { } c && now - c < NewActiveGrace;
            if (leased || young) blocked.Add(subject); else stale.Add(subject);   // no lease and older than the grace: abandoned
        }
        var nowUtc = Protocol.Utc(now);
        var cands = Read(s => s.Candidates(nowUtc, stageOk, canRun, blocked.Contains, ready));
        var candIds = cands.Select(c => c.Subject.Id).ToHashSet();
        return new QueueLook(cands, stale.Where(x => !candIds.Contains(x)).ToList(), active.Count);
    }

    /// <summary>Deletes active files whose subject has nothing left to run (a node that crashed between its final done and the delete). Returns how many.</summary>
    public async Task<int> CleanStaleAsync(IEnumerable<string> subjects, CancellationToken ct)
    {
        int n = 0;
        foreach (var subject in subjects.Take(5))
        {
            var name = paths.Active(subject);
            string? lease;
            try { lease = await store.AcquireLeaseAsync(name, Protocol.LeaseSeconds, ct); } catch (StorageException e) when (e.Status == 404) { continue; }
            if (lease == null) continue;
            bool pending = Read(s => s.Get(subject) is { } sub && sub.Active);
            if (pending) { try { await store.ReleaseLeaseAsync(name, lease, ct); } catch { } continue; }
            try { await store.DeleteAsync(name, lease, ct); n++; } catch (Exception e) { Log.Info($"queue: could not delete the stale {name} ({e.Message})"); }
        }
        return n;
    }

    /// <summary>
    /// The claim order of the protocol: (1) create jobs/active/&lt;subject&gt;.json (If-None-Match: *) and take its 60 s lease, or take the lease of one that has lapsed; (2) read the log to its end, and if the subject has nothing left to run for this node release and delete;
    /// (3) append "claim &lt;subject&gt; &lt;stages&gt; by &lt;node&gt;, epoch k". Null when another node won, or there was nothing to do.
    /// </summary>
    public Task<ClaimHandle?> TryClaimAsync(string subject, Func<QSubject, string[]?> pickStages, CancellationToken ct) => TryClaimAsync(subject, (_, s) => pickStages(s), ct);

    /// <summary>0.13.26: a claim the download service makes FOR a downloader (POST /v1/download/next): the claim line names <paramref name="by"/>; the service keeps the lease only until the node renews it with the lease id it is handed.</summary>
    public Task<ClaimHandle?> TryClaimForAsync(string subject, string by, Func<QueueState, QSubject, string[]?> pickStages, CancellationToken ct) => TryClaimAsync(subject, pickStages, ct, by);

    /// <summary>A node that was handed a claim (the service made it) takes over its handle: the lease id and epoch from the answer.</summary>
    public ClaimHandle AdoptClaim(string subject, int epoch, string lease) => new(this, store, subject, epoch, lease, paths.Active(subject));

    /// <summary>0.13.24: the picker also sees the whole state (a stage that is READY on its own is decided from it, inside the conditional append). The actors-index singleton may not exist yet: it is offered as an empty subject.</summary>
    public async Task<ClaimHandle?> TryClaimAsync(string subject, Func<QueueState, QSubject, string[]?> pickStages, CancellationToken ct, string? by = null, ReadyOptions? ready = null)
    {
        var name = paths.Active(subject);
        var body = Encoding.UTF8.GetBytes($"{{\"subject\":\"{subject}\",\"node\":\"{NodeId}\",\"at\":\"{NowUtc()}\"}}");
        await store.PutAsync(name, body, null, null, true, ct);   // already there (a lapsed one we may take over): fine, the lease decides
        string? lease;
        try { lease = await store.AcquireLeaseAsync(name, Protocol.LeaseSeconds, ct); }
        catch (StorageException e) when (e.Status == 404) { return null; }
        if (lease == null) return null;
        int epoch = 0;
        bool ok;
        try
        {
            ok = await AppendAsync(state =>
            {
                var s = state.Get(subject) ?? (subject == ActorsIndexJobs.Subject ? new QSubject { Id = subject } : null);
                var stages = s == null ? null : pickStages(state, s);
                if (stages == null || stages.Length == 0) return null;   // the work is already done (or not ours): nothing to claim
                epoch = s!.Epoch + 1;
                return [new QLine { T = "claim", S = subject, Stages = stages, By = by ?? NodeId, Epoch = epoch, At = NowUtc(), Redo = ready != null && stages.Contains("process") && s.Entries.GetValueOrDefault("process") is { Final: true } && StageDeps.StaleSteps(s, ready) is { Count: > 0 } st0 ? st0.ToArray() : null }];
            }, ct);
        }
        catch { try { await store.DeleteAsync(name, lease, CancellationToken.None); } catch { } throw; }
        if (!ok) { try { await store.DeleteAsync(name, lease, CancellationToken.None); } catch { } return null; }
        return new ClaimHandle(this, store, subject, epoch, lease, name);
    }

    // ---------------------------------------------------------------- plain appends (adds, priority, retry: no claim)

    /// <summary>Appends lines that need no claim (add, pri, due, retry, drop), as one append. <paramref name="decide"/> may inspect the state and return null to append nothing.</summary>
    public Task<bool> AppendLinesAsync(Func<QueueState, List<QLine>?> decide, CancellationToken ct) => AppendAsync(decide, ct);

    public Task<bool> AppendLinesAsync(IEnumerable<QLine> lines, CancellationToken ct)
    {
        var list = lines.ToList(); var at = NowUtc();
        foreach (var l in list) l.At ??= at;
        return AppendAsync(_ => list, ct);
    }
}
