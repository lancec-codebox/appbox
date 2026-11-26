using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.26: the download side's service (run by `indexnode serve`, which alone reads and writes the FULL download queue, jobs/download/, under a singleton lease). Downloaders work from the small WORK queue (jobs/download/work/, at most
/// <see cref="WorkMax"/> items, same format and claim/lease protocol as every queue). One <see cref="RunOnceAsync"/> pass: fold the work queue's finished items back into the full queue; run the check for every finished sample (a full download
/// is added, a duplicate or an AI upload is settled: "enter" with data precheck, or held); hand every finished download to processing ("enter", data full, AFTER its "done download" is in the full log; a crash in between is repaired by
/// <see cref="Handoff.RepairAsync"/>); top the work queue up (newest first, priorities kept) when it is under half full; clear finished items out of it. Downloaders never touch the processing queue.
/// </summary>
public sealed class DownloadService(JobQueue full, JobQueue work, JobQueue processing, Func<string, CancellationToken, Task<JsonObject?>> check, int workMax = QueueSplit.DefaultWorkMax, IStore? store = null)
{
    public int WorkMax { get; } = Math.Max(2, workMax);
    public const int ClassifyBatch = 400;
    public static readonly TimeSpan ClassifyIdle = TimeSpan.FromMinutes(10);
    static bool IsRun(string stage) => stage is "refresh" or "classify" or "availability";
    public sealed record Pass(int FoldedBack, int Checked, int Entered, int ToppedUp, int Cleared);

    static bool Ready(QEntry e, string now) => e.State == "pending" && e.Hold == null && string.CompareOrdinal(e.Due, now) <= 0;

    /// <summary>0.13.50: the checks in flight, by upload. The pass and a node's "I sampled this" call may both want the check of one sample: the second waits for the first's result, and it is written once.
    /// (0.13.49 made the two take turns on one lock: a node's call waited behind the pass's whole batch of checks, 90 s and more.)</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<bool>>> checking = new(StringComparer.Ordinal);

    async Task<bool> CheckSharedAsync(string id, QEntry sm, string? pub, CancellationToken ct)
    {
        var mine = new Lazy<Task<bool>>(() => CheckOneAsync(id, sm, pub, CancellationToken.None));   // not the caller's token: the other waiter still wants the answer
        var lazy = checking.GetOrAdd(id, mine);
        try { return await lazy.Value.WaitAsync(ct); }
        finally { if (lazy.Value.IsCompleted) checking.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(id, lazy)); }
    }

    public async Task<Pass> RunOnceAsync(CancellationToken ct)
    {
        await full.PollAsync(ct); await work.PollAsync(ct); await processing.PollAsync(ct);
        await ResolveRequestsAsync(ct);
        int folded = await FoldRunsAsync(ct) + await FoldBackAsync(ct), checks = await CheckSamplesAsync(ct);
        await DropIdleDownloadsAsync(ct);
        int entered = await EnterDownloadsAsync(ct);
        int top = await TopUpAsync(ct), cleared = await ClearOutAsync(ct);
        return new Pass(folded, checks, entered, top, cleared);
    }

    /// <summary>What <see cref="SampledAsync"/> answers: the download of the same upload, claimed for the node, or why there is none (duplicate of ..., held: ai, ...).</summary>
    public sealed record Sampled(Claimed? Claim, string Outcome);

    /// <summary>
    /// 0.13.49 (owner, 2026-11-24: "Sampling a sample should immediately proceed with its download ... batched onto a node, like processing"): POST /v1/download/sampled.
    /// A node that has just finished a sample asks what is next for THAT upload. The service folds the finished sample back, runs its check now (the same check and the same lines as the pass),
    /// and when the answer is a download that may run (new, not held), puts it on the work queue and claims it for the asking node. So a video is sampled, checked and downloaded by one node, back to back,
    /// and nothing waits behind a wall of other samples. A duplicate or an AI hold is settled as in the pass; the node is told why it got nothing.
    /// </summary>
    public async Task<Sampled> SampledAsync(string node, string id, CancellationToken ct)
    {
        {
            await work.PollAsync(ct); await full.PollAsync(ct); await processing.PollAsync(ct);
            await FoldBackAsync(ct);
            var (sm, pub) = full.Read(st => (st.Entry(id, "sample"), st.Get(id)?.Pub));
            if (sm is not { State: "done" }) return new Sampled(null, "its sample is not recorded as done");
            if (full.Read(st => st.Entry(id, "check") == null && st.Entry(id, "download") == null) && !await CheckSharedAsync(id, sm, pub, ct)) return new Sampled(null, "the check could not run; the service asks again on its next pass");
            var (chk, dl) = full.Read(st => (st.Entry(id, "check"), st.Entry(id, "download")));
            if (chk?.State == "duplicate") return new Sampled(null, "duplicate of " + (chk.Job?.DuplicateOf ?? "an indexed upload") + ": nothing to download");
            if (dl == null) return new Sampled(null, "the check added no download");
            if (dl.Hold != null) return new Sampled(null, $"its download is held ({dl.Hold})");
            if (dl.State != "pending") return new Sampled(null, "its download is already " + dl.State);
            if (!DownloadsOn) return new Sampled(null, "downloads are off in control; it stays queued");   // 0.13.60: the check is kept, the download waits for the switch
            var claim = await HandOutDownloadAsync(node, id, work.NowUtc(), ct, verify: false);   // checked a moment ago, against the index as it is
            return claim == null ? new Sampled(null, "its download was taken by another node") : new Sampled(claim, "new");
        }
    }

    /// <summary>
    /// A refresh or classify a downloader finished (its done line carries a <see cref="RunResult"/>; large ones are a blob): refresh adds the new uploads as sample-ready subjects (the ones neither queue knows), starts following the channels it found and
    /// rewrites the source (its body, modified time, next due time); classify turns each decision into the download entry's state. Then the work entry is dropped (the top-up adds it again when it is next due). A run already folded (same last-polled time) is only dropped.
    /// </summary>
    public async Task<int> FoldRunsAsync(CancellationToken ct)
    {
        await work.PollAsync(ct); await full.PollAsync(ct); await processing.PollAsync(ct);
        var runs = work.Read(w => w.Subjects.Values.Where(s => s.ClaimBy == null).SelectMany(s => s.Entries.Values.Where(e => IsRun(e.Stage) && e.Job?.Result != null).Select(e => (s.Id, E: e))).ToList());
        int n = 0;
        foreach (var (id, e) in runs)
        {
            var job = e.Job!; var res = await RunResults.ResolveAsync(store, job.Result!, ct);
            if (res == null) { Log.Info($"download service: the result of {id}/{e.Stage} is missing ({job.Result!.Blob}); it runs again"); await work.AppendLinesAsync([new QLine { T = "retry", S = id, Stage = e.Stage, Due = work.NowUtc() }], ct); continue; }
            var at = work.NowUtc(); bool already = full.Read(f => f.Entry(id, e.Stage)?.Job?.LastPolled) is { } lp && lp == job.LastPolled && job.LastPolled != null && e.Stage is "refresh" or "availability";
            if (!already)
            {
                if (e.Stage == "refresh") await FoldRefreshAsync(id, e, job, res, at, ct); else if (e.Stage == "availability") await FoldAvailabilityAsync(id, e, job, at, ct); else await FoldClassifyAsync(id, e, job, res, at, ct);
                n++;
            }
            await work.AppendLinesAsync([new QLine { T = "drop", S = id, Stage = e.Stage }], ct);
            if (job.Result!.Blob != null && store != null) await store.DeleteAsync(job.Result.Blob, ct);
        }
        return n;
    }

    /// <summary>The admin page's add requests (requests/sources/&lt;id&gt;.json): a channel or playlist URL becomes a source in the full queue whose first look is due now (a downloader lists it); the service makes no YouTube call. A URL that needs YouTube to resolve (a @handle) is marked failed with the reason.</summary>
    public async Task<int> ResolveRequestsAsync(CancellationToken ct)
    {
        if (store == null) return 0;
        int n = 0;
        foreach (var b in await store.ListAsync(RefreshStage.RequestsDir(work.Paths), false, false, ct))
        {
            if (await store.GetAsync(b.Name, ct) is not { } body || System.Text.Json.Nodes.JsonNode.Parse(body.Data) is not System.Text.Json.Nodes.JsonObject req || req["state"]?.ToString() != "pending") continue;
            var url = req["url"]?.ToString() ?? "";
            try
            {
                var known = Sources.Classify(url) ?? throw new InvalidOperationException("the download service does not call YouTube: give the channel (/channel/UC...) or playlist (list=...) URL, or add it with `indexnode sources add`");
                bool playlist = known.Kind == "playlist";
                var job = JobDescriptor.NewSource(known.Kind, known.Id, known.Id, req["schedule"]?.ToString() ?? "once", ["videos"], req["followChannels"]?.GetValue<bool>() ?? playlist, playlist ? -1 : 0, 480);
                bool made = await Sources.AddAsync(full, job, ct);
                req["state"] = "done"; req["jobId"] = job.JobId; req["name"] = job.Name; req["note"] = made ? "source added, its first look is due now" : "already watched";
            }
            catch (Exception e) when (e is not OperationCanceledException) { req["state"] = "failed"; req["error"] = Redact.Sas(e.Message.Length > 300 ? e.Message[..300] : e.Message); }
            req["resolvedAt"] = Protocol.Utc(DateTimeOffset.UtcNow);
            await store.PutAsync(b.Name, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(req), null, null, false, ct); n++;
        }
        return n;
    }

    async Task FoldRefreshAsync(string id, QEntry e, JobDescriptor job, RunResult res, string at, CancellationToken ct)
    {
        var fresh = (res.Videos ?? []).Where(d => d.JobId.Length > 0).GroupBy(d => d.JobId, StringComparer.Ordinal).Select(g => g.First()).ToList();
        foreach (var chunk in JobQueue.Chunks(fresh, d => d.ToBytes().Length))
            await full.AppendLinesAsync(st =>
            {
                var lines = chunk.Where(d => st.Get(d.JobId) == null && processing.Read(p => p.Get(d.JobId)) == null).Select(d => { d.Stage = "sample"; d.AddedAt = d.AddedAt.Length > 0 ? d.AddedAt : at; return QLine.Add(d.JobId, "sample", d, d.AddedAt, d.Priority, d.AddedAt); }).ToList();
                return lines.Count == 0 ? null : lines;
            }, ct);
        if (res.Follow is { Count: > 0 } follow) await Sources.AddBatchAsync(full, follow, ct);
        var body = Sources.Clone(job); body.Result = null; if (res.Modified != null) body.LastModified = res.Modified;
        bool ended = e.State is "done";
        await full.AppendLinesAsync(st =>
        {
            var cur = st.Entry(id, "refresh");
            if (cur == null) return [QLine.Add(id, "refresh", body, at, body.Priority, ended ? null : e.Due, ended ? "done" : "pending")];
            var lines = new List<QLine> { new() { T = "upd", S = id, Stage = "refresh", Job = body } };
            if (ended) lines.Add(new QLine { T = "done", S = id, Stage = "refresh", State = "done", At = at });
            else { lines.Add(new QLine { T = "retry", S = id, Stage = "refresh", Due = e.Due, At = at }); lines.Add(new QLine { T = "pri", S = id, Pri = body.Priority }); }
            return lines;
        }, ct);
    }

    /// <summary>The sweep's own blobs are written by the downloader; the full queue only learns when it ran and when it is next due.</summary>
    async Task FoldAvailabilityAsync(string id, QEntry e, JobDescriptor job, string at, CancellationToken ct)
    {
        var body = Sources.Clone(job); body.Result = null; body.InputBlob = null;
        await full.AppendLinesAsync(st => st.Entry(id, "availability") == null ? [QLine.Add(id, "availability", body, at, null, e.Due, "pending")] : [new QLine { T = "upd", S = id, Stage = "availability", Job = body }, new QLine { T = "retry", S = id, Stage = "availability", Due = e.Due, At = at }], ct);
        if (job.InputBlob != null && store != null) await store.DeleteAsync(job.InputBlob, ct);
    }

    /// <summary>The sweep's input for a downloader: every finished download of the full queue and the channels that have a refresh source (so the downloader never reads a queue).</summary>
    async Task<string> WriteAvailabilityInputAsync(CancellationToken ct)
    {
        var (tracked, ingest) = full.Read(st => (st.Subjects.Values.Where(x => x.Entries.TryGetValue("download", out var d) && d.State == "done").Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            st.Subjects.Values.Where(x => x.Id.StartsWith("ch-") && x.Entries.ContainsKey("refresh")).Select(x => x.Id[3..]).ToList()));
        var blob = work.Paths.Prefix + "jobs/download/results/availability-input.json";
        await store!.PutAsync(blob, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { tracked, ingest }), null, null, false, ct);
        return blob;
    }

    async Task FoldClassifyAsync(string id, QEntry e, JobDescriptor job, RunResult res, string at, CancellationToken ct)
    {
        var decisions = (res.Decisions ?? []).GroupBy(d => d.Id, StringComparer.Ordinal).Select(g => g.Last()).ToList();
        foreach (var chunk in JobQueue.Chunks(decisions, _ => 300))
            await full.AppendLinesAsync(st =>
            {
                var lines = new List<QLine>();
                foreach (var d in chunk)
                {
                    var cur = st.Entry(d.Id, "download"); if (cur == null || cur.State != Tags.Unclassified) continue;   // decided meanwhile
                    var body = Sources.Clone(cur.Job ?? new JobDescriptor { JobId = d.Id, Stage = "download" });
                    if (d.Candidate is { } c) lines.Add(ClassifyJobs.Decision(d.Id, body, c, d.Kind ?? "", d.Reason ?? "", at));
                    else { body.ClassifyMisses = d.Misses; lines.Add(new QLine { T = "upd", S = d.Id, Stage = "download", Job = body }); }
                }
                return lines.Count == 0 ? null : lines;
            }, ct);
        bool more = full.Read(st => st.Subjects.Values.Any(s => s.Entries.TryGetValue("download", out var d) && d.State == Tags.Unclassified));
        var due = Protocol.Utc(DateTimeOffset.Parse(at, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal) + (more ? TimeSpan.Zero : ClassifyIdle));
        var body2 = Sources.Clone(job); body2.Result = null; body2.Entries = null;
        await full.AppendLinesAsync(st => st.Entry(id, "classify") == null ? [QLine.Add(id, "classify", body2, at, null, due, "pending")] : [new QLine { T = "upd", S = id, Stage = "classify", Job = body2 }, new QLine { T = "retry", S = id, Stage = "classify", Due = due, At = at }], ct);
    }

    /// <summary>Carries each finished or retried item of the work queue back into the full queue, with its attempts and error.</summary>
    public async Task<int> FoldBackAsync(CancellationToken ct)
    {
        await work.PollAsync(ct); await full.PollAsync(ct);
        var items = work.Read(w => w.Subjects.Values.SelectMany(s => s.Entries.Values.Select(e => (s.Id, E: e))).ToList());
        int n = 0;
        foreach (var chunk in JobQueue.Chunks(items, _ => 400))
            await full.AppendLinesAsync(st =>
            {
                var at = full.NowUtc(); var lines = new List<QLine>();
                foreach (var (id, e) in chunk)
                {
                    if (!(e.Final || e.Attempts > 0) || e.Job?.Result != null) continue;   // a run with a result is FoldRunsAsync's
                    var cur = st.Entry(id, e.Stage); if (cur == null) { lines.Add(QLine.Add(id, e.Stage, e.Job ?? new JobDescriptor { Stage = e.Stage, JobId = id, AddedAt = at }, at, e.Priority, e.Due, "pending")); continue; }   // a subject the full queue lost: put it back first
                    if (cur.State == e.State && cur.Attempts == e.Attempts) continue;
                    lines.Add(new QLine { T = e.State == "done" || e.State == "duplicate" ? "done" : "failed", S = id, Stage = e.Stage, State = e.State, Attempt = e.Attempts, Error = e.LastError, Permanent = e.Permanent, Job = e.Job, Due = e.State == "pending" ? e.Due : null, At = e.FinishedAt ?? at });
                }
                n += lines.Count; return lines;
            }, ct);
        return n;
    }

    /// <summary>A finished sample is checked once (the full queue has no "check" entry for it yet): a duplicate settles it, AI holds its download, anything else adds the full download.</summary>
    public async Task<int> CheckSamplesAsync(CancellationToken ct)
    {
        await full.PollAsync(ct);
        var todo = full.Read(st => st.Subjects.Values.Where(s => s.Entries.GetValueOrDefault("sample") is { State: "done" } && !s.Entries.ContainsKey("check") && !s.Entries.ContainsKey("download")).Select(s => (s.Id, S: s.Entries["sample"], s.Pub)).OrderBy(x => x.Id, StringComparer.Ordinal).ToList());
        int n = 0;
        foreach (var (id, sm, pub) in todo) if (await CheckSharedAsync(id, sm, pub, ct)) n++;
        return n;
    }

    /// <summary>The check of one finished sample and its lines (see <see cref="CheckSamplesAsync"/>). False: the check did not answer (said in the log); nothing was written.</summary>
    async Task<bool> CheckOneAsync(string id, QEntry sm, string? pub, CancellationToken ct)
    {
        {
            if (full.Read(st => st.Entry(id, "check") != null || st.Entry(id, "download") != null)) return true;   // 0.13.50: decided since the caller looked (a node's call and the pass can both list the same sample)
            JsonObject? rec; try { rec = await check(id, ct); } catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"download service: check of {id} failed ({e.Message}); it is asked again on the next pass"); return false; }
            if (rec == null) return false;
            var decision = (string?)rec["decision"] ?? "new"; var at = full.NowUtc(); bool skip = PreCheckDecision.IsSkip(decision); bool ai = AiHold.Flagged(rec);
            var url = sm.Job?.Url ?? "https://www.youtube.com/watch?v=" + id; var top = (rec["top"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var lines = new List<QLine> { QLine.Add(id, "check", new JobDescriptor { Stage = "check", JobId = id, Url = url, Priority = sm.Priority, AddedAt = at, Pub = pub, DuplicateOf = skip ? (string?)rec["matchUpload"] ?? (string?)top?["upload"] : null }, at, sm.Priority, at, skip ? "duplicate" : "done") };
            if (!skip) lines.Add(new QLine { T = "add", S = id, Stage = "download", Job = new JobDescriptor { Stage = "download", JobId = id, Url = url, MaxHeight = sm.Job?.MaxHeight, Format = sm.Job?.Format, Priority = sm.Priority, AddedAt = at, Pub = pub }, Pri = sm.Priority, Due = at, At = at, Hold = ai ? "ai" : null, AiScore = ai ? (double?)(rec["ai"] as JsonObject)?["score"] : null });
            await full.AppendLinesAsync(lines, ct);   // the check line goes first: a crash after it leaves a decided upload, and a settled one is entered below
            if (skip) await Handoff.EnterAsync(processing, new Handoff.Facts(id, "precheck", Title: (string?)rec["title"], Pub: pub, Priority: sm.Priority, Check: rec, State: "duplicate", Hold: null, AiScore: (double?)(rec["ai"] as JsonObject)?["score"], DuplicateOf: (string?)rec["matchUpload"] ?? (string?)top?["upload"], DuplicateShare: (double?)top?["share"], Url: url), ct);
            return true;
        }
    }

    /// <summary>"done download" is in the full log (fold-back ran first); now each such upload enters processing (create-only; the repair covers a crash between the two appends).</summary>
    public Task<int> EnterDownloadsAsync(CancellationToken ct) => Handoff.RepairAsync(full, processing, ct);

    /// <summary>0.13.39: how many prioritised items one pass may put into the work queue over its max.</summary>
    public const int JumpMax = 50;

    /// <summary>
    /// When ready items drop below half of the max, the next ready ones of the full queue (priority, then newest) join the work queue, up to the max.
    /// 0.13.39: a ready item whose priority is HIGHER than every live item of the work queue joins at once, whatever the fill (at most <see cref="JumpMax"/> a pass, even over the max): an upload somebody asked for by hand
    /// (enqueue --priority, priority set) is the next thing a downloader takes, not something that waits for half the work queue to drain.
    /// </summary>
    public async Task<int> TopUpAsync(CancellationToken ct)
    {
        await SampleFirstAsync(ct);   // 0.13.52: before anything can be handed out
        var now = work.NowUtc(); int addedRuns = await TopUpRunsAsync(now, ct);   // due refresh and classify go first (they are few and need no room)
        int live = work.Read(w => w.Subjects.Values.Count(s => s.Entries.Values.Any(e => !e.Final && !IsRun(e.Stage))));   // the few due runs do not count against the max
        if (live >= WorkMax / 2) return addedRuns + await JumpAsync(now, ct);
        int room = WorkMax - live;
        var pick = full.Read(st => st.Subjects.Values.Where(s => s.ClaimBy == null && s.Id != null && !s.Id.StartsWith("ch-") && !s.Id.StartsWith("pl-"))
            .Select(s => (S: s, E: s.Entries.Values.Where(e => e.Stage is "sample" && Ready(e, now)).FirstOrDefault()))   // 0.13.55: samples only; a download is put on the work queue when it is handed out (HandOutDownloadAsync), never ahead of time
            .Where(x => x.E != null).OrderByDescending(x => x.E!.Priority).ThenByDescending(x => x.S.Pub ?? x.E!.Job?.Pub ?? "", StringComparer.Ordinal).ThenBy(x => x.S.Id, StringComparer.Ordinal)
            .Select(x => (x.S.Id, E: x.E!, x.S.Pub)).ToList());
        var have = work.Read(w => w.Subjects.Values.SelectMany(s => s.Entries.Keys.Select(k => s.Id + "/" + k)).ToHashSet(StringComparer.Ordinal)); int added = 0;
        var take = new List<(string Id, QEntry E, string? Pub)>();
        foreach (var x in pick) { if (take.Count >= room) break; if (have.Contains(x.Id + "/" + x.E.Stage)) continue; take.Add(x); }
        foreach (var chunk in JobQueue.Chunks(take, _ => 700))
            await work.AppendLinesAsync(st => { var at = work.NowUtc(); var l = chunk.Where(x => st.Entry(x.Id, x.E.Stage) == null).Select(x => { var j = x.E.Job ?? new JobDescriptor { Stage = x.E.Stage, JobId = x.Id }; j.Pub ??= x.Pub; return QLine.Add(x.Id, x.E.Stage, j, at, x.E.Priority, x.E.Due.Length > 0 ? x.E.Due : at); }).ToList(); added += l.Count; return l; }, ct);
        return added + addedRuns;
    }

    /// <summary>
    /// 0.13.52 (owner, 2026-11-24: "Everything should get a sample check"): no download is handed out for an upload that was never sampled. Every pending, unheld download of the full queue whose upload has
    /// no sample and no check entry becomes a sample entry (same body, priority, due and added time: <see cref="SampleFirst.Plan"/>); its check adds the download again when the upload is new.
    /// It runs at the start of every top-up, so it covers every way such a download comes to be (a classify decision, `classify set`, `enqueue`, an old entry) without each of them knowing the rule.
    /// A copy already in the work queue is dropped there first when nobody holds it; one a node is working on (or has finished, not folded back yet) is left to finish as it is.
    /// In 1,000 downloads on 2026-11-24 only 242 uploads had been sampled: the others were pending downloads from classify decisions, and their duplicates were found only after the download.
    /// </summary>
    public async Task<int> SampleFirstAsync(CancellationToken ct)
    {
        var at = full.NowUtc();
        var plan = full.Read(st => SampleFirst.Plan(st, at, out _, out _, out _, out _).Where(l => st.Entry(l.S, "download") is { State: "pending", Hold: null }).ToList());   // a held download stays a held download
        var ids = plan.Where(l => l.T == "add" && l.Stage == "sample").Select(l => l.S).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return 0;
        static bool Idle(QSubject? s) => s is { ClaimBy: null } && s.Entries.GetValueOrDefault("download") is { Final: false };
        if (work.Read(w => ids.Any(id => Idle(w.Get(id)))))
            foreach (var chunk in JobQueue.Chunks(ids.ToList(), _ => 120))
                await work.AppendLinesAsync(w => { var l = chunk.Where(id => Idle(w.Get(id))).Select(id => new QLine { T = "drop", S = id, Stage = "download" }).ToList(); return l.Count == 0 ? null : l; }, ct);
        ids.RemoveWhere(id => work.Read(w => w.Entry(id, "download") != null));   // claimed or finished there: it ends as the download it is
        var lines = plan.Where(l => ids.Contains(l.S)).ToList();
        for (int i = 0; i < lines.Count; )
        {   // an add and its drop stay in one append
            int n = Math.Min(JobQueue.BatchChunk, lines.Count - i); if (i + n < lines.Count && lines[i + n - 1].T == "add") n++;
            await full.AppendLinesAsync(lines.GetRange(i, n), ct); i += n;
        }
        if (ids.Count > 0) Log.Info($"download service: {ids.Count} pending download(s) of uploads never sampled became sample entries (sample first)");
        return ids.Count;
    }

    /// <summary>0.13.39: the ready items of the full queue with a priority above the work queue's highest live priority join the work queue (see <see cref="TopUpAsync"/>).</summary>
    async Task<int> JumpAsync(string now, CancellationToken ct)
    {
        int top = work.Read(w => w.Subjects.Values.SelectMany(s => s.Entries.Values.Where(e => !e.Final && !IsRun(e.Stage))).Select(e => e.Priority).DefaultIfEmpty(int.MinValue).Max());
        var have = work.Read(w => w.Subjects.Values.SelectMany(s => s.Entries.Keys.Select(k => s.Id + "/" + k)).ToHashSet(StringComparer.Ordinal));
        var jump = full.Read(st => st.Subjects.Values.Where(s => s.ClaimBy == null && s.Id != null && !s.Id.StartsWith("ch-") && !s.Id.StartsWith("pl-"))
            .Select(s => (S: s, E: s.Entries.Values.Where(e => e.Stage is "sample" && Ready(e, now) && e.Priority > top).FirstOrDefault()))
            .Where(x => x.E != null && !have.Contains(x.S.Id + "/" + x.E!.Stage)).OrderByDescending(x => x.E!.Priority).ThenByDescending(x => x.S.Pub ?? x.E!.Job?.Pub ?? "", StringComparer.Ordinal).ThenBy(x => x.S.Id, StringComparer.Ordinal)
            .Take(JumpMax).Select(x => (x.S.Id, E: x.E!, x.S.Pub)).ToList());
        if (jump.Count == 0) return 0;
        int added = 0;
        await work.AppendLinesAsync(st => { var at = work.NowUtc(); var l = jump.Where(x => st.Entry(x.Id, x.E.Stage) == null).Select(x => { var j = x.E.Job ?? new JobDescriptor { Stage = x.E.Stage, JobId = x.Id }; j.Pub ??= x.Pub; return QLine.Add(x.Id, x.E.Stage, j, at, x.E.Priority, x.E.Due.Length > 0 ? x.E.Due : at); }).ToList(); added = l.Count; return l; }, ct);
        return added;
    }

    /// <summary>The refresh entries (channel / playlist subjects) and the classify singleton that are due and not in the work queue yet; classify carries its batch of unclassified uploads on the job.</summary>
    async Task<int> TopUpRunsAsync(string now, CancellationToken ct)
    {
        var have = work.Read(w => w.Subjects.Values.SelectMany(s => s.Entries.Keys.Select(k => s.Id + "/" + k)).ToHashSet(StringComparer.Ordinal));
        var ctl = store != null ? ControlFile.Parse((await store.GetAsync(work.Paths.Control, ct))?.Data) : new ControlFile(); bool dyn = ctl.DynamicRefreshOn, cls = ctl.ClassifyOn;
        var due = full.Read(st => st.Subjects.Values.Where(s => s.ClaimBy == null).SelectMany(s => s.Entries.Values.Where(e => IsRun(e.Stage) && Ready(e, now) && QueueSplit.RefreshSeedable(e, dyn, cls)).Select(e => (s.Id, E: e))).ToList())
            .Where(x => !have.Contains(x.Id + "/" + x.E.Stage)).OrderByDescending(x => x.E.Priority).ThenBy(x => x.E.Due, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        if (due.Count == 0) return 0;
        var unclassified = due.Any(x => x.E.Stage == "classify") ? ClassifyJobs.ListUnclassified(full).Take(ClassifyBatch).Select(x => new ClassifyEntry { Id = x.Id, Title = x.Entry.Job?.Title, Duration = x.Entry.Job?.Duration, ChannelName = x.Entry.Job?.ChannelName, Availability = x.Entry.Job?.Availability }).ToList() : [];
        string? availBlob = null;
        if (due.Any(x => x.E.Stage == "availability")) { if (store == null) due = due.Where(x => x.E.Stage != "availability").ToList(); else availBlob = await WriteAvailabilityInputAsync(ct); }
        if (due.Count == 0) return 0;
        int added = 0;
        foreach (var chunk in JobQueue.Chunks(due, _ => 200))
            await work.AppendLinesAsync(st =>
            {
                var at = work.NowUtc(); var l = new List<QLine>();
                foreach (var (id, e) in chunk)
                {
                    if (st.Entry(id, e.Stage) != null) continue;
                    var j = Sources.Clone(e.Job ?? new JobDescriptor { Stage = e.Stage, JobId = id }); j.Result = null;
                    if (e.Stage == "classify") { if (unclassified.Count == 0) continue; j.Entries = unclassified; }
                    if (e.Stage == "availability") j.InputBlob = availBlob;   // nothing to decide: it stays in the full queue until there is
                    l.Add(QLine.Add(id, e.Stage, j, at, e.Priority, e.Due.Length > 0 ? e.Due : at));
                }
                added += l.Count; return l.Count == 0 ? null : l;
            }, ct);
        return added;
    }

    /// <summary>Drops the finished items (their outcome is folded back) so the work queue's next snapshot holds only live ones; then a rollover writes that snapshot.</summary>
    public async Task<int> ClearOutAsync(CancellationToken ct)
    {
        await work.PollAsync(ct); await full.PollAsync(ct);
        var gone = work.Read(w => w.Subjects.Values.Where(s => s.ClaimBy == null && s.Entries.Count > 0 && s.Entries.Values.All(e => e.Final)).Select(s => (s.Id, Entries: s.Entries.Values.ToList())).ToList())
            .Where(x => full.Read(f => x.Entries.All(e => f.Entry(x.Id, e.Stage) is { } fe && fe.State == e.State))).Select(x => x.Id).ToList();   // dropped only once the full queue has the same outcome
        foreach (var chunk in JobQueue.Chunks(gone, _ => 120))
            await work.AppendLinesAsync(chunk.Select(id => new QLine { T = "drop", S = id }), ct);
        return gone.Count;
    }

    public Task RollOverWorkAsync(CancellationToken ct) => work.RollOverAsync(ct);

    public sealed record Claimed(string Subject, string Stage, int Epoch, string Lease, string Active);

    /// <summary>POST /v1/download/next: tops the work queue up at once, then claims ONE ready item for <paramref name="node"/> with the claim line and the lease a node would make. Two concurrent callers never get the same item (the active file's lease decides). Null: nothing ready.</summary>
    /// <summary>0.13.60: is this stage switched on in control ("download" covers samples too)? Not set: on. The service hands nothing out while downloads are off, whatever the node that asks believes:
    /// the switch must hold against a node that has not seen it (an old version, a missed line).</summary>
    public Func<string, bool>? StageOn { get; set; }
    bool DownloadsOn => StageOn == null || StageOn("download");

    public async Task<Claimed?> NextAsync(string node, string[] stages, CancellationToken ct)
    {
        if (!DownloadsOn) return null;
        await work.PollAsync(ct); await full.PollAsync(ct); await TopUpAsync(ct);
        var now = work.NowUtc(); var ok = stages.Length == 0 ? (Func<string, bool>)(_ => true) : st => Array.IndexOf(stages, st) >= 0;
        var look = await work.LookAsync(st => st is "sample" or "refresh" or "classify" && ok(st), null, ct);
        var cands = look.Candidates; var downloads = ok("download") ? ReadyDownloads(now) : []; int ci = 0, di = 0, checkedNow = 0; var started = Clock();
        while (ci < cands.Count || di < downloads.Count)
        {
            // the higher priority goes first; on a tie the download (its upload is one step further)
            bool download = di < downloads.Count && (ci >= cands.Count || downloads[di].Priority >= cands[ci].Runnable.Max(e => e.Priority));
            if (download)
            {
                var (id, _) = downloads[di++];
                if (!handing.TryAdd(id, 0)) continue;   // another caller is on it right now (its check, its hand-out): this caller takes the next one, so nodes that ask at the same moment do not queue behind one check
                try
                {
                    bool stale = await IsStaleAsync(id, ct);
                    // enough checks for one call (each takes about 12 s and the node waits): the rest of this call is samples
                    if (stale && (checkedNow >= MaxChecksPerCall || Clock() - started > CheckTime)) { di = downloads.Count; continue; }
                    if (stale) checkedNow++;
                    if (await HandOutDownloadAsync(node, id, now, ct, verify: true) is { } c) return c;
                    if (stale && verifyFailed.ContainsKey(id)) di = downloads.Count;   // the check cannot run right now (a store limit, the index): no more of them in this call, or each would wait out its 30 min
                }
                finally { handing.TryRemove(id, out _); }
                continue;
            }
            var (s, runnable) = cands[ci++];
            var h = await work.TryClaimForAsync(s.Id, node, (_, sub) => sub.ClaimBy == null ? sub.Entries.Values.Where(e => e.Runnable(now) && ok(e.Stage) && e.Stage != "download").Select(e => e.Stage).Take(1).ToArray() : null, ct);
            if (h != null) return new Claimed(s.Id, runnable[0].Stage, h.Epoch, h.Lease, work.Paths.Active(s.Id));
        }
        return null;
    }

    /// <summary>How many downloads one <see cref="NextAsync"/> call may check again before it falls back to samples (a check takes several seconds, the caller waits).</summary>
    public const int MaxChecksPerCall = 4;
    /// <summary>No further check starts once a call has spent this long checking: with one more check (about 12 s on the 2026-11-25 dry run) the answer still comes well inside the node's 90 s.</summary>
    public static readonly TimeSpan CheckTime = TimeSpan.FromSeconds(25);
    public const string Go = "go";

    /// <summary>0.13.55: the time the published search index was built (the serve's own cache answers). Null, or not set: nothing is known to be newer than a check, so no check is run again.</summary>
    public Func<CancellationToken, Task<DateTimeOffset?>>? IndexBuiltAt { get; set; }

    /// <summary>The uploads being handed out right now (their download is on the work queue for the moment between its add and its claim): <see cref="DropIdleDownloadsAsync"/> leaves them alone.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> handing = new(StringComparer.Ordinal);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<string>>> verifying = new(StringComparer.Ordinal);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> verifyFailed = new(StringComparer.Ordinal);
    /// <summary>The clock for "a check that could not run is not tried again for a while" (tests set it).</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public static readonly TimeSpan VerifyRetry = TimeSpan.FromMinutes(30);

    /// <summary>The downloads that may be handed out: pending, not held, due, of an upload that was sampled or checked, and not on the work queue already (there = handed out and running, or finished and not folded back yet). Priority first, then newest.</summary>
    List<(string Id, int Priority)> ReadyDownloads(string now)
    {
        var busy = work.Read(w => w.Subjects.Values.Where(s => s.Entries.ContainsKey("download")).Select(s => s.Id).ToHashSet(StringComparer.Ordinal));
        return full.Read(st => st.Subjects.Values.Where(s => s.ClaimBy == null && s.Entries.GetValueOrDefault("download") is { } e && Ready(e, now) && (s.Entries.ContainsKey("sample") || s.Entries.ContainsKey("check")) && !busy.Contains(s.Id))
            .Select(s => (S: s, E: s.Entries["download"])).OrderByDescending(x => x.E.Priority).ThenByDescending(x => x.S.Pub ?? x.E.Job?.Pub ?? "", StringComparer.Ordinal).ThenBy(x => x.S.Id, StringComparer.Ordinal)
            .Take(64).Select(x => (x.S.Id, x.E.Priority)).ToList());
    }

    /// <summary>Was the upload's check made before the published index was built? Then its verdict says nothing about the dramas indexed since.</summary>
    async Task<bool> IsStaleAsync(string id, CancellationToken ct)
    {
        var (sm, chk) = full.Read(st => (st.Entry(id, "sample"), st.Entry(id, "check")));
        if (sm is not { State: "done" } || chk == null || IndexBuiltAt == null) return false;   // no stored clips, or never checked the new way: nothing to run again
        if (verifyFailed.TryGetValue(id, out var until) && Clock() < until) return false;       // VerifyAsync answers "could not run" without trying
        return await IndexBuiltAt(ct) is { } built && string.CompareOrdinal(chk.FinishedAt ?? chk.Added, Protocol.Utc(built)) < 0;
    }

    /// <summary>
    /// 0.13.55 (owner, 2026-11-25: "roll the whole sampling into the download stage"; "As a part of the download we'll have it always do sampling"): no download starts on a verdict older than the index.
    /// When an upload was sampled earlier and the published index has been built since its check, the check runs again on the STORED clips (no YouTube request), against the index as it is now.
    /// "go": the download may be handed out (the check's time moves up). A duplicate now: the check and the download end as duplicate, the upload enters the catalog as one, nothing is downloaded.
    /// Only duplicates are decided here: the AI hold is left as the first check, the owner or the threshold set it (the same clips give the same score).
    /// Of 1,000 downloads on 2026-11-25, 824 had been sampled three days before; 65 of the first 558 processed were copies of dramas indexed in between.
    /// </summary>
    public async Task<string> VerifyAsync(string id, CancellationToken ct)
    {
        var mine = new Lazy<Task<string>>(() => VerifyOneAsync(id, CancellationToken.None));   // not the caller's token: another waiter still wants the answer
        var lazy = verifying.GetOrAdd(id, mine);
        try { return await lazy.Value.WaitAsync(ct); }
        finally { if (lazy.Value.IsCompleted) verifying.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(id, lazy)); }
    }

    async Task<string> VerifyOneAsync(string id, CancellationToken ct)
    {
        var (chk, dl, pub) = full.Read(st => (st.Entry(id, "check"), st.Entry(id, "download"), st.Get(id)?.Pub));
        if (dl is not { State: "pending" }) return "its download is " + (dl?.State ?? "gone");
        if (dl.Hold != null) return $"its download is held ({dl.Hold})";
        if (verifyFailed.TryGetValue(id, out var until) && Clock() < until) return "its check could not run; it is tried again later";
        if (!await IsStaleAsync(id, ct)) return Go;
        JsonObject? rec; try { rec = await check(id, ct); } catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"download service: the check of {id} before its download failed ({e.Message}); not handed out, tried again in {VerifyRetry.TotalMinutes:0} min"); rec = null; }
        if (rec == null) { verifyFailed[id] = Clock() + VerifyRetry; return "its check could not run; it is tried again later"; }
        verifyFailed.TryRemove(id, out _);
        var decision = (string?)rec["decision"] ?? "new"; var at = full.NowUtc(); bool skip = PreCheckDecision.IsSkip(decision);
        var url = dl.Job?.Url ?? chk!.Job?.Url ?? "https://www.youtube.com/watch?v=" + id; var top = (rec["top"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var dupOf = skip ? (string?)rec["matchUpload"] ?? (string?)top?["upload"] : null;
        var lines = new List<QLine> { new QLine { T = "done", S = id, Stage = "check", State = skip ? "duplicate" : "done", At = at, Job = new JobDescriptor { Stage = "check", JobId = id, Url = url, Priority = dl.Priority, AddedAt = chk!.Added, Pub = pub, DuplicateOf = dupOf } } };
        if (skip) lines.Add(new QLine { T = "done", S = id, Stage = "download", State = "duplicate", At = at });
        if (skip) await work.AppendLinesAsync(w => w.Get(id) is { ClaimBy: null } s && s.Entries.GetValueOrDefault("download") is { Final: false } ? [new QLine { T = "drop", S = id, Stage = "download" }] : null, ct);
        await full.AppendLinesAsync(lines, ct);   // the check line first, as in the first check
        if (!skip) return Go;
        await Handoff.EnterAsync(processing, new Handoff.Facts(id, "precheck", Title: (string?)rec["title"], Pub: pub, Priority: dl.Priority, Check: rec, State: "duplicate", Hold: null, AiScore: (double?)(rec["ai"] as JsonObject)?["score"], DuplicateOf: dupOf, DuplicateShare: (double?)top?["share"], Url: url), ct);
        Log.Info($"download service: {id} is a duplicate of {dupOf ?? "an indexed upload"} by the check before its download (first checked {chk.FinishedAt ?? chk.Added}): nothing is downloaded");
        return "duplicate of " + (dupOf ?? "an indexed upload") + ": nothing to download";
    }

    /// <summary>Puts one download on the work queue and claims it for <paramref name="node"/>, after <see cref="VerifyAsync"/> when asked. Null: not handed out (said in the log when the check stopped it), or another node has it.</summary>
    async Task<Claimed?> HandOutDownloadAsync(string node, string id, string now, CancellationToken ct, bool verify)
    {
        handing[id] = 0;
        try
        {
            if (verify && await VerifyAsync(id, ct) is var v && v != Go) return null;
            var dl = full.Read(st => st.Entry(id, "download")); if (dl is not { State: "pending", Hold: null }) return null;
            await work.AppendLinesAsync(st => st.Entry(id, "download") != null ? null : [QLine.Add(id, "download", dl.Job ?? new JobDescriptor { Stage = "download", JobId = id, Url = "https://www.youtube.com/watch?v=" + id, AddedAt = now }, now, dl.Priority, now)], ct);
            var h = await work.TryClaimForAsync(id, node, (_, sub) => sub.ClaimBy == null && sub.Entries.GetValueOrDefault("download") is { } e && e.Runnable(now) ? ["download"] : null, ct);
            return h == null ? null : new Claimed(id, "download", h.Epoch, h.Lease, work.Paths.Active(id));
        }
        finally { handing.TryRemove(id, out _); }
    }

    /// <summary>
    /// A download nobody holds does not stay on the work queue: one that was handed out and failed (its attempt is folded back first), one an older service topped up ahead of time.
    /// It is handed out again from the full queue, checked again if the index has moved. So a node never finds a download to claim by itself, whatever its version.
    /// </summary>
    public async Task<int> DropIdleDownloadsAsync(CancellationToken ct)
    {
        bool IdleNow(QSubject? s) => s is { ClaimBy: null } && !handing.ContainsKey(s.Id) && s.Entries.GetValueOrDefault("download") is { Final: false };
        var idle = work.Read(w => w.Subjects.Values.Where(IdleNow).Select(s => s.Id).ToList());
        if (idle.Count == 0) return 0;
        int n = 0;
        foreach (var chunk in JobQueue.Chunks(idle, _ => 120))
            await work.AppendLinesAsync(w => { var l = chunk.Where(id => IdleNow(w.Get(id))).Select(id => new QLine { T = "drop", S = id, Stage = "download" }).ToList(); n += l.Count; return l.Count == 0 ? null : l; }, ct);
        if (n > 0) Log.Info($"download service: {n} download(s) nobody held left the work queue; they are handed out from the full queue");
        return n;
    }
}
