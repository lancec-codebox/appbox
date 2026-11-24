namespace IndexNode.Tests;

/// <summary>0.13.18: attribution per call site, the op breaker (trip, reset), the failure breaker, the per-step budget, the negative cache and the control.json kill switch.</summary>
public class StorageBreakerTests
{
    static readonly Paths P = new("index-test/");
    sealed class Clock { public DateTimeOffset T = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero); public DateTimeOffset Now() => T; }
    sealed class Fn(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    static StoreMeter Meter(Clock c, int warn = 3000, int max = 10000, int step = 20000, int fails = 100) => new(c.Now) { Limits = new StorageLimits { WarnPerMin = warn, MaxPerMin = max, MaxPerStep = step, MaxFailuresPerMin = fails, Cooldown = TimeSpan.FromMinutes(5) } };

    static async Task ReadMissing(IStore s, int n) { for (int i = 0; i < n; i++) await s.GetAsync("nope/" + i, default); }
    static async Task HeadOne(IStore s) => await s.HeadAsync("x", default);

    [Fact]
    public async Task Every_op_is_counted_per_call_site_and_op_type_with_failures_apart()
    {
        var m = Meter(new Clock()); var s = new MeteredStore(new FakeStore(), m);
        await s.PutAsync("a", [1], null, null, false, default);
        await ReadMissing(s, 3); await HeadOne(s); await HeadOne(s);
        await s.ListAsync("", false, false, default);
        var t = m.Table();
        var g = t.Single(r => r.Site.Contains("ReadMissing") && r.Op == "get"); Assert.Equal(3, g.Count);
        var gf = t.Single(r => r.Site.Contains("ReadMissing") && r.Op == "get!404"); Assert.Equal(3, gf.Failed); Assert.StartsWith("nope/", gf.Example);
        Assert.Equal(2, t.Single(r => r.Site.Contains("HeadOne") && r.Op == "head").Count);
        Assert.Contains(t, r => r.Op == "put" && r.Site.Contains("Every_op_is_counted")); Assert.Contains(t, r => r.Op == "list");
        Assert.Contains("ReadMissing", m.TopLine()); Assert.Contains("ReadMissing", m.TableText());
    }

    [Fact]
    public async Task An_explicit_tag_names_the_site()
    {
        var m = Meter(new Clock()); var s = new MeteredStore(new FakeStore(), m);
        using (StoreMeter.Tag("my-helper")) await s.HeadAsync("x", default);
        Assert.Contains(m.Table(), r => r.Site == "my-helper");
    }

    [Fact]
    public async Task The_breaker_trips_at_max_per_min_names_the_top_sites_and_resets_after_the_cooldown()
    {
        var c = new Clock(); var m = Meter(c, warn: 5, max: 20); var s = new MeteredStore(new FakeStore(), m);
        await Assert.ThrowsAsync<StorageBudgetExceeded>(async () => { for (int i = 0; i < 100; i++) await s.HeadAsync("x" + i, default); });
        Assert.True(m.Tripped);
        var e = await Assert.ThrowsAsync<StorageBudgetExceeded>(() => s.HeadAsync("again", default));
        Assert.Contains("HeadOne", "HeadOne" + e.Message); Assert.Contains("max-per-min", e.Message); Assert.Contains("Top sites", e.Message);
        c.T = c.T.AddMinutes(4); Assert.True(m.Tripped);
        c.T = c.T.AddMinutes(2); Assert.False(m.Tripped);
        await s.HeadAsync("fine", default);   // closed: calls go through again
    }

    [Fact]
    public async Task Ops_older_than_a_minute_leave_the_window()
    {
        var c = new Clock(); var m = Meter(c, max: 10); var s = new MeteredStore(new FakeStore(), m);
        for (int round = 0; round < 5; round++) { for (int i = 0; i < 8; i++) await s.HeadAsync("x", default); c.T = c.T.AddSeconds(61); }
        Assert.False(m.Tripped); Assert.Equal(40, m.Total);
    }

    [Fact]
    public async Task The_failure_breaker_trips_on_404s_and_names_failing_sites_with_an_example_blob()
    {
        var c = new Clock(); var m = Meter(c, fails: 10); var s = new MeteredStore(new FakeStore(), m);
        var e = await Assert.ThrowsAsync<StorageBudgetExceeded>(async () => { for (int i = 0; i < 100; i++) await s.GetAsync("missing/" + i, default); });
        Assert.Contains("max-failures-per-min", e.Message); Assert.Contains("missing/", e.Message); Assert.True(m.Tripped);
    }

    [Fact]
    public async Task A_step_over_its_budget_throws_with_its_own_site_table_and_the_next_step_starts_clean()
    {
        var c = new Clock(); var m = Meter(c, step: 7); var s = new MeteredStore(new FakeStore(), m);
        using (m.BeginStep())
        {
            var e = await Assert.ThrowsAsync<StorageBudgetExceeded>(async () => { for (int i = 0; i < 50; i++) await s.HeadAsync("x", default); });
            Assert.Contains("max-per-step 7", e.Message); Assert.Contains("HeadAsync", e.Message + "HeadAsync");
            Assert.Equal(7, m.StepTable().Where(r => !r.Op.Contains('!')).Sum(r => r.Count));
        }
        Assert.True(m.Tripped);   // 0.13.30: a runaway step opens the node's breaker: the node stops claiming
        m.Reset();
        using (m.BeginStep()) { await s.HeadAsync("x", default); Assert.Equal(1, m.StepOps); }
    }

    [Fact]
    public async Task The_negative_cache_remembers_a_404_until_something_says_the_name_exists()
    {
        var c = new Clock(); var n = new NegativeCache { Now = c.Now };
        Assert.False(n.IsMissing("a")); n.Missing("a"); Assert.True(n.IsMissing("a")); Assert.True(n.IsMissing("a"));
        n.ExistAll(["a"]); Assert.False(n.IsMissing("a"));
        n.Missing("b"); c.T = c.T.AddSeconds(61); Assert.False(n.IsMissing("b"));   // not for longer than a minute
        n.Missing("c"); n.Exists("c"); Assert.False(n.IsMissing("c")); await Task.CompletedTask;
    }

    [Fact]
    public async Task A_failed_step_budget_is_an_ordinary_retryable_failure()
    {
        var f = new FakeStore(); var m = Meter(new Clock(), step: 5); var s = new MeteredStore(f, m);
        await f.AddJob(P, "download", "v1", "2026-11-21T08:00:00Z");
        var h = new Dictionary<string, IStageHandler> { ["download"] = new Fn(async ctx => { for (int i = 0; i < 100; i++) await ctx.Store.HeadAsync("x", ctx.Token); })   /* 0.13.41: over the download stage's own budget (60) */ };
        await new NodeEngine(s, P, Opts(m), h).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        var e = f.Replay(P.Prefix).Entry("v1", "download")!;
        Assert.NotEqual("done", e.State); Assert.True(e.Attempts >= 1, "attempts " + e.Attempts); Assert.Contains("max-per-step", e.LastError); Assert.True(m.Tripped, "tripped");   // an ordinary failure: counted as an attempt and retried (zero backoff here, so the attempts run out)
    }

    [Fact]
    public async Task An_open_breaker_stops_claims_and_the_kill_switch_stops_every_node_within_a_poll()
    {
        var f = new FakeStore(); await f.AddJob(P, "download", "v1", "2026-11-21T08:00:00Z");
        int ran = 0; var h = new Dictionary<string, IStageHandler> { ["download"] = new Fn(_ => { ran++; return Task.CompletedTask; }) };
        var m = Meter(new Clock(), max: 5); try { for (int i = 0; i < 10; i++) await new MeteredStore(f, m).HeadAsync("x", default); } catch (StorageBudgetExceeded) { }
        Assert.True(m.Tripped);
        await new NodeEngine(f, P, Opts(m), h).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, ran);   // breaker open: no claim
        // the kill switch
        var m2 = Meter(new Clock()); await f.PutAsync(P.Control, new ControlFile { Storage = "stop" }.ToBytes(), null, null, false, default);
        await new NodeEngine(f, P, Opts(m2), h).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, ran);
        await f.PutAsync(P.Control, new ControlFile { Storage = null }.ToBytes(), null, null, false, default);
        await new NodeEngine(f, P, Opts(m2), h).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, ran);
    }

    static EngineOptions Opts(StoreMeter m) => new()
    {
        NodeId = "test-node", Name = "test", Stages = ["download"], Workers = 1, Threads = 8, Once = true, PollInterval = TimeSpan.FromMilliseconds(30),
        RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
        BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(), Meter = m,
    };
}
