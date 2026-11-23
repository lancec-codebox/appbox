using System.Text;
using System.Text.Json;
namespace IndexNode.Tests;

public class QueueSplitTests
{
    static readonly Paths P = new("index/");
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
    static string At => Protocol.Utc(T0);
    static async Task<(FakeStore, JobQueue)> Legacy()
    {
        var s = new FakeStore { Clock = () => T0 }; var q = new JobQueue(s, P, "t", () => T0); await q.LoadAsync(default);
        QLine Add(string id, string stage, string? state = null, int pri = 0) => QLine.Add(id, stage, new JobDescriptor { Stage = stage, JobId = id, AddedAt = At, Pub = "2026-11-" + (10 + id.Length % 9) + "T00:00:00Z" }, At, pri, At, state);
        var lines = new List<QLine>
        {
            Add("u1", "download", "unclassified"), Add("u2", "download", "unclassified"),   // listed, unclassified
            Add("s1", "sample"), Add("s2", "sample", pri: 5),                              // sample pending
            Add("f1", "download", "failed"),                                               // failed download
            Add("ch-1", "refresh"), Add("pl-1", "refresh"),                                // refresh subjects
            Add("d1", "download", "done"),                                                 // done download -> processing
            Add("c1", "check", "duplicate"),                                               // a check decision -> processing
            Add("p1", "process"), Add("g1", "group", "done"),                              // processing-stage entries
            Add("w1", "download", "pending"),                                              // a pending download (check settled it: not a duplicate)
        };
        await q.AppendLinesAsync(lines, default);
        return (s, q);
    }

    [Fact]
    public async Task The_dry_run_counts_per_destination_and_per_entry_kind_and_changes_nothing()
    {
        var (s, q) = await Legacy(); var before = s.Names("index/").OrderBy(x => x).ToList();
        var rep = await QueueSplit.WriteLocalAsync(q, P, "unused", true, 1000, default);
        Assert.Equal(12, rep.Total); Assert.Equal(4, rep.ToProcessing); Assert.Equal(8, rep.ToDownload);
        Assert.Equal(1, rep.ProcessingKinds["download:done"]); Assert.Equal(1, rep.ProcessingKinds["check:duplicate"]); Assert.Equal(1, rep.ProcessingKinds["process:pending"]); Assert.Equal(1, rep.ProcessingKinds["group:done"]);
        Assert.Equal(2, rep.DownloadKinds["download:unclassified"]); Assert.Equal(2, rep.DownloadKinds["sample:pending"]); Assert.Equal(2, rep.DownloadKinds["refresh:pending"]); Assert.Equal(1, rep.DownloadKinds["download:failed"]); Assert.Equal(1, rep.DownloadKinds["download:pending"]);
        Assert.Equal(before, s.Names("index/").OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task Out_local_then_from_local_splits_once_and_a_rerun_is_a_no_op()
    {
        var (s, q) = await Legacy(); var dir = Path.Combine(Path.GetTempPath(), "split-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var rep = await QueueSplit.WriteLocalAsync(q, P, dir, false, 2, default);
            Assert.Equal(4, rep.Seeded);   // download:pending s2 (priority 5) and one more (at most work-max), plus the two due refresh sources (outside the max)
            Assert.False(s.Exists(P.Moved));
            var r1 = await QueueSplit.ApplyAsync(s, P, dir, () => T0, default);
            Assert.False(r1.AlreadySplit); Assert.Equal(3, r1.Written); Assert.True(s.Exists(P.Moved));
            var r2 = await QueueSplit.ApplyAsync(s, P, dir, () => T0, default); Assert.True(r2.AlreadySplit); Assert.Equal(0, r2.Written);
            // the new queues load: processing has the 4 subjects, download the 8, the work queue 2
            var pq = new JobQueue(s, P, "n", () => T0); await pq.LoadAsync(default); Assert.Equal(4, pq.Read(st => st.Subjects.Count)); Assert.Equal("index/jobs/process/", pq.Paths.QueueDir);
            var dq = new JobQueue(s, P.DownloadQueue, "svc", () => T0); await dq.LoadAsync(default); Assert.Equal(8, dq.Read(st => st.Subjects.Count));
            var wq = new JobQueue(s, P.DownloadWork, "dl", () => T0); await wq.LoadAsync(default); Assert.Equal(4, wq.Read(st => st.Subjects.Count)); Assert.True(wq.Read(st => st.Subjects.ContainsKey("s2")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task The_split_refuses_while_a_lease_is_live_and_when_the_log_moved_after_the_plan()
    {
        var (s, q) = await Legacy(); var dir = Path.Combine(Path.GetTempPath(), "split-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await QueueSplit.WriteLocalAsync(q, P, dir, false, 10, default);
            await s.PutAsync(P.Active("x"), Encoding.UTF8.GetBytes("{}"), null, null, true, default); var lease = await s.AcquireLeaseAsync(P.Active("x"), 60, default);
            var r = await QueueSplit.ApplyAsync(s, P, dir, () => T0, default); Assert.Contains("lease", r.Message); Assert.False(s.Exists(P.Moved));
            await s.DeleteAsync(P.Active("x"), lease, default);
            await q.AppendLinesAsync([QLine.Add("late", "sample", new JobDescriptor { Stage = "sample", JobId = "late", AddedAt = At }, At)], default);   // the log moved on after the plan
            var r2 = await QueueSplit.ApplyAsync(s, P, dir, () => T0, default); Assert.Contains("changed since the plan", r2.Message); Assert.False(s.Exists(P.Moved));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task An_old_binary_cannot_read_the_split_layout_and_this_one_follows_the_marker_and_refuses_a_newer_version()
    {
        var (s, q) = await Legacy(); var dir = Path.Combine(Path.GetTempPath(), "split-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await QueueSplit.WriteLocalAsync(q, P, dir, false, 10, default); await QueueSplit.ApplyAsync(s, P, dir, () => T0, default);
            // a pre-split node reads the old snapshot path: it holds a document it cannot read as a snapshot, and says why
            var old = (await s.GetAsync(P.QueueSnapshotGz, default))!.Data;
            var ex = Assert.Throws<QueueMovedException>(() => QueueState.FromSnapshot(old)); Assert.Contains("update indexnode", ex.Message);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<QueueState.SnapshotDoc>(QueueState.Unpack(old), Protocol.Json));   // what 0.13.24 does: it stops at start
            // each new snapshot carries format version 2; a version this binary does not know is refused
            var snap = (await s.GetAsync(P.ProcessQueue.QueueSnapshotGz, default))!.Data; Assert.Equal(2, QueueState.FromSnapshot(snap).Doc.V);
            var future = Encoding.UTF8.GetBytes("{\"v\":3,\"seg\":1,\"subjects\":[]}"); Assert.Throws<QueueFormatException>(() => QueueState.FromSnapshot(future));
            await s.PutAsync(P.Moved, Encoding.UTF8.GetBytes("{\"v\":9}"), null, null, false, default);
            await Assert.ThrowsAsync<QueueFormatException>(() => new JobQueue(s, P, "n").LoadAsync(default));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static string TmpDir() => Path.Combine(Path.GetTempPath(), "split-" + Guid.NewGuid().ToString("N")[..8]);
    static async Task<SortedDictionary<string, string>> Dump(FakeStore s)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in s.Names("index/").Where(n => !n.Contains("/active/"))) d[n] = Convert.ToHexString((await s.GetAsync(n, default))?.Data ?? []);
        return d;
    }

    [Fact]
    public async Task Out_local_writes_every_file_the_real_run_writes_and_names_the_segment_it_will_seal()
    {
        var (s, q) = await Legacy(); var dir = TmpDir(); var before = await Dump(s);
        try
        {
            var rep = await QueueSplit.WriteLocalAsync(q, P, dir, false, 10, default);
            Assert.Contains("index/jobs/queue.log", rep.Text(false)); Assert.Contains("at length " + q.Offset, rep.Text(false));
            Assert.Equal(before, await Dump(s));   // the store is untouched
            Assert.True((await QueueSplit.ApplyAsync(s, P, dir, () => T0, default)).Written == 3);
            var after = await Dump(s); int checkedFiles = 0;
            foreach (var (n, hex) in after.Where(kv => !before.TryGetValue(kv.Key, out var b) || b != kv.Value))
            {
                var path = Path.Combine(dir, n.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), "out-local lacks " + n);
                Assert.Equal(hex, Convert.ToHexString(File.ReadAllBytes(path))); checkedFiles++;
            }
            Assert.True(checkedFiles >= 9, "files compared: " + checkedFiles);   // 3 snapshots, 3 first segments, moved snapshot, moved.json, seal marker
            foreach (var n in new[] { P.Moved, P.QueueSnapshotGz, P.ProcessQueue.QueueSegment(1), P.DownloadQueue.QueueSegment(1), P.DownloadWork.QueueSegment(1) }) Assert.True(File.Exists(Path.Combine(dir, n.Replace('/', Path.DirectorySeparatorChar))), n);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Out_local_then_from_local_leaves_the_store_exactly_as_a_direct_split_does()
    {
        var (s1, q1) = await Legacy(); var (s2, q2) = await Legacy(); var d1 = TmpDir(); var d2 = TmpDir();
        try
        {
            await QueueSplit.WriteLocalAsync(q1, P, d1, false, 10, default); await QueueSplit.ApplyAsync(s1, P, d1, () => T0, default);   // the two steps
            await QueueSplit.WriteLocalAsync(q2, P, d2, false, 10, default); await QueueSplit.ApplyAsync(s2, P, d2, () => T0.AddMinutes(5), default);   // what a direct run does, applied at another time
            Assert.Equal(await Dump(s1), await Dump(s2));   // nothing in the result depends on when it is applied: the folder decides
        }
        finally { foreach (var d in new[] { d1, d2 }) try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public async Task From_local_refuses_when_the_old_snapshot_or_the_log_moved_on_after_the_plan()
    {
        var (s, q) = await Legacy(); var dir = TmpDir();
        try
        {
            await QueueSplit.WriteLocalAsync(q, P, dir, false, 10, default);
            await q.RollOverAsync(default);   // a node rolls the log over: new segment, new snapshot
            var r = await QueueSplit.ApplyAsync(s, P, dir, () => T0, default);
            Assert.Contains("changed since the plan", r.Message); Assert.False(s.Exists(P.Moved)); Assert.False(s.Exists(P.ProcessQueue.QueueSnapshotGz));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task A_pre_split_node_that_is_still_running_cannot_append_to_the_sealed_old_queue()
    {
        var (s, q) = await Legacy(); var dir = TmpDir();
        try
        {
            await QueueSplit.WriteLocalAsync(q, P, dir, false, 10, default); await QueueSplit.ApplyAsync(s, P, dir, () => T0, default);
            var queuesBefore = await Dump(s);
            // a node that loaded the old queue before the split: its next append meets the seal and must NOT end up in a log of its own
            await Assert.ThrowsAnyAsync<Exception>(() => q.AppendLinesAsync([QLine.Add("late", "sample", new JobDescriptor { Stage = "sample", JobId = "late", AddedAt = At }, At)], default));
            var info = await s.GetAppendInfoAsync(P.QueueSegment(1), default); Assert.True(info!.Sealed);
            Assert.Null(await s.GetAppendInfoAsync(P.QueueSegment(2), default));   // no append blob was created for the next segment: a plain blob holds the name
            var after = await Dump(s);
            foreach (var n in new[] { P.ProcessQueue.QueueSnapshotGz, P.DownloadQueue.QueueSnapshotGz, P.DownloadWork.QueueSnapshotGz, P.ProcessQueue.QueueSegment(1), P.DownloadQueue.QueueSegment(1) }) Assert.Equal(queuesBefore[n], after[n]);
            Assert.DoesNotContain(after.Keys, n => n.Contains("late"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Commands_open_the_queue_they_belong_to_and_a_video_id_is_looked_up_in_processing_then_download()
    {
        var (s0, _) = await Legacy();   // before the split every command sees the one queue
        Assert.Equal("index/jobs/", (await JobQueue.OpenAsync(s0, P, "cli", true, default)).Paths.QueueDir); Assert.Equal("index/jobs/", (await JobQueue.OpenForVideoAsync(s0, P, "cli", "s1", default)).Paths.QueueDir);
        var (s, _) = await Split();
        var proc = await JobQueue.OpenAsync(s, P, "cli", false, default); var full = await JobQueue.OpenAsync(s, P, "cli", true, default);   // jobs / groups: processing; sources / priority / classify: full download
        Assert.Equal("index/jobs/process/", proc.Paths.QueueDir); Assert.Equal(4, proc.Read(st => st.Subjects.Count));
        Assert.Equal("index/jobs/download/", full.Paths.QueueDir); Assert.Equal(8, full.Read(st => st.Subjects.Count)); Assert.NotNull(full.Read(st => st.Entry("ch-1", "refresh")));
        Assert.Equal("index/jobs/process/", (await JobQueue.OpenForVideoAsync(s, P, "cli", "d1", default)).Paths.QueueDir);   // a done download is processing's
        Assert.Equal("index/jobs/download/", (await JobQueue.OpenForVideoAsync(s, P, "cli", "s2", default)).Paths.QueueDir);   // a pending sample is download's
        Assert.Equal("index/jobs/process/", (await JobQueue.OpenForVideoAsync(s, P, "cli", "nobody", default)).Paths.QueueDir);
    }

    static async Task<(FakeStore, JobQueue)> Split()
    {
        var (s, q) = await Legacy(); var dir = Path.Combine(Path.GetTempPath(), "split-" + Guid.NewGuid().ToString("N")[..8]);
        await QueueSplit.WriteLocalAsync(q, P, dir, false, 10, default); await QueueSplit.ApplyAsync(s, P, dir, () => T0, default); Directory.Delete(dir, true);
        var pq = new JobQueue(s, P, "n", () => T0); await pq.LoadAsync(default); return (s, pq);
    }

    [Fact]
    public async Task Enter_full_makes_process_ready_with_no_add_line_and_a_repeat_is_a_no_op()
    {
        var (s, pq) = await Split();
        Assert.Equal(1, await Handoff.EnterAsync(pq, new Handoff.Facts("v1", "full", Duration: 600, Title: "T", Channel: "C", Pub: "2026-11-20T00:00:00Z", Priority: 3), default));
        Assert.Equal(0, await Handoff.EnterAsync(pq, new Handoff.Facts("v1", "full"), default));   // exists: nothing appended
        var log = s.AppendText("index/jobs/process/queue.log"); Assert.Contains("\"t\":\"enter\"", log); Assert.DoesNotContain("\"stage\":\"process\",\"t\":\"add\"", log); Assert.Equal(1, log.Split('\n').Count(l => l.Contains("\"s\":\"v1\"")));
        var sub = pq.Read(st => st.Get("v1"))!; Assert.Equal("full", sub.Data); Assert.Equal("done", sub.Entries["download"].State); Assert.False(sub.Entries.ContainsKey("process"));
        var look = await pq.LookAsync(_ => true, null, default, new ReadyOptions());
        var c = look.Candidates.Single(x => x.Subject.Id == "v1"); Assert.Contains(c.Runnable, e => e.Stage == "process" && e.Priority == 3);
        // a precheck entry is a catalog and group member only: process is never ready
        await Handoff.EnterAsync(pq, new Handoff.Facts("v2", "precheck", State: "duplicate", DuplicateOf: "v1", DuplicateShare: 0.9), default);
        look = await pq.LookAsync(_ => true, null, default, new ReadyOptions()); Assert.DoesNotContain(look.Candidates, x => x.Subject.Id == "v2");
        // an upload processing already has (the migrated p1, d1) is not re-entered and not re-processed
        Assert.Equal(0, await Handoff.EnterAsync(pq, new Handoff.Facts("d1", "full"), default)); Assert.DoesNotContain(look.Candidates, x => x.Subject.Id == "d1");
    }

    [Fact]
    public async Task A_done_download_with_no_processing_subject_is_re_entered_on_the_next_look()
    {
        var (s, pq) = await Split();
        var dl = new JobQueue(s, P.DownloadQueue, "svc", () => T0); await dl.LoadAsync(default);
        // the crash: "done download" is in the download log, "enter" never reached the processing log
        await dl.AppendLinesAsync([new QLine { T = "done", S = "u1", Stage = "download", State = "done", At = At }], default);
        Assert.Null(pq.Read(st => st.Get("u1")));
        int n = await Handoff.RepairAsync(dl, pq, default); Assert.Equal(1, n); Assert.Equal("full", pq.Read(st => st.Get("u1"))!.Data);
        Assert.Equal(0, await Handoff.RepairAsync(dl, pq, default));   // the next look finds nothing left
    }

    [Fact]
    public async Task A_processing_node_never_touches_the_download_queue_and_the_import_works_before_and_after_the_split()
    {
        var (s, pq) = await Split(); while (s.Seen.TryDequeue(out _)) { }
        var node = new JobQueue(s, P, "node", () => T0); await node.LoadAsync(default); await node.LookAsync(_ => true, null, default, new ReadyOptions()); await Handoff.EnterAsync(node, new Handoff.Facts("v9", "full"), default);
        Assert.DoesNotContain(s.Seen, n => n.StartsWith("index/jobs/download")); Assert.Contains(s.Seen, n => n.StartsWith("index/jobs/process/"));
        // the group import (queue import-done group) reads the aggregate and appends to the queue it is given: the processing queue after the split
        var imp = new JobQueue(s, P, "cli", () => T0); await imp.LoadAsync(default); Assert.Equal("index/jobs/process/", imp.Paths.QueueDir);
        var r = await QueueCommand.ImportDoneGroupAsync(imp, s, P, true, default); Assert.Equal(0, r.Appends);   // dry run on an empty aggregate: works on the split layout
        var (s0, q0) = await Legacy(); var r0 = await QueueCommand.ImportDoneGroupAsync(q0, s0, P, true, default); Assert.Equal(0, r0.Appends);   // and before it
    }

    [Fact]
    public async Task The_split_lists_classify_once_applies_stored_decisions_and_reads_only_unclassified_blobs()
    {
        var (s, q) = await Legacy();
        var unc = q.Read(st => st.Subjects.Values.Where(x => x.Entries.TryGetValue("download", out var d) && d.State == Tags.Unclassified).Select(x => x.Id).OrderBy(x => x).ToList());
        Assert.NotEmpty(unc);
        foreach (var id in new[] { unc[0], "zzzzzzzzzzz" }) await s.PutAsync(P.Prefix + "classify/" + id + ".json", System.Text.Encoding.UTF8.GetBytes("{\"candidate\":true,\"kind\":\"drama\",\"reason\":\"old\"}"), null, null, false, default);
        int gets = s.Gets.Count;
        var rep = await QueueSplit.WriteLocalAsync(q, P, "unused", true, 1000, default);
        Assert.Equal(1, s.Gets.Skip(gets).Count(g => g.Contains("classify/")));   // the one still-unclassified id's blob; the unknown id is never read
        Assert.Equal(2, rep.StoredListed); Assert.Equal(1, rep.StoredApplied);
    }
}
