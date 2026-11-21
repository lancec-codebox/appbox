using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.10: per-drama sequencing, the group step, retained vectors, the index fold and the bootstrap commands.</summary>
[Collection("console")]
public class GroupStepTests
{
    static readonly Paths P = new("index/");
    const int D = CentersFile.Dim;
    sealed class Clock { public DateTimeOffset T = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero); public DateTimeOffset Now() => T; }
    static string[]? Pick(QSubject s) => s.Entries.Values.Any(e => e.State == "pending") ? s.Entries.Values.Where(e => e.State == "pending").Select(e => e.Stage).ToArray() : null;
    static QLine AddProcess(string id) => QLine.Add(id, "process", new JobDescriptor { Stage = "process", JobId = id, AddedAt = "2026-11-21T08:00:00Z" }, "2026-11-21T08:00:00Z");
    static async Task<JobQueue> Node(FakeStore s, Clock c, string id) { var q = new JobQueue(s, P, id, c.Now); await q.LoadAsync(default); return q; }
    static string Tmp() => Path.Combine(Path.GetTempPath(), "gs-" + Guid.NewGuid().ToString("N")[..8]);

    // ---- sequencing
    [Fact]
    public async Task Concurrent_first_pre_check_lines_get_distinct_increasing_numbers_and_a_repeat_never_renumbers()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now };
        var nodes = new List<JobQueue>(); for (int i = 0; i < 4; i++) nodes.Add(await Node(s, c, "n" + i));
        await nodes[0].AppendLinesAsync(Enumerable.Range(0, 8).Select(i => AddProcess("v" + i)), default);
        foreach (var n in nodes) await n.PollAsync(default);
        var claims = new List<ClaimHandle>();
        for (int i = 0; i < 8; i++) claims.Add((await nodes[i % 4].TryClaimAsync("v" + i, Pick, default))!);
        var seqs = await Task.WhenAll(claims.Select(h => h.AssignSeqAsync(default)));
        Assert.Equal(8, seqs.Distinct().Count()); Assert.Equal(Enumerable.Range(1, 8).Select(x => (long)x), seqs.OrderBy(x => x));
        Assert.Equal(seqs[3], await claims[3].AssignSeqAsync(default));   // already numbered: the same number, no new line
        await nodes[0].PollAsync(default);
        Assert.Equal(8, nodes[0].Read(st => st.Subjects.Values.Count(x => x.Seq != null)));
        Assert.Equal(8, s.AppendText("index/jobs/queue.log").Split('\n').Count(l => l.Contains("\"t\":\"seq\"")));
        // a node that starts later reads the same numbers (log, then the snapshot keeps the maximum)
        var late = await Node(s, c, "late"); Assert.Equal(seqs[5], late.Read(st => st.Get("v5")!.Seq!.Value)); Assert.Equal(9, late.Read(st => st.NextSeq()));
    }

    static async Task<List<string>> Blockers(JobQueue q, IStore s, string id) => await GroupSequencing.BlockersAsync(q, s, P, id, default);

    [Fact]
    public async Task A_drama_waits_only_for_an_earlier_one_still_on_its_way_to_frames_and_a_stuck_group_step_blocks_nobody()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        await q.AppendLinesAsync([AddProcess("e1"), AddProcess("e2"), AddProcess("e3")], default);
        await q.AppendLinesAsync([new QLine { T = "seq", S = "e1", Seq = 1 }, new QLine { T = "seq", S = "e2", Seq = 2 }, new QLine { T = "seq", S = "e3", Seq = 3 }], default);
        Assert.Empty(await Blockers(q, s, "e1"));
        Assert.Equal(["e1", "e2"], await Blockers(q, s, "e3"));                      // both mid-processing: no frames yet
        await PutFrames(s, "e1", Make(40, 5, 1), 40);
        Assert.Equal(["e2"], await Blockers(q, s, "e3"));                            // e1 has frames: it no longer blocks
        var h = (await q.TryClaimAsync("e1", Pick, default))!;                       // e1's process is done and its GROUP step is pending/stuck
        await h.AppendAsync([new QLine { T = "done", Stage = "process" }, QLine.Add("e1", "group", GroupSequencing.NewJob("e1", 0), "2026-11-21T08:00:00Z")], true, default);
        Assert.Equal(["e2"], await Blockers(q, s, "e3"));
        Assert.Empty(await Blockers(q, s, "e2"));
        await PutFrames(s, "e2", Make(40, 5, 2), 40);                                // e2's frames now exist
        Assert.Empty(await Blockers(q, s, "e3"));
    }

    [Fact]
    public async Task Fifty_bootstrap_group_steps_on_eight_lanes_run_in_parallel_without_waiting_and_see_all_earlier_vectors()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        var ids = Enumerable.Range(1, 50).Select(i => "b" + i.ToString("D2")).ToList(); var src = Enumerable.Range(0, 10).Select(i => Make(40, 8, 100 + i)).ToList();
        for (int i = 0; i < 50; i++) await PutFrames(s, ids[i], src[i % 10], 40, 1.0, i);   // 10 distinct videos, five copies each
        await q.AppendLinesAsync(ids.Select(AddProcess), default);
        await q.AppendLinesAsync(ids.Select((id, i) => new QLine { T = "seq", S = id, Seq = i + 1 }), default);
        await q.AppendLinesAsync(ids.Select(id => new QLine { T = "done", S = id, Stage = "process", State = "done", At = "2026-11-21T08:01:00Z" }), default);
        var stage = new GroupStage(s); int inFlight = 0, peak = 0, waited = 0;
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (id, ct) =>
        {
            if ((await Blockers(q, s, id)).Count > 0) Interlocked.Increment(ref waited);
            int n = Interlocked.Increment(ref inFlight); int pk; while (n > (pk = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, n, pk);
            var ctx = new JobContext { Job = new JobDescriptor { Stage = "group", JobId = id }, Stage = "group", BlobName = id, Worker = 0, Store = s, Paths = P, NodeId = "t", Token = default, Queue = q, ThreadShare = () => 1, Report = (_, _) => { } };
            await stage.RunAsync(ctx); await Task.Delay(5); Interlocked.Decrement(ref inFlight);
        });
        Assert.Equal(0, waited); Assert.True(peak > 1, "group steps ran one after another");
        for (int i = 10; i < 50; i++)
        {
            var rec = (JsonObject)(await VideoJson.ReadAsync(s, P, ids[i], default))!.Root["grouping"]!;
            Assert.Equal("join", (string)rec["proposal"]!["kind"]!);   // a copy sees the vectors of the earlier copies, however the lanes were scheduled
        }
    }


    [Fact]
    public async Task Seq_assign_orders_existing_dramas_by_first_pre_check_dry_run_changes_nothing_and_a_rerun_adds_nothing()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        await q.AppendLinesAsync([AddProcess("x"), AddProcess("y"), AddProcess("z")], default);
        await VideoJsonSeed.PutAsync(s, P, "x", 300, precheck: new JsonObject { ["at"] = "2026-01-03T00:00:00Z", ["decision"] = "new" });
        await VideoJsonSeed.PutAsync(s, P, "y", 300, precheck: new JsonObject { ["at"] = "2026-01-01T00:00:00Z", ["decision"] = "new" });
        await VideoJsonSeed.PutAsync(s, P, "z", 300, precheck: new JsonObject { ["at"] = "2026-01-02T00:00:00Z", ["decision"] = "new" });
        var dry = await SeqCommand.AssignAsync(q, s, P, true, default); Assert.Equal(3, dry.Assigned); Assert.Equal(0, q.Read(st => st.Subjects.Values.Count(x => x.Seq != null)));
        await SeqCommand.AssignAsync(q, s, P, false, default);
        Assert.Equal([1L, 2L, 3L], new[] { "y", "z", "x" }.Select(id => q.Read(st => st.Get(id)!.Seq!.Value)));
        var again = await SeqCommand.AssignAsync(q, s, P, false, default); Assert.Equal(0, again.Assigned);
        // groups enqueue: dry run counts, then create-only
        await q.AppendLinesAsync([new QLine { T = "done", S = "x", Stage = "process", State = "done", At = "2026-11-21T08:01:00Z" }, new QLine { T = "done", S = "y", Stage = "process", State = "done", At = "2026-11-21T08:01:00Z" }], default);
        var d2 = await SeqCommand.EnqueueGroupsAsync(q, s, P, true, default); Assert.Equal(2, d2.Added); Assert.Null(q.Read(st => st.Entry("x", "group")));
        var r2 = await SeqCommand.EnqueueGroupsAsync(q, s, P, false, default); Assert.Equal(2, r2.Added); Assert.NotNull(q.Read(st => st.Entry("y", "group")));
        var r3 = await SeqCommand.EnqueueGroupsAsync(q, s, P, false, default); Assert.Equal(0, r3.Added); Assert.Equal(2, r3.Existing);
    }

    // ---- stretches: reordered scenes at different offsets
    [Fact]
    public void Stretches_map_each_covered_run_to_its_source_and_offset_even_when_the_scenes_are_reordered()
    {
        var times = Enumerable.Range(0, 30).Select(i => (double)i).ToList(); var best = new FrameBest?[30];
        for (int i = 0; i < 10; i++) best[i] = new FrameBest(0, 100 + i, 0.97f);       // scene A is at 100.. in drama u0 (offset +100)
        for (int i = 10; i < 20; i++) best[i] = new FrameBest(1, 5 + (i - 10), 0.95f);  // scene B at 5.. in u1 (offset -5)
        for (int i = 20; i < 30; i++) best[i] = new FrameBest(0, 40 + (i - 20), 0.4f);  // nothing covers the rest
        var st = GroupStage.Stretches(times, best, u => "u" + u, 0.90, 30);
        Assert.Equal(2, st.Count);
        Assert.Equal(("u0", 100.0, 0.0, 9.0), ((string)st[0]["upload"]!, (double)st[0]["offset"]!, (double)st[0]["from"]!, (double)st[0]["to"]!));
        Assert.Equal(("u1", -5.0, 10.0, 19.0), ((string)st[1]["upload"]!, (double)st[1]["offset"]!, (double)st[1]["from"]!, (double)st[1]["to"]!));
    }

    // ---- the group step on real frames files
    static float[] Make(int n, int groups, int seed)
    {
        var rng = new Random(seed); var dirs = new float[groups][]; var x = new float[n * D];
        float[] G() { var v = new float[D]; for (int j = 0; j < D; j++) v[j] = (float)(Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble())); Vec.Normalize(v); return v; }
        for (int g = 0; g < groups; g++) dirs[g] = G();
        for (int i = 0; i < n; i++) { var d = dirs[i % groups]; var z = G(); var row = new float[D]; for (int j = 0; j < D; j++) row[j] = d[j] + 0.05f * z[j]; Vec.Normalize(row); row.CopyTo(x, i * D); }
        return x;
    }
    static byte[] F16(float[] x) { var b = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2, 2), (Half)x[i]); return b; }
    static async Task PutFrames(IStore store, string id, float[] x, int n, double dt = 1.0, double t0 = 0)
    {
        var bin = CentersStep.Build(id, F16(x), Enumerable.Range(0, n).Select(i => t0 + i * dt).ToList(), 2).Bin;
        await FramesFile.WriteAsync(store, P, id, bin, 0, false, default);
        await VideoJsonSeed.PutAsync(store, P, id, 300);
    }

    [Fact]
    public async Task The_group_step_joins_a_duplicate_retains_almost_nothing_keeps_a_new_drama_whole_and_the_index_then_never_opens_centers_zip()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a"); var cache = Tmp();
        try
        {
            var a = Make(300, 40, 1); var other = Make(200, 40, 2);
            await PutFrames(s, "a1", a, 300); await PutFrames(s, "a2", a, 300, 1.0, 50);   // a2: the same video, shifted by 50 s
            await PutFrames(s, "z9", other, 200);
            await q.AppendLinesAsync([AddProcess("a1"), AddProcess("a2"), AddProcess("z9")], default);
            await q.AppendLinesAsync([new QLine { T = "seq", S = "a1", Seq = 1 }, new QLine { T = "seq", S = "a2", Seq = 2 }, new QLine { T = "seq", S = "z9", Seq = 3 }], default);
            // the index at this point holds a1 only (a2 and z9 are fresh)
            var ups = await SearchIndexBuild.ListCentersAsync(s, P, default); var first = await SearchIndexBuild.BuildAsync(s, P, ups.Where(u => u.Id == "a1").ToList(), null, null, 1, 1, 2, _ => { }, default);
            first.BuiltAt = DateTimeOffset.UtcNow.AddMinutes(-5); await first.PublishAsync(s, P, default);
            var stage = new GroupStage(s);
            async Task Run(string id) { var ctx = new JobContext { Job = new JobDescriptor { Stage = "group", JobId = id }, Stage = "group", BlobName = id, Worker = 0, Store = s, Paths = P, NodeId = "t", Token = default, Queue = q, ThreadShare = () => 2, Report = (_, _) => { } }; await stage.RunAsync(ctx); }
            await Run("a1"); await Run("a2"); await Run("z9");
            JsonObject Rec(string id) => (JsonObject)VideoJson.ReadAsync(s, P, id, default).Result!.Root["grouping"]!;
            var r1 = Rec("a1"); Assert.Equal("new", (string)r1["proposal"]!["kind"]!); Assert.Null(r1["retainedFile"]);        // first drama: nothing is before it
            var r2 = Rec("a2"); Assert.Equal("join", (string)r2["proposal"]!["kind"]!); Assert.Equal("a1", (string)r2["proposal"]!["joinUpload"]!);
            Assert.True((double)r2["coveredShare"]! >= 0.95); Assert.InRange((int)r2["retained"]!, 1, 15);                     // a duplicate keeps almost nothing...
            Assert.NotNull(await s.HeadAsync(P.RetainedFile("a2"), default));
            var kept = CentersFile.ParseHeader((await s.GetAsync(P.RetainedFile("a2"), default))!.Data); Assert.Equal((int)r2["retained"]!, kept.Frames);
            Assert.Equal(-50.0, (double)r2["stretches"]![0]!["offset"]!, 1);                                                   // ... and the stretch maps back to a1 (a2 is 50 s later: offset -50)
            var r3 = Rec("z9"); Assert.Equal("new", (string)r3["proposal"]!["kind"]!); Assert.Null(r3["retainedFile"]);        // an unrelated drama keeps all its frames
            // the index build now takes a2 from its retained file, the others from frames files, with no centers.zip anywhere
            var ups2 = await SearchIndexBuild.ListCentersAsync(s, P, default);
            Assert.Equal(P.RetainedFile("a2"), ups2.First(u => u.Id == "a2").Zip.Name); Assert.Equal(P.FramesFile("z9"), ups2.First(u => u.Id == "z9").Zip.Name);
            var second = await SearchIndexBuild.BuildAsync(s, P, ups2, first.Mean, first, 2, 1, 2, _ => { }, default);
            Assert.Equal(kept.Frames, second.Uploads.First(u => u.Id == "a2").Frames);
            Assert.DoesNotContain(s.Calls, x => x.Contains("centers.zip"));
        }
        finally { try { Directory.Delete(cache, true); } catch { } }
    }

    static async Task<JobQueue> QueueWith(FakeStore s, string id, long seq)
    {
        var c = new Clock(); var q = new JobQueue(s, P, "a", c.Now); await q.LoadAsync(default);
        await q.AppendLinesAsync([AddProcess(id), new QLine { T = "seq", S = id, Seq = seq }], default); return q;
    }

    // ---- 0.13.11: numbering every drama, claim order, jobs list
    static async Task<SearchData> HeadWith(FakeStore s, params string[] ids)
    {
        var ups = await SearchIndexBuild.ListCentersAsync(s, P, default); var head = await SearchIndexBuild.BuildAsync(s, P, ups.Where(u => ids.Contains(u.Id)).OrderBy(u => Array.IndexOf(ids, u.Id)).ToList(), null, null, 1, 1, 2, _ => { }, default);
        head.BuiltAt = DateTimeOffset.UtcNow.AddMinutes(-5); await head.PublishAsync(s, P, default); return head;
    }

    [Fact]
    public async Task Seq_assign_numbers_every_drama_with_frames_pre_log_ones_first_in_head_row_order_then_the_log_ones_and_a_rerun_adds_nothing()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        foreach (var id in new[] { "p1", "p2", "p3", "l1", "l2" }) await PutFrames(s, id, Make(20, 4, id.GetHashCode()), 20);
        await HeadWith(s, "p3", "p1", "p2");                                         // head rows: p3, p1, p2 (no log line for any of them)
        await q.AppendLinesAsync([AddProcess("l1"), AddProcess("l2")], default);
        await VideoJsonSeed.PutAsync(s, P, "l1", 300, precheck: new JsonObject { ["at"] = "2026-01-02T00:00:00Z", ["decision"] = "new" });
        await VideoJsonSeed.PutAsync(s, P, "l2", 300, precheck: new JsonObject { ["at"] = "2026-01-01T00:00:00Z", ["decision"] = "new" });
        var dry = await SeqCommand.AssignAsync(q, s, P, true, default); Assert.Equal(5, dry.Assigned); Assert.Equal(3, dry.PreLog); Assert.Equal(0, q.Read(st => st.Subjects.Values.Count(x => x.Seq != null)));
        await SeqCommand.AssignAsync(q, s, P, false, default);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], new[] { "p3", "p1", "p2", "l2", "l1" }.Select(id => q.Read(st => st.Get(id)!.Seq!.Value)));   // pre-log in head order, then log by first pre-check
        var again = await SeqCommand.AssignAsync(q, s, P, false, default); Assert.Equal(0, again.Assigned); Assert.Equal(5, again.Already);
        // groups enqueue now covers every numbered drama that has frames, pre-log ones included
        var er = await SeqCommand.EnqueueGroupsAsync(q, s, P, false, default); Assert.Equal(5, er.Added); Assert.Equal(0, er.NoSeq);
        Assert.NotNull(q.Read(st => st.Entry("p1", "group"))); Assert.Equal(0, (await SeqCommand.EnqueueGroupsAsync(q, s, P, false, default)).Added);
    }

    [Fact]
    public async Task Seq_assign_renumber_renumbers_everyone_by_the_rule_is_idempotent_and_is_refused_once_a_group_step_has_finished()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        foreach (var id in new[] { "p1", "p2", "l1" }) await PutFrames(s, id, Make(20, 4, id.GetHashCode()), 20);
        await HeadWith(s, "p2", "p1");
        await q.AppendLinesAsync([AddProcess("l1"), new QLine { T = "seq", S = "l1", Seq = 1 }, new QLine { T = "seq", S = "p1", Seq = 2 }], default);   // the old (log-only) numbering
        var r = await SeqCommand.AssignAsync(q, s, P, false, default, renumber: true);
        Assert.Equal(3, r.Assigned); Assert.Equal(2, r.Renumbered);
        Assert.Equal([1L, 2L, 3L], new[] { "p2", "p1", "l1" }.Select(id => q.Read(st => st.Get(id)!.Seq!.Value)));
        Assert.Equal(3, q.Read(st => st.Subjects.Values.Select(x => x.Seq).Distinct().Count()));
        await SeqCommand.AssignAsync(q, s, P, false, default, renumber: true);
        Assert.Equal([1L, 2L, 3L], new[] { "p2", "p1", "l1" }.Select(id => q.Read(st => st.Get(id)!.Seq!.Value)));   // same rule, same numbers
        Assert.Equal(3, q.Read(st => st.MaxSeq));
        // a group step has finished: refused, nothing changes
        await q.AppendLinesAsync([QLine.Add("p2", "group", GroupSequencing.NewJob("p2", 0), "2026-11-21T08:00:00Z")], default);
        var h = (await q.TryClaimAsync("p2", st => new[] { "group" }, default))!; await h.AppendAsync([new QLine { T = "done", Stage = "group" }], true, default);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => SeqCommand.AssignAsync(q, s, P, false, default, renumber: true));
        Assert.Contains("refused", e.Message); Assert.Equal(1L, q.Read(st => st.Get("p2")!.Seq!.Value));
    }

    [Fact]
    public async Task One_lane_with_50_older_process_entries_and_10_due_group_entries_claims_the_group_entries_first_but_after_search_index()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        var older = Enumerable.Range(0, 50).Select(i => QLine.Add("pr" + i.ToString("D2"), "process", new JobDescriptor { Stage = "process", JobId = "pr" + i.ToString("D2"), AddedAt = "2026-11-20T08:00:00Z" }, "2026-11-20T08:00:00Z"));
        await q.AppendLinesAsync(older, default); c.T = c.T.AddHours(24);
        await q.AppendLinesAsync(Enumerable.Range(0, 10).Select(i => QLine.Add("gr" + i, "group", GroupSequencing.NewJob("gr" + i, 0), Protocol.Utc(c.T))), default);
        await q.AppendLinesAsync([QLine.Add("search-index", "search-index", new JobDescriptor { Stage = "search-index", JobId = "search-index", AddedAt = Protocol.Utc(c.T) }, Protocol.Utc(c.T))], default);
        var order = q.Read(st => st.Candidates(Protocol.Utc(c.T), _ => true)).Select(x => x.Subject.Id).ToList();
        Assert.Equal("search-index", order[0]);
        Assert.All(order.Skip(1).Take(10), id => Assert.StartsWith("gr", id));      // the due group entries, then
        Assert.All(order.Skip(11), id => Assert.StartsWith("pr", id));              // the 50 older process entries
    }

    [Fact]
    public async Task Jobs_list_prints_the_pending_entries_of_a_stage_and_the_held_ones()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        await q.AppendLinesAsync([AddProcess("v1"), QLine.Add("g1", "group", GroupSequencing.NewJob("g1", 0), "2026-11-21T08:00:00Z")], default);
        var rows = PriorityTool.List(q, "group", 20); Assert.Single(rows); Assert.Equal(("group", "g1"), (rows[0].Stage, rows[0].Id));
        var o = new StringWriter(); var old = Console.Out; Console.SetOut(o);
        try { Assert.Equal(0, await JobsCommand.ListAsync(Args.Parse(["list", "--stage", "group"]), q)); Assert.Equal(0, await JobsCommand.ListAsync(Args.Parse(["list", "--held", "ai"]), q)); } finally { Console.SetOut(old); }
        Assert.Contains("group/g1", o.ToString()); Assert.DoesNotContain("process/v1", o.ToString());
    }


    // ---- 0.13.12: unstarted dramas carry no number, the claim gate, group entries in seq order

    [Fact]
    public async Task A_forced_seq_line_without_a_number_clears_it_and_the_next_number_is_still_highest_plus_one()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        await q.AppendLinesAsync([AddProcess("x"), AddProcess("y"), new QLine { T = "seq", S = "x", Seq = 1 }, new QLine { T = "seq", S = "y", Seq = 2 }], default);
        await q.AppendLinesAsync([new QLine { T = "seq", S = "y", Force = true }], default);
        Assert.Null(q.Read(st => st.Get("y")!.Seq)); Assert.Equal(1L, q.Read(st => st.Get("x")!.Seq!.Value)); Assert.Equal(3, q.Read(st => st.NextSeq()));
        var late = await Node(s, c, "late"); Assert.Null(late.Read(st => st.Get("y")!.Seq)); Assert.Equal(3, late.Read(st => st.NextSeq()));   // a later node replays the same
    }

    [Fact]
    public async Task Clear_unstarted_clears_only_unframed_unstarted_dramas_keeps_framed_claimed_stepped_and_pre_checked_ones_and_works_after_group_steps_finished()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        string[] ids = ["framed", "stepped", "claimed", "prechecked", "idle1", "idle2", "gdone"];
        await q.AppendLinesAsync(ids.Select(AddProcess), default);
        await q.AppendLinesAsync(ids.Select((id, k) => new QLine { T = "seq", S = id, Seq = k + 1 }), default);
        await PutFrames(s, "framed", Make(20, 4, 1), 20); await PutFrames(s, "gdone", Make(20, 4, 2), 20);
        await VideoJsonSeed.PutAsync(s, P, "prechecked", 300, precheck: new JsonObject { ["at"] = "2026-01-01T00:00:00Z", ["decision"] = "new" });
        await VideoJsonSeed.PutAsync(s, P, "idle1", 300); await VideoJsonSeed.PutAsync(s, P, "idle2", 300);
        var hs = (await q.TryClaimAsync("stepped", Pick, default))!; await hs.AppendAsync([new QLine { T = "done", Stage = "process", Step = "frames", State = "pending", Due = "2026-11-21T09:00:00Z" }], true, default);   // one step done, claim released
        var hc = (await q.TryClaimAsync("claimed", Pick, default))!;                                                           // claimed right now
        // a finished group step
        await q.AppendLinesAsync([QLine.Add("gdone", "group", GroupSequencing.NewJob("gdone", 0), "2026-11-21T08:00:00Z")], default);
        var hg = (await q.TryClaimAsync("gdone", st => new[] { "group" }, default))!; await hg.AppendAsync([new QLine { T = "done", Stage = "group" }], true, default);
        var dry = await SeqCommand.ClearUnstartedAsync(q, s, P, true, default);
        Assert.Equal(2, dry.Cleared); Assert.Equal(["idle1", "idle2"], dry.First); Assert.Equal(7, q.Read(st => st.Subjects.Values.Count(x => x.Seq != null)));
        var real = await SeqCommand.ClearUnstartedAsync(q, s, P, false, default);
        Assert.Equal(2, real.Cleared);
        Assert.Null(q.Read(st => st.Get("idle1")!.Seq)); Assert.Null(q.Read(st => st.Get("idle2")!.Seq));
        foreach (var id in new[] { "framed", "stepped", "claimed", "prechecked", "gdone" }) Assert.NotNull(q.Read(st => st.Get(id)!.Seq));   // never cleared
        Assert.Equal(0, (await SeqCommand.ClearUnstartedAsync(q, s, P, false, default)).Cleared);                              // idempotent
        Assert.Equal(8, q.Read(st => st.NextSeq()));                                                                           // gaps are fine: the next number is highest + 1
        await hc.AppendAsync([new QLine { T = "done", Stage = "process" }], true, default);
    }

    [Fact]
    public async Task Seq_assign_and_renumber_never_number_an_unstarted_drama_and_renumber_clears_the_old_numbers_of_unstarted_ones()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        await PutFrames(s, "f1", Make(20, 4, 1), 20); await PutFrames(s, "f2", Make(20, 4, 2), 20);
        await VideoJsonSeed.PutAsync(s, P, "u1", 300); await VideoJsonSeed.PutAsync(s, P, "u2", 300);
        await q.AppendLinesAsync([AddProcess("f1"), AddProcess("u1"), AddProcess("u2"), new QLine { T = "seq", S = "u2", Seq = 5 }], default);   // u2: numbered by 0.13.11's renumber, never started
        var r = await SeqCommand.AssignAsync(q, s, P, false, default); Assert.Equal(2, r.Assigned);                           // f1, f2 only
        Assert.Null(q.Read(st => st.Get("u1")!.Seq)); Assert.Equal(5L, q.Read(st => st.Get("u2")!.Seq!.Value));                  // assign leaves an existing number alone (clear-unstarted removes it)
        var rn = await SeqCommand.AssignAsync(q, s, P, false, default, renumber: true);
        Assert.Equal(2, rn.Assigned); Assert.Null(q.Read(st => st.Get("u1")!.Seq)); Assert.Null(q.Read(st => st.Get("u2")!.Seq));
        Assert.Equal([1L, 2L], new[] { "f1", "f2" }.Select(id => q.Read(st => st.Get(id)!.Seq!.Value)).OrderBy(x => x));
    }

    [Fact]
    public async Task A_blocked_group_entry_produces_no_claim_line_and_becomes_runnable_when_the_lowest_unframed_drama_clears_and_group_entries_go_in_seq_order()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        // seq 1 framed, 2 stuck in processing (no frames), 3 and 4 framed with group entries added in reverse order
        await q.AppendLinesAsync(new[] { "d1", "d2", "d3", "d4" }.Select(AddProcess), default);
        await q.AppendLinesAsync(new[] { "d1", "d2", "d3", "d4" }.Select((id, k) => new QLine { T = "seq", S = id, Seq = k + 1 }), default);
        foreach (var id in new[] { "d1", "d3", "d4" }) await PutFrames(s, id, Make(20, 4, id.GetHashCode()), 20);
        foreach (var id in new[] { "d1", "d3" }) await q.AppendLinesAsync([new QLine { T = "done", S = id, Stage = "process", State = "done", At = "2026-11-21T08:01:00Z" }], default);
        c.T = c.T.AddMinutes(1);
        await q.AppendLinesAsync([QLine.Add("d4", "group", GroupSequencing.NewJob("d4", 0), Protocol.Utc(c.T)), QLine.Add("d3", "group", GroupSequencing.NewJob("d3", 0), Protocol.Utc(c.T)), QLine.Add("d1", "group", GroupSequencing.NewJob("d1", 0), Protocol.Utc(c.T))], default);
        var gate = new GroupSequencing.Gate(s, P); var now = new DateTimeOffset(c.T.UtcDateTime, TimeSpan.Zero);
        var v = await gate.RefreshAsync(q, now, default);
        Assert.Equal(2L, v.L); Assert.Equal("d2", v.LId);
        var cands = q.Read(st => st.Candidates(Protocol.Utc(c.T), x => x == "group", (sub, e) => v.Runnable_(sub))).Select(x => x.Subject.Id).ToList();
        Assert.Equal(["d1"], cands);                                                                                          // d3, d4 wait behind seq 2: not even a candidate, so no claim and no append
        Assert.Equal(2, v.Waiting); Assert.Equal(1, v.Runnable);
        Assert.NotNull(gate.Summary(v, now)); Assert.Null(gate.Summary(v, now.AddMinutes(1))); Assert.Contains("2 waiting behind seq 2 (d2), 1 runnable", gate.Summary(v, now.AddMinutes(6)));
        // d2 gets its frames: L clears (the listing is re-done at most every two minutes)
        await PutFrames(s, "d2", Make(20, 4, 9), 20);
        var same = await gate.RefreshAsync(q, now.AddMinutes(1), default); Assert.Equal(2L, same.L);                          // listed 1 minute ago: not re-listed yet
        var v2 = await gate.RefreshAsync(q, now.AddMinutes(3), default); Assert.Null(v2.L);
        var order = q.Read(st => st.Candidates(Protocol.Utc(c.T), x => x == "group", (sub, e) => v2.Runnable_(sub))).Select(x => x.Subject.Id).ToList();
        Assert.Equal(["d1", "d3", "d4"], order);                                                                              // lowest seq first, whatever the order they were added in
    }

    [Fact]
    public async Task The_gate_runs_no_claim_for_a_blocked_group_entry_in_the_engine_log()
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a");
        await q.AppendLinesAsync([AddProcess("w1"), AddProcess("w2"), new QLine { T = "seq", S = "w1", Seq = 1 }, new QLine { T = "seq", S = "w2", Seq = 2 }], default);
        await PutFrames(s, "w2", Make(20, 4, 3), 20);
        await q.AppendLinesAsync([QLine.Add("w2", "group", GroupSequencing.NewJob("w2", 0), "2026-11-21T08:00:00Z")], default);
        var before = s.AppendText("index/jobs/queue.log").Length;
        var v = await new GroupSequencing.Gate(s, P).RefreshAsync(q, c.T, default);
        Assert.Empty(q.Read(st => st.Candidates("2026-11-21T09:00:00Z", x => x == "group", (sub, e) => v.Runnable_(sub))));
        Assert.Equal(before, s.AppendText("index/jobs/queue.log").Length);                                                    // looking appends nothing
    }

    // ---- 0.13.12: the group step samples first
    static async Task<(FakeStore S, JobQueue Q, GroupStage Stage, List<int> Searched)> Rig(Func<FakeStore, Task> seed, params (string Id, long Seq)[] dramas)
    {
        var c = new Clock(); var s = new FakeStore { Clock = c.Now }; var q = await Node(s, c, "a"); await seed(s);
        await q.AppendLinesAsync(dramas.Select(d => AddProcess(d.Id)), default); await q.AppendLinesAsync(dramas.Select(d => new QLine { T = "seq", S = d.Id, Seq = d.Seq }), default);
        await q.AppendLinesAsync(dramas.Select(d => new QLine { T = "done", S = d.Id, Stage = "process", State = "done", At = "2026-11-21T08:01:00Z" }), default);
        var searched = new List<int>(); return (s, q, new GroupStage(s) { OnSearch = n => { lock (searched) searched.Add(n); } }, searched);
    }
    static Task RunGroup(FakeStore s, JobQueue q, GroupStage g, string id) =>
        g.RunAsync(new JobContext { Job = new JobDescriptor { Stage = "group", JobId = id }, Stage = "group", BlobName = id, Worker = 0, Store = s, Paths = P, NodeId = "t", Token = default, Queue = q, ThreadShare = () => 2, Report = (_, _) => { } });

    [Fact]
    public async Task A_new_drama_searches_only_the_even_sample_and_records_it_a_join_runs_the_full_pass_and_still_retains_and_a_mixed_one_writes_full_stretches()
    {
        var cfg = new PreCheckSettings(); int sample = cfg.GroupCheckFrames;
        var (s, q, g, searched) = await Rig(async st =>
        {
            await PutFrames(st, "n1", Make(1200, 60, 1), 1200);                              // 1,200 frames: more than the sample
            await PutFrames(st, "n2", Make(1200, 60, 2), 1200);                              // unrelated: new
            await PutFrames(st, "j1", Make(1200, 60, 1), 1200, 1.0, 5000);                   // the same video as n1: join
            var half = new float[1200 * D]; Array.Copy(Make(1200, 60, 1), 0, half, 0, 600 * D); Array.Copy(Make(1200, 60, 2), 600 * D, half, 600 * D, 600 * D);
            await PutFrames(st, "m1", half, 1200, 1.0, 9000);                                // half n1, half n2: mixed
        }, ("n1", 1), ("n2", 2), ("j1", 3), ("m1", 4));
        JsonObject Rec(string id) => (JsonObject)VideoJson.ReadAsync(s, P, id, default).Result!.Root["grouping"]!;
        await RunGroup(s, q, g, "n1"); Assert.Empty(searched);                               // nothing is before the first: no search at all
        await RunGroup(s, q, g, "n2");
        Assert.Equal([sample], searched.ToList()); searched.Clear();                         // a new drama: the sample only (240 of 1,200 frames), one pass
        var r2 = Rec("n2"); Assert.Equal("new", (string)r2["proposal"]!["kind"]!);
        Assert.Equal(1200, (int)r2["frames"]!); Assert.Equal(sample, (int)r2["sampled"]!); Assert.Null(r2["covered"]); Assert.NotNull(r2["sampleCovered"]); Assert.Null(r2["retainedFile"]);
        await RunGroup(s, q, g, "j1");
        Assert.Equal(1200, searched.Sum()); Assert.Equal(sample, searched[0]); searched.Clear();   // a join: the sample, then the rest (the sample's results are reused, nothing searched twice)
        var r3 = Rec("j1"); Assert.Equal("join", (string)r3["proposal"]!["kind"]!); Assert.Equal(1200, (int)r3["sampled"]!); Assert.NotNull(r3["covered"]);
        Assert.NotNull(r3["retainedFile"]); Assert.NotNull(await s.HeadAsync(P.RetainedFile("j1"), default));
        await RunGroup(s, q, g, "m1");
        Assert.Equal(1200, searched.Sum()); searched.Clear();                                // mixed: the full pass too
        var r4 = Rec("m1"); Assert.Equal("mixed", (string)r4["proposal"]!["kind"]!); Assert.True(((JsonArray)r4["stretches"]!).Count >= 2, "a mixed drama writes full stretches (one per source)");
        Assert.Null(r4["retainedFile"]);
    }

    [Fact]
    public async Task The_index_fold_accepts_a_group_record_whose_covered_is_null()
    {
        var s = new FakeStore(); await PutFrames(s, "k1", Make(100, 10, 1), 100);
        await VideoJson.UpdateAsync(s, P, "k1", root => root["grouping"] = new JsonObject { ["v"] = 1, ["proposal"] = GroupProposal.None.ToJson(), ["frames"] = 100, ["sampled"] = 50, ["sampleCovered"] = 0, ["covered"] = null, ["coveredShare"] = null, ["stretches"] = new JsonArray() }, default);
        var ups = await SearchIndexBuild.ListCentersAsync(s, P, default); var idx = await SearchIndexBuild.BuildAsync(s, P, ups, null, null, 1, 1, 2, _ => { }, default);
        var set = await GroupBuild.ApplyAsync(s, P, null, idx, default, null, 2, null, requireGroups: true, foldOnly: true);
        Assert.True(set.GroupOf().ContainsKey("k1"));
    }


    [Fact]
    public async Task The_index_job_only_folds_records_a_drama_without_one_stays_ungrouped_and_no_frames_are_read()
    {
        var s = new FakeStore(); var a = Make(200, 30, 7);
        await PutFrames(s, "f1", a, 200); await PutFrames(s, "f2", a, 200); await PutFrames(s, "f3", Make(200, 30, 8), 200);   // f3 has no grouping record yet
        async Task Grouping(string id, JsonObject prop) => await VideoJson.UpdateAsync(s, P, id, root => root["grouping"] = new JsonObject { ["v"] = 1, ["proposal"] = prop }, default);
        await Grouping("f1", GroupProposal.None.ToJson());
        await Grouping("f2", new GroupProposal("join", "g-f1", "f1", 0, 0.99, 0.98, 100, 0.99, false, [new GroupCopy("f1", null, null)]).ToJson());
        var ups = await SearchIndexBuild.ListCentersAsync(s, P, default); var idx = await SearchIndexBuild.BuildAsync(s, P, ups, null, null, 1, 1, 2, _ => { }, default);
        s.Calls.Clear(); var say = new List<string>();
        var set = await GroupBuild.ApplyAsync(s, P, null, idx, default, say.Add, 2, new PreCheckSettings { GroupMaxNewPerBuild = 1 }, requireGroups: true, foldOnly: true);
        var of = set.GroupOf(); Assert.Equal(of["f1"], of["f2"]); Assert.False(of.ContainsKey("f3"));   // f3: ungrouped until its group step has run
        Assert.Null(idx.Uploads.First(u => u.Id == "f3").Group);
        Assert.Contains(say, m => m.Contains("without a group record"));
        Assert.DoesNotContain(s.Calls, x => x.Contains("search/frames/") || x.Contains("search/retained/") || x.Contains("centers.zip"));   // no full check, whatever the per-build cap
        var ctx = new JobContext { Job = new JobDescriptor { Stage = "group", JobId = "f3" }, Stage = "group", BlobName = "f3", Worker = 0, Store = s, Paths = P, NodeId = "t", Token = default, Queue = await QueueWith(s, "f3", 3), ThreadShare = () => 1, Report = (_, _) => { } };
        await new GroupStage(s).RunAsync(ctx);   // its group step runs; the next fold places it
        var set2 = await GroupBuild.ApplyAsync(s, P, null, idx, default, null, 2, null, requireGroups: true, foldOnly: true);
        Assert.True(set2.GroupOf().ContainsKey("f3"));
    }


    [Fact]
    public async Task The_index_fold_places_dramas_from_their_group_records_without_reading_frames_and_elects_one_canonical()
    {
        var s = new FakeStore(); var a = Make(200, 30, 7);
        await PutFrames(s, "f1", a, 200); await PutFrames(s, "f2", a, 200); await PutFrames(s, "f3", Make(200, 30, 8), 200);
        async Task Grouping(string id, JsonObject prop) => await VideoJson.UpdateAsync(s, P, id, root => root["grouping"] = new JsonObject { ["v"] = 1, ["proposal"] = prop }, default);
        await Grouping("f1", GroupProposal.None.ToJson());
        await Grouping("f2", new GroupProposal("join", "g-f1", "f1", 0, 0.99, 0.98, 100, 0.99, false, [new GroupCopy("f1", null, null)]).ToJson());
        await Grouping("f3", GroupProposal.None.ToJson());
        var ups = await SearchIndexBuild.ListCentersAsync(s, P, default); var idx = await SearchIndexBuild.BuildAsync(s, P, ups, null, null, 1, 1, 2, _ => { }, default);
        s.Calls.Clear();
        var set = await GroupBuild.ApplyAsync(s, P, null, idx, default, null, 2, null, bootstrap: true, write: false);
        Assert.Equal(2, set.Groups.Count); var of = set.GroupOf(); Assert.Equal(of["f1"], of["f2"]); Assert.NotEqual(of["f1"], of["f3"]);
        Assert.All(set.Groups, g => Assert.False(string.IsNullOrEmpty(g.Canonical)));
        Assert.DoesNotContain(s.Calls, x => x.Contains("search/frames/") || x.Contains("search/retained/") || x.Contains("centers.zip"));   // folded from video.json: no frames read
    }
}
