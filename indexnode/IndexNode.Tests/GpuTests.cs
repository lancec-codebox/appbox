namespace IndexNode.Tests;

public class GpuTests
{
    private sealed class FuncHandler(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    private static readonly Paths P = new("index-test/");
    private static string Ago(TimeSpan t) => Protocol.Utc(DateTimeOffset.UtcNow - t);

    private static EngineOptions Opts(string[] stages, bool gpu = false, int maxHeavy = 2, TimeSpan? fallback = null, TimeSpan? idle = null, bool once = true) => new()
    {
        NodeId = "gpu-test", Name = "gpu-test", Stages = stages, Jobs = stages.ToDictionary(s => s, _ => 1), Threads = 8, Once = once,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromSeconds(30), RegistryInterval = TimeSpan.FromSeconds(30), ControlInterval = TimeSpan.FromSeconds(30),
        Gpu = gpu, MaxHeavy = maxHeavy, GpuFallback = fallback ?? GpuRouting.CpuFallbackAfter, ExitWhenIdle = idle, IdleCheck = TimeSpan.FromMilliseconds(20), HeavyRetry = TimeSpan.FromMilliseconds(10),
    };

    private static NodeEngine Engine(FakeStore s, EngineOptions o, Func<JobContext, Task> h) =>
        new(s, P, o, o.Stages.ToDictionary(x => x, x => (IStageHandler)new FuncHandler(h)));

    private static async Task Frames(FakeStore s, string id)
    {
        await s.PutAsync(P.Video(id, "frames/manifest.json"), [1], null, null, false, default);
        await s.PutAsync(P.Video(id, "keyframes/manifest.json"), [1], null, null, false, default);
    }

    private static async Task Job(FakeStore s, string stage, string id, TimeSpan age)
    {
        var j = JobDescriptor.NewStep(stage, id);
        await s.PutAsync(P.Job(stage, id), j.ToBytes(), Tags.Job(stage, "pending", Ago(age)), null, false, default);
    }

    // ---------------------------------------------------------------- pure rules

    [Fact]
    public void Marker_names_and_bodies_round_trip()
    {
        Assert.Equal("index-test/gpu-ready/clip.abc", GpuRouting.MarkerName(P, "clip", "abc"));
        var at = new DateTimeOffset(2026, 11, 14, 5, 6, 7, TimeSpan.Zero);
        Assert.Equal(at, GpuRouting.ReadyAtOf(GpuRouting.MarkerBody("clip", "abc", at)));
        Assert.Null(GpuRouting.ReadyAtOf([1, 2, 3]));
    }

    [Theory]
    [InlineData(false, "sscd", 5, false)]
    [InlineData(false, "clip", 29, false)]
    [InlineData(false, "cast", 31, true)]
    [InlineData(false, "ocrsubs", 0, true)]
    [InlineData(false, "frames", 0, true)]
    [InlineData(true, "sscd", 0, true)]
    public void A_cpu_node_takes_a_gpu_stage_job_only_after_30_minutes(bool gpu, string stage, int minutes, bool allowed)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(allowed, GpuRouting.MayClaim(gpu, stage, now.AddMinutes(-minutes), now));
    }

    [Fact]
    public void A_cpu_node_never_takes_a_marker_job_with_unknown_ready_time()
    {
        Assert.False(GpuRouting.MayClaim(false, "clip", null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_gpu_node_keeps_only_the_gpu_stages()
    {
        Assert.Equal(["sscd", "clip"], GpuRouting.GpuNodeStages(["download", "sscd", "clip", "watch", "sscd"]));
        Assert.Empty(GpuRouting.GpuNodeStages(["download"]));
    }

    [Fact]
    public void Device_parses_and_batches()
    {
        Assert.Equal("cuda", OrtOptions.ParseDevice(" CUDA "));
        Assert.Equal("cpu", OrtOptions.ParseDevice(null));
        Assert.Throws<ArgumentException>(() => OrtOptions.ParseDevice("tpu"));
        Assert.Equal(32, OrtOptions.Batch(32, 64));
    }

    [Fact]
    public void Cuda_without_the_gpu_runtime_fails_hard_not_on_the_cpu()
    {
        // this machine has the CPU build of ONNX Runtime: asking for CUDA must throw, never quietly run on the CPU
        var e = Assert.Throws<InvalidOperationException>(() => OrtOptions.Make(1, true, "cuda"));
        Assert.Contains("refusing to fall back to the CPU", e.Message);
    }

    [Fact]
    public void Strict_mode_is_off_by_default_and_on_with_the_env_var()
    {
        var old = Environment.GetEnvironmentVariable("INDEXNODE_CUDA_STRICT");
        try
        {
            Environment.SetEnvironmentVariable("INDEXNODE_CUDA_STRICT", null);
            Assert.False(OrtOptions.Strict);
            Environment.SetEnvironmentVariable("INDEXNODE_CUDA_STRICT", "1");
            Assert.True(OrtOptions.Strict);
        }
        finally { Environment.SetEnvironmentVariable("INDEXNODE_CUDA_STRICT", old); }
    }

    [Fact]
    public async Task Frames_completion_marks_each_pending_gpu_step_once_and_skips_done_ones()
    {
        var s = new FakeStore();
        await Job(s, "sscd", "v1", TimeSpan.Zero); await Job(s, "clip", "v1", TimeSpan.Zero); await Job(s, "cast", "v1", TimeSpan.Zero);
        await s.PutAsync(P.Job("clip", "v1"), JobDescriptor.NewStep("clip", "v1").ToBytes(), Tags.Job("clip", "done", Ago(TimeSpan.Zero)), null, false, default);
        Assert.Equal(["sscd", "cast"], await GpuRouting.WriteReadyMarkersAsync(s, P, "v1", default));
        Assert.True(s.Exists(GpuRouting.MarkerName(P, "sscd", "v1")));
        Assert.False(s.Exists(GpuRouting.MarkerName(P, "clip", "v1")));
        var first = s[GpuRouting.MarkerName(P, "cast", "v1")].Data.ToArray();
        await GpuRouting.WriteReadyMarkersAsync(s, P, "v1", default);
        Assert.Equal(first, s[GpuRouting.MarkerName(P, "cast", "v1")].Data); // the first readyAt stays
    }

    // ---------------------------------------------------------------- engine

    [Fact]
    public async Task A_gpu_node_claims_a_fresh_marker_job_at_once_a_cpu_node_leaves_it()
    {
        var s = new FakeStore(); await Frames(s, "v1"); await Job(s, "clip", "v1", TimeSpan.FromMinutes(1));
        await GpuRouting.WriteMarkerAsync(s, P, "clip", "v1", default);
        var cpuRan = 0;
        await Engine(s, Opts(["clip"]), _ => { cpuRan++; return Task.CompletedTask; }).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, cpuRan); Assert.Equal("pending", s.State(P.Job("clip", "v1")));
        var gpuRan = 0;
        await Engine(s, Opts(["clip"], gpu: true), _ => { gpuRan++; return Task.CompletedTask; }).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, gpuRan); Assert.Equal("done", s.State(P.Job("clip", "v1")));
    }

    [Fact]
    public async Task A_cpu_node_takes_the_job_once_its_marker_is_older_than_the_wait()
    {
        var s = new FakeStore(); await Frames(s, "v1"); await Job(s, "sscd", "v1", TimeSpan.FromHours(3));
        await GpuRouting.WriteMarkerAsync(s, P, "sscd", "v1", default, DateTimeOffset.UtcNow.AddMinutes(-31));
        var ran = 0;
        await Engine(s, Opts(["sscd"]), _ => { ran++; return Task.CompletedTask; }).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Without_a_marker_the_later_of_added_and_due_counts()
    {
        var s = new FakeStore(); await Frames(s, "old"); await Frames(s, "new");
        await Job(s, "cast", "old", TimeSpan.FromMinutes(45)); await Job(s, "cast", "new", TimeSpan.FromMinutes(5));
        var ids = new List<string>();
        await Engine(s, Opts(["cast"]), c => { ids.Add(c.Job.JobId); return Task.CompletedTask; }).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["old"], ids);
    }

    [Fact]
    public async Task The_marker_is_deleted_when_the_job_is_done_and_when_it_fails_for_good_not_when_released()
    {
        var s = new FakeStore();
        foreach (var id in new[] { "ok", "bad", "rel" }) { await Frames(s, id); await Job(s, "clip", id, TimeSpan.FromMinutes(1)); await GpuRouting.WriteMarkerAsync(s, P, "clip", id, default); }
        await Engine(s, Opts(["clip"], gpu: true, maxHeavy: 1, once: false, idle: TimeSpan.FromMilliseconds(300)), c =>
        {
            if (c.Job.JobId == "bad") throw new PermanentFailure("nope");
            if (c.Job.JobId == "rel") throw new JobSkipped("not here");
            return Task.CompletedTask;
        }).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(s.Exists(GpuRouting.MarkerName(P, "clip", "ok")));
        Assert.False(s.Exists(GpuRouting.MarkerName(P, "clip", "bad")));
        Assert.True(s.Exists(GpuRouting.MarkerName(P, "clip", "rel")));
        Assert.Equal("failed", s.State(P.Job("clip", "bad")));
    }

    [Fact]
    public async Task Exit_when_idle_stops_a_node_with_nothing_to_do()
    {
        var s = new FakeStore();
        var e = Engine(s, Opts(["clip"], gpu: true, once: false, idle: TimeSpan.FromMilliseconds(150)), _ => Task.CompletedTask);
        await e.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(e.ExitedIdle);
    }

    [Fact]
    public async Task Exit_when_idle_waits_for_a_running_job()
    {
        var s = new FakeStore(); await Frames(s, "v1"); await Job(s, "clip", "v1", TimeSpan.FromMinutes(1));
        var e = Engine(s, Opts(["clip"], gpu: true, once: false, idle: TimeSpan.FromMilliseconds(100)), async c => await Task.Delay(500, c.Token));
        await e.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("done", s.State(P.Job("clip", "v1"))); // not cut short by the idle timer
        Assert.True(e.ExitedIdle);
    }

    [Fact]
    public async Task At_most_the_heavy_limit_of_model_steps_run_at_once_and_all_finish()
    {
        var s = new FakeStore();
        foreach (var id in new[] { "a", "b", "c", "d", "e", "f" }) { await Frames(s, id); await Job(s, "sscd", id, TimeSpan.FromHours(2)); }
        int now = 0, peak = 0;
        var o = Opts(["sscd", "clip", "cast"], maxHeavy: 2, once: false, idle: TimeSpan.FromMilliseconds(300));
        await Engine(s, o, async c =>
        {
            var n = Interlocked.Increment(ref now); lock (s) peak = Math.Max(peak, n);
            await Task.Delay(60, c.Token);
            Interlocked.Decrement(ref now);
        }).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(peak <= 2, "peak " + peak);
        Assert.All(new[] { "a", "b", "c", "d", "e", "f" }, id => Assert.Equal("done", s.State(P.Job("sscd", id))));
    }

    [Fact]
    public async Task The_heavy_slot_is_released_when_a_job_fails_so_the_next_one_runs()
    {
        var s = new FakeStore();
        foreach (var id in new[] { "a", "b", "c" }) { await Frames(s, id); await Job(s, "sscd", id, TimeSpan.FromHours(2)); }
        await Engine(s, Opts(["sscd"], maxHeavy: 1, once: false, idle: TimeSpan.FromMilliseconds(300)), c => c.Job.JobId == "a" ? throw new PermanentFailure("x") : Task.CompletedTask)
            .RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("failed", s.State(P.Job("sscd", "a"))); Assert.Equal("done", s.State(P.Job("sscd", "b"))); Assert.Equal("done", s.State(P.Job("sscd", "c")));
    }

    [Fact]
    public async Task Maxheavy_zero_means_no_limit()
    {
        var s = new FakeStore();
        foreach (var id in new[] { "a", "b", "c" }) { await Frames(s, id); await Job(s, "sscd", id, TimeSpan.FromHours(2)); }
        int now = 0, peak = 0;
        var o = Opts(["sscd"], maxHeavy: 0, once: false, idle: TimeSpan.FromMilliseconds(300));
        o.Jobs["sscd"] = 3;
        await Engine(s, o, async c => { var n = Interlocked.Increment(ref now); lock (s) peak = Math.Max(peak, n); await Task.Delay(150, c.Token); Interlocked.Decrement(ref now); })
            .RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(3, peak);
    }
}
