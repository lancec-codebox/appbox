using Xunit;

namespace IndexNode.Tests;

public class DownloadClaimOrderTests
{
    private static readonly Paths P = new("index-test/");
    private sealed class FuncHandler(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }

    private static async Task Dl(FakeStore s, string id, string added, int priority, bool explicitSet = false, string? due = null)
    {
        var job = JobDescriptor.NewStep("download", id); job.AddedAt = added; job.Priority = priority; job.Explicit = explicitSet ? true : null;
        await s.PutAsync(P.Job("download", id), job.ToBytes(), Tags.Job("download", "pending", added, due, priority), null, true, default);
    }

    [Fact]
    public async Task Priority_jobs_are_claimed_before_older_priority_zero_downloads()
    {
        var s = new FakeStore(); var ran = new List<string>();
        for (int i = 0; i < 60; i++) await Dl(s, $"zero{i:D2}", $"2026-11-1{i % 4}T00:04:{i % 60:D2}Z", 0);   // older (and newer) priority-0 jobs
        foreach (var id in new[] { "wMR9KMkSDlk", "sIJVBqk0iWk", "X_grr4kOBV8", "IQ5--Js8DyM" }) await Dl(s, id, "2026-11-14T08:00:00Z", 100, explicitSet: true, due: "2026-11-14T08:00:00Z");
        var o = new EngineOptions { NodeId = "dl-abc123", Stages = ["download"], Jobs = new() { ["download"] = 1 }, Workers = 1, Once = true, MaxJobs = 4, PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(30), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40), TransientBackoff = _ => TimeSpan.Zero };
        await new NodeEngine(s, P, o, new Dictionary<string, IStageHandler> { ["download"] = new FuncHandler(ctx => { ran.Add(ctx.Job.JobId); return Task.CompletedTask; }) }).RunAsync(default).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["IQ5--Js8DyM", "X_grr4kOBV8", "sIJVBqk0iWk", "wMR9KMkSDlk"], ran.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.Equal(4, ran.Count);
    }
}
