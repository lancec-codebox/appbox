using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.37: stage start/stop marks in the media table (fakes only).</summary>
[Collection("SeqBlobs")]
public class StageMarksTests
{
    static readonly Paths P = new("index-test/");
    sealed class Fn(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    static readonly DateTimeOffset Srv = new(2026, 11, 23, 20, 15, 0, TimeSpan.Zero);

    static NodeEngine Engine(FakeStore s, Func<JobContext, Task> stage, StoreMeter? meter = null)
    {
        var o = new EngineOptions
        {
            NodeId = "test-node", Name = "test", Stages = ["download"], Workers = 1, Threads = 8, Once = true,
            PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
            BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(), Meter = meter ?? StoreMeter.Current,
        };
        var h = new Dictionary<string, IStageHandler>(); foreach (var st in new[] { "download", "refresh", "classify", "availability" }) h[st] = new Fn(stage);
        foreach (var st in Protocol.StepStages) h[st] = new Fn(_ => Task.CompletedTask);
        h["process"] = h["frames"];
        return new NodeEngine(s, P, o, h);
    }

    [Fact]
    public async Task A_done_stage_writes_the_stage_partition_and_the_upload_property_with_the_server_time_of_the_queue_append()
    {
        var s = new FakeStore { ServerClock = () => Srv }; var t = new FakeMediaTable(); MediaTables.Attach(s, t);
        await s.AddJob(P, "download", "vid1", "2026-11-20T08:00:00Z");
        await Engine(s, _ => Task.CompletedTask).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        var st = await t.GetAsync("stage-download", "vid1", null, default);
        Assert.NotNull(st);
        Assert.Equal("done", st!.Str("run_s")); Assert.Equal(1, (int)st.Props["v"]!); Assert.Equal("full", st.Str("mode")); Assert.Equal("test-node", st.Str("node"));
        Assert.Equal(Srv.UtcDateTime.ToString("o"), st.Str("at")); Assert.Equal(Srv.UtcDateTime.ToString("o"), st.Str("run_at"));
        var up = await t.GetAsync("upload", "vid1", null, default);
        var prop = up!.Section("download")!; Assert.Equal(1, (int)prop["v"]!); Assert.Equal("full", (string?)prop["mode"]); Assert.Equal(Srv.UtcDateTime.ToString("o"), (string?)prop["at"]);
        // a start (run_* only) and a done: 3 merges in all (start; done into the partition; done into the upload row)
        Assert.Equal(3, t.InsertOrMerges);
        Assert.Contains(t.Log, l => l.Op == "upsert" && l.P == "stage-download" && l.Props["run_s"]!.ToString() == "running" && l.Props["v"] == null);
    }

    [Fact]
    public async Task A_v2_run_that_fails_leaves_v1_in_the_stage_partition_and_in_the_upload_property()
    {
        var s = new FakeStore { ServerClock = () => Srv }; var t = new FakeMediaTable(); MediaTables.Attach(s, t);
        await StageMarks.DoneAsync(t, "download", "vid2", 1, "full", "old-node", 1, Srv.AddDays(-1), default);   // the stored output: v1
        await s.AddJob(P, "download", "vid2", "2026-11-20T08:00:00Z");
        var old = StepVersions.Code("download"); StepVersions.SetCode("download", 2);
        try { await Engine(s, _ => throw new InvalidOperationException("boom")).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { StepVersions.SetCode("download", old); }
        var st = await t.GetAsync("stage-download", "vid2", null, default)!;
        Assert.Equal(1, (int)st!.Props["v"]!); Assert.Equal("old-node", st.Str("node"));   // the stored output is untouched
        Assert.Equal("transient", st.Str("run_s")); Assert.Equal(2, (int)st.Props["run_v"]!);
        var up = await t.GetAsync("upload", "vid2", null, default);
        Assert.Equal(1, (int)up!.Section("download")!["v"]!);   // and so is the upload row's property
    }

    [Fact]
    public async Task A_failed_merge_is_retried_once_and_never_fails_the_step()
    {
        var s = new FakeStore { ServerClock = () => Srv }; var t = new FakeMediaTable(); MediaTables.Attach(s, t);
        int calls = 0; t.Fault = (p, r, props) => p == "stage-download" && props["run_s"]?.ToString() == "running" && calls++ == 0 ? new IOException("table down") : null;
        await s.AddJob(P, "download", "vid3", "2026-11-20T08:00:00Z");
        await Engine(s, _ => Task.CompletedTask).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("done", s.Replay(P.Prefix).Entry("vid3", "download")!.State);   // the step still finished
        Assert.Equal(2, calls);   // the start mark was tried twice
        Assert.Equal("done", (await t.GetAsync("stage-download", "vid3", null, default))!.Str("run_s"));
        // a table that stays down never fails the step either
        var s2 = new FakeStore { ServerClock = () => Srv }; var t2 = new FakeMediaTable { Fault = (p, r, props) => new IOException("table down") }; MediaTables.Attach(s2, t2);
        await s2.AddJob(P, "download", "vid4", "2026-11-20T08:00:00Z");
        await Engine(s2, _ => Task.CompletedTask).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("done", s2.Replay(P.Prefix).Entry("vid4", "download")!.State);
    }

    [Fact]
    public async Task The_table_merges_of_the_marks_count_in_the_steps_own_budget()
    {
        var s = new FakeStore { ServerClock = () => Srv }; var t = new FakeMediaTable(); MediaTables.Attach(s, new MeteredMediaTable(t, new StoreMeter()));
        var meter = new StoreMeter(); MediaTables.Attach(s, new MeteredMediaTable(t, meter));
        await s.AddJob(P, "download", "vid5", "2026-11-20T08:00:00Z");
        await Engine(s, _ => Task.CompletedTask, meter).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(meter.Table().Any(x => x.Op == "table" && x.Count >= 3), string.Join("; ", meter.Table().Select(x => $"{x.Site} {x.Op} x{x.Count}")));
    }
}
