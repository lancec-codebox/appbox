using System.Text;
using System.Text.Json.Nodes;

namespace IndexNode.Tests;

/// <summary>0.13.2 live fixes: a step with no old job is not done, the pre-check gate and the final done read the store, repair-steps, no Shorts tab, search-index claimed first.</summary>
[Collection("console")]
public class RepairStepsTests
{
    static readonly Paths P = new("index-test/");
    sealed class Fn(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-20T12:00:00Z");

    static async Task Blob(FakeStore s, string stage, string id, string state)
    {
        var j = new JobDescriptor { Stage = stage, JobId = id, AddedAt = "2026-11-01T00:00:00Z" };
        await s.PutAsync(P.Job(stage, id), j.ToBytes(), Tags.Job(stage, state, j.AddedAt, null, 0), null, false, default);
    }

    [Fact]
    public async Task Migration_does_not_count_a_step_with_no_old_job_as_done_unless_its_zip_is_on_the_store()
    {
        var s = new FakeStore();
        // part-way drama: frames and sscd done, thumbs pending; centers never had a job (they were only made after sscd) and has no zip
        foreach (var st in new[] { "frames", "sscd" }) await Blob(s, st, "p1", "done");
        await Blob(s, "thumbs", "p1", "pending");
        // the same, but its centers.zip exists (a node made it outside a job)
        foreach (var st in new[] { "frames", "sscd" }) await Blob(s, st, "p2", "done");
        await Blob(s, "thumbs", "p2", "pending"); await s.PutAsync(P.Video("p2", "centers.zip"), [1], null, null, false, default);
        var (state, rep) = await Migration.BuildAsync(s, P, "2026-11-20T00:00:00Z", default);
        var d1 = state.Entry("p1", "process")!.StepsDone!; Assert.Contains("frames", d1); Assert.Contains("sscd", d1); Assert.DoesNotContain("centers", d1);   // never ran
        Assert.Contains("centers", state.Entry("p2", "process")!.StepsDone!);   // its output is there
        Assert.DoesNotContain("thumbs", d1);
    }

    static EngineOptions Opts(int? maxJobs = null) => new()
    {
        MaxJobs = maxJobs,
        NodeId = "n1", Name = "n1", Stages = ["process"], Workers = 1, Threads = 4, Once = true, SearchIndexCatchUp = false, Now = () => T0,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
        TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(),
    };

    static Dictionary<string, IStageHandler> Handlers(List<string> order, Func<string, bool>? makesZip = null)
    {
        var h = new Dictionary<string, IStageHandler>();
        foreach (var st in new[] { "download", "refresh", "classify", "availability" }) h[st] = new Fn(_ => Task.CompletedTask);
        foreach (var st in Protocol.StepStages) h[st] = new Fn(async ctx => { order.Add(ctx.Job.JobId + ":" + ctx.Stage); if ((makesZip?.Invoke(ctx.Stage) ?? true) && st is not ("describe" or "translate" or "sync")) await ctx.Store.PutAsync(P.Video(ctx.Job.JobId, st + ".zip"), [1], null, null, false, default); });
        h["process"] = h["frames"];
        h["precheck"] = new Fn(ctx => { order.Add(ctx.Job.JobId + ":precheck"); return PreCheckStage.RecordAsync(ctx.Store, P, ctx.Job.JobId, JsonNode.Parse("{\"decision\":\"new\"}")!.AsObject(), default); });
        return h;
    }

    static async Task Downloaded(FakeStore s, string id) { await s.PutAsync(P.Video(id, "download.zip"), [1, 2, 3], null, null, false, default); await VideoJsonSeed.PutAsync(s, P, id, 600); }

    [Fact]
    public async Task The_precheck_runs_for_a_drama_whose_done_set_is_not_empty_but_whose_store_has_no_processing_outputs_and_a_claimed_done_step_without_its_zip_runs_again()
    {
        var s = new FakeStore(); await Downloaded(s, "v1");
        await s.AddJob(P, "process", "v1", Protocol.Utc(T0.AddDays(-1)));
        var q = new JobQueue(s, P, "t", () => T0); await q.LoadAsync(default);
        await q.AppendLinesAsync([new QLine { T = "fixsteps", S = "v1", Stage = "process", Done = ["centers"] }], default);   // the migration's wrong state: centers "done", nothing on the store
        var order = new List<string>();
        await new NodeEngine(s, P, Opts(), Handlers(order)).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("v1:precheck", order[0]);                      // decided from the store, not the done set
        Assert.Contains("v1:centers", order);                        // a done step with no zip runs
        Assert.Equal("done", s.Replay(P.Prefix).Entry("v1", "process")!.State);
    }

    [Fact]
    public async Task The_final_done_is_refused_while_a_standard_steps_output_is_missing()
    {
        var s = new FakeStore(); await Downloaded(s, "v2"); await s.AddJob(P, "process", "v2", Protocol.Utc(T0.AddDays(-1)));
        var order = new List<string>();
        await new NodeEngine(s, P, Opts(), Handlers(order, st => st != "centers")).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));   // centers "runs" but leaves no zip
        var e = s.Replay(P.Prefix).Entry("v2", "process")!;
        Assert.NotEqual("done", e.State); Assert.Contains("centers.zip", e.LastError ?? "");
    }

    [Fact]
    public async Task Repair_steps_dry_run_counts_and_the_real_run_fixes_the_179_style_and_40_style_subjects_in_batched_lines()
    {
        var s = new FakeStore(); var q = new JobQueue(s, P, "cli", () => T0); await q.LoadAsync(default);
        var lines = new List<QLine>();
        for (int i = 0; i < 6; i++)   // 179-style: only centers listed as done, nothing on the store
        {
            var id = "only" + i; var j = JobDescriptor.NewProcess(id); j.AddedAt = "2026-11-19T00:00:00Z";
            lines.Add(QLine.Add(id, "process", j, j.AddedAt)); lines.Add(new QLine { T = "fixsteps", S = id, Stage = "process", Done = ["centers"] });
        }
        for (int i = 0; i < 3; i++)   // 40-style: a finished process entry with every output but centers.zip
        {
            var id = "fin" + i; var j = JobDescriptor.NewProcess(id); j.AddedAt = "2026-11-19T00:00:00Z";
            lines.Add(QLine.Add(id, "process", j, j.AddedAt)); lines.Add(new QLine { T = "done", S = id, Stage = "process", Fin = true, At = Protocol.Utc(T0) });
            foreach (var st in NodeEngine.StandardSteps.Where(x => x is not ("centers" or "sync"))) await s.PutAsync(P.Video(id, st + ".zip"), [1], null, null, false, default);
        }
        var okj = JobDescriptor.NewProcess("good"); okj.AddedAt = "2026-11-19T00:00:00Z"; lines.Add(QLine.Add("good", "process", okj, okj.AddedAt)); lines.Add(new QLine { T = "fixsteps", S = "good", Stage = "process", Done = ["frames"] });
        await s.PutAsync(P.Video("good", "frames.zip"), [1], null, null, false, default);   // consistent: left alone
        await q.AppendLinesAsync(lines, default);
        int appends = s.Ops.GetValueOrDefault("append");
        var dry = await RepairSteps.RunAsync(q, s, P, true, default);
        Assert.Equal((10, 9, 9, 3), (dry.Subjects, dry.Fixed, dry.StepsRemoved, dry.Reopened)); Assert.Equal(9, dry.PerStep["centers"]);
        Assert.Equal(appends, s.Ops.GetValueOrDefault("append"));   // dry run writes nothing
        var rep = await RepairSteps.RunAsync(q, s, P, false, default);
        Assert.Equal(dry.Fixed, rep.Fixed); Assert.Equal(1, s.Ops.GetValueOrDefault("append") - appends);   // one append for the batch
        var st2 = s.Replay(P.Prefix);
        Assert.Empty(st2.Entry("only0", "process")!.StepsDone!); Assert.Equal("pending", st2.Entry("only0", "process")!.State);
        var f = st2.Entry("fin0", "process")!; Assert.Equal("pending", f.State); Assert.DoesNotContain("centers", f.StepsDone!); Assert.Contains("frames", f.StepsDone!);   // re-opened with only the missing step
        Assert.Equal(["frames"], st2.Entry("good", "process")!.StepsDone);
        Assert.Equal(0, (await RepairSteps.RunAsync(q, s, P, true, default)).Fixed);   // idempotent
    }

    // ------------------------------------------------------------------ shorts
    [Fact]
    public async Task A_channel_with_no_shorts_tab_refreshes_cleanly_and_the_default_tabs_are_videos_only()
    {
        Assert.Equal(["videos"], Sources.ParseTabs(null));
        var s = new FakeStore { Clock = () => T0 }; var q = new JobQueue(s, P, "n1", () => T0); await q.LoadAsync(default);
        var calls = new List<string>();
        var tools = new Tools(new HttpClient()) { YtDlpOverride = (args, _, _) => { var url = args[^1]; calls.Add(url); return Task.FromResult(url.EndsWith("/shorts") ? new ProcResult(1, "", "ERROR: This channel does not have a shorts tab") : new ProcResult(0, "abcdefghij1\tNA\tNA\n", "")); } };
        var job = JobDescriptor.NewSource("channel", "UCaaaaaaaaaaaaaaaaaaaaaa", "c", "1h", ["videos", "shorts"], false, 0, 360);   // an existing source that still lists both tabs
        var ctx = new JobContext { Job = job, Stage = "refresh", Store = s, Paths = P, NodeId = "n1", Token = default, ThreadShare = () => 1, Report = (_, _) => { }, Queue = q };
        await new RefreshStage(tools, s, () => T0).RunAsync(ctx);   // no exception: the missing tab is an empty listing
        Assert.Contains(calls, c => c.EndsWith("/shorts")); Assert.Equal(1, q.Read(st => st.Subjects.Values.Count(v => v.Entries.ContainsKey("download"))));
        // and the batched set-tabs moves every existing source to videos
        var a = JobDescriptor.NewSource("channel", "UCbbbbbbbbbbbbbbbbbbbbbb", "b", "1h", ["videos", "shorts"], false, 0, 360); var b2 = JobDescriptor.NewSource("playlist", "PLx", "p", "1h", ["videos", "shorts"], false, -1, 360);
        await Sources.AddBatchAsync(q, [a, b2], default); int ap = s.Ops.GetValueOrDefault("append");
        Assert.Equal(2, await Sources.SetTabsAsync(q, ["videos"], true, default)); Assert.Equal(ap, s.Ops.GetValueOrDefault("append"));
        Assert.Equal(2, await Sources.SetTabsAsync(q, ["videos"], false, default)); Assert.Equal(1, s.Ops.GetValueOrDefault("append") - ap);
        Assert.All(Sources.List(q).Where(j => j.JobId is "ch-UCbbbbbbbbbbbbbbbbbbbbbb" or "pl-PLx"), j => Assert.Equal(["videos"], j.Tabs));
        Assert.Equal(0, await Sources.SetTabsAsync(q, ["videos"], false, default));
    }

    // ------------------------------------------------------------------ search-index first
    [Fact]
    public async Task A_due_search_index_entry_is_claimed_before_fifty_older_process_entries_and_the_dramas_then_resume_in_their_usual_order()
    {
        var s = new FakeStore();
        for (int i = 0; i < 50; i++) { var id = "d" + i.ToString("D3"); await s.AddJob(P, "process", id, Protocol.Utc(T0.AddDays(-1).AddMinutes(i))); await Downloaded(s, id); }
        var q0 = new JobQueue(s, P, "t", () => T0); await q0.LoadAsync(default);
        await q0.AppendLinesAsync([SearchIndexJobs.AddLine(q0.Read(st => st), T0.AddMinutes(-20), TimeSpan.Zero)!], default);   // due now, added today
        var order = new List<string>();
        var h = Handlers(order); h["search-index"] = new Fn(ctx => { order.Add("search-index"); return Task.CompletedTask; });
        var o = Opts(4);
        await new NodeEngine(s, P, o, h).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("search-index", order[0]);
        Assert.Equal(["d000", "d001", "d002"], order.Where(x => x.StartsWith("d")).Select(x => x[..4]).Distinct().ToList());
    }
}
