using System.Text;
using Xunit;

namespace IndexNode.Tests;

public class StepJobTests
{
    static readonly Paths P = new("index/");

    static NodeEngine Eng(FakeStore s, string stage, Func<JobContext, Task> h, Func<JobDescriptor, bool>? can = null) => new(s, P,
        new EngineOptions { NodeId = "n-1", Stages = [stage], Jobs = new() { [stage] = 1 }, Once = true, PollInterval = TimeSpan.FromMilliseconds(20), CanRun = can },
        new Dictionary<string, IStageHandler> { [stage] = new Fh(h) });

    sealed class Fh(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext c) => f(c); }

    static Task Add(FakeStore s, string step, string id)
    {
        var j = JobDescriptor.NewStep(step, id);
        return s.PutAsync(P.Job(step, id), j.ToBytes(), Tags.Job(step, "pending", j.AddedAt), null, true, default);
    }

    [Fact]
    public void Step_jobs_know_their_dependencies()
    {
        Assert.Null(JobDescriptor.NewStep("frames", "v").Needs);
        Assert.Equal(["frames/manifest.json"], JobDescriptor.NewStep("sscd", "v").Needs);
        Assert.Equal(2, JobDescriptor.NewStep("cast", "v").Needs!.Length);
        Assert.Equal(2, JobDescriptor.NewStep("sync", "v").Needs!.Length);
        Assert.Equal(3, JobDescriptor.NewStep("describe", "v").Needs!.Length);
        Assert.Equal(["transcribe"], JobDescriptor.NewStep("transcribe", "v").Steps);
        Assert.Equal(Protocol.StepStages.Length + 1, Protocol.ExpandStages(["process"]).Length);
        Assert.Equal(["watch"], Protocol.ExpandStages(["watch"]));
    }

    [Fact]
    public async Task A_job_whose_inputs_are_missing_stays_pending_with_its_due_pushed_forward_and_is_not_run()
    {
        var s = new FakeStore(); await Add(s, "sync", "vid"); var ran = false;
        await Eng(s, "sync", _ => { ran = true; return Task.CompletedTask; }).RunAsync(default);
        Assert.False(ran);
        var n = P.Job("sync", "vid");
        Assert.Equal("pending", s.State(n));
        Assert.True(string.CompareOrdinal(s[n].Tags[Tags.Due], Protocol.NowUtc()) > 0);
        Assert.Null(s.LeaseOf(n));
        Assert.Equal(0, s.Job(n).Attempts);
    }

    [Fact]
    public async Task A_job_runs_once_its_inputs_exist()
    {
        var s = new FakeStore(); await Add(s, "sync", "vid"); var ran = false;
        foreach (var f in Protocol.NeedsOf("sync")) await s.PutAsync(P.Video("vid", f), [1], null, null, false, default);
        await Eng(s, "sync", _ => { ran = true; return Task.CompletedTask; }).RunAsync(default);
        Assert.True(ran); Assert.Equal("done", s.State(P.Job("sync", "vid")));
    }

    [Fact]
    public void A_step_follows_the_process_control_setting_unless_set_itself()
    {
        var c = new ControlFile { Stages = new() { ["process"] = "drain" } };
        Assert.Equal(Level.Drain, c.Effective("sscd"));
        c.Stages["sscd"] = "run";
        Assert.Equal(Level.Run, c.Effective("sscd"));
        Assert.Equal(Level.Run, new ControlFile().Effective("clip"));
    }

    [Fact]
    public void Jobs_spec_accepts_steps_and_a_bare_number_covers_them()
    {
        var j = NodeConfig.ParseJobs("sscd=1,clip=1,transcribe=2,ocrsubs=1");
        Assert.Equal(2, j["transcribe"]);
        Assert.Equal(3, NodeConfig.ParseJobs("3")["describe"]);
    }
}
