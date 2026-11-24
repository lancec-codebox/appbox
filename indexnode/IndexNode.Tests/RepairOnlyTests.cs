using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.37: run --repair-only claims only stale versioned-step repairs (and actors / actors-index), never pending or missing process steps; whatif --repair-only uses the same rule.</summary>
public class RepairOnlyTests
{
    static readonly Paths P = new("index/");
    const string At = "2026-01-01T00:00:00Z";
    sealed class Rec(List<string> ran) : IStageHandler { public Task RunAsync(JobContext ctx) { lock (ran) ran.Add(ctx.BlobName + ":" + ctx.Stage); return Task.CompletedTask; } }

    static async Task SeedStale(FakeStore s, string id, long seq)
    {
        var q = new JobQueue(s, P, "seed"); await q.LoadAsync(default);
        await q.AppendLinesAsync([new QLine { T = "enter", S = id, Data = "full", At = At }, QLine.Add(id, "process", new JobDescriptor { Stage = "process", JobId = id }, At, null, null, "done"),
            new QLine { T = "fixsteps", S = id, Stage = "process", Done = NodeEngine.StandardSteps.ToArray(), At = At }, QLine.Add(id, "group", new JobDescriptor { Stage = "group", JobId = id }, At, null, null, "done"), new QLine { T = "seq", S = id, Seq = seq, At = At }], default);
        foreach (var z in new[] { "download", "cast", "centers", "frames", "sscd" }) await s.PutAsync(P.Video(id, z + ".zip"), [1], null, null, true, default);
    }
    /// <summary>A pending process entry whose sscd step is not done.</summary>
    static async Task SeedSscdPending(FakeStore s, string id)
    {
        var q = new JobQueue(s, P, "seed"); await q.LoadAsync(default);
        await q.AppendLinesAsync([new QLine { T = "enter", S = id, Data = "full", At = At }, QLine.Add(id, "process", new JobDescriptor { Stage = "process", JobId = id }, At),
            new QLine { T = "fixsteps", S = id, Stage = "process", Done = NodeEngine.StandardSteps.Where(x => x != "sscd").ToArray(), At = At }], default);
        await s.PutAsync(P.Video(id, "download.zip"), [1], null, null, true, default);
    }
    static EngineOptions Opts(bool repairOnly, bool once = true) => new()
    {
        NodeId = "n-1", Name = "n", Stages = ["process"], Jobs = new() { ["process"] = 1 }, Threads = 1, Once = once, RepairOnly = repairOnly, ControlOverride = c => WhatIf.Apply(c, ["versions.cast=2"]),
        Ready = new ReadyOptions(3, 10) { Code = _ => 2 }, PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(40), ControlInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromSeconds(30), Ops = new OpCounter(),
    };

    [Fact]
    public async Task Repair_only_claims_the_stale_cast_and_not_the_sscd_pending_upload()
    {
        var s = new FakeStore(); await SeedStale(s, "stale", 1); await SeedSscdPending(s, "pend");
        var ran = new List<string>();
        await new NodeEngine(s, P, Opts(true), Protocol.StepStages.Append("process").ToDictionary(x => x, x => (IStageHandler)new Rec(ran))).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(["stale"], ran.Select(x => x.Split(':')[0]).Distinct().ToList());
    }

    [Fact]
    public async Task Without_repair_only_both_are_claimed()
    {
        var s = new FakeStore(); await SeedStale(s, "stale", 1); await SeedSscdPending(s, "pend");
        var ran = new List<string>();
        using var cts = new CancellationTokenSource(); var o = Opts(false, once: false);
        var t = new NodeEngine(s, P, o, Protocol.StepStages.Append("process").ToDictionary(x => x, x => (IStageHandler)new Rec(ran))).RunAsync(cts.Token);
        await Task.Delay(2500); cts.Cancel(); try { await t.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { }
        Assert.Contains("pend", ran.Select(x => x.Split(':')[0])); Assert.Contains("stale", ran.Select(x => x.Split(':')[0]));
    }

    [Fact]
    public void Whatif_repair_only_matches_the_claim_rule()
    {
        var s = new FakeStore(); SeedStale(s, "stale", 1).Wait(); SeedSscdPending(s, "pend").Wait();
        var q = new JobQueue(s, P, "w"); q.LoadAsync(default).Wait();
        var ctl = WhatIf.Apply(new ControlFile(), ["versions.cast=2"]); var now = "2026-02-01T00:00:00Z";
        var ro = new ReadyOptions(3, 10) { Required = ctl.RequiredVersion, Code = _ => 2, RepairOnly = true };
        Assert.Equal(["stale"], q.Read(st => WhatIf.Run(st, "cast", ctl, now, ro)).First);
        Assert.Empty(q.Read(st => WhatIf.Run(st, "sscd", ctl, now, ro)).First);
        Assert.Contains("pend", q.Read(st => WhatIf.Run(st, "sscd", ctl, now, ro with { RepairOnly = false })).First);
    }
}
