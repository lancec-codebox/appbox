using System.Text;

namespace IndexNode.Tests;

public class QueueLogTests
{
    static readonly Paths P = new("index/");
    sealed class Clock { public DateTimeOffset T = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero); public DateTimeOffset Now() => T; public void Add(TimeSpan s) => T += s; }
    static (FakeStore Store, Clock Clock) NewStore() { var c = new Clock(); return (new FakeStore { Clock = c.Now }, c); }
    static async Task<JobQueue> Node(FakeStore s, Clock c, string id) { var q = new JobQueue(s, P, id, c.Now); await q.LoadAsync(default); return q; }
    static QLine AddProcess(string id, string at, int pri = 0) => QLine.Add(id, "process", new JobDescriptor { Stage = "process", JobId = id, AddedAt = at, Priority = pri }, at);
    static string[]? Pick(QSubject s) => s.Entries.Values.Any(e => e.State == "pending") ? s.Entries.Values.Where(e => e.State == "pending").Select(e => e.Stage).ToArray() : null;
    static string[] Lines(FakeStore s, string blob = "index/jobs/queue.log") => s.AppendText(blob).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    static List<string> Ids(JobQueue q) => q.Read(st => st.Subjects.Keys.OrderBy(x => x).ToList());

    [Fact]
    public async Task First_load_creates_the_first_segment_and_a_second_node_sees_the_adds()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z"), AddProcess("v2", "2026-11-20T08:00:01Z")], default);
        var b = await Node(s, c, "b");
        Assert.Equal(2, b.Read(st => st.Subjects.Count));
        Assert.Equal(2, Lines(s).Length);
    }

    [Fact]
    public async Task Two_claimers_one_wins()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a"); var b = await Node(s, c, "b");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        await b.PollAsync(default);
        var won = (await Task.WhenAll(a.TryClaimAsync("v1", Pick, default), b.TryClaimAsync("v1", Pick, default))).Where(h => h != null).ToList();
        Assert.Single(won);
        Assert.Equal(1, won[0]!.Epoch);
        Assert.Single(Lines(s).Where(l => l.Contains("\"t\":\"claim\"")));
    }

    [Fact]
    public async Task A_claim_with_a_stale_view_gets_412_reads_and_gives_up_when_the_work_is_done()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a"); var b = await Node(s, c, "b");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        await b.PollAsync(default);   // b sees v1 pending ...
        var h = (await a.TryClaimAsync("v1", Pick, default))!;   // ... and then a runs it to the end
        await h.AppendAsync([new QLine { T = "done", Stage = "process" }], true, default);
        await h.FinishAsync();
        Assert.Null(await b.TryClaimAsync("v1", Pick, default));   // b's append is at a stale length: 412, it reads, the work is done
        Assert.False(s.Exists("index/jobs/active/v1.json"));
        Assert.Equal("done", b.Read(st => st.Entry("v1", "process")!.State));
    }

    [Fact]
    public async Task Append_race_between_two_writers_loses_no_line()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a"); var b = await Node(s, c, "b");
        s.BeforeAppend = _ => b.AppendLinesAsync([AddProcess("from-b", "2026-11-20T08:00:00Z")], default).GetAwaiter().GetResult();
        await a.AppendLinesAsync([AddProcess("from-a", "2026-11-20T08:00:01Z")], default);   // a's append hits 412 once, reads b's line, appends its own
        await b.PollAsync(default);
        foreach (var n in new[] { a, b }) Assert.Equal(["from-a", "from-b"], Ids(n));
    }

    [Fact]
    public async Task Lapsed_lease_is_taken_over_and_the_slow_worker_drops_its_done()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a"); var b = await Node(s, c, "b");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        var slow = (await a.TryClaimAsync("v1", Pick, default))!;
        c.Add(TimeSpan.FromMinutes(5));   // the slow worker's lease (60 s, never renewed) lapses
        Assert.Single((await b.LookAsync(_ => true, null, default)).Candidates);   // lapsed and older than the grace: abandoned, claimable
        var fast = (await b.TryClaimAsync("v1", Pick, default))!;
        Assert.Equal(2, fast.Epoch);
        // the slow worker finishes and tries to record its result: it reads to the end first, sees epoch 2 and drops
        await Assert.ThrowsAsync<LeaseLostException>(() => slow.AppendAsync([new QLine { T = "done", Stage = "process" }], true, default));
        Assert.True(slow.Lost);
        Assert.Empty(Lines(s).Where(l => l.Contains("\"t\":\"done\"")));
        await fast.AppendAsync([new QLine { T = "done", Stage = "process" }, QLine.Add("v1", "describe", new JobDescriptor { Stage = "describe", JobId = "v1" }, "2026-11-20T08:06:00Z")], false, default);
        await a.PollAsync(default);
        Assert.Equal("done", a.Read(st => st.Entry("v1", "process")!.State));
        Assert.Equal("pending", a.Read(st => st.Entry("v1", "describe")!.State));
    }

    [Fact]
    public async Task A_brand_new_active_file_without_a_lease_is_not_abandoned_until_two_minutes_old()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        await s.PutAsync("index/jobs/active/v1.json", Encoding.UTF8.GetBytes("{}"), null, null, true, default);   // created, lease not taken yet
        Assert.Empty((await a.LookAsync(_ => true, null, default)).Candidates);
        c.Add(TimeSpan.FromMinutes(3));
        Assert.Single((await a.LookAsync(_ => true, null, default)).Candidates);
    }

    [Fact]
    public async Task Crash_between_the_final_done_and_the_delete_does_not_rerun()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a"); var b = await Node(s, c, "b");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        var h = (await a.TryClaimAsync("v1", Pick, default))!;
        await h.AppendAsync([new QLine { T = "done", Stage = "process" }, QLine.Add("v1", "describe", new JobDescriptor { Stage = "describe", JobId = "v1" }, "2026-11-20T08:00:05Z")], true, default);
        // the node dies here: the active file stays, its lease lapses
        c.Add(TimeSpan.FromMinutes(5));
        var look = await b.LookAsync(st => st == "process", null, default);
        Assert.Empty(look.Candidates);                // the work is recorded as done: nothing to run
        Assert.Equal(["v1"], look.Stale);             // the leftover file is found; describe is still pending for an AI node, so a plain node leaves it
        Assert.Single((await b.LookAsync(st => st == "describe", null, default)).Candidates);
        var d = (await b.TryClaimAsync("v1", st => st.Entries.Values.Any(e => e.Stage == "describe" && e.State == "pending") ? ["describe"] : null, default))!;
        Assert.Equal(2, d.Epoch);
        await d.AppendAsync([new QLine { T = "done", Stage = "describe" }], true, default);
        await d.FinishAsync();
        Assert.False(s.Exists("index/jobs/active/v1.json"));
        Assert.Single(Lines(s).Where(l => l.Contains("\"t\":\"done\"") && l.Contains("\"st\":\"process\"")));   // process ran once
    }

    [Fact]
    public async Task Leftover_active_file_of_a_finished_subject_is_cleaned_up()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        var h = (await a.TryClaimAsync("v1", Pick, default))!;
        await h.AppendAsync([new QLine { T = "done", Stage = "process" }], true, default);   // crash before the delete
        c.Add(TimeSpan.FromMinutes(5));
        var look = await a.LookAsync(_ => true, null, default);
        Assert.Equal(["v1"], look.Stale);
        Assert.Equal(1, await a.CleanStaleAsync(look.Stale, default));
        Assert.False(s.Exists("index/jobs/active/v1.json"));
    }

    [Fact]
    public async Task A_stale_marker_of_a_subject_with_pending_work_is_left_alone_for_a_while()
    {
        // 0.13.66: 2026-11-26, such markers had their lease taken and given back on every look for work (about 150 times a minute)
        var (s, c) = NewStore();
        var a = await Node(s, c, "a");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        var h = (await a.TryClaimAsync("v1", Pick, default))!;   // claimed, never finished, the lease lapses: the subject still has pending work
        c.Add(TimeSpan.FromMinutes(5));
        var look = await a.LookAsync(_ => false, null, default);   // this node runs no stage of it (otherwise it would be a candidate, not stale)
        Assert.Equal(["v1"], look.Stale);
        s.Ops.Clear(); int Total() => s.Ops.Values.Sum();
        Assert.Equal(0, await a.CleanStaleAsync(look.Stale, default)); Assert.True(s.Exists("index/jobs/active/v1.json"));   // pending: kept
        int first = Total(); Assert.True(first > 0);
        for (int i = 0; i < 20; i++) await a.CleanStaleAsync(look.Stale, default);
        Assert.Equal(first, Total());                                             // the next looks touch nothing
        c.Add(JobQueue.StalePendingPause + TimeSpan.FromSeconds(1));
        await a.CleanStaleAsync(look.Stale, default); Assert.True(Total() > first);   // looked at again after the pause
    }

    [Fact]
    public async Task Claim_of_work_that_is_already_done_releases_and_deletes_the_file()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        var h = (await a.TryClaimAsync("v1", Pick, default))!;
        await h.AppendAsync([new QLine { T = "done", Stage = "process" }], true, default);
        c.Add(TimeSpan.FromMinutes(5));
        var b = await Node(s, c, "b");
        Assert.Null(await b.TryClaimAsync("v1", Pick, default));   // 0.13.37: nothing to run -> decided from the queue state, no blob touched
        var look = await b.LookAsync(_ => true, null, default);     // the one listing of jobs/active/ per look names the abandoned file ...
        Assert.Contains("v1", look.Stale);
        await b.CleanStaleAsync(look.Stale, default);               // ... and it is cleaned there, not per candidate
        Assert.False(s.Exists("index/jobs/active/v1.json"));
    }

    [Fact]
    public async Task Rollover_while_another_writer_appends_loses_no_line()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a"); var b = await Node(s, c, "b");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        // b appends after a read the log: a's seal fails (length changed), a reads the line and seals again
        s.BeforeAppend = null;
        await b.AppendLinesAsync([AddProcess("v-sneaky", "2026-11-20T08:00:02Z")], default);
        await a.RollOverAsync(default);
        Assert.Equal(2, a.Segment);
        await b.AppendLinesAsync([AddProcess("v-after", "2026-11-20T08:00:03Z")], default);   // b's segment is sealed: it moves to segment 2 by itself
        Assert.Equal(2, b.Segment);
        await a.PollAsync(default);
        foreach (var n in new[] { a, b }) Assert.Equal(["v-after", "v-sneaky", "v1"], Ids(n));
        Assert.Equal(["v-after", "v-sneaky", "v1"], Ids(await Node(s, c, "fresh")));   // starts from the snapshot
        Assert.True((await s.GetAppendInfoAsync("index/jobs/queue.log", default))!.Sealed);
    }

    [Fact]
    public async Task Two_nodes_racing_a_rollover_while_a_third_appends_land_one_snapshot_lose_no_line_and_converge()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a"); var b = await Node(s, c, "b"); var t = await Node(s, c, "t");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        await b.PollAsync(default); await t.PollAsync(default);
        var snap = "index/jobs/queue.snapshot.json.gz";
        int before = s.Puts.GetValueOrDefault(snap), snapsBefore = s.Puts.GetValueOrDefault(snap);
        var appends = Task.Run(async () => { for (int i = 0; i < 20; i++) await t.AppendLinesAsync([AddProcess("t" + i.ToString("00"), "2026-11-20T08:01:" + i.ToString("00") + "Z")], default); });
        await Task.WhenAll(Task.Run(() => a.RollOverAsync(default)), Task.Run(() => b.RollOverAsync(default)), appends);
        await Task.WhenAll(a.PollAsync(default), b.PollAsync(default), t.PollAsync(default));
        Assert.Equal(1, s.Puts.GetValueOrDefault(snap) - before);                                  // one snapshot landed for the rollover
        var want = new[] { "v1" }.Concat(Enumerable.Range(0, 20).Select(i => "t" + i.ToString("00"))).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (var n in new[] { a, b, t }) { Assert.Equal(want, Ids(n)); Assert.Equal(2, n.Segment); }   // no line lost; all three converge on the new segment
        Assert.Equal(want, Ids(await Node(s, c, "fresh")));
    }

    [Fact]
    public async Task A_node_that_finds_a_sealed_segment_the_snapshot_does_not_point_at_finishes_the_rollover()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a"); var b = await Node(s, c, "b");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        await b.PollAsync(default);
        // the roller dies after step (1): the segment is sealed, nothing else was done
        var info = await s.GetAppendInfoAsync("index/jobs/queue.log", default);
        Assert.True(await s.SealAppendBlobAsync("index/jobs/queue.log", info!.Length, default));
        await b.PollAsync(default);
        Assert.Equal(2, b.Segment);
        Assert.NotNull(await s.GetAppendInfoAsync(P.QueueSegment(2), default));
        Assert.Equal(["v1"], Ids(await Node(s, c, "fresh")));
        Assert.Equal(2, System.Text.Json.Nodes.JsonNode.Parse(QueueState.Unpack(s.Blobs["index/jobs/queue.snapshot.json.gz"].Data))!["seg"]!.GetValue<int>());
    }

    [Fact]
    public async Task Append_to_a_sealed_segment_re_reads_and_lands_in_the_live_one()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a"); var b = await Node(s, c, "b");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        await a.RollOverAsync(default);
        await b.AppendLinesAsync([AddProcess("v2", "2026-11-20T08:00:01Z")], default);   // b was still on segment 1
        Assert.Equal(2, b.Segment);
        Assert.Equal(2, (await Node(s, c, "c")).Read(st => st.Subjects.Count));
    }

    [Fact]
    public async Task Rollover_happens_by_itself_at_the_byte_limit()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a");
        var big = new string('x', 8000);
        for (int i = 0; i < 140 && a.Segment == 1; i++)
            await a.AppendLinesAsync([QLine.Add("v" + i, "download", new JobDescriptor { Stage = "download", JobId = "v" + i, Title = big }, "2026-11-20T08:00:00Z")], default);
        Assert.Equal(2, a.Segment);
        Assert.True(s.Exists("index/jobs/queue.2.log"));
        Assert.True(s.Exists("index/jobs/queue.snapshot.json.gz"));
    }

    [Fact]
    public async Task An_idle_look_is_one_ranged_read_and_one_listing()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        s.Ops.Clear();
        await a.LookAsync(_ => false, null, default);
        Assert.Equal(1, s.Ops.GetValueOrDefault("get")); Assert.Equal(1, s.Ops.GetValueOrDefault("list"));
        Assert.Equal(2, s.Ops.Values.Sum());
    }

    [Fact]
    public async Task Claim_order_is_priority_then_oldest_then_id_and_due_is_respected()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a");
        await a.AppendLinesAsync([AddProcess("old", "2026-11-20T07:00:00Z"), AddProcess("new", "2026-11-20T07:30:00Z"), AddProcess("vip", "2026-11-20T07:45:00Z", 10),
            QLine.Add("later", "process", new JobDescriptor { Stage = "process", JobId = "later" }, "2026-11-20T06:00:00Z", due: "2026-11-20T09:00:00Z")], default);
        Assert.Equal(["vip", "old", "new"], (await a.LookAsync(_ => true, null, default)).Candidates.Select(x => x.Subject.Id).ToList());
        await a.AppendLinesAsync([new QLine { T = "pri", S = "new", Pri = 50 }], default);
        Assert.Equal("new", (await a.LookAsync(_ => true, null, default)).Candidates[0].Subject.Id);
        c.Add(TimeSpan.FromHours(2));
        Assert.Equal(4, (await a.LookAsync(_ => true, null, default)).Candidates.Count);
    }

    [Fact]
    public async Task Failed_line_records_step_error_attempt_and_steps_done_and_the_next_claim_resumes()
    {
        var (s, c) = NewStore();
        var a = await Node(s, c, "a");
        await a.AppendLinesAsync([AddProcess("v1", "2026-11-20T08:00:00Z")], default);
        var h = (await a.TryClaimAsync("v1", Pick, default))!;
        await h.AppendAsync([new QLine { T = "done", Stage = "process", Step = "frames" }, new QLine { T = "done", Stage = "process", Step = "sscd" }], false, default);
        await h.AppendAsync([new QLine { T = "failed", Stage = "process", Step = "centers", Error = "boom", Attempt = 1, State = "pending", Due = "2026-11-20T08:02:00Z", Done = ["frames", "sscd"] }], true, default);
        await h.FinishAsync();
        var e = a.Read(st => st.Entry("v1", "process")!);
        Assert.Equal(["frames", "sscd"], e.StepsDone);
        Assert.Equal(1, e.Attempts); Assert.Equal("boom", e.LastError);
        Assert.Empty((await a.LookAsync(_ => true, null, default)).Candidates);   // not due yet
        c.Add(TimeSpan.FromMinutes(3));
        var look = await a.LookAsync(_ => true, null, default);
        Assert.Single(look.Candidates);
        Assert.Equal(["frames", "sscd"], look.Candidates[0].Runnable[0].StepsDone);
    }

    [Fact]
    public async Task The_snapshot_is_slimmed_for_finished_subjects_gzipped_and_a_plain_one_from_the_first_migration_still_loads()
    {
        var s = new FakeStore(); var P2 = new Paths("index/"); var now = DateTimeOffset.Parse("2026-11-20T12:00:00Z");
        var q = new JobQueue(s, P2, "t1", () => now); await q.LoadAsync(default);
        var lines = new List<QLine>();
        for (int i = 0; i < 300; i++)
        {
            var id = "v" + i.ToString("D10"); var d = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + id, 480, null); d.JobId = id; d.Title = new string('t', 80); d.ChannelName = "A channel name";
            lines.Add(QLine.Add(id, "download", d, "2026-11-19T00:00:00Z")); lines.Add(QLine.Add(id, "process", JobDescriptor.NewProcess(id), "2026-11-19T00:00:00Z"));
        }
        await q.AppendLinesAsync(lines, default);
        // finish: the downloads are done, the processes failed for good (a subject that is not all-done)
        var fin = new List<QLine>(); for (int i = 0; i < 300; i++) { var id = "v" + i.ToString("D10"); fin.Add(new QLine { T = "done", S = id, Stage = "download", At = "2026-11-19T01:00:00Z" }); fin.Add(new QLine { T = "failed", S = id, Stage = "process", Permanent = true, Error = "classify: music (not a drama)", At = "2026-11-19T01:00:00Z" }); }
        await q.AppendLinesAsync(fin, default);
        byte[] plain; (plain, _) = (q.Read(st => st.ToSnapshot(2, 0, 0, "x", now)), 0);
        var gz = QueueState.Pack(plain);
        Assert.True(plain.Length / 300 < 500, "bytes per finished subject: " + plain.Length / 300); Assert.True(gz.Length / 300 < 120, "gzip bytes per subject: " + gz.Length / 300);
        var (st2, doc) = QueueState.FromSnapshot(gz); Assert.Equal(300, st2.Subjects.Count);
        var e = st2.Entry("v0000000007", "process")!; Assert.Equal("failed", e.State); Assert.True(e.Permanent); Assert.Equal("classify: music (not a drama)", e.LastError);
        Assert.Equal("done", st2.Entry("v0000000007", "download")!.State); Assert.Null(st2.Entry("v0000000007", "download")!.Job!.Title);
        // a plain snapshot (the 25 MB file of the first migration) still loads, and the first rollover writes the .gz
        var s2 = new FakeStore(); await s2.PutAsync(P2.QueueSnapshot, plain, null, null, false, default); await s2.CreateAppendBlobAsync(P2.QueueSegment(2), true, default);
        var q2 = new JobQueue(s2, P2, "t2", () => now); await q2.LoadAsync(default);
        Assert.Equal("failed", q2.Read(x => x.Entry("v0000000007", "process"))!.State); Assert.True(s2.Exists(P2.QueueSnapshotGz));
    }
}
