using IndexNode;
using Xunit;

namespace IndexNode.Tests;

public class PriorityTests
{
    private static readonly Paths P = new("index-test/");
    private static string T(int minute) => $"2026-11-13T19:{minute:D2}:00Z";

    private static async Task Add(FakeStore s, string stage, string id, string added, int priority = 0, string? rawTag = null)
    {
        var url = "https://www.youtube.com/watch?v=" + id.PadRight(11, 'x');
        var job = stage == "download" ? JobDescriptor.NewDownload(url, 480, null) : JobDescriptor.NewStep(stage, id);
        job.JobId = id; job.AddedAt = added; job.Priority = priority;
        var tags = Tags.Job(stage, "pending", added, null, priority);
        if (rawTag != null) tags[Tags.Priority] = rawTag;
        await s.PutAsync(P.Job(stage, id), job.ToBytes(), tags, null, true, default);
    }

    private static EngineOptions Opts() => new()
    {
        NodeId = "test-abc123", Name = "test", Stages = ["download"], Jobs = new() { ["download"] = 1 }, Threads = 8, Once = true,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100),
        BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, ControlInterval = TimeSpan.FromMilliseconds(40),
    };

    private static EngineOptions OptsFor(string[] stages) => new()
    {
        NodeId = "test-abc123", Name = "test", Stages = stages, Jobs = stages.ToDictionary(x => x, _ => 1), Threads = 8, Once = true,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100),
        BotBackoff = TimeSpan.FromHours(1), TransientBackoff = _ => TimeSpan.Zero, ControlInterval = TimeSpan.FromMilliseconds(40),
    };

    private static async Task<List<string>> ClaimOrder(FakeStore s)
    {
        var order = new List<string>();
        var e = EngineTests.Engine(s, Opts(), download: ctx => { order.Add(ctx.Job.JobId); return Task.CompletedTask; });
        await e.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        return order;
    }

    [Fact]
    public async Task Priority_beats_age_ties_fall_back_to_age_then_name_and_negative_goes_last()
    {
        var s = new FakeStore();
        await Add(s, "download", "old0", T(0));
        await Add(s, "download", "newhigh", T(9), 5);
        await Add(s, "download", "midhigh", T(5), 5);
        await Add(s, "download", "tieB", T(3), 5);
        await Add(s, "download", "tieA", T(3), 5);
        await Add(s, "download", "low", T(1), -1);
        await Add(s, "download", "lowest", T(0), -50);
        Assert.Equal(["tieA", "tieB", "midhigh", "newhigh", "old0", "low", "lowest"], await ClaimOrder(s));
    }

    [Fact]
    public async Task A_missing_or_unparsable_tag_counts_as_0()
    {
        var s = new FakeStore();
        await Add(s, "download", "plain", T(2));
        await Add(s, "download", "junk", T(1), 0, "high");
        await Add(s, "download", "huge", T(0), 0, "99999"); // out of range: 0 as well
        await Add(s, "download", "up", T(9), 1);
        Assert.Equal(["up", "huge", "junk", "plain"], await ClaimOrder(s));
        Assert.Equal(0, Tags.PriorityOf(new Dictionary<string, string> { [Tags.Priority] = "" }));
        Assert.Equal(-7, Tags.PriorityOf(new Dictionary<string, string> { [Tags.Priority] = "-7" }));
    }

    [Fact]
    public async Task A_job_waiting_on_an_earlier_step_is_skipped_even_when_it_has_priority()
    {
        var s = new FakeStore();
        await Add(s, "download", "d1", T(0));
        var step = JobDescriptor.NewStep("describe", "hi"); step.Priority = 9; step.AddedAt = T(1);
        Assert.NotNull(step.Needs);
        await s.PutAsync(P.Job("describe", "hi"), step.ToBytes(), Tags.Job("describe", "pending", T(1), null, 9), null, true, default);
        var order = new List<string>();
        var o = OptsFor(["download", "describe"]);
        var e = new NodeEngine(s, P, o, new Dictionary<string, IStageHandler> { ["download"] = new H(c => order.Add(c.Job.JobId)), ["describe"] = new H(c => order.Add("describe")) });
        await e.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["d1"], order); // describe waits for its needs; it stays pending and keeps its priority
        Assert.Equal("pending", s.State(P.Job("describe", "hi"))); Assert.Equal("9", s[P.Job("describe", "hi")].Tags[Tags.Priority]);
    }

    private sealed class H(Action<JobContext> f) : IStageHandler { public Task RunAsync(JobContext c) { f(c); return Task.CompletedTask; } }

    [Fact]
    public void Priority_tag_is_only_written_when_not_0_and_must_be_in_range()
    {
        Assert.False(Tags.Job("download", "pending", T(0)).ContainsKey(Tags.Priority));
        Assert.Equal("-3", Tags.Job("download", "pending", T(0), null, -3)[Tags.Priority]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Tags.Job("download", "pending", T(0), null, 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => Tags.CheckPriority(-1001));
        Assert.Equal(1000, Tags.CheckPriority(1000));
        Assert.Throws<ArgumentException>(() => PriorityTool.ParseOption("x"));
        Assert.Equal(0, PriorityTool.ParseOption(null));
    }

    // ---------------------------------------------------------------- inheritance

    private static JobContext Ctx(FakeStore s, JobDescriptor job, string stage) => new()
    {
        Job = job, Stage = stage, BlobName = P.Job(stage, job.JobId), LeaseId = "L", Worker = 1, Store = s, Paths = P, NodeId = "n-1", Token = default,
        ThreadShare = () => 1, Report = (_, _) => { },
    };

    [Fact]
    public async Task A_downloads_step_jobs_and_comments_job_carry_its_priority()
    {
        var s = new FakeStore();
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=abcdefghij1", 480, null); job.Priority = 7;
        await new DownloadStage(new Tools(new HttpClient()), s, new DownloadGate((_, _) => Task.CompletedTask)).EnsureFollowUpJobs(Ctx(s, job, "download"));
        var cn = P.Job("comments", "abcdefghij1");
        Assert.Equal("7", s[cn].Tags[Tags.Priority]); Assert.Equal(7, s.Job(cn).Priority);
        foreach (var step in Protocol.IngestStepStages) { var n = P.Job(step, "abcdefghij1"); Assert.Equal("7", s[n].Tags[Tags.Priority]); Assert.Equal(7, s.Job(n).Priority); }
        // priority 0: no tag
        var j0 = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=zzzzzzzzzz1", 480, null);
        await new DownloadStage(new Tools(new HttpClient()), s, new DownloadGate((_, _) => Task.CompletedTask)).EnsureFollowUpJobs(Ctx(s, j0, "download"));
        Assert.False(s[P.Job("sscd", "zzzzzzzzzz1")].Tags.ContainsKey(Tags.Priority));
    }

    [Fact]
    public async Task The_engine_keeps_the_tag_when_a_job_is_rewritten_and_follow_on_jobs_inherit_through_the_chain()
    {
        var s = new FakeStore();
        await Add(s, "download", "keepme", T(0), 4);
        await EngineTests.Engine(s, Opts()).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("done", s.State(P.Job("download", "keepme"))); Assert.Equal("4", s[P.Job("download", "keepme")].Tags[Tags.Priority]);
        // a legacy process job fanning out carries the priority to every step job
        var legacy = JobDescriptor.NewProcess("legacy1"); legacy.Priority = 3;
        Assert.Equal(3, JobDescriptor.Parse(legacy.ToBytes()).Priority);
    }

    [Fact]
    public async Task A_sources_priority_reaches_its_downloads_playlist_poll()
    {
        var s = new FakeStore();
        var watch = JobDescriptor.NewWatch("playlist", "PLp", "pl", "1h", ["videos"], false, 2, 360); watch.Priority = 12;
        var name = P.Job("watch", watch.JobId);
        await s.PutAsync(name, watch.ToBytes(), Tags.Job("watch", "pending", watch.AddedAt, null, 12), null, false, default);
        var tools = new Tools(new HttpClient()) { YtDlpOverride = (args, _, _) => Task.FromResult(new ProcResult(0, "aaaaaaaaaaa\tNA\tNA\nbbbbbbbbbbb\tNA\tNA\n", "")) };
        var ctx = new JobContext { Job = watch, Stage = "watch", BlobName = name, LeaseId = "l", Worker = 1, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new WatchStage(tools, s, () => new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)).RunAsync(ctx);
        foreach (var id in new[] { "aaaaaaaaaaa", "bbbbbbbbbbb" }) { Assert.Equal("12", s[P.Job("download", id)].Tags[Tags.Priority]); Assert.Equal(12, s.Job(P.Job("download", id)).Priority); }
    }

    [Fact]
    public async Task Importing_sources_with_a_priority_stores_it_on_each_source()
    {
        var s = new FakeStore();
        var json = System.Text.Encoding.UTF8.GetBytes("[{\"channelId\":\"UCabcdefghijklmnopqrstuv\",\"channelName\":\"A\"}]");
        await Sources.ImportAsync(s, P, json, "1d", ["videos"], 480, default, 20);
        var n = P.Job("watch", "ch-UCabcdefghijklmnopqrstuv");
        Assert.Equal("20", s[n].Tags[Tags.Priority]); Assert.Equal(20, s.Job(n).Priority);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sources.ImportAsync(s, P, json, "1d", ["videos"], 480, default, 5000));
    }

    // ---------------------------------------------------------------- priority set

    private static async Task<FakeStore> Mixed()
    {
        var s = new FakeStore();
        await Add(s, "download", "pend1", T(0)); await Add(s, "download", "pend2", T(1)); await Add(s, "download", "leased1", T(2)); await Add(s, "download", "done1", T(3)); await Add(s, "download", "fail1", T(4));
        await s.AcquireLeaseAsync(P.Job("download", "leased1"), 60, default);
        s[P.Job("download", "done1")].Tags[Tags.State] = "done"; s[P.Job("download", "fail1")].Tags[Tags.State] = "failed";
        return s;
    }

    [Fact]
    public async Task Set_changes_only_pending_jobs_and_reports_why_the_others_were_skipped()
    {
        var s = await Mixed();
        var ids = new[] { "pend1", "pend2", "leased1", "done1", "fail1", "ghost" };
        var r = await PriorityTool.SetAsync(s, P, 9, ids.Select(i => ("download", i)), default);
        Assert.Equal(2, r.Changed);
        Assert.Equal(new Dictionary<string, int> { ["leased"] = 1, ["done"] = 1, ["failed"] = 1, ["no such job"] = 1 }, r.Skipped);
        foreach (var id in new[] { "pend1", "pend2" }) { Assert.Equal("9", s[P.Job("download", id)].Tags[Tags.Priority]); Assert.Equal(9, s.Job(P.Job("download", id)).Priority); Assert.Null(s.LeaseOf(P.Job("download", id))); }
        foreach (var id in new[] { "leased1", "done1", "fail1" }) Assert.False(s[P.Job("download", id)].Tags.ContainsKey(Tags.Priority));
        // back to 0 removes the tag
        await PriorityTool.SetAsync(s, P, 0, [("download", "pend1")], default);
        Assert.False(s[P.Job("download", "pend1")].Tags.ContainsKey(Tags.Priority));
    }

    [Fact]
    public async Task Set_with_an_out_of_range_value_is_rejected_and_changes_nothing()
    {
        var s = await Mixed();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PriorityTool.SetAsync(s, P, 1001, [("download", "pend1")], default));
        Assert.False(s[P.Job("download", "pend1")].Tags.ContainsKey(Tags.Priority));
        var a = Args.Parse(["set", "-1001", "--job", "pend1"]);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => PriorityTool.RunAsync(a, s, P));
    }

    [Fact]
    public async Task Set_with_steps_also_retags_the_pending_step_jobs_of_the_same_videos()
    {
        var s = new FakeStore();
        await Add(s, "download", "vid1", T(0)); await Add(s, "sscd", "vid1", T(0)); await Add(s, "thumbs", "vid1", T(0)); await Add(s, "sscd", "other", T(0));
        s[P.Job("thumbs", "vid1")].Tags[Tags.State] = "done";
        var o = new StringWriter(); var old = Console.Out; Console.SetOut(o);
        try { Assert.Equal(0, await PriorityTool.RunAsync(Args.Parse(["set", "5", "--job", "vid1", "--with-steps"]), s, P)); } finally { Console.SetOut(old); }
        Assert.Equal("5", s[P.Job("sscd", "vid1")].Tags[Tags.Priority]); Assert.Equal("5", s[P.Job("download", "vid1")].Tags[Tags.Priority]);
        Assert.False(s[P.Job("thumbs", "vid1")].Tags.ContainsKey(Tags.Priority)); Assert.False(s[P.Job("sscd", "other")].Tags.ContainsKey(Tags.Priority));
        Assert.Contains("2 job(s) changed to priority 5", o.ToString()); Assert.Contains("1 done", o.ToString());
    }

    [Fact]
    public async Task Set_by_channel_finds_the_downloads_the_channel_created_and_from_reads_a_file_of_ids()
    {
        var s = new FakeStore();
        foreach (var (id, src) in new[] { ("c1", "ch-UCabcdefghijklmnopqrstuv"), ("c2", "ch-UCabcdefghijklmnopqrstuv"), ("x1", "ch-other") })
        {
            await Add(s, "download", id, T(0)); var j = s.Job(P.Job("download", id)); j.Source = src;
            await s.PutAsync(P.Job("download", id), j.ToBytes(), s[P.Job("download", id)].Tags, null, false, default);
        }
        var o = new StringWriter(); var old = Console.Out; Console.SetOut(o);
        try { await PriorityTool.RunAsync(Args.Parse(["set", "4", "--channel", "https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv"]), s, P); } finally { Console.SetOut(old); }
        Assert.Equal("4", s[P.Job("download", "c1")].Tags[Tags.Priority]); Assert.Equal("4", s[P.Job("download", "c2")].Tags[Tags.Priority]); Assert.False(s[P.Job("download", "x1")].Tags.ContainsKey(Tags.Priority));
        var f = Path.Combine(Path.GetTempPath(), "prio-ids-" + Guid.NewGuid().ToString("N") + ".txt"); await File.WriteAllLinesAsync(f, ["# ids", "x1", ""]);
        try { var o2 = new StringWriter(); Console.SetOut(o2); await PriorityTool.RunAsync(Args.Parse(["set", "-2", "--from", f]), s, P); Console.SetOut(old); } finally { Console.SetOut(old); File.Delete(f); }
        Assert.Equal("-2", s[P.Job("download", "x1")].Tags[Tags.Priority]);
    }

    [Fact]
    public async Task List_shows_pending_jobs_in_claim_order()
    {
        var s = new FakeStore();
        await Add(s, "download", "a", T(0)); await Add(s, "download", "b", T(5), 3); await Add(s, "download", "c", T(1), -1);
        var rows = await PriorityTool.ListAsync(s, P, "download", 2, default);
        Assert.Equal(["b", "a"], rows.Select(r => r.Id)); Assert.Equal(3, rows[0].Priority);
    }

    [Fact]
    public async Task Enqueue_priority_writes_the_tag()
    {
        var s = new FakeStore();
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=abcdefghij1", 480, null); job.Priority = PriorityTool.ParseOption("15");
        await s.PutAsync(P.Job("download", job.JobId), job.ToBytes(), Tags.Job("download", "pending", job.AddedAt, null, job.Priority), null, true, default);
        Assert.Equal("15", s[P.Job("download", job.JobId)].Tags[Tags.Priority]);
    }

    // ---------------------------------------------------------------- source priority

    private static async Task<JobDescriptor> Watch(FakeStore s, string id, int priority = 0)
    {
        var w = JobDescriptor.NewWatch("playlist", id, "pl", "1h", ["videos"], false, 0, 360); w.Priority = priority;
        await s.PutAsync(P.Job("watch", w.JobId), w.ToBytes(), Tags.Job("watch", "pending", w.AddedAt, null, priority), null, true, default);
        return w;
    }

    private static async Task Video(FakeStore s, string id, params string[] sources)
    {
        await Add(s, "download", id, T(0));
        await Add(s, "comments", id, T(0)); await Add(s, "sscd", id, T(0)); await Add(s, "thumbs", id, T(0));
        if (sources.Length > 0)
        {
            var j = s.Job(P.Job("download", id)); j.Source = sources[0]; j.Sources = sources.ToList();
            await s.PutAsync(P.Job("download", id), j.ToBytes(), s[P.Job("download", id)].Tags, null, false, default);
        }
    }

    static string Tag(FakeStore s, string stage, string id) => s[P.Job(stage, id)].Tags.GetValueOrDefault(Tags.Priority) ?? "";

    [Fact]
    public async Task Source_priority_retags_its_pending_jobs_with_steps_and_skips_leased_done_failed()
    {
        var s = new FakeStore(); var w = await Watch(s, "PLa");
        await Video(s, "vid1", w.JobId); await Video(s, "vid2", w.JobId); await Video(s, "other");
        await s.AcquireLeaseAsync(P.Job("sscd", "vid1"), 60, default);
        s[P.Job("thumbs", "vid1")].Tags[Tags.State] = "done"; s[P.Job("comments", "vid2")].Tags[Tags.State] = "failed";
        var r = await PriorityTool.SetSourceAsync(s, P, w.JobId, 8, null, default);
        Assert.Equal(2, r.Videos);
        Assert.Equal("8", Tag(s, "download", "vid1")); Assert.Equal("8", Tag(s, "sscd", "vid2")); Assert.Equal("8", Tag(s, "thumbs", "vid2")); Assert.Equal("8", Tag(s, "comments", "vid1"));
        Assert.Equal("", Tag(s, "sscd", "vid1")); Assert.Equal("", Tag(s, "thumbs", "vid1")); Assert.Equal("", Tag(s, "comments", "vid2")); // leased, done, failed
        Assert.Equal("", Tag(s, "download", "other"));
        Assert.Equal(new Dictionary<string, int> { ["leased"] = 1, ["done"] = 1, ["failed"] = 1 }, r.Skipped);
        Assert.Equal("8", Tag(s, "watch", w.JobId)); Assert.Equal(8, s.Job(P.Job("watch", w.JobId)).Priority);
    }

    [Fact]
    public async Task A_video_in_several_sources_gets_the_highest_priority()
    {
        var s = new FakeStore(); var a = await Watch(s, "PLa", 3); var b = await Watch(s, "PLb", 9);
        await Video(s, "shared", a.JobId, b.JobId); await Video(s, "onlya", a.JobId);
        await PriorityTool.SetSourceAsync(s, P, a.JobId, 5, null, default); // source a: 3 -> 5; the video also belongs to b (9)
        Assert.Equal("9", Tag(s, "download", "shared")); Assert.Equal("9", Tag(s, "sscd", "shared"));
        Assert.Equal("5", Tag(s, "download", "onlya"));
        await PriorityTool.SetSourceAsync(s, P, b.JobId, 20, null, default);
        Assert.Equal("20", Tag(s, "download", "shared"));
    }

    [Fact]
    public async Task An_explicit_set_wins_until_the_source_priority_is_changed_again()
    {
        var s = new FakeStore(); var w = await Watch(s, "PLa");
        await Video(s, "vid1", w.JobId);
        await PriorityTool.SetSourceAsync(s, P, w.JobId, 4, null, default);
        await PriorityTool.SetAsync(s, P, 50, [("download", "vid1")], default); // explicit
        Assert.Equal("50", Tag(s, "download", "vid1")); Assert.True(s.Job(P.Job("download", "vid1")).Explicit);
        await PriorityTool.SetSourceAsync(s, P, w.JobId, 6, null, default); // the source changes: it takes over again
        Assert.Equal("6", Tag(s, "download", "vid1")); Assert.Null(s.Job(P.Job("download", "vid1")).Explicit);
    }

    [Fact]
    public async Task Older_jobs_without_a_recorded_source_are_matched_by_the_listing()
    {
        var s = new FakeStore(); var w = await Watch(s, "PLa");
        await Video(s, "old1"); await Video(s, "notlisted");
        var r = await PriorityTool.SetSourceAsync(s, P, w.JobId, 7, ["old1", "zzz"], default);
        Assert.Equal(1, r.Matched); Assert.Equal("7", Tag(s, "download", "old1")); Assert.Equal("7", Tag(s, "sscd", "old1")); Assert.Equal("", Tag(s, "download", "notlisted"));
        Assert.Contains(w.JobId, s.Job(P.Job("download", "old1")).Sources!);
        // without a listing they are left alone
        await Video(s, "old2");
        await PriorityTool.SetSourceAsync(s, P, w.JobId, 8, null, default);
        Assert.Equal("", Tag(s, "download", "old2"));
    }

    [Fact]
    public async Task A_direct_video_takes_its_own_priority_and_enqueue_records_it()
    {
        var s = new FakeStore();
        await Video(s, "direct1");
        var rr = await PriorityTool.SetSourceAsync(s, P, "v-direct1", 11, null, default);
        Assert.Equal(1, rr.Videos); Assert.Equal("11", Tag(s, "download", "direct1")); Assert.Equal("11", Tag(s, "sscd", "direct1")); Assert.Equal(11, s.Job(P.Job("download", "direct1")).OwnPriority);
        Assert.Equal("v-abcdefghij1", PriorityTool.SourceOrVideoId("https://www.youtube.com/watch?v=abcdefghij1"));
        Assert.Equal("ch-UCabcdefghijklmnopqrstuv", PriorityTool.SourceOrVideoId("https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv"));
    }

    [Fact]
    public async Task New_downloads_of_a_watch_record_their_source()
    {
        var s = new FakeStore();
        var watch = JobDescriptor.NewWatch("playlist", "PLq", "pl", "1h", ["videos"], false, 1, 360); watch.Priority = 2;
        var name = P.Job("watch", watch.JobId);
        await s.PutAsync(name, watch.ToBytes(), Tags.Job("watch", "pending", watch.AddedAt, null, 2), null, false, default);
        var tools = new Tools(new HttpClient()) { YtDlpOverride = (args, _, _) => Task.FromResult(new ProcResult(0, "qqqqqqqqqqq\tNA\tNA\n", "")) };
        var ctx = new JobContext { Job = watch, Stage = "watch", BlobName = name, LeaseId = "l", Worker = 1, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new WatchStage(tools, s, () => new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)).RunAsync(ctx);
        Assert.Equal([watch.JobId], s.Job(P.Job("download", "qqqqqqqqqqq")).Sources);
        Assert.Contains("priority", Sources.FormatRow(watch));
    }
}
