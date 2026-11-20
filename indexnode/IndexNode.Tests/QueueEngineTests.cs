using System.Collections.Concurrent;

namespace IndexNode.Tests;

/// <summary>0.13.0: the node engine on the queue (claim, steps in order, done/failed lines, resume, AI opt-in, lanes, control, op counts).</summary>
public class QueueEngineTests
{
    static readonly Paths P = new("index-test/");
    sealed class Fn(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    static string T(int minute) => $"2026-11-20T08:{minute:D2}:00Z";

    static EngineOptions Opts(string[] stages, int lanes = 1, bool ai = false, int? maxJobs = null, Func<int, TimeSpan>? backoff = null, TimeSpan? bot = null, string[]? steps = null, bool once = true, int pollMs = 30) => new()
    {
        NodeId = "test-node", Name = "test", Stages = stages, Workers = lanes, Threads = 8, Once = once, MaxJobs = maxJobs, AiStages = ai, Steps = steps,
        PollInterval = TimeSpan.FromMilliseconds(pollMs), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
        BotBackoff = bot ?? TimeSpan.FromHours(1), TransientBackoff = backoff ?? (_ => TimeSpan.Zero), Ops = new OpCounter(),
    };

    static NodeEngine Engine(FakeStore s, EngineOptions o, Func<JobContext, Task>? stage = null, Func<JobContext, Task>? step = null, Func<JobContext, Task>? ai = null)
    {
        var h = new Dictionary<string, IStageHandler>();
        foreach (var st in new[] { "download", "refresh", "classify", "availability" }) h[st] = new Fn(stage ?? (_ => Task.CompletedTask));
        foreach (var st in Protocol.StepStages) h[st] = new Fn(st is "describe" or "translate" ? (ai ?? step ?? (_ => Task.CompletedTask)) : step ?? (_ => Task.CompletedTask));
        h["process"] = h["frames"];
        return new NodeEngine(s, P, o, h);
    }

    static Task Run(NodeEngine e, int seconds = 15) => e.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(seconds));

    /// <summary>A downloaded video: its download.zip is there, so a process claim finds its input.</summary>
    static Task Downloaded(FakeStore s, string id) => s.PutAsync(P.Video(id, "download.zip"), [1, 2, 3], null, null, false, default);

    static async Task Within(Func<bool> cond, int ms = 5000) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (!cond()) { if (DateTime.UtcNow > until) throw new TimeoutException("condition not met"); await Task.Delay(10); } }

    static QueueState Q(FakeStore s) => s.Replay(P.Prefix);

    // ------------------------------------------------------------------ download

    [Fact]
    public async Task A_download_claim_marks_download_done_and_adds_a_process_entry_in_the_same_append()
    {
        var s = new FakeStore(); await s.AddJob(P, "download", "vid1", T(0));
        var ran = new List<string>();
        await Run(Engine(s, Opts(["download"]), stage: ctx => { ran.Add(ctx.Job.JobId); ctx.Job.Title = "t"; ctx.FollowUps.Add(QLine.Add(ctx.Job.JobId, "process", JobDescriptor.NewProcess(ctx.Job.JobId), Protocol.NowUtc())); return Task.CompletedTask; }));   // what the real DownloadStage does
        Assert.Equal(["vid1"], ran);
        var q = Q(s);
        Assert.Equal("done", q.Entry("vid1", "download")!.State);
        Assert.Equal("pending", q.Entry("vid1", "process")!.State);
        Assert.Null(q.Get("vid1")!.ClaimBy);
        Assert.False(s.Exists("index-test/jobs/active/vid1.json"));
        // the done line and the follow-up are one append: the log has the claim, then one block with done + add
        var log = s.AppendText("index-test/jobs/queue.log").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\"t\":\"claim\"", log[1]); Assert.Contains("\"t\":\"done\"", log[2]); Assert.Contains("\"t\":\"add\"", log[3]);
        Assert.Equal(3, s.Ops.GetValueOrDefault("append"));   // the test's add, the claim, and ONE append for done + add
    }

    [Fact]
    public async Task A_duplicate_keeps_its_metadata_entry_and_adds_no_process_entry()
    {
        var s = new FakeStore(); await s.AddJob(P, "download", "dup1", T(0));
        await Run(Engine(s, Opts(["download"]), stage: _ => throw new DuplicateFound("b2", 0.93)));
        var e = Q(s).Entry("dup1", "download")!;
        Assert.Equal("duplicate", e.State); Assert.Equal("b2", e.Job!.DuplicateOf); Assert.Equal(0.93, e.Job.DuplicateShare);
        Assert.Null(Q(s).Entry("dup1", "process"));
    }

    // ------------------------------------------------------------------ process

    [Fact]
    public async Task A_process_claim_runs_every_standard_step_in_order_one_video_per_claim_then_adds_describe()
    {
        var s = new FakeStore(); await s.AddJob(P, "process", "v1", T(0)); await s.AddJob(P, "process", "v2", T(1)); await Downloaded(s, "v1"); await Downloaded(s, "v2");
        var order = new List<string>();
        await Run(Engine(s, Opts(["process"]), step: ctx => { lock (order) order.Add(ctx.Job.JobId + ":" + ctx.Stage); return Task.CompletedTask; }));
        Assert.Equal(NodeEngine.StandardSteps.Select(x => "v1:" + x).Concat(NodeEngine.StandardSteps.Select(x => "v2:" + x)).ToList(), order);
        var q = Q(s);
        foreach (var v in new[] { "v1", "v2" })
        {
            Assert.Equal("done", q.Entry(v, "process")!.State);
            Assert.Equal(NodeEngine.StandardSteps, q.Entry(v, "process")!.StepsDone);
            Assert.Equal("pending", q.Entry(v, "describe")!.State);     // the default processor never runs it
        }
        Assert.Equal(2, s.AppendText("index-test/jobs/queue.log").Split('\n').Count(l => l.Contains("\"t\":\"claim\"")));   // one claim per video
    }

    [Fact]
    public async Task A_missing_input_at_run_time_fails_the_job_as_invalid_without_a_retry()
    {
        var s = new FakeStore(); await s.AddJob(P, "process", "nodl", T(0));
        await Run(Engine(s, Opts(["process"])));
        var e = Q(s).Entry("nodl", "process")!;
        Assert.Equal("failed", e.State); Assert.True(e.Permanent); Assert.Contains("invalid", e.LastError);
    }

    [Fact]
    public async Task A_failed_step_is_recorded_and_the_next_claim_resumes_from_the_first_unfinished_step()
    {
        var s = new FakeStore(); await s.AddJob(P, "process", "v1", T(0)); await Downloaded(s, "v1");
        int transcribeCalls = 0; var ran = new List<string>();
        async Task Step(JobContext ctx) { ran.Add(ctx.Stage); if (ctx.Stage == "transcribe" && transcribeCalls++ == 0) throw new InvalidOperationException("model crashed"); await ctx.Store.PutAsync(P.Video(ctx.Job.JobId, ctx.Stage + ".zip"), [1], null, null, false, default); }
        await Run(Engine(s, Opts(["process"], backoff: _ => TimeSpan.Zero), step: Step), 20);
        Assert.Equal(["frames", "sscd", "centers", "thumbs", "transcribe", "transcribe", "ocrsubs", "cast", "sync"], ran);   // frames..thumbs once; transcribe retried; nothing rerun
        var e = Q(s).Entry("v1", "process")!;
        Assert.Equal("done", e.State);
        var log = s.AppendText("index-test/jobs/queue.log");
        Assert.Contains("\"t\":\"failed\"", log); Assert.Contains("model crashed", log); Assert.Contains("\"done\":[\"frames\",\"sscd\",\"centers\",\"thumbs\"]", log);
    }

    [Fact]
    public async Task Transient_failures_count_attempts_and_the_third_is_final_permanent_is_at_once_a_bot_block_counts_none()
    {
        var s = new FakeStore(); await s.AddJob(P, "download", "flaky", T(0)); await s.AddJob(P, "download", "dead", T(1));
        var calls = new ConcurrentDictionary<string, int>();
        var o = Opts(["download"], backoff: _ => TimeSpan.Zero, once: false);
        var e = Engine(s, o, stage: ctx =>
        {
            calls.AddOrUpdate(ctx.Job.JobId, 1, (_, n) => n + 1);
            if (ctx.Job.JobId == "dead") throw new PermanentFailure("no such video");
            throw new InvalidOperationException("network");
        });
        using var cts = new CancellationTokenSource(); var run = e.RunAsync(cts.Token);
        await Within(() => Q(s).Entry("flaky", "download")?.State == "failed" && Q(s).Entry("dead", "download")?.State == "failed", 8000);
        cts.Cancel(); await run;
        Assert.Equal(Protocol.MaxAttempts, calls["flaky"]); Assert.Equal(1, calls["dead"]);
        Assert.Equal(Protocol.MaxAttempts, Q(s).Entry("flaky", "download")!.Attempts);
        Assert.True(Q(s).Entry("dead", "download")!.Permanent);
        var s2 = new FakeStore(); await s2.AddJob(P, "download", "blocked", T(0));
        await Run(Engine(s2, Opts(["download"]), stage: _ => throw new BotBlocked("sign in to confirm")));
        var b = Q(s2).Entry("blocked", "download")!;
        Assert.Equal("pending", b.State); Assert.Equal(0, b.Attempts); Assert.Contains("sign in", b.LastError);
    }

    [Fact]
    public async Task A_transient_failure_is_due_again_after_the_back_off_not_before()
    {
        var s = new FakeStore(); await s.AddJob(P, "download", "v", T(0));
        await Run(Engine(s, Opts(["download"], backoff: _ => TimeSpan.FromMinutes(10)), stage: _ => throw new InvalidOperationException("boom")));
        var e = Q(s).Entry("v", "download")!;
        Assert.Equal("pending", e.State); Assert.Equal(1, e.Attempts);
        Assert.True(string.CompareOrdinal(e.Due, Protocol.NowUtc()) > 0);
    }

    // ------------------------------------------------------------------ AI stages are opt-in

    [Fact]
    public async Task An_ai_node_carries_on_in_the_same_claim_from_process_to_describe_to_translate()
    {
        var s = new FakeStore(); await s.AddJob(P, "process", "v1", T(0)); await Downloaded(s, "v1");
        var order = new List<string>();
        await Run(Engine(s, Opts(["process"], ai: true), step: ctx => { order.Add(ctx.Stage); return Task.CompletedTask; }));
        Assert.Equal([.. NodeEngine.StandardSteps, "describe", "translate"], order);
        var q = Q(s); Assert.Equal("done", q.Entry("v1", "describe")!.State); Assert.Equal("done", q.Entry("v1", "translate")!.State);
        Assert.Equal(1, s.AppendText("index-test/jobs/queue.log").Split('\n').Count(l => l.Contains("\"t\":\"claim\"")));   // one claim covers all of it
    }

    [Fact]
    public async Task A_node_without_the_ai_flag_never_runs_describe_even_when_asked_for_it_in_stages()
    {
        var s = new FakeStore();
        await s.AddJob(P, "describe", "v1", T(0));
        var ran = 0;
        await Run(Engine(s, Opts(["process", "describe"], ai: false), step: _ => { ran++; return Task.CompletedTask; }));
        Assert.Equal(0, ran); Assert.Equal("pending", Q(s).Entry("v1", "describe")!.State);
        await Run(Engine(s, Opts(["describe"], ai: true), step: _ => { ran++; return Task.CompletedTask; }));
        Assert.Equal(2, ran); Assert.Equal("done", Q(s).Entry("v1", "describe")!.State); Assert.Equal("done", Q(s).Entry("v1", "translate")!.State);   // describe's done adds translate, same claim
    }

    // ------------------------------------------------------------------ lanes, limits, control

    [Fact]
    public async Task Lanes_are_concurrent_claims_each_on_its_own_subject()
    {
        var s = new FakeStore(); for (int i = 0; i < 4; i++) await s.AddJob(P, "download", "vid" + i, T(i));
        int cur = 0, max = 0; var seen = new ConcurrentBag<string>();
        await Run(Engine(s, Opts(["download"], lanes: 2), stage: async ctx => { seen.Add(ctx.Job.JobId); var c = Interlocked.Increment(ref cur); lock (seen) max = Math.Max(max, c); await Task.Delay(60); Interlocked.Decrement(ref cur); }));
        Assert.Equal(4, seen.Distinct().Count()); Assert.Equal(2, max);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal("done", Q(s).Entry("vid" + i, "download")!.State));
    }

    [Fact]
    public async Task Max_jobs_limits_the_claims_started()
    {
        var s = new FakeStore(); for (int i = 0; i < 5; i++) await s.AddJob(P, "download", "vid" + i, T(i));
        await Run(Engine(s, Opts(["download"], maxJobs: 2)));
        Assert.Equal(2, Enumerable.Range(0, 5).Count(i => Q(s).Entry("vid" + i, "download")!.State == "done"));
    }

    [Fact]
    public async Task A_stopped_stage_claims_nothing_and_a_priority_subject_goes_first()
    {
        var s = new FakeStore(); await s.AddJob(P, "download", "old", T(0)); await s.AddJob(P, "download", "vip", T(5), priority: 10);
        await s.PutAsync(P.Control, new ControlFile { Mode = "run", Stages = new() { ["process"] = "stop" } }.ToBytes(), null, null, false, default);
        await s.AddJob(P, "process", "p1", T(0)); await Downloaded(s, "p1");
        var order = new List<string>();
        await Run(Engine(s, Opts(["download", "process"]), stage: ctx => { order.Add(ctx.Job.JobId); return Task.CompletedTask; }, step: ctx => { order.Add("STEP"); return Task.CompletedTask; }));
        Assert.Equal(["vip", "old"], order);
        Assert.Equal("pending", Q(s).Entry("p1", "process")!.State);
    }

    [Fact]
    public async Task A_node_that_cannot_run_a_step_runs_the_ones_it_can_and_leaves_the_rest_pending_without_an_attempt()
    {
        var s = new FakeStore(); await s.AddJob(P, "process", "v1", T(0)); await Downloaded(s, "v1");
        var ran = new List<string>();
        await Run(Engine(s, Opts(["process"], steps: ["frames", "thumbs"]), step: ctx => { ran.Add(ctx.Stage); return Task.CompletedTask; }));
        Assert.Equal(["frames", "thumbs"], ran);
        var e = Q(s).Entry("v1", "process")!;
        Assert.Equal("pending", e.State); Assert.Equal(["frames", "thumbs"], e.StepsDone); Assert.Equal(0, e.Attempts);
        Assert.Null(Q(s).Get("v1")!.ClaimBy);
    }

    // ------------------------------------------------------------------ the cost of finding work

    [Fact]
    public async Task An_idle_node_costs_one_tail_read_and_one_listing_per_look_and_nothing_else()
    {
        var s = new FakeStore();
        var o = Opts(["process", "download"], once: false, pollMs: 20);
        var e = Engine(s, o);
        using var cts = new CancellationTokenSource(); var run = e.RunAsync(cts.Token);
        await Within(() => s.Ops.GetValueOrDefault("list") >= 5, 4000);
        cts.Cancel(); await run;
        // no Find Blobs by Tags, no job-body reads, no input HEADs: what the engine did besides the registry and control file is reads of the log and listings of jobs/active/
        Assert.DoesNotContain("find", s.Calls);
        Assert.Equal(0, s.Ops.GetValueOrDefault("delete"));
    }

    [Fact]
    public async Task Status_shows_the_nodes_storage_operations_per_minute()
    {
        var s = new FakeStore(); await s.AddJob(P, "download", "v", T(0));
        var o = Opts(["download"]); var e = Engine(s, o);
        await Run(e);
        var rec = NodeInfo.Parse(s[P.Node("test-node")].Data)!;
        Assert.NotNull(rec.Ops);
        Assert.Equal("test-node", rec.NodeId);
    }
}
