using IndexNode;
using Xunit;

namespace IndexNode.Tests;

public class WorkerTests
{
    private static readonly Paths P = new("index-test/");
    private sealed class FuncHandler(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }

    private static async Task Add(FakeStore s, string stage, string id, string added, int priority = 0, bool needs = false)
    {
        var job = JobDescriptor.NewStep(stage, id);
        job.JobId = id; job.AddedAt = added; job.Priority = priority;
        if (!needs) job.Needs = null;
        await s.PutAsync(P.Job(stage, id), job.ToBytes(), Tags.Job(stage, "pending", added, null, priority), null, true, default);
    }

    private static EngineOptions Opts(string[] stages, int workers, int threads = 8, int? workerThreads = null) => new()
    {
        NodeId = "test-abc123", Name = "test", Stages = stages, Workers = workers, WorkerThreads = workerThreads, Threads = threads, Once = true,
        PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100),
        BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, ControlInterval = TimeSpan.FromMilliseconds(40),
    };

    private sealed class Probe
    {
        public readonly List<string> Order = new(); public readonly HashSet<int> WorkerIds = new(); public readonly List<int> Shares = new(); public int Now, Max;
        private readonly object gate = new();
        public IStageHandler Handler(int ms = 40) => new FuncHandler(async ctx =>
        {
            lock (gate) { Order.Add(ctx.Job.JobId); WorkerIds.Add(ctx.Worker); Shares.Add(ctx.ThreadShare()); Now++; Max = Math.Max(Max, Now); }
            await Task.Delay(ms);
            lock (gate) Now--;
        });
    }

    private static async Task Run(FakeStore s, EngineOptions o, Probe p, int ms = 40)
    {
        var e = new NodeEngine(s, P, o, o.Stages.ToDictionary(x => x, _ => p.Handler(ms)));
        await e.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task N_workers_each_claim_independently_and_nothing_else_limits_them()
    {
        var s = new FakeStore(); var p = new Probe();
        foreach (var st in new[] { "compress", "dino", "clip" }) for (int i = 0; i < 4; i++) await Add(s, st, st + i, $"2026-11-17T17:0{i}:00Z");
        await Run(s, Opts(["compress", "dino", "clip"], 4), p);
        Assert.Equal(12, p.Order.Count);
        Assert.Equal(4, p.WorkerIds.Count); Assert.Equal(4, p.Max); // exactly the 4 workers, all busy at once: no worker waits on another or on a slot
    }

    [Fact]
    public async Task A_single_worker_takes_jobs_oldest_first_across_stages()
    {
        var s = new FakeStore(); var p = new Probe();
        await Add(s, "compress", "t1", "2026-11-17T17:12:00Z"); await Add(s, "compress", "t2", "2026-11-17T17:13:00Z");
        await Add(s, "dino", "o1", "2026-11-17T17:14:00Z"); await Add(s, "dino", "o2", "2026-11-17T17:15:00Z"); await Add(s, "dino", "o3", "2026-11-17T17:16:00Z");
        await Add(s, "clip", "s1", "2026-11-17T17:13:30Z");
        await Run(s, Opts(["compress", "dino", "clip"], 1), p);
        Assert.Equal(["t1", "t2", "s1", "o1", "o2", "o3"], p.Order); // no stage runs in a long batch ahead of an older job elsewhere
    }

    [Fact]
    public async Task Priority_beats_age_across_stages()
    {
        var s = new FakeStore(); var p = new Probe();
        await Add(s, "compress", "old", "2026-11-17T17:12:00Z");
        await Add(s, "dino", "newUrgent", "2026-11-17T18:06:00Z", 5);
        await Add(s, "clip", "newer", "2026-11-17T18:07:00Z");
        await Run(s, Opts(["compress", "dino", "clip"], 1), p);
        Assert.Equal(["newUrgent", "old", "newer"], p.Order);
    }

    [Fact]
    public async Task Needs_blocked_jobs_are_skipped_and_the_next_stage_is_used()
    {
        var s = new FakeStore(); var p = new Probe();
        await Add(s, "clip", "blocked", "2026-11-17T17:00:00Z", needs: true); // its needs do not exist
        await Add(s, "dino", "free", "2026-11-17T18:00:00Z");
        await Run(s, Opts(["clip", "dino"], 1), p);
        Assert.Equal(["free"], p.Order);
        Assert.Equal("pending", s.State(P.Job("clip", "blocked")));
    }

    [Fact]
    public async Task Downloads_are_just_another_stage_for_a_worker()
    {
        var s = new FakeStore(); var p = new Probe();
        await Add(s, "dino", "o1", "2026-11-17T18:00:00Z");
        await Add(s, "download", "d1", "2026-11-17T17:00:00Z");
        await Run(s, Opts(["download", "dino"], 1), p);
        Assert.Equal(["d1", "o1"], p.Order);
    }

    [Fact]
    public async Task Threads_are_split_evenly_across_workers_unless_worker_threads_is_given()
    {
        Assert.Equal(4, ThreadSplit.PerWorker(8, 2, null)); Assert.Equal(1, ThreadSplit.PerWorker(2, 3, null)); Assert.Equal(6, ThreadSplit.PerWorker(8, 2, 6));
        Assert.Equal(4, Opts(["clip"], 2).ThreadsPerWorker); Assert.Equal(3, Opts(["clip"], 2, workerThreads: 3).ThreadsPerWorker);
        var s = new FakeStore(); var p = new Probe();
        for (int i = 0; i < 4; i++) await Add(s, "clip", "s" + i, $"2026-11-17T17:0{i}:00Z");
        await Run(s, Opts(["clip"], 2), p);
        Assert.All(p.Shares, x => Assert.Equal(4, x));
        var q = new Probe();
        for (int i = 0; i < 2; i++) await Add(s, "clip", "c" + i, $"2026-11-17T17:0{i}:00Z");
        await Run(s, Opts(["clip"], 2, workerThreads: 3), q);
        Assert.All(q.Shares, x => Assert.Equal(3, x));
    }

    [Fact]
    public async Task Rank_orders_stages_by_their_best_claimable_job()
    {
        BlobEntry B(string st, string id, string added, int prio = 0) => new($"index/jobs/{st}/{id}.json", Tags.Job(st, "pending", added, null, prio), new Dictionary<string, string>(), "available", 1);
        var q = new Dictionary<string, IReadOnlyList<BlobEntry>>
        {
            ["a"] = [B("a", "x", "2026-11-17T18:00:00Z")], ["b"] = [B("b", "blocked", "2026-11-17T10:00:00Z"), B("b", "y", "2026-11-17T17:00:00Z")], ["c"] = [], ["d"] = [B("d", "z", "2026-11-17T19:00:00Z", 3)],
        };
        var r = await StageOrder.RankAsync(["a", "b", "c", "d"], (st, _) => Task.FromResult(q[st]), (_, b, _) => Task.FromResult(!b.Name.Contains("blocked")), default);
        Assert.Equal(["d", "b", "a"], r);
    }

    [Fact]
    public void Rolling_log_keeps_two_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inlog" + Guid.NewGuid().ToString("N"));
        var f = new RollingFile(RollingFile.FileFor(dir, "my node"), 100);
        for (int i = 0; i < 20; i++) f.WriteLine("line number " + i + " xxxxxxxxxx");
        Assert.Equal(2, Directory.GetFiles(dir).Length);
        Assert.EndsWith("run-my_node.log", RollingFile.FileFor(dir, "my node"));
        Directory.Delete(dir, true);
    }
}
