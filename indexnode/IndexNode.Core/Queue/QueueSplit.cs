using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;

namespace IndexNode;

/// <summary>
/// 0.13.26 `indexnode queue split`: the one-time move from the single queue (index/jobs/) to two: processing (index/jobs/process/) and download (index/jobs/download/, which only the service reads), plus the downloaders' work queue
/// (index/jobs/download/work/, the first ~download.work-max ready items). A subject goes to processing when it has a done download, a check decision (duplicate), or any processing-stage entry; everything else goes to the download queue.
/// The plan is pure (<see cref="Plan"/>); <see cref="WriteLocalAsync"/> puts the result in a local folder to inspect; <see cref="ApplyAsync"/> writes it to the store: it refuses while a lease is live, seals the old log first, writes the three queues
/// create-only, rewrites the old snapshot so a pre-split node refuses, and writes jobs/moved.json last. A rerun is a no-op.
/// </summary>
public static class QueueSplit
{
    /// <summary>The stages the download side keeps; every other stage (process, group, actors, describe, translate, search-index, actors-index, ...) is processing's.</summary>
    public static readonly string[] DownloadSide = ["sample", "check", "download", "refresh", "classify", "availability"];
    public const int DefaultWorkMax = 1000;

    public sealed class Report
    {
        public int Total, ToProcessing, ToDownload, Seeded, StoredApplied, StoredListed; public string? Seals;
        public SortedDictionary<string, int> ProcessingKinds = new(StringComparer.Ordinal), DownloadKinds = new(StringComparer.Ordinal);
        public string Text(bool dry) => (Seals != null ? Seals + "\n" : "") + $"{(dry ? "dry run, nothing written: " : "")}{Total} subjects: {ToProcessing} to processing, {ToDownload} to download ({Seeded} seeded into the work queue); {StoredApplied} stored classify decision(s) applied to still-unclassified uploads ({StoredListed} found under classify/)\n  processing entries: {Fmt(ProcessingKinds)}\n  download entries:   {Fmt(DownloadKinds)}\n  note: split.json is local-only (it stays in the --out-local folder, is read by --from-local, and is never written to the store)";
        static string Fmt(SortedDictionary<string, int> d) => d.Count == 0 ? "-" : string.Join(", ", d.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    public sealed class PlanResult
    {
        public QueueState Processing = new(), Download = new(), Work = new(); public Report Report = new();
    }

    public static bool ToProcessing(QSubject s) =>
        s.Entries.Values.Any(e => Array.IndexOf(DownloadSide, e.Stage) < 0)
        || s.Entries.TryGetValue("download", out var d) && (d.State == "done" || d.State == "duplicate")
        || s.Entries.TryGetValue("check", out var c) && c.State == "duplicate";

    /// <summary>Whether a due refresh entry may be seeded: not a dynamic-schedule source unless dynamicRefresh is on, except a manual refresh (priority now) or a first listing (run lookup).</summary>
    public static bool RefreshSeedable(QEntry e, bool dynamicRefresh, bool classify = true) =>
        e.Stage == "classify" ? classify : e.Stage != "refresh" || dynamicRefresh || e.Job == null || e.Priority >= Protocol.RefreshNowPriority || e.Job.Run == "lookup" || !Schedule.Of(e.Job).IsDynamic;

    public static PlanResult Plan(QueueState legacy, string nowUtc, int workMax = DefaultWorkMax, bool dynamicRefresh = false, bool classify = false, IReadOnlyDictionary<string, JsonObject>? stored = null)
    {
        var r = new PlanResult { Report = new Report { Total = legacy.Subjects.Count, StoredListed = stored?.Count ?? 0 } };
        // 0.13.26: the queue is the record of a decision; an old stored decision (classify/<id>.json: pre-0.9.1 per-channel jobs, migrations) is applied here, once, to the uploads still unclassified
        if (stored != null)
            foreach (var s in legacy.Subjects.Values.Where(s => s.Entries.TryGetValue("download", out var d) && d.State == Tags.Unclassified && stored.ContainsKey(s.Id)).ToList())
            {
                var n = stored[s.Id]; var d = s.Entries["download"];
                legacy.Apply(ClassifyJobs.Decision(s.Id, d.Job ?? new JobDescriptor { JobId = s.Id, Stage = "download" }, n["candidate"]?.GetValue<bool>() == true, n["kind"]?.ToString() ?? "", n["reason"]?.ToString() ?? "", nowUtc));
                r.Report.StoredApplied++;
            }
        r.Processing.MaxSeq = legacy.MaxSeq;
        foreach (var s in legacy.Subjects.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            bool proc = ToProcessing(s); var kinds = proc ? r.Report.ProcessingKinds : r.Report.DownloadKinds;
            foreach (var e in s.Entries.Values) { var k = e.Stage + ":" + e.State + (e.Hold != null ? "+hold" : ""); kinds[k] = kinds.GetValueOrDefault(k) + 1; }
            if (proc)
            {
                r.Report.ToProcessing++;
                // Data stays null for a migrated subject: only a subject that ENTERS later as "full" has its process stage ready on its own (a done upload must never be reprocessed by the split)
                if (s.Entries.TryGetValue("check", out var c) && c.State == "duplicate" && !(s.Entries.TryGetValue("download", out var d0) && d0.State == "done")) s.Data = "precheck";
                r.Processing.Subjects[s.Id] = s;
            }
            else { r.Report.ToDownload++; r.Download.Subjects[s.Id] = s; }
        }
        // the first work-max ready items: pending, due, not held, sample / check / download; best first (priority, then newest)
        var ready = r.Download.Subjects.Values.Where(s => s.ClaimBy == null).Select(s => (S: s, E: s.Entries.Values.Where(e => e.Stage is "sample" or "download" && e.Runnable(nowUtc) && e.Hold == null).OrderBy(e => QueueState.Rank(e.Stage)).FirstOrDefault())).Where(x => x.E != null)
            .OrderByDescending(x => x.E!.Priority).ThenByDescending(x => x.S.Pub ?? x.E!.Job?.Pub ?? "", StringComparer.Ordinal).ThenBy(x => x.S.Id, StringComparer.Ordinal).Take(workMax);
        // due refresh and classify first, outside the max (they are few); the service's top-up does the same, and adds the availability sweep itself (it needs an input blob)
        var runs = r.Download.Subjects.Values.Where(s => s.ClaimBy == null).SelectMany(s => s.Entries.Values.Where(e => e.Stage is "refresh" or "classify" && e.Runnable(nowUtc) && e.Hold == null && RefreshSeedable(e, dynamicRefresh, classify)).Select(e => (S: s, E: e)))
            .OrderByDescending(x => x.E.Priority).ThenBy(x => x.E.Due, StringComparer.Ordinal).ThenBy(x => x.S.Id, StringComparer.Ordinal).ToList();
        var unclassified = runs.Any(x => x.E.Stage == "classify") ? ClassifyJobs.ListUnclassified(r.Download).Take(DownloadService.ClassifyBatch).Select(x => new ClassifyEntry { Id = x.Id, Title = x.Entry.Job?.Title, Duration = x.Entry.Job?.Duration, ChannelName = x.Entry.Job?.ChannelName, Availability = x.Entry.Job?.Availability }).ToList() : [];
        foreach (var (s, e) in runs)
        {
            if (e.Stage == "classify" && unclassified.Count == 0) continue;
            var copy = JsonSerializer.Deserialize<QEntry>(JsonSerializer.SerializeToUtf8Bytes(e, Protocol.Json), Protocol.Json)!;
            if (copy.Job != null) { copy.Job.Result = null; if (e.Stage == "classify") copy.Job.Entries = unclassified; }
            r.Work.Subjects[s.Id] = new QSubject { Id = s.Id, Pub = s.Pub, LastAt = s.LastAt, Entries = { [e.Stage] = copy } }; r.Report.Seeded++;
        }
        foreach (var (s, e) in ready) { r.Work.Subjects[s.Id] = new QSubject { Id = s.Id, Pub = s.Pub, LastAt = s.LastAt, Entries = { [e!.Stage] = e } }; r.Report.Seeded++; }
        return r;
    }

    public static string[] Files(Paths root) => [root.ProcessQueue.QueueSnapshotGz, root.DownloadQueue.QueueSnapshotGz, root.DownloadWork.QueueSnapshotGz];
    /// <summary>Sealing the old log alone does not stop a pre-split node that is still running: its append meets the seal, it moves on to the next segment and creates that as an append blob. A plain blob (not an append blob) at that name makes that create and every append there fail.</summary>
    static readonly byte[] SealMarker = Encoding.UTF8.GetBytes("the queue moved: see jobs/moved.json\n");
    sealed record PlanMeta(int Seg, long Off, int Blocks, string At, string? SnapEtag = null);

    static byte[] Snap(QueueState st, string at, DateTimeOffset now) => QueueState.Pack(st.ToSnapshot(1, 0, 0, at, now, 2));
    static byte[] MovedDoc(string at) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = JobQueue.MovedVersion, at, process = "jobs/process/", download = "jobs/download/", work = "jobs/download/work/", note = "the queue moved; update indexnode" }));
    /// <summary>What a pre-split node reads in place of the old snapshot: it cannot be parsed as a snapshot (seg is a string), so such a node stops at start instead of working on a sealed queue; this binary reads the "moved" field and says why.</summary>
    static byte[] MovedSnapshot(string at) => QueueState.Pack(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object> { ["v"] = 2, ["seg"] = "QUEUE-MOVED", ["moved"] = JobQueue.MovedHint, ["at"] = at })));

    /// <summary>Plans from the live legacy queue and writes the three queues (and split.json, the position the plan was made at) under <paramref name="dir"/>, laid out as the store is (dir/index/jobs/...). Nothing in the store changes.</summary>
    public static async Task<Report> WriteLocalAsync(JobQueue legacy, Paths paths, string dir, bool dry, int workMax, CancellationToken ct)
    {
        await legacy.PollAsync(ct);
        string at = legacy.NowUtc(); var ctl = ControlFile.Parse((await legacy.Store.GetAsync(paths.Control, ct))?.Data);
        // one listing of classify/ (pages, not a GET per upload); only the blobs of still-unclassified uploads are read
        var unclassified = legacy.Read(s => s.Subjects.Values.Where(x => x.Entries.TryGetValue("download", out var d) && d.State == Tags.Unclassified).Select(x => x.Id).ToHashSet(StringComparer.Ordinal));
        var listed = (await legacy.Store.ListAsync(paths.Prefix + "classify/", false, false, ct)).Select(b => b.Name).Where(n => n.EndsWith(".json", StringComparison.Ordinal)).ToList();
        var stored = new Dictionary<string, JsonObject>(StringComparer.Ordinal); int found = 0;
        foreach (var name in listed)
        {
            found++; var id = Path.GetFileNameWithoutExtension(name);
            if (unclassified.Contains(id) && await legacy.Store.GetAsync(name, ct) is { } b && JsonNode.Parse(b.Data) is JsonObject o) stored[id] = o;
        }
        var plan = Plan(legacy.Read(s => s), at, workMax, ctl.DynamicRefreshOn, ctl.ClassifyOn, stored); plan.Report.StoredListed = found;
        plan.Report.Seals = $"will seal the old live segment {paths.QueueSegment(legacy.Segment)} at length {legacy.Offset}";
        if (dry) return plan.Report;
        var local = new LocalStore(Path.GetFullPath(dir));
        var oldSnap = await legacy.Store.HeadAsync(paths.QueueSnapshotGz, ct);
        foreach (var (p, st) in new[] { (paths.ProcessQueue, plan.Processing), (paths.DownloadQueue, plan.Download), (paths.DownloadWork, plan.Work) })
        {
            await local.PutAsync(p.QueueSnapshotGz, Snap(st, at, legacy.Now), null, null, false, ct);
            await local.PutAsync(p.QueueSegment(1), [], null, null, false, ct);   // each new queue's first (empty) log segment, as the real run creates it
        }
        await local.PutAsync(paths.QueueSegment(legacy.Segment + 1), SealMarker, null, null, false, ct);   // a plain blob where a pre-split node would create the next log segment (see ApplyAsync)
        await local.PutAsync(paths.QueueSnapshotGz, MovedSnapshot(at), null, null, false, ct);   // the old snapshot as it will read after the split
        await local.PutAsync(paths.Moved, MovedDoc(at), null, null, false, ct);
        await local.PutAsync(paths.Prefix + "jobs/split.json", JsonSerializer.SerializeToUtf8Bytes(new PlanMeta(legacy.Segment, legacy.Offset, legacy.Blocks, at, oldSnap?.ETag)), null, null, false, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "split-report.txt"), plan.Report.Text(false) + "\n", ct);
        return plan.Report;
    }

    public sealed record ApplyResult(bool AlreadySplit, int Written, string Message);

    /// <summary>Writes a plan folder (<see cref="WriteLocalAsync"/>) to the store. Refuses while a lease is live or when the old log moved on since the plan.</summary>
    public static async Task<ApplyResult> ApplyAsync(IStore store, Paths paths, string dir, Func<DateTimeOffset> clock, CancellationToken ct)
    {
        if (await store.HeadAsync(paths.Moved, ct) != null) return new(true, 0, "already split: index/jobs/moved.json exists, nothing to do");
        var local = new LocalStore(Path.GetFullPath(dir));
        var meta = JsonSerializer.Deserialize<PlanMeta>((await local.GetAsync(paths.Prefix + "jobs/split.json", ct))?.Data ?? throw new InvalidOperationException("no split.json in " + dir + ": run queue split --out-local first"), Protocol.Json)!;
        var oldHead = await store.HeadAsync(paths.QueueSnapshotGz, ct);
        if (oldHead?.ETag != meta.SnapEtag) return new(false, 0, "refused: the queue snapshot changed since the plan: run queue split --out-local again");
        var active = await store.ListAsync(paths.ActiveDir, false, false, ct);
        var live = active.Count(b => b.LeaseState == "leased" || b.Created is { } c && clock() - c < JobQueue.NewActiveGrace);
        if (live > 0) return new(false, 0, $"refused: {live} lease(s) are live in jobs/active/ (stop the nodes, let their claims finish, then run again)");
        // seal the old log at the planned length first: an append after the plan makes this fail, so the plan is never stale
        var info = await store.GetAppendInfoAsync(paths.QueueSegment(meta.Seg), ct);
        if (info == null) return new(false, 0, "refused: the old queue log is missing");
        if (!(info.Sealed && info.Length == meta.Off))
        {
            var sealedOk = info.Sealed ? false : await store.SealAppendBlobAsync(paths.QueueSegment(meta.Seg), meta.Off, ct);
            if (sealedOk != true) return new(false, 0, "refused: the queue changed since the plan (its log is longer or sealed elsewhere): run queue split --out-local again");
        }
        int written = 0;
        foreach (var p in new[] { paths.ProcessQueue, paths.DownloadQueue, paths.DownloadWork })
        {
            var snap = await local.GetAsync(p.QueueSnapshotGz, ct) ?? throw new InvalidOperationException("incomplete plan folder: " + p.QueueSnapshotGz);
            if (await store.PutAsync(p.QueueSnapshotGz, snap.Data, null, null, true, ct)) written++;   // create-only: a rerun leaves what is there
            if (await local.HeadAsync(p.QueueSegment(1), ct) == null) throw new InvalidOperationException("incomplete plan folder: " + p.QueueSegment(1));
            await store.CreateAppendBlobAsync(p.QueueSegment(1), true, ct);
        }
        var marker = await local.GetAsync(paths.QueueSegment(meta.Seg + 1), ct) ?? throw new InvalidOperationException("incomplete plan folder: " + paths.QueueSegment(meta.Seg + 1));
        await store.PutAsync(paths.QueueSegment(meta.Seg + 1), marker.Data, null, null, true, ct);   // create-only
        var movedSnap = await local.GetAsync(paths.QueueSnapshotGz, ct) ?? throw new InvalidOperationException("incomplete plan folder: " + paths.QueueSnapshotGz);
        var movedDoc = await local.GetAsync(paths.Moved, ct) ?? throw new InvalidOperationException("incomplete plan folder: " + paths.Moved);
        if (oldHead != null) await store.PutIfMatchAsync(paths.QueueSnapshotGz, movedSnap.Data, oldHead.ETag, ct);
        else await store.PutAsync(paths.QueueSnapshotGz, movedSnap.Data, null, null, true, ct);   // a queue that never rolled over has no snapshot yet: an old node would start an empty one beside the split queues, so there is one now   // conditional: a node that rewrote it meanwhile would have appended to the sealed log and failed
        await store.PutAsync(paths.Moved, movedDoc.Data, null, null, true, ct);   // last: from now on every new node follows it
        return new(false, written, $"split: {written} queue snapshot(s) written; the old queue is sealed and marked moved (index/jobs/moved.json)");
    }
}
