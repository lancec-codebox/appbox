namespace IndexNode.Tests;

public class EngineTests
{
    private sealed class FuncHandler(Func<JobContext, Task> f) : IStageHandler
    {
        public Task RunAsync(JobContext ctx) => f(ctx);
    }

    private static readonly Paths P = new("index-test/");
    private static string T(int minute) => $"2026-11-13T19:{minute:D2}:00Z";

    private static EngineOptions Opts(string[]? stages = null, int download = 1, int process = 1, bool once = true, int threads = 8, int? maxJobs = null,
        Func<int, TimeSpan>? backoff = null, TimeSpan? bot = null) => new()
    {
        NodeId = "test-abc123", Name = "test", Stages = stages ?? ["download", "process"],
        Jobs = new() { ["download"] = download, ["process"] = process }, Threads = threads, Once = once, MaxJobs = maxJobs,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), ControlInterval = TimeSpan.FromMilliseconds(40),
        RegistryInterval = TimeSpan.FromMilliseconds(100), BotBackoff = bot ?? TimeSpan.FromHours(1),
        TransientBackoff = backoff ?? (_ => TimeSpan.Zero),
    };

    private static NodeEngine Engine(FakeStore s, EngineOptions o, Func<JobContext, Task>? download = null, Func<JobContext, Task>? process = null) =>
        new(s, P, o, new Dictionary<string, IStageHandler>
        {
            ["download"] = new FuncHandler(download ?? (_ => Task.CompletedTask)),
            ["process"] = new FuncHandler(process ?? (_ => Task.CompletedTask)),
        });

    private static async Task Within(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > until) throw new TimeoutException("condition not met"); await Task.Delay(10); }
    }

    private static Task Run(NodeEngine e, int seconds = 10) => e.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(seconds));

    // ------------------------------------------------------------ state machine

    [Fact]
    public async Task Claim_finish_marks_done_with_metadata_and_releases_the_lease()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "vid1", T(0));
        string? lease = null;
        await Run(Engine(s, Opts(["download"]), download: ctx => { lease = ctx.LeaseId; Assert.Equal(lease, s.LeaseOf(ctx.BlobName)); return Task.CompletedTask; }));
        var name = P.Job("download", "vid1");
        Assert.Equal("done", s.State(name));
        Assert.Equal("download", s[name].Tags[Tags.Stage]);
        Assert.Equal(T(0), s[name].Tags[Tags.Added]);
        var j = s.Job(name);
        Assert.NotNull(j.FinishedAt); Assert.Equal("test-abc123", j.NodeId); Assert.NotNull(j.ElapsedSeconds);
        Assert.Null(s.LeaseOf(name));
    }

    [Fact]
    public async Task Oldest_pending_job_goes_first()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "newer", T(5));
        await s.AddJob(P, "download", "older", T(1));
        var order = new List<string>();
        await Run(Engine(s, Opts(["download"]), download: ctx => { order.Add(ctx.Job.JobId); return Task.CompletedTask; }));
        Assert.Equal(["older", "newer"], order);
    }

    [Fact]
    public async Task Lease_409_moves_on_to_the_next_job()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "taken", T(1));
        await s.AddJob(P, "download", "free", T(2));
        var other = await s.AcquireLeaseAsync(P.Job("download", "taken"), 60, default);
        var ran = new List<string>();
        await Run(Engine(s, Opts(["download"]), download: ctx => { ran.Add(ctx.Job.JobId); return Task.CompletedTask; }));
        Assert.Equal(["free"], ran);
        Assert.Equal("pending", s.State(P.Job("download", "taken")));
        Assert.Equal(other, s.LeaseOf(P.Job("download", "taken")));
    }

    [Fact]
    public async Task Pending_tag_is_rechecked_under_the_lease()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "stale", T(1));
        // the tag index still says pending for the finder, but the blob is done by the time we hold the lease
        var name = P.Job("download", "stale");
        var real = new StaleFindStore(s, name);
        var ran = 0;
        var e = new NodeEngine(real, P, Opts(["download"]), new Dictionary<string, IStageHandler> { ["download"] = new FuncHandler(_ => { ran++; return Task.CompletedTask; }) });
        await Run(e);
        Assert.Equal(0, ran);
        Assert.Null(s.LeaseOf(name));
    }

    private sealed class StaleFindStore(FakeStore inner, string name) : IStore
    {
        public async Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string where, CancellationToken ct)
        {
            var r = await inner.FindByTagsAsync(where, ct);
            await inner.SetTagsAsync(name, new Dictionary<string, string> { [Tags.Stage] = "download", [Tags.State] = "done", [Tags.Added] = "x" }, null, ct);
            return r;
        }
        public Task<IReadOnlyList<BlobEntry>> ListAsync(string p, bool t, bool m, CancellationToken ct) => inner.ListAsync(p, t, m, ct);
        public Task<BlobContent?> GetAsync(string n, CancellationToken ct) => inner.GetAsync(n, ct);
        public Task<ConditionalResult> GetIfChangedAsync(string n, string? e, CancellationToken ct) => inner.GetIfChangedAsync(n, e, ct);
        public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string n, CancellationToken ct) => inner.GetTagsAsync(n, ct);
        public Task<bool> PutAsync(string n, byte[] d, IReadOnlyDictionary<string, string>? t, string? l, bool o, CancellationToken ct, string c = "application/json") => inner.PutAsync(n, d, t, l, o, ct, c);
        public Task PutFileAsync(string n, string p, string c, IProgress<long>? pr, CancellationToken ct) => inner.PutFileAsync(n, p, c, pr, ct);
        public Task DownloadToFileAsync(string n, string p, CancellationToken ct) => inner.DownloadToFileAsync(n, p, ct);
        public Task<string?> AcquireLeaseAsync(string n, int s, CancellationToken ct) => inner.AcquireLeaseAsync(n, s, ct);
        public Task RenewLeaseAsync(string n, string l, CancellationToken ct) => inner.RenewLeaseAsync(n, l, ct);
        public Task ReleaseLeaseAsync(string n, string l, CancellationToken ct) => inner.ReleaseLeaseAsync(n, l, ct);
        public Task SetMetadataAsync(string n, IReadOnlyDictionary<string, string> m, string? l, CancellationToken ct) => inner.SetMetadataAsync(n, m, l, ct);
        public Task SetTagsAsync(string n, IReadOnlyDictionary<string, string> t, string? l, CancellationToken ct) => inner.SetTagsAsync(n, t, l, ct);
        public Task DeleteAsync(string n, CancellationToken ct) => inner.DeleteAsync(n, ct);
    }

    [Fact]
    public async Task Permanent_failure_marks_failed_with_the_error_and_does_not_retry()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "gone", T(0));
        int runs = 0;
        await Run(Engine(s, Opts(["download"]), download: _ => { runs++; throw new PermanentFailure("Video unavailable"); }));
        var name = P.Job("download", "gone");
        Assert.Equal(1, runs);
        Assert.Equal("failed", s.State(name));
        var j = s.Job(name);
        Assert.True(j.Permanent); Assert.Equal("Video unavailable", j.LastError); Assert.Equal(0, j.Attempts);
        Assert.Null(s.LeaseOf(name));
    }

    [Fact]
    public async Task Transient_failure_counts_an_attempt_releases_at_once_and_fails_on_the_third()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "flaky", T(0));
        var name = P.Job("download", "flaky");
        for (int round = 1; round <= 3; round++)
        {
            await Run(Engine(s, Opts(["download"], backoff: _ => TimeSpan.FromHours(1)), download: _ => throw new IOException("disk full"))); // one attempt per node run
            Assert.Equal(round, s.Job(name).Attempts);
            Assert.Equal("disk full", s.Job(name).LastError);
            Assert.Null(s.LeaseOf(name));
            Assert.Equal(round < 3 ? "pending" : "failed", s.State(name));
        }
        Assert.Null(s.Job(name).Permanent);
        // a failed job is never claimed again
        int runs = 0;
        await Run(Engine(s, Opts(["download"]), download: _ => { runs++; return Task.CompletedTask; }));
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task Transient_failure_backs_off_this_job_on_this_node()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "flaky", T(0));
        int runs = 0;
        var e = Engine(s, Opts(["download"], once: false, backoff: _ => TimeSpan.FromHours(1)), download: _ => { runs++; throw new IOException("x"); });
        using var cts = new CancellationTokenSource(500);
        await e.RunAsync(cts.Token);
        Assert.Equal(1, runs);
        Assert.Equal(1, s.Job(P.Job("download", "flaky")).Attempts);
    }

    [Fact]
    public async Task Bot_block_releases_without_counting_an_attempt_and_backs_the_stage_off()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "a", T(0));
        await s.AddJob(P, "download", "b", T(1));
        int runs = 0;
        await Run(Engine(s, Opts(["download"]), download: _ => { runs++; throw new BotBlocked("Sign in to confirm you're not a bot"); }));
        Assert.Equal(1, runs); // the second job is not tried during the back-off
        var a = P.Job("download", "a");
        Assert.Equal("pending", s.State(a));
        Assert.Equal(0, s.Job(a).Attempts);
        Assert.Null(s.LeaseOf(a));
    }

    [Fact]
    public async Task Fallback_to_listing_when_the_sas_cannot_find_blobs_by_tags()
    {
        var s = new FakeStore { ForbidFind = true };
        await s.AddJob(P, "download", "a", T(0));
        int runs = 0;
        await Run(Engine(s, Opts(["download"]), download: _ => { runs++; return Task.CompletedTask; }));
        Assert.Equal(1, runs);
        Assert.Contains("list", s.Calls);
    }

    [Fact]
    public async Task An_empty_find_is_double_checked_with_a_listing_because_the_tag_index_lags()
    {
        var s = new FakeStore { StaleFind = true };
        await s.AddJob(P, "download", "fresh", T(0));
        int runs = 0;
        await Run(Engine(s, Opts(["download"]), download: _ => { runs++; return Task.CompletedTask; }));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Find_by_tags_is_container_wide_so_other_prefixes_jobs_are_never_claimed()
    {
        var s = new FakeStore();
        var other = new Paths("index/");
        await s.AddJob(other, "download", "theirs", T(0));
        await s.AddJob(P, "download", "mine", T(5));
        var ran = new List<string>();
        await Run(Engine(s, Opts(["download"]), download: ctx => { ran.Add(ctx.Job.JobId); return Task.CompletedTask; }));
        Assert.Equal(["mine"], ran);
        Assert.Equal("pending", s.State(other.Job("download", "theirs")));
        Assert.Null(s.LeaseOf(other.Job("download", "theirs")));
    }

    [Fact]
    public async Task Max_jobs_limits_the_jobs_started()
    {
        var s = new FakeStore();
        for (int i = 0; i < 4; i++) await s.AddJob(P, "download", "j" + i, T(i));
        int runs = 0;
        await Run(Engine(s, Opts(["download"], maxJobs: 2), download: _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; }));
        Assert.Equal(2, runs);
    }

    // ------------------------------------------------------------ parallelism

    [Fact]
    public async Task Two_process_jobs_at_once_each_get_half_of_the_thread_budget()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "p1", T(0));
        await s.AddJob(P, "process", "p2", T(1));
        var shares = new List<int>();
        var barrier = new Barrier(2);
        await Run(Engine(s, Opts(["process"], process: 2, threads: 8), process: ctx =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(5));
            lock (shares) shares.Add(ctx.ThreadShare());
            barrier.SignalAndWait(TimeSpan.FromSeconds(5));
            return Task.CompletedTask;
        }));
        Assert.Equal([4, 4], shares);
        Assert.Equal(1, Math.Max(1, new ThreadBudget(1).Share));
    }

    [Fact]
    public async Task A_single_process_job_gets_the_whole_budget_and_the_share_is_at_least_one()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "p1", T(0));
        int share = 0;
        await Run(Engine(s, Opts(["process"], threads: 6), process: ctx => { share = ctx.ThreadShare(); return Task.CompletedTask; }));
        Assert.Equal(6, share);
        var b = new ThreadBudget(2);
        using var a1 = b.Enter(); using var a2 = b.Enter(); using var a3 = b.Enter();
        Assert.Equal(1, b.Share);
    }

    [Fact]
    public async Task Leases_renew_independently_for_each_running_job()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "p1", T(0));
        await s.AddJob(P, "process", "p2", T(1));
        var n1 = P.Job("process", "p1"); var n2 = P.Job("process", "p2");
        var leases = new Dictionary<string, string>();
        await Run(Engine(s, Opts(["process"], process: 2), process: async ctx =>
        {
            lock (leases) leases[ctx.Job.JobId] = ctx.LeaseId;
            await Task.Delay(ctx.Job.JobId == "p1" ? 400 : 200);
        }));
        Assert.NotEqual(leases["p1"], leases["p2"]);
        Assert.InRange(s.RenewCount(n1), 5, 20);
        Assert.InRange(s.RenewCount(n2), 3, 20);
        Assert.True(s.RenewCount(n1) > s.RenewCount(n2)); // p2 stopped renewing when it finished; p1 went on
        Assert.Equal("done", s.State(n1)); Assert.Equal("done", s.State(n2));
    }

    [Fact]
    public async Task Progress_metadata_is_written_with_the_lease_while_working()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "p1", T(0));
        var name = P.Job("process", "p1");
        Dictionary<string, string>? seen = null;
        await Run(Engine(s, Opts(["process"]), process: async ctx =>
        {
            ctx.Report("sscd", "42%");
            await Within(() => s[name].Meta.ContainsKey("progress"));
            seen = new(s[name].Meta);
        }));
        Assert.Equal("test-abc123", seen!["node"]);
        Assert.Equal("sscd 42%", seen["progress"]);
        Assert.True(seen.ContainsKey("heartbeat"));
    }

    [Fact]
    public async Task One_failing_job_does_not_stop_the_other()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "bad", T(0));
        await s.AddJob(P, "process", "good", T(1));
        await Run(Engine(s, Opts(["process"], process: 2, backoff: _ => TimeSpan.FromHours(1)), process: async ctx =>
        {
            if (ctx.Job.JobId == "bad") throw new IOException("boom");
            await Task.Delay(250);
        }));
        Assert.Equal("done", s.State(P.Job("process", "good")));
        Assert.Equal(1, s.Job(P.Job("process", "bad")).Attempts);
        Assert.Equal("pending", s.State(P.Job("process", "bad")));
        Assert.Null(s.LeaseOf(P.Job("process", "good")));
    }

    // ------------------------------------------------------------ control

    [Fact]
    public async Task Stop_releases_every_lease_within_one_renewal_cycle_without_counting_an_attempt_and_run_resumes()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "p1", T(0));
        await s.AddJob(P, "process", "p2", T(1));
        int started = 0, cancelled = 0, completed = 0;
        var stopAt = DateTime.MinValue; var cancelledAt = new List<DateTime>();
        Func<JobContext, Task> handler = async ctx =>
        {
            Interlocked.Increment(ref started);
            try { await Task.Delay(Timeout.Infinite, ctx.Token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); lock (cancelledAt) cancelledAt.Add(DateTime.UtcNow); throw; }
        };
        var e = Engine(s, Opts(["process"], process: 2, once: false), process: handler);
        using var cts = new CancellationTokenSource();
        var run = e.RunAsync(cts.Token);
        await Within(() => started == 2);

        stopAt = DateTime.UtcNow;
        await ControlWatcher.WriteAsync(s, P, "stop", null, "maintenance", "owner", default);
        await Within(() => cancelled == 2, 2000);
        await Within(() => s.LeaseOf(P.Job("process", "p1")) == null && s.LeaseOf(P.Job("process", "p2")) == null, 2000);

        foreach (var t in cancelledAt) Assert.True((t - stopAt).TotalMilliseconds < 400, "released within one renewal cycle"); // renewal 40 ms, control poll 40 ms
        foreach (var id in new[] { "p1", "p2" })
        {
            var n = P.Job("process", id);
            Assert.Equal("pending", s.State(n));
            Assert.Equal(0, s.Job(n).Attempts);
        }
        // stays idle while stopped
        await Task.Delay(300);
        Assert.Equal(2, started);
        var info = NodeInfo.Parse(s[P.Node("test-abc123")].Data)!;
        Assert.Equal("stopped", info.State);

        // run resumes on its own
        Func<JobContext, Task> finish = _ => { Interlocked.Increment(ref completed); return Task.CompletedTask; };
        // swap behaviour: the same engine now sees run; the handler (still the blocking one) is started again, so release by stopping afterwards
        await ControlWatcher.WriteAsync(s, P, "run", null, null, "owner", default);
        await Within(() => started == 4, 3000);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, s.Job(P.Job("process", "p1")).Attempts); // Ctrl+C released too, uncounted
        Assert.Equal("offline", NodeInfo.Parse(s[P.Node("test-abc123")].Data)!.State);
    }

    [Fact]
    public async Task Per_stage_stop_only_stops_that_stage()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "d1", T(0));
        await s.AddJob(P, "process", "p1", T(0));
        int d = 0, p = 0;
        var e = Engine(s, Opts(once: false),
            download: async ctx => { Interlocked.Increment(ref d); await Task.Delay(Timeout.Infinite, ctx.Token); },
            process: async ctx => { Interlocked.Increment(ref p); await Task.Delay(Timeout.Infinite, ctx.Token); });
        using var cts = new CancellationTokenSource();
        var run = e.RunAsync(cts.Token);
        await Within(() => d == 1 && p == 1);
        await ControlWatcher.WriteAsync(s, P, "stop", "download", null, "owner", default);
        await Within(() => s.LeaseOf(P.Job("download", "d1")) == null, 2000);
        Assert.NotNull(s.LeaseOf(P.Job("process", "p1")));
        Assert.Equal(0, s.Job(P.Job("download", "d1")).Attempts);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Drain_finishes_current_jobs_and_claims_no_new_one_then_run_resumes()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "first", T(0));
        await s.AddJob(P, "process", "second", T(1));
        var release = new TaskCompletionSource();
        var ran = new List<string>();
        var e = Engine(s, Opts(["process"], once: false), process: async ctx =>
        {
            lock (ran) ran.Add(ctx.Job.JobId);
            if (ctx.Job.JobId == "first") await release.Task;
        });
        using var cts = new CancellationTokenSource();
        var run = e.RunAsync(cts.Token);
        await Within(() => ran.Count == 1);
        await ControlWatcher.WriteAsync(s, P, "drain", null, null, "owner", default);
        await Task.Delay(200);
        release.SetResult();
        await Within(() => s.State(P.Job("process", "first")) == "done");
        await Task.Delay(300);
        Assert.Equal(["first"], ran);                       // nothing new was claimed
        Assert.Equal("pending", s.State(P.Job("process", "second")));
        Assert.Equal("draining", NodeInfo.Parse(s[P.Node("test-abc123")].Data)!.State);

        await ControlWatcher.WriteAsync(s, P, "run", null, null, "owner", default);
        await Within(() => s.State(P.Job("process", "second")) == "done", 3000);
        Assert.Equal(["first", "second"], ran);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Control_is_read_with_a_conditional_get()
    {
        var s = new FakeStore();
        var w = new ControlWatcher(s, P);
        await w.RefreshAsync(default, force: true);          // missing file means run
        Assert.Equal(Level.Run, w.Effective("download"));
        await ControlWatcher.WriteAsync(s, P, "drain", "process", "x", "owner", default);
        int changes = 0; w.Changed += () => changes++;
        await w.RefreshAsync(default, force: true);
        await w.RefreshAsync(default, force: true);          // unchanged etag: no change event
        Assert.Equal(1, changes);
        Assert.Equal(Level.Drain, w.Effective("process"));
        Assert.Equal(Level.Run, w.Effective("download"));
        // a global setting replaces the per-stage overrides
        await ControlWatcher.WriteAsync(s, P, "run", null, null, "owner", default);
        await w.RefreshAsync(default, force: true);
        Assert.Equal(Level.Run, w.Effective("process"));
    }

    // ------------------------------------------------------------ registry

    [Fact]
    public async Task Registry_is_written_at_start_refreshed_while_running_and_set_offline_on_ctrl_c()
    {
        var s = new FakeStore();
        var name = P.Node("test-abc123");
        var e = Engine(s, Opts(once: false, download: 1, process: 2));
        using var cts = new CancellationTokenSource();
        var run = e.RunAsync(cts.Token);
        await Within(() => s.Exists(name));
        var first = NodeInfo.Parse(s[name].Data)!;
        Assert.Equal("test-abc123", first.NodeId);
        Assert.Equal("desktop", first.Kind);
        Assert.Equal(["download", "process"], first.Stages);
        Assert.Equal(1, first.Jobs["download"]); Assert.Equal(2, first.Jobs["process"]);
        Assert.Equal(8, first.Threads);
        Assert.Equal("node", s[name].Tags[Tags.Kind]);
        Assert.Contains(first.State, new[] { "idle", "working" });

        await Within(() => s.PutCount(name) >= 4, 3000);     // refreshed on its interval
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var last = NodeInfo.Parse(s[name].Data)!;
        Assert.Equal("offline", last.State);
        Assert.Equal("offline", last.EffectiveState(DateTimeOffset.UtcNow));
        Assert.Equal("node", s[name].Tags[Tags.Kind]);
    }

    [Fact]
    public async Task Registry_lists_running_jobs_with_worker_numbers()
    {
        var s = new FakeStore();
        await s.AddJob(P, "process", "p1", T(0));
        var name = P.Node("test-abc123");
        var gate = new TaskCompletionSource();
        var e = Engine(s, Opts(["process"], once: false), process: async ctx => { ctx.Report("frames", "3/8 segments"); await gate.Task; });
        using var cts = new CancellationTokenSource();
        var run = e.RunAsync(cts.Token);
        await Within(() => s.Exists(name) && NodeInfo.Parse(s[name].Data)!.Running.Count == 1);
        var r = NodeInfo.Parse(s[name].Data)!.Running.Single();
        Assert.Equal("p1", r.JobId); Assert.Equal("process", r.Stage); Assert.Equal(1, r.Worker);
        Assert.Equal("working", NodeInfo.Parse(s[name].Data)!.State);
        gate.SetResult();
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
