using System.Text;
using Xunit;

namespace IndexNode.Tests;

[Collection("ort")]
public class Batch0101Tests
{
    private static readonly Paths P = new("index-test/");
    private sealed class FuncHandler(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    private static readonly string[] Steps = StageOrder.PassOrder;

    private static EngineOptions Opts(int batch = 6, double hours = 2) => new()
    {
        NodeId = "batch-abc123", Name = "batch", Stages = Steps, Workers = 1, Lanes = 2, Batch = batch, BatchHours = hours, Threads = 8, Once = true,
        PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(30), RegistryInterval = TimeSpan.FromMilliseconds(100),
        TransientBackoff = _ => TimeSpan.Zero, ControlInterval = TimeSpan.FromMilliseconds(40), HoldRenew = TimeSpan.FromMilliseconds(30), UnloadModels = () => { },
    };

    static NodeEngine Engine(FakeStore s, EngineOptions o, Func<JobContext, Task>? h = null) => new(s, P, o, Steps.ToDictionary(x => x, x => (IStageHandler)new FuncHandler(h ?? (async ctx =>
    {
        var file = ProcessStage.Outputs.First(y => y.Step == ctx.Stage).File;
        await ctx.Store.PutAsync(P.Video(ctx.Job.JobId, file), "{\"v\":1}"u8.ToArray(), null, null, false, ctx.Token);
        await ctx.Store.PutAsync(P.Video(ctx.Job.JobId, ctx.Stage + ".zip"), [1], null, null, false, ctx.Token);
    }))));

    static async Task Job(FakeStore s, string step, string id, string added, string[]? needs)
    {
        var job = JobDescriptor.NewStep(step, id); job.AddedAt = added; job.Needs = needs;
        await s.PutAsync(P.Job(step, id), job.ToBytes(), Tags.Job(step, "pending", added, null, 0), null, true, default);
    }

    [Fact]
    public void Legacy_needs_map_to_the_stage_zips()
    {
        Assert.Equal("frames.zip", Protocol.NormalizeNeed("frames/manifest.json")); Assert.Equal("frames.zip", Protocol.NormalizeNeed("keyframes/manifest.json"));
        Assert.Equal("sscd.zip", Protocol.NormalizeNeed("sscd/manifest.json")); Assert.Equal("transcribe.zip", Protocol.NormalizeNeed("asr/sensevoice.zh.json"));
        Assert.Equal("ocrsubs.zip", Protocol.NormalizeNeed("ocr/lines.json")); Assert.Equal("cast.zip", Protocol.NormalizeNeed("cast/clusters.json"));
        Assert.Equal("describe.zip", Protocol.NormalizeNeed("describe.json")); Assert.Equal("frames.zip", Protocol.NormalizeNeed("frames.zip"));
        Assert.Equal("download.zip", Protocol.NormalizeNeed("info.json"));
        Assert.Equal(["frames.zip", "sscd.zip"], Protocol.NormalizeNeeds(["frames/manifest.json", "keyframes/manifest.json", "sscd/manifest.json"]));
    }

    [Fact]
    public async Task A_job_that_still_names_a_loose_file_runs_once_its_zip_exists()
    {
        var s = new FakeStore(); var ran = new List<string>();
        await Job(s, "frames", "d", "2026-11-17T10:00:00Z", null);
        await Job(s, "sscd", "d", "2026-11-17T10:00:00Z", ["frames/manifest.json"]);   // written by 0.9.0
        await s.PutAsync(P.Video("d", "download.zip"), [1], null, null, false, default);
        await Engine(s, Opts(), async ctx => { ran.Add(ctx.Stage); await ctx.Store.PutAsync(P.Video("d", ctx.Stage + ".zip"), [1], null, null, false, default); }).RunAsync(default).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["frames", "sscd"], ran);
    }

    [Fact]
    public async Task Selection_goes_past_twenty_ineligible_dramas_to_the_eligible_one()
    {
        var s = new FakeStore(); var ran = new List<string>();
        for (int i = 0; i < 30; i++) await Job(s, "frames", $"old{i:D2}", $"2026-11-17T09:00:{i:D2}Z", ["info.json.never"]);   // older, never eligible
        await Job(s, "frames", "good", "2026-11-17T11:00:00Z", null);
        await Engine(s, Opts(), async ctx => { ran.Add(ctx.Job.JobId); await ctx.Store.PutAsync(P.Video(ctx.Job.JobId, "frames.zip"), [1], null, null, false, default); }).RunAsync(default).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["good"], ran);
    }

    [Fact]
    public async Task The_duration_comes_from_download_zips_summary()
    {
        var s = new FakeStore();
        await s.PutAsync(P.Video("long", "download.zip"), [1], null, null, false, default); s.Blobs[P.Video("long", "download.zip")].Meta["idx_summary"] = "{\"durationSeconds\":5400}";
        await s.PutAsync(P.Video("short", "download.zip"), [1], null, null, false, default); s.Blobs[P.Video("short", "download.zip")].Meta["idx_summary"] = "{\"durationSeconds\":60}";
        foreach (var id in new[] { "long", "short" }) await Job(s, "frames", id, "2026-11-17T10:00:0" + (id == "long" ? 0 : 1) + "Z", null);
        var o = Opts(hours: 1);   // 3600 s: the 5400 s drama fills the batch alone, so the short one waits for the next batch
        var ran = new List<string>();
        await Engine(s, o, async ctx => { ran.Add(ctx.Job.JobId); await ctx.Store.PutAsync(P.Video(ctx.Job.JobId, "frames.zip"), [1], null, null, false, default); }).RunAsync(default).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("long", ran[0]);
    }

    [Fact]
    public async Task Explain_names_each_reason_and_the_picks_and_writes_nothing()
    {
        var s = new FakeStore();
        await Job(s, "frames", "blocked", "2026-11-17T09:00:00Z", ["asr/sensevoice.zh.json"]);
        await Job(s, "frames", "fine", "2026-11-17T10:00:00Z", null);
        await s.PutAsync(P.Video("loose", "info.json"), [1], null, null, false, default); await Job(s, "frames", "loose", "2026-11-17T09:30:00Z", null);
        var before = s.Blobs.Keys.OrderBy(k => k).ToList(); var lines = new List<string>();
        var z = new StageZipStore(s, Path.Combine(Path.GetTempPath(), "x-" + Guid.NewGuid().ToString("N")[..6]));
        var rc = await new NodeEngine(z, P, Opts(), Steps.ToDictionary(x => x, x => (IStageHandler)new FuncHandler(_ => Task.CompletedTask))).ExplainAsync(50, lines.Add, default);
        Assert.Equal(0, rc);
        Assert.Contains(lines, l => l.Contains("blocked") && l.Contains("-> no")); Assert.Contains(lines, l => l.Contains("need asr/sensevoice.zh.json missing") && l.Contains("transcribe.zip"));
        Assert.Contains(lines, l => l.Contains("loose 0.9 layout")); Assert.Contains(lines, l => l.Contains("batch picks") && l.Contains("fine") && !l.Contains("blocked"));
        Assert.Equal(before, s.Blobs.Keys.OrderBy(k => k).ToList());   // read-only: no hold, no claim
    }

    [Fact]
    public async Task A_failing_pending_list_is_logged_not_swallowed()
    {
        var sw = new StringWriter(); var old = Log.Out; Log.Out = sw;
        try
        {
            var picks = await StageOrder.DramasAsync(["frames"], (st, c) => throw new InvalidOperationException("boom"), (st, b, c) => Task.FromResult(true), default);
            Assert.Empty(picks); Assert.Contains("boom", sw.ToString()); Assert.Contains("[frames]", sw.ToString());
        }
        finally { Log.Out = old; }
    }
}
