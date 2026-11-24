namespace IndexNode.Tests;

/// <summary>Behaviour that lost its (blob-based) tests when the queue replaced the job blobs: sources, priority, classify, comments, on the queue.</summary>
public class QueueBehaviourTests
{
    static readonly Paths P = new("index/");
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T12:00:00Z");
    static async Task<(FakeStore, JobQueue)> Q() { var s = new FakeStore(); var q = new JobQueue(s, P, "t1", () => Now); await q.LoadAsync(default); return (s, q); }
    static JobDescriptor Src(string id, string every = "1d") { var j = JobDescriptor.NewSource("channel", id, "N" + id, every, ["videos"], false, 0, 480); j.JobId = id; j.AddedAt = Protocol.Utc(Now); return j; }

    // ---- sources
    [Fact]
    public async Task Adding_a_source_makes_its_lookup_due_now_at_priority_500_and_a_repeat_is_refused()
    {
        var (s, q) = await Q();
        Assert.True(await Sources.AddAsync(q, Src("ch-1"), default)); Assert.False(await Sources.AddAsync(q, Src("ch-1"), default));
        var e = q.Read(st => st.Entry("ch-1", "refresh"))!; Assert.Equal("pending", e.State); Assert.Equal(Protocol.RefreshNowPriority, e.Priority); Assert.Equal(Protocol.Utc(Now), e.Due); Assert.Equal("lookup", e.Job!.Run);
    }

    [Fact]
    public async Task A_source_keeps_its_own_priority_for_its_videos_while_its_lookup_is_claimed_at_500()
    {
        // 0.13.46: found on 2026-11-24 with the owner's playlist added at priority 100: its videos were queued at 500
        var (s, q) = await Q();
        var src = Src("pl-1"); src.Priority = 100;
        await Sources.AddAsync(q, src, default);
        var e = q.Read(st => st.Entry("pl-1", "refresh"))!;
        Assert.Equal(Protocol.RefreshNowPriority, e.Priority); Assert.Equal(100, e.Job!.Priority);
        Assert.Equal(100, NodeEngine.BodyOf(e, "pl-1").Priority);                       // what the refresh stage reads, and what its videos get
        // the run ends: the entry goes back to the source's priority and its next refresh
        await q.AppendLinesAsync([new QLine { T = "retry", S = "pl-1", Stage = "refresh", Due = Protocol.Utc(Now.AddDays(1)) }, new QLine { T = "pri", S = "pl-1", Pri = 100 }], default);
        e = q.Read(st => st.Entry("pl-1", "refresh"))!; Assert.Equal(100, e.Priority); Assert.Equal(100, e.Job!.Priority);
        // made due by hand: claimed at 500 again, still a priority-100 source
        Assert.True(await Sources.TriggerAsync(q, "pl-1", "refresh", default, () => Now));
        e = q.Read(st => st.Entry("pl-1", "refresh"))!; Assert.Equal(Protocol.RefreshNowPriority, e.Priority); Assert.Equal(100, e.Job!.Priority);
        Assert.Equal(100, NodeEngine.BodyOf(e, "pl-1").Priority);
        // a node that starts later replays the same lines and sees the same
        var late = new JobQueue(s, P, "late", () => Now); await late.LoadAsync(default);
        Assert.Equal(100, late.Read(st => st.Entry("pl-1", "refresh"))!.Job!.Priority);
        // any other stage: the entry's priority is the job's, as before
        await q.AppendLinesAsync([QLine.Add("v1", "sample", new JobDescriptor { Stage = "sample", JobId = "v1", AddedAt = Protocol.Utc(Now) }, Protocol.Utc(Now), 100, Protocol.Utc(Now))], default);
        Assert.Equal(100, NodeEngine.BodyOf(q.Read(st => st.Entry("v1", "sample"))!, "v1").Priority);
    }

    [Fact]
    public async Task Setting_a_schedule_keeps_a_lookup_that_is_due_now_and_off_or_once_ends_a_finished_source()
    {
        var (s, q) = await Q();
        await Sources.AddAsync(q, Src("ch-1"), default);
        var j = await Sources.SetScheduleAsync(q, "ch-1", "6h", default, () => Now);
        Assert.Equal("6h", j.Schedule); Assert.Equal(Protocol.Utc(Now), q.Read(st => st.Entry("ch-1", "refresh"))!.Due);   // still due now
        await Assert.ThrowsAsync<ArgumentException>(() => Sources.SetScheduleAsync(q, "nope", "6h", default, () => Now));
        Assert.ThrowsAny<Exception>(() => Schedule.Parse("sometimes"));
    }

    [Fact]
    public async Task Trigger_makes_a_finished_source_due_now_at_500_and_all_due_lists_only_scheduled_pending_ones()
    {
        var (s, q) = await Q();
        await Sources.AddAsync(q, Src("ch-1"), default); await Sources.AddAsync(q, Src("ch-2", "off"), default);
        // both finish their lookup: ch-1 is pending again in a day, ch-2 (off) is done
        await q.AppendLinesAsync([new QLine { T = "done", S = "ch-1", Stage = "refresh", State = "pending", Due = Protocol.Utc(Now.AddDays(1)) }, new QLine { T = "pri", S = "ch-1", Pri = 0 }, new QLine { T = "done", S = "ch-2", Stage = "refresh" }], default);
        Assert.Empty(await Sources.DueAsync(q, () => Now));
        Assert.True(await Sources.TriggerAsync(q, "ch-2", "refresh", default, () => Now));
        var e = q.Read(st => st.Entry("ch-2", "refresh"))!; Assert.Equal("pending", e.State); Assert.Equal(Protocol.RefreshNowPriority, e.Priority); Assert.Equal("refresh", e.Job!.Run);
        Assert.Equal(["ch-1"], await Sources.DueAsync(q, () => Now.AddDays(2)));
    }

    [Fact]
    public async Task Import_adds_one_source_per_channel_and_skips_existing_and_bad_ids_and_takes_the_priority()
    {
        var (s, q) = await Q();
        var json = System.Text.Encoding.UTF8.GetBytes("[{\"channelId\":\"UC" + new string('a', 22) + "\",\"channelName\":\"A\"},{\"channelId\":\"bad\"}]");
        var r = await Sources.ImportAsync(q, json, "1d", ["videos"], 480, default, 3); Assert.Equal(new Sources.ImportResult(1, 0, 1), r);
        r = await Sources.ImportAsync(q, json, "1d", ["videos"], 480, default, 3); Assert.Equal(new Sources.ImportResult(0, 1, 1), r);
        await Assert.ThrowsAnyAsync<Exception>(() => Sources.ImportAsync(q, json, "1d", ["videos"], 480, default, 5000));   // out of range
    }

    // ---- priority
    [Fact]
    public async Task Priority_changes_only_open_entries_beats_age_and_an_out_of_range_value_changes_nothing()
    {
        var (s, q) = await Q();
        foreach (var (id, at) in new[] { ("old", "2026-11-01T00:00:00Z"), ("new", "2026-11-02T00:00:00Z"), ("fin", "2026-11-01T00:00:00Z") })
            await q.AppendLinesAsync([QLine.Add(id, "process", JobDescriptor.NewProcess(id), at)], default);
        await q.AppendLinesAsync([new QLine { T = "done", S = "fin", Stage = "process" }], default);
        var r = await PriorityTool.SetAsync(q, 9, ["new", "fin"], default);
        Assert.Equal(1, r.Changed); Assert.Single(r.Skipped);   // the finished one is reported, not changed
        Assert.Equal(9, q.Read(st => st.Entry("new", "process"))!.Priority); Assert.Equal(0, q.Read(st => st.Entry("fin", "process"))!.Priority);
        var order = q.Read(st => st.Candidates(Protocol.Utc(Now), _ => true)).Select(c => c.Subject.Id).ToList(); Assert.Equal(["new", "old"], order);   // priority beats age
        Assert.ThrowsAny<Exception>(() => PriorityTool.ParseOption("9999")); Assert.Equal(9, q.Read(st => st.Entry("new", "process"))!.Priority);
        await q.AppendLinesAsync([new QLine { T = "pri", S = "old", Pri = -5 }], default);
        Assert.Equal(["new", "old"], q.Read(st => st.Candidates(Protocol.Utc(Now), _ => true)).Select(c => c.Subject.Id).ToList());
    }

    // ---- classify
    [Fact]
    public async Task A_new_unclassified_download_creates_the_classify_singleton_once_and_decisions_move_it_on()
    {
        var (s, q) = await Q();
        var d = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=aaaaaaaaaaa", 480, null); d.JobId = "aaaaaaaaaaa"; d.AddedAt = Protocol.Utc(Now);
        Assert.True(await ClassifyJobs.CreateUnclassifiedAsync(q, d, default)); Assert.False(await ClassifyJobs.CreateUnclassifiedAsync(q, d, default));
        await ClassifyJobs.EnsureSingletonAsync(q, default); await ClassifyJobs.EnsureSingletonAsync(q, default);
        Assert.Equal(Tags.Unclassified, q.Read(st => st.Entry("aaaaaaaaaaa", "download"))!.State); Assert.Single(ClassifyJobs.ListUnclassified(q));
        Assert.Equal("pending", q.Read(st => st.Entry(ClassifyJobs.Subject, "classify"))!.State);
        // an unclassified download is never offered to a downloader
        Assert.DoesNotContain(q.Read(st => st.Candidates(Protocol.Utc(Now), _ => true)), c => c.Subject.Id == "aaaaaaaaaaa");
        await q.AppendLinesAsync([ClassifyJobs.Decision("aaaaaaaaaaa", d, true, "drama", "ok", Protocol.Utc(Now))], default);
        Assert.Equal("pending", q.Read(st => st.Entry("aaaaaaaaaaa", "download"))!.State); Assert.Contains("aaaaaaaaaaa", q.Read(st => st.Candidates(Protocol.Utc(Now), _ => true)).Select(c => c.Subject.Id));
        var d2 = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=bbbbbbbbbbb", 480, null); d2.JobId = "bbbbbbbbbbb"; d2.AddedAt = Protocol.Utc(Now); await ClassifyJobs.CreateUnclassifiedAsync(q, d2, default);
        await q.AppendLinesAsync([ClassifyJobs.Decision("bbbbbbbbbbb", d2, false, "music", "not a drama", Protocol.Utc(Now))], default);
        var e = q.Read(st => st.Entry("bbbbbbbbbbb", "download"))!; Assert.Equal("failed", e.State); Assert.True(e.Permanent); Assert.StartsWith("classify: music", e.LastError);
    }

    [Fact]
    public void Dead_videos_are_recognised_by_availability_or_by_a_placeholder_title_without_duration()
    {
        Assert.True(ClassifyJobs.IsUnavailable(new JobDescriptor { Availability = "Private" })); Assert.True(ClassifyJobs.IsUnavailable(new JobDescriptor { Title = "[Deleted video]" }));
        Assert.False(ClassifyJobs.IsUnavailable(new JobDescriptor { Title = "[Deleted video]", Duration = 100 })); Assert.False(ClassifyJobs.IsUnavailable(new JobDescriptor { Title = "Ep 1" }));
    }

    // ---- comments (part of the download since 0.13.0)
    [Fact]
    public async Task A_downloads_comments_failure_is_best_effort_and_the_download_still_finishes()
    {
        var s = new FakeStore(); var h = new Dictionary<string, IStageHandler> { ["download"] = new Fn(ctx => { ctx.Job.Title = "t"; return Task.CompletedTask; }) };
        await s.AddJob(P, "download", "vid1", "2026-11-20T08:00:00Z");
        await new NodeEngine(s, P, new EngineOptions { NodeId = "t", Name = "t", Stages = ["download"], Workers = 1, Threads = 1, Once = true, PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), ControlInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromSeconds(30) }, h).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("done", s.Replay(P.Prefix).Entry("vid1", "download")!.State);
    }
    sealed class Fn(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext c) => f(c); }
}
