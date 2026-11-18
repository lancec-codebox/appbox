using Xunit;

namespace IndexNode.Tests;

/// <summary>Azure's Find Blobs by Tags returns only the tags the where expression names (the FakeStore does the same now). Claims sorted what they got and saw no priority and no date: blob-name order.</summary>
[Collection("ort")]
public class FindOnlyQueriedTagsTests
{
    private static readonly Paths P = new("index-test/");
    private sealed class FuncHandler(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }

    private static async Task Add(FakeStore s, string stage, string id, string added, int priority)
    {
        var job = JobDescriptor.NewStep(stage, id); job.AddedAt = added; job.Priority = priority;
        await s.PutAsync(P.Job(stage, id), job.ToBytes(), Tags.Job(stage, "pending", added, null, priority), null, true, default);
    }

    [Fact]
    public async Task The_fake_returns_only_the_queried_tags()
    {
        var s = new FakeStore(); await Add(s, "download", "a", "2026-11-14T00:00:00Z", 100);
        var plain = await s.FindByTagsAsync(Tags.PendingWhere("download"), default);
        Assert.DoesNotContain(Tags.Priority, plain[0].Tags.Keys); Assert.DoesNotContain(Tags.Added, plain[0].Tags.Keys);
        var full = await Tags.FindFullAsync(s, Tags.PendingWhere("download", Protocol.NowUtc()), default);
        Assert.Equal("100", full[0].Tags[Tags.Priority]); Assert.Equal("2026-11-14T00:00:00Z", full[0].Tags[Tags.Added]);
    }

    [Fact]
    public async Task Full_find_merges_priorities_dates_and_negative_priorities()
    {
        var s = new FakeStore();
        await Add(s, "download", "n", "2026-11-14T03:00:00Z", -10); await Add(s, "download", "z", "2026-11-14T02:00:00Z", 0); await Add(s, "download", "p", "2026-11-14T01:00:00Z", 100);
        var order = Tags.ClaimOrder(await Tags.FindFullAsync(s, Tags.PendingWhere("download", Protocol.NowUtc()), default)).Select(b => Path.GetFileNameWithoutExtension(b.Name)).ToList();
        Assert.Equal(["p", "z", "n"], order);
    }

    private static EngineOptions Opts(string[] stages, int batch = 1) => new()
    {
        NodeId = "claim-abc123", Stages = stages, Jobs = stages.ToDictionary(x => x, _ => 1), Workers = 1, Lanes = 1, Batch = batch, Threads = 2, Once = true, MaxJobs = 1,
        PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(30), RegistryInterval = TimeSpan.FromMilliseconds(100),
        ControlInterval = TimeSpan.FromMilliseconds(40), HoldRenew = TimeSpan.FromMilliseconds(30), TransientBackoff = _ => TimeSpan.Zero, UnloadModels = () => { },
    };

    private static async Task<List<string>> Run(FakeStore s, string stage, EngineOptions o)
    {
        var ran = new List<string>();
        await new NodeEngine(s, P, o, o.Stages.ToDictionary(x => x, x => (IStageHandler)new FuncHandler(ctx => { ran.Add(ctx.Job.JobId); return ctx.Store.PutAsync(P.Video(ctx.Job.JobId, ctx.Stage + ".zip"), [1], null, null, false, ctx.Token); })))
            .RunAsync(default).WaitAsync(TimeSpan.FromSeconds(30));
        return ran;
    }

    [Theory]
    [InlineData("download")]
    [InlineData("comments")]
    public async Task A_non_batch_stage_claims_the_priority_job_added_last_and_named_last_first(string stage)
    {
        var s = new FakeStore();
        for (int i = 0; i < 20; i++) await Add(s, stage, $"a{i:D2}", $"2026-11-14T00:00:{i:D2}Z", 0);
        await Add(s, stage, "zzz", "2026-11-18T00:00:00Z", 100);
        Assert.Equal(["zzz"], await Run(s, stage, Opts([stage])));
    }

    [Fact]
    public async Task The_oldest_job_beats_a_newer_one_at_equal_priority()
    {
        var s = new FakeStore();
        await Add(s, "download", "aaa", "2026-11-18T00:00:00Z", 0); await Add(s, "download", "zzz", "2026-11-14T00:00:00Z", 0);
        Assert.Equal(["zzz"], await Run(s, "download", Opts(["download"])));
    }

    [Fact]
    public async Task Batch_selection_takes_the_priority_drama_and_the_oldest_otherwise()
    {
        var s = new FakeStore();
        for (int i = 0; i < 12; i++) { await Add(s, "frames", $"a{i:D2}", $"2026-11-14T00:00:{i:D2}Z", 0); await s.PutAsync(P.Video($"a{i:D2}", "download.zip"), [1], null, null, false, default); }
        await Add(s, "frames", "zzz", "2026-11-18T00:00:00Z", 100); await s.PutAsync(P.Video("zzz", "download.zip"), [1], null, null, false, default);
        Assert.Equal(["zzz"], await Run(s, "frames", Opts(["frames"])));
        var s2 = new FakeStore();
        await Add(s2, "frames", "aaa", "2026-11-18T00:00:00Z", 0); await Add(s2, "frames", "zzz", "2026-11-14T00:00:00Z", 0);
        foreach (var id in new[] { "aaa", "zzz" }) await s2.PutAsync(P.Video(id, "download.zip"), [1], null, null, false, default);
        Assert.Equal(["zzz"], await Run(s2, "frames", Opts(["frames"])));
    }
}
