namespace IndexNode.Tests;

public class LimitsTests
{
    private static DateTime T(int day, int h, int m = 0) => new(2026, 11, day, h, m, 0, DateTimeKind.Local);

    // ---------------------------------------------------------------- cron

    [Fact]
    public void Cron_fields_lists_ranges_and_steps()
    {
        var c = CronExpr.Parse("*/15 8-10 * * 1,3");
        Assert.True(c.Matches(T(16, 9, 30)));   // Monday
        Assert.True(c.Matches(T(18, 8, 0)));    // Wednesday
        Assert.False(c.Matches(T(17, 9, 30)));  // Tuesday
        Assert.False(c.Matches(T(16, 9, 31)));
        Assert.False(c.Matches(T(16, 11, 0)));
    }

    [Theory]
    [InlineData("* * * *")] [InlineData("61 * * * *")] [InlineData("* 24 * * *")] [InlineData("a * * * *")] [InlineData("*/0 * * * *")]
    public void Cron_rejects_bad_expressions(string s) => Assert.Throws<ArgumentException>(() => CronExpr.Parse(s));

    [Fact]
    public void Sunday_is_0_or_7() { Assert.True(CronExpr.Parse("0 0 * * 7").Matches(T(15, 0))); Assert.True(CronExpr.Parse("0 0 * * 0").Matches(T(15, 0))); }

    [Fact]
    public void Window_inside_and_outside_a_same_day_window()
    {
        var c = CronExpr.Parse("0 1 * * *"); var w = TimeSpan.FromHours(3);
        Assert.Equal(T(14, 1), c.WindowStart(T(14, 1), w));
        Assert.Equal(T(14, 1), c.WindowStart(T(14, 3, 59), w));
        Assert.Null(c.WindowStart(T(14, 4), w));
        Assert.Null(c.WindowStart(T(14, 0, 59), w));
        Assert.Equal(T(15, 1), c.Next(T(14, 4)));
    }

    [Fact]
    public void A_window_can_cross_midnight()
    {
        var c = CronExpr.Parse("0 23 * * *"); var w = TimeSpan.FromHours(3);
        Assert.NotNull(c.WindowStart(T(14, 23, 30), w));
        Assert.Equal(T(14, 23), c.WindowStart(T(15, 0, 30), w));   // after midnight, a window that started yesterday
        Assert.Equal(T(14, 23), c.WindowStart(T(15, 1, 59), w));
        Assert.Null(c.WindowStart(T(15, 2, 0), w));
        Assert.Null(c.WindowStart(T(15, 22, 59), w));
    }

    // ---------------------------------------------------------------- budget

    private static string TempState() => Path.Combine(Path.GetTempPath(), "indexnode-test-" + Guid.NewGuid().ToString("N"), "state.json");

    private static void Finish(LimitState l, string stage = "process") { Assert.True(l.TryAcquire(stage)); l.Release(stage, true); }

    [Fact]
    public void Budget_counts_down_per_finished_job_then_says_budget_spent()
    {
        var l = new LimitState("n", new NodeLimits { Budget = 2 }, "", TempState());
        Assert.Null(l.Status());
        Finish(l); Assert.Equal(1, l.Remaining);
        Finish(l); Assert.Equal(0, l.Remaining);
        Assert.False(l.TryAcquire("process"));
        Assert.Equal("budget spent", l.Status());
    }

    [Fact]
    public void Failed_or_unclaimed_attempts_do_not_use_budget_and_in_flight_jobs_reserve_it()
    {
        var l = new LimitState("n", new NodeLimits { Budget = 1 }, "", TempState());
        Assert.True(l.TryAcquire("download"));
        Assert.False(l.TryAcquire("download"));        // the only unit is in flight
        l.Release("download", false);                    // it failed
        Assert.Equal(1, l.Remaining);
        Assert.True(l.TryAcquire("download")); l.Release("download", true);
        Assert.Equal(0, l.Remaining);
    }

    [Fact]
    public void Watch_polls_are_not_counted_and_not_blocked_by_the_budget()
    {
        var l = new LimitState("n", new NodeLimits { Budget = 1 }, "", TempState());
        Finish(l);
        Assert.False(l.TryAcquire("process"));
        Assert.True(l.TryAcquire("watch")); l.Release("watch", true);
        Assert.Equal(0, l.Remaining);
    }

    [Fact]
    public void The_count_survives_a_restart_with_the_same_setting()
    {
        var path = TempState();
        var a = new LimitState("n", new NodeLimits { Budget = 3 }, "t1", path);
        Finish(a);
        var b = new LimitState("n", new NodeLimits { Budget = 3 }, "t1", path);
        Assert.Equal(2, b.Remaining);
    }

    [Fact]
    public void Setting_the_budget_again_restarts_the_count()
    {
        var path = TempState();
        Finish(new LimitState("n", new NodeLimits { Budget = 3 }, "t1", path));
        Assert.Equal(3, new LimitState("n", new NodeLimits { Budget = 3 }, "t2", path).Remaining);   // set again later (new stamp)
        Assert.Equal(5, new LimitState("n", new NodeLimits { Budget = 5 }, "t2", path).Remaining);   // new value
    }

    [Fact]
    public void A_control_override_wins_and_restarts_the_count_when_its_value_or_changedAt_changes()
    {
        var path = TempState();
        var l = new LimitState("node-1", new NodeLimits { Budget = 10 }, "t", path);
        Assert.Equal(10, l.Remaining);
        var ctl = new ControlFile { ChangedAt = "2026-11-14T10:00:00Z", Nodes = new() { ["node-1"] = new NodeLimits { Budget = 2 } } };
        l.Apply(ctl);
        Assert.Equal(2, l.Remaining);
        Finish(l); Assert.Equal(1, l.Remaining);
        l.Apply(ctl);                                    // same override re-read: no reset
        Assert.Equal(1, l.Remaining);
        ctl.ChangedAt = "2026-11-14T11:00:00Z";          // same value, new changedAt: restart
        l.Apply(ctl);
        Assert.Equal(2, l.Remaining);
        Finish(l);
        ctl.Nodes!["node-1"].Budget = 4; l.Apply(ctl);   // new value: restart
        Assert.Equal(4, l.Remaining);
        ctl.Nodes.Remove("node-1"); l.Apply(ctl);        // override removed: back to the local setting
        Assert.Equal(10, l.Remaining);
        // another node's override does not touch this one
        l.Apply(new ControlFile { Nodes = new() { ["other"] = new NodeLimits { Budget = 1 } } });
        Assert.Equal(10, l.Budget);
    }

    [Fact]
    public void An_override_without_a_budget_still_overrides_the_schedule_per_field()
    {
        var now = T(14, 12);
        var l = new LimitState("n", new NodeLimits { Budget = 5, Schedule = "0 1 * * *", Window = "2h" }, "", TempState(), () => now);
        Assert.StartsWith("next window ", l.Status());
        l.Apply(new ControlFile { Nodes = new() { ["n"] = new NodeLimits { Schedule = "0 12 * * *" } } });
        Assert.Null(l.Status());
        Assert.Equal(5, l.Budget);
    }

    // ---------------------------------------------------------------- schedule

    [Fact]
    public void Outside_a_window_nothing_new_is_claimed_and_the_registry_shows_the_next_window()
    {
        var now = T(14, 12);
        var l = new LimitState("n", new NodeLimits { Schedule = "0 23 * * *", Window = "3h" }, "", null, () => now);
        Assert.False(l.TryAcquire("process")); Assert.False(l.TryAcquire("watch"));
        Assert.Equal("next window " + Protocol.Utc(new DateTimeOffset(T(14, 23))), l.Status());
        now = T(15, 0, 30);   // inside the window that started at 23:00
        Assert.True(l.TryAcquire("process")); Assert.Null(l.Status());
    }

    [Fact]
    public async Task The_engine_claims_nothing_outside_the_window_and_stops_after_the_budget()
    {
        var P = new Paths("index-test/");
        var s = new FakeStore();
        for (int i = 0; i < 4; i++) await s.AddJob(P, "download", "vid" + i, $"2026-11-13T19:0{i}:00Z");
        int ran = 0;
        EngineOptions Opts(LimitState l) => new()
        {
            NodeId = "n-1", Name = "n", Stages = ["download"], Jobs = new() { ["download"] = 1 }, Threads = 1, Once = true, Limits = l,
            PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(40), ControlInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromSeconds(30),
        };
        IReadOnlyDictionary<string, IStageHandler> H() => new Dictionary<string, IStageHandler> { ["download"] = new Fn(() => Interlocked.Increment(ref ran)) };
        var closed = new LimitState("n-1", new NodeLimits { Schedule = "0 23 * * *", Window = "1h" }, "", null, () => T(14, 12));
        await new NodeEngine(s, P, Opts(closed), H()).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, ran);
        var two = new LimitState("n-1", new NodeLimits { Budget = 2 }, "", null);
        // Once stops a worker at its first empty claim, so run until the budget is what stops it
        for (int i = 0; i < 4; i++) await new NodeEngine(s, P, Opts(two), H()).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, ran);
        Assert.Equal("budget spent", two.Status());
    }

    private sealed class Fn(Action a) : IStageHandler { public Task RunAsync(JobContext ctx) { a(); return Task.CompletedTask; } }

}
