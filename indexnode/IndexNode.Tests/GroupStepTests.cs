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
        var d2 = await SeqCommand.EnqueueGroupsAsync(q, true, default); Assert.Equal(2, d2.Added); Assert.Null(q.Read(st => st.Entry("x", "group")));
        var r2 = await SeqCommand.EnqueueGroupsAsync(q, false, default); Assert.Equal(2, r2.Added); Assert.NotNull(q.Read(st => st.Entry("y", "group")));
        var r3 = await SeqCommand.EnqueueGroupsAsync(q, false, default); Assert.Equal(0, r3.Added); Assert.Equal(2, r3.Existing);
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
