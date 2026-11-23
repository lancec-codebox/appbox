using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.32: versions of steps. Done lines carry them (old lines count as v1), raising the required version makes exactly the lower-version steps ready, actors waits for cast v2, and `queue whatif` uses the same rule read-only.</summary>
public class StepVersionTests
{
    static readonly Paths P = new("index/");
    sealed class Clock { public DateTimeOffset T = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero); public DateTimeOffset Now() => T; }
    static ReadyOptions Ro(int castReq = 0, int castCode = 2) => new(3, 10) { Required = s => s == "cast" ? castReq : 0, Code = s => s == "cast" ? castCode : 1 };
    static bool Ok(QSubject s, QEntry e) => !StageDeps.Held(e);

    /// <summary>An upload whose process (every step) and group are done; the cast step finished at <paramref name="castVer"/> (null = a line without a version: v1).</summary>
    static void Upload(QueueState st, string id, int seq, int? castVer, bool ai = false)
    {
        string at = "2026-01-01T00:00:00Z";
        st.Apply(new QLine { T = "enter", S = id, Data = "full", At = at }); st.Apply(new QLine { T = "add", S = id, Stage = "process", Job = new JobDescriptor { Stage = "process", JobId = id }, At = at });
        foreach (var step in NodeEngine.StandardSteps) st.Apply(new QLine { T = "done", S = id, Stage = "process", Step = step, StepVer = step == "cast" ? castVer : null, At = at });
        st.Apply(new QLine { T = "done", S = id, Stage = "process", At = at }); st.Apply(new QLine { T = "seq", S = id, Seq = seq, At = at });
        st.Apply(new QLine { T = "add", S = id, Stage = "group", Job = new JobDescriptor { Stage = "group", JobId = id }, At = at }); st.Apply(new QLine { T = "done", S = id, Stage = "group", JoinKind = "new", At = at });
        if (ai) st.Apply(new QLine { T = "add", S = id, Stage = "actors", Job = new JobDescriptor { Stage = "actors", JobId = id }, At = at, Hold = "ai" });
    }
    static QueueState Build()
    {
        var st = new QueueState();
        Upload(st, "old1", 1, null); Upload(st, "old2", 2, 1); Upload(st, "new1", 3, 2); Upload(st, "new2", 4, 2); Upload(st, "ai1", 5, null, ai: true);
        return st;
    }
    static List<string> ReadyCast(QueueState st, ReadyOptions ro) => st.Candidates(Protocol.Utc(new Clock().T), x => true, Ok, null, ro).Where(c => c.Runnable.Any(e => StageDeps.IsVersionEntry(e))).Select(c => c.Subject.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Fact]
    public void Done_lines_carry_versions_and_a_line_without_one_counts_as_v1()
    {
        var st = Build(); Assert.Equal(1, st.Get("old1")!.VerOf("cast")); Assert.Equal(1, st.Get("old2")!.VerOf("cast")); Assert.Equal(2, st.Get("new1")!.VerOf("cast")); Assert.Equal(1, st.Get("new1")!.VerOf("frames"));
        var line = new QLine { T = "done", S = "x", Stage = "process", Step = "cast", StepVer = 2 }; Assert.Contains("\"sv\":2", line.ToJson()); Assert.Null(QLine.Parse("{\"t\":\"done\",\"s\":\"x\",\"st\":\"process\",\"step\":\"cast\"}")!.StepVer);   // an old line has none
        st.Apply(new QLine { T = "done", S = "actors-x", Stage = "actors", StepVer = 2 });   // (no subject: ignored)
        var snap = QueueState.FromSnapshot(st.ToSnapshot(1, 0, 1, "2026-01-01T00:00:00Z")).State; Assert.Equal(2, snap.Get("new1")!.VerOf("cast")); Assert.Equal(1, snap.Get("old2")!.VerOf("cast"));   // the snapshot keeps them
    }

    [Fact]
    public void Raising_the_required_version_makes_exactly_the_lower_version_steps_ready_and_none_before()
    {
        var st = Build();
        Assert.Empty(ReadyCast(st, Ro(castReq: 0)));            // nothing required: nothing re-runs, however old
        Assert.Empty(ReadyCast(st, Ro(castReq: 1)));            // v1 required: every upload is at it
        Assert.Equal(["old1", "old2"], ReadyCast(st, Ro(castReq: 2)));   // v2 required: exactly the uploads below it (the AI-held one is not)
        Assert.Empty(ReadyCast(st, Ro(castReq: 2, castCode: 1)));        // a node whose code cannot make v2 claims nothing
        // the claim re-opens the step, keeps the others done, and the done line records the new version
        var q = st; var line = new QLine { T = "claim", S = "old1", Stages = ["process"], By = "n", Epoch = 1, At = "2026-01-02T00:00:00Z", Redo = ["cast"] }; q.Apply(line);
        var pe = q.Entry("old1", "process")!; Assert.Equal("pending", pe.State); Assert.DoesNotContain("cast", pe.StepsDone!); Assert.Contains("frames", pe.StepsDone!);
        q.Apply(new QLine { T = "done", S = "old1", Stage = "process", Step = "cast", StepVer = 2, At = "2026-01-02T00:00:01Z" }); q.Apply(new QLine { T = "done", S = "old1", Stage = "process", Fin = true, At = "2026-01-02T00:00:02Z" });
        Assert.Equal(["old2"], ReadyCast(q, Ro(castReq: 2)));
    }

    [Fact]
    public void Actors_stays_unready_until_its_upload_cast_is_at_v2()
    {
        var st = Build(); string At = Protocol.Utc(new Clock().T);
        bool Ready(string id) => StageDeps.ReadyEntry(st, st.Get(id)!, "actors", At, Ro(castReq: 2)) != null;
        Assert.False(Ready("old1")); Assert.False(Ready("old2")); Assert.True(Ready("new1")); Assert.True(Ready("new2")); Assert.False(Ready("ai1"));   // group done, but cast below v2 (or AI-held)
        st.Apply(new QLine { T = "claim", S = "old2", Stages = ["process"], By = "n", Epoch = 1, At = "2026-01-02T00:00:00Z", Redo = ["cast"] });
        st.Apply(new QLine { T = "done", S = "old2", Stage = "process", Step = "cast", StepVer = 2, At = "2026-01-02T00:00:01Z" }); st.Apply(new QLine { T = "done", S = "old2", Stage = "process", Fin = true, At = "2026-01-02T00:00:02Z" });
        Assert.True(Ready("old2")); Assert.False(Ready("old1"));   // old2's cast reached v2: actors follows it, per upload
    }

    [Fact]
    public void Whatif_lists_every_done_upload_cast_step_as_ready_with_the_override_and_none_without_and_the_blockers_add_up()
    {
        var st = Build(); string now = Protocol.Utc(new Clock().T);
        var none = WhatIf.Run(st, "cast", new ControlFile(), now, null, 10); Assert.Equal(0, none.Ready); Assert.Equal(none.Total, none.Ready + none.Blockers.Values.Sum());
        var ctl = WhatIf.Apply(new ControlFile(), ["versions.cast=2"]); Assert.Null(new ControlFile().Versions);   // in memory only
        var rep = WhatIf.Run(st, "cast", ctl, now, null, 10);
        Assert.Equal(["old1", "old2"], rep.First); Assert.Equal(2, rep.Ready); Assert.Equal(5, rep.Total); Assert.Equal(rep.Total, rep.Ready + rep.Blockers.Values.Sum());
        Assert.Contains(rep.Blockers, kv => kv.Key.StartsWith("already done at the required version") && kv.Value == 2); Assert.Contains(rep.Blockers, kv => kv.Key.StartsWith("hold: AI") && kv.Value == 1);
        var act = WhatIf.Run(st, "actors", ctl, now, null, 10); Assert.Equal(["new1", "new2"], act.First); Assert.Equal(act.Total, act.Ready + act.Blockers.Values.Sum());
        Assert.Contains(act.Blockers, kv => kv.Key.Contains("version below the minimum: cast is at v1, needs v2") && kv.Value == 2);
        var off = WhatIf.Run(st, "actors", WhatIf.Apply(ctl, ["stage.actors=stop"]), now, null, 10); Assert.Equal(0, off.Ready); Assert.Contains(off.Blockers, kv => kv.Key.StartsWith("the stage is off in control") && kv.Value == 5);
        var idx = WhatIf.Run(st, "actors-index", ctl, now, null, 5); Assert.Equal(1, idx.Total); Assert.Equal(idx.Total, idx.Ready + idx.Blockers.Values.Sum());
    }

    [Fact]
    public async Task The_control_version_command_writes_versions_and_the_engine_reads_them()
    {
        var fs = new FakeStore(); var c = await ControlWatcher.SetVersionAsync(fs, P, "cast", "2", "t", default); Assert.Equal(2, c.RequiredVersion("cast"));
        var back = ControlFile.Parse((await fs.GetAsync(P.Control, default))!.Data); Assert.Equal(2, back.RequiredVersion("cast")); Assert.Equal(0, back.RequiredVersion("actors"));
        c = await ControlWatcher.SetVersionAsync(fs, P, "cast", "off", "t", default); Assert.Equal(0, c.RequiredVersion("cast"));
        await Assert.ThrowsAsync<ArgumentException>(() => ControlWatcher.SetVersionAsync(fs, P, "frames", "2", "t", default));
    }

    /// <summary>A subject the queue split migrated: no "enter" line, so no data fact; its download entry is done.</summary>
    static void Migrated(QueueState st, string id, int seq)
    {
        string at = "2026-01-01T00:00:00Z";
        st.Apply(new QLine { T = "add", S = id, Stage = "download", Job = new JobDescriptor { Stage = "download", JobId = id }, At = at, State = "done" });
        st.Apply(new QLine { T = "add", S = id, Stage = "process", Job = new JobDescriptor { Stage = "process", JobId = id }, At = at });
        foreach (var step in NodeEngine.StandardSteps) st.Apply(new QLine { T = "done", S = id, Stage = "process", Step = step, At = at });
        st.Apply(new QLine { T = "done", S = id, Stage = "process", At = at }); st.Apply(new QLine { T = "seq", S = id, Seq = seq, At = at });
        st.Apply(new QLine { T = "add", S = id, Stage = "group", Job = new JobDescriptor { Stage = "group", JobId = id }, At = at }); st.Apply(new QLine { T = "done", S = id, Stage = "group", JoinKind = "new", At = at });
    }

    [Fact]
    public void A_migrated_subject_with_download_done_and_no_data_fact_is_cast_repair_ready()
    {
        var st = new QueueState(); Migrated(st, "mig1", 1);
        Assert.Null(st.Get("mig1")!.Data); Assert.True(st.Get("mig1")!.IsFull);
        Assert.Equal(["mig1"], ReadyCast(st, Ro(castReq: 2)));
        Assert.Empty(ReadyCast(st, Ro(castReq: 1)));
        var rep = WhatIf.Run(st, "cast", WhatIf.Apply(new ControlFile(), ["versions.cast=2"]), Protocol.Utc(new Clock().T)); Assert.Equal(1, rep.Ready);
        var pre = new QueueState(); pre.Apply(new QLine { T = "enter", S = "p1", Data = "precheck", State = "duplicate", At = "2026-01-01T00:00:00Z" }); Assert.False(pre.Get("p1")!.IsFull);   // a precheck entry never is
    }

    [Fact]
    public void A_lapsed_lease_does_not_block_whatif_but_a_live_one_does()
    {
        var st = new QueueState(); Upload(st, "a", 1, 1); st.Apply(new QLine { T = "claim", S = "a", By = "gone-node", Stages = ["actors"], At = "2026-01-01T00:00:00Z" });
        var ctl = WhatIf.Apply(new ControlFile(), ["versions.cast=2"]); var now = Protocol.Utc(new Clock().T);
        Assert.Equal(0, WhatIf.Run(st, "cast", ctl, now).Ready);                                   // no lease information: the claim stands
        Assert.Equal(0, WhatIf.Run(st, "cast", ctl, now, null, 20, id => true).Ready);            // live lease
        Assert.Equal(1, WhatIf.Run(st, "cast", ctl, now, null, 20, id => false).Ready);           // lapsed lease: free
    }

    [Fact]
    public void An_actors_done_at_v1_is_ready_again_at_versions_actors_2_once_its_cast_is_v2()
    {
        var st = new QueueState(); Upload(st, "u1", 1, 2); Upload(st, "u2", 2, 1);
        foreach (var id in new[] { "u1", "u2" }) { st.Apply(new QLine { T = "add", S = id, Stage = "actors", Job = new JobDescriptor { Stage = "actors", JobId = id }, At = "2026-01-01T00:00:00Z", State = "done" }); st.Apply(new QLine { T = "done", S = id, Stage = "actors", At = "2026-01-01T00:00:00Z" }); }
        var now = Protocol.Utc(new Clock().T);
        var none = WhatIf.Run(st, "actors", WhatIf.Apply(new ControlFile(), []), now); Assert.Equal(0, none.Ready);
        var rep = WhatIf.Run(st, "actors", WhatIf.Apply(new ControlFile(), ["versions.cast=2", "versions.actors=2"]), now);
        Assert.Equal(["u1"], rep.First); Assert.Contains(rep.Blockers.Keys, k => k.Contains("waits for cast"));   // u2's cast is still v1: it waits for its repair
        st.Apply(new QLine { T = "done", S = "u1", Stage = "actors", StepVer = 2, At = "2026-01-02T00:00:00Z" });
        Assert.Equal(0, WhatIf.Run(st, "actors", WhatIf.Apply(new ControlFile(), ["versions.actors=2"]), now).First.Count);   // finished at v2: done
    }

    [Fact]
    public async Task Import_done_process_makes_migrated_processed_subjects_cast_repair_ready()
    {
        var store = new FakeStore(); var paths = new Paths("index/"); var q = new JobQueue(store, paths, "cli"); await q.LoadAsync(default);
        string at = "2026-01-01T00:00:00Z"; var ids = new[] { "m1", "m2", "m3" };
        foreach (var id in ids) { await q.AppendLinesAsync([QLine.Add(id, "download", new JobDescriptor { Stage = "download", JobId = id }, at, null, null, "done")], default); }
        foreach (var z in new[] { "cast", "centers", "frames", "sscd" }) { await store.PutAsync(paths.Video("m1", z + ".zip"), [1], null, null, true, default); if (z != "frames") await store.PutAsync(paths.Video("m2", z + ".zip"), [1], null, null, true, default); }
        var rep = await QueueCommand.ImportDoneProcessAsync(q, store, paths, true, default); Assert.Equal(1, rep.Imported); Assert.Equal(2, rep.Partial); Assert.Equal(3, rep.NoProcess);
        Assert.Null(q.Read(s => s.Entry("m1", "process")));   // dry run writes nothing
        rep = await QueueCommand.ImportDoneProcessAsync(q, store, paths, false, default); Assert.Equal(1, rep.Imported);
        var again = await QueueCommand.ImportDoneProcessAsync(q, store, paths, false, default); Assert.Equal(0, again.Imported);   // idempotent
        var ready = q.Read(s => s.Candidates(at, x => true, null, null, new ReadyOptions(3, 10) { Required = x => x == "cast" ? 2 : 0, Code = _ => 2 })).Where(c => c.Runnable.Any(StageDeps.IsVersionEntry)).Select(c => c.Subject.Id).ToList();
        Assert.Empty(ready);   // no group record yet: the repair waits for it
        await q.AppendLinesAsync([QLine.Add("m1", "group", new JobDescriptor { Stage = "group", JobId = "m1" }, at, null, null, "done")], default);
        ready = q.Read(s => s.Candidates("2026-02-01T00:00:00Z", x => true, null, null, new ReadyOptions(3, 10) { Required = x => x == "cast" ? 2 : 0, Code = _ => 2 })).Where(c => c.Runnable.Any(StageDeps.IsVersionEntry)).Select(c => c.Subject.Id).ToList();
        Assert.Equal(["m1"], ready); Assert.Equal(1, q.Read(s => s.Get("m1")!.VerOf("cast")));
    }

    sealed class Rec(List<string> ran) : IStageHandler { public Task RunAsync(JobContext ctx) { lock (ran) ran.Add(ctx.BlobName + ":" + ctx.Stage); return Task.CompletedTask; } }

    [Fact]
    public async Task A_shadow_run_with_set_versions_cast_2_and_import_process_runs_the_cast_repair_of_migrated_subjects()
    {
        var real = new FakeStore(); var paths = new Paths("index/"); var shadow = new ShadowStore(real, Path.Combine(Path.GetTempPath(), "shadow-" + Guid.NewGuid().ToString("N")[..8]));
        var q0 = new JobQueue(real, paths, "setup"); await q0.LoadAsync(default); string at = "2026-01-01T00:00:00Z";
        foreach (var id in new[] { "m1", "m2" })
        {
            await q0.AppendLinesAsync([QLine.Add(id, "download", new JobDescriptor { Stage = "download", JobId = id }, at, null, null, "done"), QLine.Add(id, "group", new JobDescriptor { Stage = "group", JobId = id }, at, null, null, "done"), new QLine { T = "seq", S = id, Seq = id == "m1" ? 1 : 2, At = at }], default);
            foreach (var z in new[] { "download", "cast", "centers", "frames", "sscd" }) await real.PutAsync(paths.Video(id, z + ".zip"), [1], null, null, true, default);
        }
        var iq = new JobQueue(shadow, paths, "shadow-import"); await iq.LoadAsync(default);
        Assert.Equal(2, (await QueueCommand.ImportDoneProcessAsync(iq, shadow, paths, false, default)).Imported);   // the lines go to the overlay only
        var ran = new List<string>(); var ctl = new[] { "versions.cast=2" };
        var o = new EngineOptions
        {
            NodeId = "n-1", Name = "n", Stages = ["process"], Jobs = new() { ["process"] = 1 }, Threads = 1, Once = true, Shadow = true, ControlOverride = c => WhatIf.Apply(c, ctl),
            Ready = new ReadyOptions(3, 10) { Code = _ => 2 }, PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(40), ControlInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromSeconds(30),
        };
        await new NodeEngine(shadow, paths, o, Protocol.StepStages.Append("process").ToDictionary(x => x, x => (IStageHandler)new Rec(ran))).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(["m1", "m2"], ran.Select(x => x.Split(':')[0]).Distinct().Order().ToList()); Assert.Contains("m1:cast", ran);   // the cast repair of each migrated subject ran
        var rq = new JobQueue(real, paths, "check"); await rq.LoadAsync(default); Assert.Null(rq.Read(s => s.Entry("m1", "process")));   // the real queue was not written
    }
}
