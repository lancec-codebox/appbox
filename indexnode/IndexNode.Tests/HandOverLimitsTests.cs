using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.48: two things a download node on the work queue got wrong on 2026-11-24: the service's hand-over ignored the node's budget, and a control change never reached the node.</summary>
public class HandOverLimitsTests
{
    static readonly Paths P = new("index/");
    static string TempState() => Path.Combine(Path.GetTempPath(), "indexnode-test-" + Guid.NewGuid().ToString("N"), "state.json");

    static EngineOptions Opts(LimitState limits, List<string[]> asked, string[]? stages = null) => new()
    {
        NodeId = "test-node", Name = "test", Stages = stages ?? ["sample", "download"], Workers = 1, Threads = 2, Once = true,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
        BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(), Limits = limits,
        FetchNext = (stages, _) => { lock (asked) asked.Add(stages); return Task.FromResult<NextClaim?>(null); },
    };
    sealed class Never : IStageHandler { public Task RunAsync(JobContext ctx) => throw new InvalidOperationException("nothing is handed over in these tests"); }
    static Dictionary<string, IStageHandler> Handlers() => new() { ["sample"] = new Never(), ["download"] = new Never() };
    static Task Run(NodeEngine e) => e.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

    [Fact]
    public async Task The_hand_over_is_asked_only_for_the_stages_the_limits_have_room_for()
    {
        var limits = new LimitState("test-node", new NodeLimits { Budget = 1, SampleBudget = 1 }, "", TempState());
        var asked = new List<string[]>();
        await Run(new NodeEngine(new FakeStore(), P, Opts(limits, asked), Handlers()));
        Assert.NotEmpty(asked); Assert.All(asked, a => Assert.Equal(["sample", "download"], a));
        Assert.Equal(1, limits.Remaining);                                     // an empty answer used nothing
        Assert.True(limits.TryAcquire("download")); limits.Release("download", true);   // the one full download of the budget is done
        Assert.Equal(0, limits.Remaining);

        asked.Clear();
        await Run(new NodeEngine(new FakeStore(), P, Opts(limits, asked), Handlers()));
        Assert.Empty(asked);   // 0.13.55: on a node that downloads, a sample is the first half of a download: with no room for the download it asks for neither (0.13.48 asked for the sample alone and so sampled ahead)

        // a sample-only node (sampling ahead, the directed tool) has only its sample budget
        asked.Clear();
        await Run(new NodeEngine(new FakeStore(), P, Opts(limits, asked, ["sample"]), new Dictionary<string, IStageHandler> { ["sample"] = new Never() }));
        Assert.NotEmpty(asked); Assert.All(asked, a => Assert.Equal(["sample"], a));
        Assert.True(limits.TryAcquire("sample")); limits.Release("sample", true);       // and the one sample

        asked.Clear();
        await Run(new NodeEngine(new FakeStore(), P, Opts(limits, asked, ["sample"]), new Dictionary<string, IStageHandler> { ["sample"] = new Never() }));
        Assert.Empty(asked);                                                   // nothing the limits allow: the service is not asked at all (an empty list would mean any stage)
    }

    sealed class Rec(List<string> ran, string stage) : IStageHandler { public Task RunAsync(JobContext ctx) { lock (ran) ran.Add(stage + ":" + ctx.Job.JobId); return Task.CompletedTask; } }

    [Fact]
    public async Task After_a_sample_the_node_runs_the_same_uploads_download_before_the_next_sample_and_not_when_the_budget_is_used_up()
    {
        // 0.13.49: the engine side of "a sample proceeds straight to its download": after each finished sample it asks FetchSampled; a claim that comes back is adopted and run before anything else
        var s = new FakeStore(); var ran = new List<string>(); var asked = new List<string>();
        var seed = new JobQueue(s, P, "seed"); await seed.LoadAsync(default);
        QLine Add(string id, string stage) => QLine.Add(id, stage, new JobDescriptor { Stage = stage, JobId = id, Url = "https://www.youtube.com/watch?v=" + id, AddedAt = "2026-11-24T08:00:00Z" }, "2026-11-24T08:00:00Z", 0, "2026-11-24T08:00:00Z");
        await seed.AppendLinesAsync([Add("v1", "sample"), Add("v2", "sample"), Add("v3", "sample"), Add("v4", "sample")], default);
        var svc = new JobQueue(s, P, "service"); await svc.LoadAsync(default);
        // the stand-in for the service: v1, v3 and v4 are new (their download is added and claimed for the node), v2 is a duplicate
        async Task<(NextClaim?, string)> Sampled(string id, CancellationToken ct)
        {
            lock (asked) asked.Add(id);
            if (id == "v2") return (null, "duplicate of orig: nothing to download");
            await svc.PollAsync(ct); await svc.AppendLinesAsync([Add(id, "download")], ct);
            var h = await svc.TryClaimForAsync(id, "test-node", (_, sub) => ["download"], ct);
            return (new NextClaim(id, "download", h!.Epoch, h.Lease), "new");
        }
        var limits = new LimitState("test-node", new NodeLimits { Budget = 2 }, "", TempState());
        var o = new EngineOptions
        {
            NodeId = "test-node", Name = "test", Stages = ["sample", "download"], Workers = 1, Threads = 2, Once = true,
            PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
            BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(), Limits = limits, FetchSampled = Sampled,
        };
        await Run(new NodeEngine(s, P, o, new Dictionary<string, IStageHandler> { ["sample"] = new Rec(ran, "sample"), ["download"] = new Rec(ran, "download") }));
        Assert.Equal(["sample:v1", "download:v1", "sample:v2", "sample:v3", "download:v3"], ran.ToArray());
        // v1: its download follows its own sample, before the next sample. v2: a duplicate, nothing to download (and it used none of the budget). v3: the same as v1.
        // v4: the budget of two full downloads is used up, so it is not even sampled (0.13.55: no sampling ahead on a node that downloads; 0.13.49 sampled it and left its download queued).
        Assert.Equal(["v1", "v2", "v3"], asked.ToArray());
        Assert.Equal(0, limits.Remaining);
    }

    [Fact]
    public async Task A_download_node_asks_the_service_before_it_takes_anything_from_the_work_queue_itself()
    {
        // 0.13.55: the service hands out by priority over samples and downloads together, and a download only after its check; the node's own claim on the work queue is the fallback
        var s = new FakeStore(); var seen = new List<string>();
        var seed = new JobQueue(s, P, "seed"); await seed.LoadAsync(default);
        await seed.AppendLinesAsync([QLine.Add("v1", "sample", new JobDescriptor { Stage = "sample", JobId = "v1", Url = "https://www.youtube.com/watch?v=v1", AddedAt = "2026-11-24T08:00:00Z" }, "2026-11-24T08:00:00Z", 0, "2026-11-24T08:00:00Z")], default);
        var o = new EngineOptions
        {
            NodeId = "test-node", Name = "test", Stages = ["sample", "download"], Workers = 1, Threads = 2, Once = true,
            PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
            BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(),
            FetchNext = (stages, _) => { lock (seen) seen.Add("asked " + string.Join(",", stages)); return Task.FromResult<NextClaim?>(null); },
        };
        await Run(new NodeEngine(s, P, o, new Dictionary<string, IStageHandler> { ["sample"] = new Rec(seen, "sample"), ["download"] = new Rec(seen, "download") }));
        Assert.Equal(["asked sample,download", "sample:v1"], seen.Take(2).ToArray());   // the service first (it had nothing); then the sample the node found itself
    }

    [Fact]
    public async Task A_node_that_is_being_handed_work_still_sees_downloads_switched_off()
    {
        // 0.13.60: the service hands d1 over and, in the same moment, downloads are switched off (control file + the "ctl" line). The node runs d1 and must NOT ask again.
        var s = new FakeStore(); var seen = new List<string>(); int calls = 0;
        var seed = new JobQueue(s, P, "seed"); await seed.LoadAsync(default);
        var svc = new JobQueue(s, P, "service"); await svc.LoadAsync(default);
        QLine Add(string id) => QLine.Add(id, "download", new JobDescriptor { Stage = "download", JobId = id, Url = "https://www.youtube.com/watch?v=" + id, AddedAt = "2026-11-24T08:00:00Z" }, "2026-11-24T08:00:00Z", 0, "2026-11-24T08:00:00Z");
        async Task<NextClaim?> Next(string[] stages, CancellationToken ct)
        {
            int k = Interlocked.Increment(ref calls); string id = "d" + k;
            await svc.PollAsync(ct); await svc.AppendLinesAsync([Add(id)], ct);
            var h = await svc.TryClaimForAsync(id, "test-node", (_, sub) => ["download"], ct);
            if (k == 1)
            {
                await s.PutAsync(P.Control, new ControlFile { Stages = new() { ["download"] = "stop" } }.ToBytes(), null, null, false, ct);
                await ControlWatcher.AnnounceAsync(s, P, "test", ct);
            }
            return new NextClaim(id, "download", h!.Epoch, h.Lease);
        }
        var o = new EngineOptions
        {
            NodeId = "test-node", Name = "test", Stages = ["sample", "download"], Workers = 1, Threads = 2, Once = true,
            PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
            BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(), FetchNext = Next,
        };
        await Run(new NodeEngine(s, P, o, new Dictionary<string, IStageHandler> { ["sample"] = new Rec(seen, "sample"), ["download"] = new Rec(seen, "download") }));
        Assert.Equal(1, calls); Assert.Equal(["download:d1"], seen.ToArray());
    }

    [Fact]
    public async Task A_timed_out_call_to_the_service_does_not_stop_the_node()
    {
        // 0.13.50: the client's timeout is an OperationCanceledException; 0.13.49 rethrew it as if the node were being stopped, and the node exited with "node stopped" and no reason
        var s = new FakeStore(); var ran = new List<string>();
        var seed = new JobQueue(s, P, "seed"); await seed.LoadAsync(default);
        QLine Add(string id) => QLine.Add(id, "sample", new JobDescriptor { Stage = "sample", JobId = id, Url = "https://www.youtube.com/watch?v=" + id, AddedAt = "2026-11-24T08:00:00Z" }, "2026-11-24T08:00:00Z", 0, "2026-11-24T08:00:00Z");
        await seed.AppendLinesAsync([Add("v1"), Add("v2")], default);
        int calls = 0, fetches = 0;
        var o = new EngineOptions
        {
            NodeId = "test-node", Name = "test", Stages = ["sample", "download"], Workers = 1, Threads = 2, Once = true,
            PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
            BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(),
            FetchSampled = (id, ct) => { Interlocked.Increment(ref calls); throw new TaskCanceledException("the call timed out"); },
            FetchNext = (stages, ct) => { Interlocked.Increment(ref fetches); throw new TaskCanceledException("the call timed out"); },
        };
        await Run(new NodeEngine(s, P, o, new Dictionary<string, IStageHandler> { ["sample"] = new Rec(ran, "sample"), ["download"] = new Rec(ran, "download") }));
        Assert.Equal(["sample:v1", "sample:v2"], ran.ToArray());                         // the second sample ran: the first call's timeout did not end the node
        Assert.Equal(2, calls); Assert.True(fetches >= 1);                               // and the empty-queue hand-over's timeout did not either
    }
}
