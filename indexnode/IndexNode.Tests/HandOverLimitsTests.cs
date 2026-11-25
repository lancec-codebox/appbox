using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.48: two things a download node on the work queue got wrong on 2026-11-24: the service's hand-over ignored the node's budget, and a control change never reached the node.</summary>
public class HandOverLimitsTests
{
    static readonly Paths P = new("index/");
    static string TempState() => Path.Combine(Path.GetTempPath(), "indexnode-test-" + Guid.NewGuid().ToString("N"), "state.json");

    static EngineOptions Opts(LimitState limits, List<string[]> asked) => new()
    {
        NodeId = "test-node", Name = "test", Stages = ["sample", "download"], Workers = 1, Threads = 2, Once = true,
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
        Assert.NotEmpty(asked); Assert.All(asked, a => Assert.Equal(["sample"], a));   // 0.13.47 asked for download too, and the service handed one over
        Assert.True(limits.TryAcquire("sample")); limits.Release("sample", true);       // and the one sample

        asked.Clear();
        await Run(new NodeEngine(new FakeStore(), P, Opts(limits, asked), Handlers()));
        Assert.Empty(asked);                                                   // nothing the limits allow: the service is not asked at all (an empty list would mean any stage)
    }
}
