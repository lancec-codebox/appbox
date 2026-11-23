using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.36: the ready view and the claim agree; a subject that looks ready but cannot run is not attempted again without a change signal.</summary>
public class ClaimAgreementTests
{
    static readonly Paths P = new("index/");
    const string At = "2026-01-01T00:00:00Z";
    sealed class Rec(List<string> ran) : IStageHandler { public Task RunAsync(JobContext ctx) { lock (ran) ran.Add(ctx.BlobName + ":" + ctx.Stage); return Task.CompletedTask; } }

    /// <summary>A finished upload at cast v1. data = "full" or null (a migrated one: its download is done, no data fact).</summary>
    static async Task Seed(FakeStore s, string id, long seq, string? data)
    {
        var q = new JobQueue(s, P, "seed"); await q.LoadAsync(default);
        var l = new List<QLine>();
        if (data != null) l.Add(new QLine { T = "enter", S = id, Data = data, At = At });
        else l.Add(QLine.Add(id, "download", new JobDescriptor { Stage = "download", JobId = id }, At, null, null, "done"));
        l.Add(QLine.Add(id, "process", new JobDescriptor { Stage = "process", JobId = id }, At, null, null, "done"));
        l.Add(new QLine { T = "fixsteps", S = id, Stage = "process", Done = NodeEngine.StandardSteps.ToArray(), At = At });
        l.Add(QLine.Add(id, "group", new JobDescriptor { Stage = "group", JobId = id }, At, null, null, "done")); l.Add(new QLine { T = "seq", S = id, Seq = seq, At = At });
        await q.AppendLinesAsync(l, default);
        foreach (var z in new[] { "download", "cast", "centers", "frames", "sscd" }) await s.PutAsync(P.Video(id, z + ".zip"), [1], null, null, true, default);
    }
    static EngineOptions Opts(bool once = true, string[]? steps = null) => new()
    {
        NodeId = "n-1", Name = "n", Stages = ["process"], Jobs = new() { ["process"] = 1 }, Threads = 1, Once = once, ControlOverride = c => WhatIf.Apply(c, ["versions.cast=2"]),
        Ready = new ReadyOptions(3, 10) { Code = _ => 2 }, PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(40), ControlInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromSeconds(30), Ops = new OpCounter(), Steps = steps,
    };

    [Fact]
    public async Task A_subject_without_a_data_fact_is_repaired_by_the_claim_the_ready_view_offers()
    {
        var s = new FakeStore(); await Seed(s, "m1", 1, null); await Seed(s, "m2", 2, "full");
        var ran = new List<string>();
        await new NodeEngine(s, P, Opts(), Protocol.StepStages.Append("process").ToDictionary(x => x, x => (IStageHandler)new Rec(ran))).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(["m1", "m2"], ran.Select(x => x.Split(':')[0]).Distinct().Order().ToList());
    }

    /// <summary>A pending process entry whose only runnable step (cast) has no input on the store: the claim runs nothing and releases it.</summary>
    static async Task SeedStuck(FakeStore s, string id)
    {
        var q = new JobQueue(s, P, "seed"); await q.LoadAsync(default);
        await q.AppendLinesAsync([new QLine { T = "enter", S = id, Data = "full", At = At }, QLine.Add(id, "process", new JobDescriptor { Stage = "process", JobId = id }, At), new QLine { T = "fixsteps", S = id, Stage = "process", Done = NodeEngine.StandardSteps.Where(x => x != "cast").ToArray(), At = At }], default);
        await s.PutAsync(P.Video(id, "download.zip"), [1], null, null, true, default); await VideoJsonSeed.PutAsync(s, P, id, 600);
    }
    static int Attempts(FakeStore s, string id) => s.PutCount(P.Active(id));
    static NodeEngine StuckEngine(FakeStore s, List<string> ran) => new(s, P, Opts(once: false, steps: ["cast"]), Protocol.StepStages.Append("process").ToDictionary(x => x, x => (IStageHandler)new Rec(ran)));

    [Fact]
    public async Task Lane_iterations_over_subjects_that_cannot_run_make_one_attempt_each_and_the_ops_stay_bounded()
    {
        var s = new FakeStore(); var ids = Enumerable.Range(0, 8).Select(i => "stuck" + i).ToArray();
        foreach (var id in ids) await SeedStuck(s, id);
        var ran = new List<string>(); using var cts = new CancellationTokenSource();
        var task = StuckEngine(s, ran).RunAsync(cts.Token);
        await Task.Delay(1500);   // dozens of lane iterations (poll 20 ms)
        int ClaimOps() => new[] { "put", "lease", "delete", "append" }.Sum(k => s.Ops.GetValueOrDefault(k)); int settled = ClaimOps(); await Task.Delay(1000); int later = ClaimOps();
        cts.Cancel(); try { await task.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { }
        Assert.All(ids, id => Assert.True(Attempts(s, id) <= 1, id + " was attempted " + Attempts(s, id) + " times"));
        Assert.Equal(settled, later);   // no claim traffic (put / lease / delete / append) once every subject was tried: only the idle polls go on
    }

    [Fact]
    public async Task A_released_subject_is_not_claimed_again_without_a_change_and_is_after_a_line_for_it()
    {
        var s = new FakeStore(); await SeedStuck(s, "one");
        var ran = new List<string>(); using var cts = new CancellationTokenSource();
        var task = StuckEngine(s, ran).RunAsync(cts.Token);
        await Task.Delay(1200); Assert.Equal(1, Attempts(s, "one"));
        await Task.Delay(800); Assert.Equal(1, Attempts(s, "one"));   // nothing changed: no second attempt, however long it waits
        var q = new JobQueue(s, P, "poke"); await q.LoadAsync(default);
        await q.AppendLinesAsync([new QLine { T = "pri", S = "one", Stage = "process", Pri = 5, At = At }], default);   // a queue line for it: the change signal
        await Task.Delay(1200); Assert.Equal(2, Attempts(s, "one"));
        cts.Cancel(); try { await task.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Whatif_counts_exactly_the_subjects_a_claim_would_run()
    {
        var s = new FakeStore(); await Seed(s, "good1", 1, "full"); await Seed(s, "good2", 2, null);
        var q = new JobQueue(s, P, "w"); await q.LoadAsync(default);
        // process and group done, but a check is still pending: the claim never runs it
        await q.AppendLinesAsync([new QLine { T = "enter", S = "blocked", Data = "full", At = At }, QLine.Add("blocked", "check", new JobDescriptor { Stage = "check", JobId = "blocked" }, At), QLine.Add("blocked", "process", new JobDescriptor { Stage = "process", JobId = "blocked" }, At, null, null, "done"), QLine.Add("blocked", "group", new JobDescriptor { Stage = "group", JobId = "blocked" }, At, null, null, "done"), new QLine { T = "seq", S = "blocked", Seq = 3, At = At }], default);
        var ctl = WhatIf.Apply(new ControlFile(), ["versions.cast=2"]); var ro = new ReadyOptions(3, 10) { Required = ctl.RequiredVersion, Code = _ => 2 };
        var now = "2026-02-01T00:00:00Z";
        var rep = q.Read(st => WhatIf.Run(st, "cast", ctl, now, ro));
        var claimable = q.Read(st => st.Subjects.Values.Where(x => StageDeps.ClaimStages(st, x, now, _ => true, (sub, e) => !StageDeps.Held(e) && !(e.Stage == "process" && sub.Entries.TryGetValue("check", out var ck) && !ck.Final), ro) is { } cs && cs.Contains("process")).Select(x => x.Id).Order().ToList());
        Assert.Equal(["good1", "good2"], claimable);
        Assert.Equal(2, rep.Ready); Assert.DoesNotContain("blocked", rep.First);
    }

    [Fact]
    public async Task The_snapshot_keeps_the_announced_versions_so_a_node_that_loads_it_needs_no_head_json()
    {
        var st = new QueueState();
        st.Apply(new QLine { T = "done", S = ActorsIndexJobs.Subject, Stage = "actors-index", Ver = 45, At = At });
        st.Apply(new QLine { T = "ctl", Ets = "\"abc\"", At = At });
        var (back, _) = QueueState.FromSnapshot(st.ToSnapshot(1, 0, 0, At));
        Assert.Equal(45, back.ActorsIndexVer); Assert.Equal("\"abc\"", back.ControlETag);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task One_actors_index_cache_serves_every_claim_wrapper_of_the_same_store()
    {
        var s = new FakeStore(); var paths = P;
        var a = new ActorsIO(new LocalCacheStore(s, Path.Combine(Path.GetTempPath(), "lc-" + Guid.NewGuid().ToString("N")[..8])), paths);
        var b = new ActorsIO(new LocalCacheStore(s, Path.Combine(Path.GetTempPath(), "lc-" + Guid.NewGuid().ToString("N")[..8])), paths);
        ActorsIndex.ForgetCache();
        await ActorsIndex.ForAnnouncedAsync(a, 0, default);   // cold: one head.json read (none exists: null)
        int gets = s.Ops.GetValueOrDefault("get");
        await ActorsIndex.ForAnnouncedAsync(b, 0, default);
        Assert.Equal(gets, s.Ops.GetValueOrDefault("get"));   // the second claim's wrapper reads nothing
    }
}
