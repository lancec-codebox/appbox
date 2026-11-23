using System.Text.Json.Nodes;
namespace IndexNode.Tests;

/// <summary>0.13.26: refresh and classify go through the work queue: the service seeds them, a downloader hands the result back on its done line, the service folds it into the full queue.</summary>
public class DownloadRunsTests
{
    static readonly Paths P = new("index/");
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
    static string At => Protocol.Utc(T0);
    static string Later => Protocol.Utc(T0.AddHours(6));
    sealed class Rig
    {
        public FakeStore S = new() { Clock = () => T0 }; public JobQueue Full = null!, Work = null!, Proc = null!, Dl = null!; public DownloadService Svc = null!;
        public static async Task<Rig> Make(params QLine[] fullLines)
        {
            var r = new Rig();
            r.Full = new JobQueue(r.S, P.DownloadQueue, "svc", () => T0); await r.Full.LoadAsync(default);
            r.Work = new JobQueue(r.S, P.DownloadWork, "svc", () => T0); await r.Work.LoadAsync(default);
            r.Proc = new JobQueue(r.S, P.ProcessQueue, "svc", () => T0); await r.Proc.LoadAsync(default);
            r.Dl = new JobQueue(r.S, P.DownloadWork, "dl1", () => T0, downloader: true); await r.Dl.LoadAsync(default);
            if (fullLines.Length > 0) await r.Full.AppendLinesAsync(fullLines, default);
            r.Svc = new DownloadService(r.Full, r.Work, r.Proc, (id, ct) => Task.FromResult<JsonObject?>(new JsonObject { ["decision"] = "new" }), 10, r.S);
            return r;
        }
        /// <summary>What a downloader does: claim the item and finish it with this body (pending again, due later, like a refresh).</summary>
        public async Task Finish(string id, string stage, JobDescriptor body, string? due)
        {
            await Dl.PollAsync(default);
            var h = await Dl.TryClaimAsync(id, s => [stage], default); Assert.NotNull(h);
            await h!.AppendAsync([new QLine { T = "done", Stage = stage, Job = body, State = due != null ? "pending" : "done", Due = due }], true, default);
        }
    }
    static JobDescriptor Source(string id, string? due = null) => new() { Stage = "refresh", JobId = id, Kind = id.StartsWith("ch-") ? "channel" : "playlist", Id = id[3..], Name = id, Schedule = "6h", AddedAt = At, Run = "refresh" };
    static QLine AddSource(string id, string due, int pri = 0, string state = "pending") => QLine.Add(id, "refresh", Source(id), At, pri, due, state);
    static JobDescriptor Video(string id) => new() { Stage = "download", JobId = id, Url = "https://www.youtube.com/watch?v=" + id, AddedAt = At, Pub = "2026-11-21T00:00:00Z", Title = "t " + id, Source = "ch-a", Sources = ["ch-a"] };
    static QLine AddSample(string id, string pub) => QLine.Add(id, "sample", new JobDescriptor { Stage = "sample", JobId = id, AddedAt = At, Pub = pub }, At, 0, At);

    [Fact]
    public async Task A_due_refresh_goes_into_the_work_queue_before_the_samples_and_a_not_yet_due_one_does_not()
    {
        var r = await Rig.Make(AddSource("ch-due", At), AddSource("pl-later", Later), AddSample("v1", "2026-11-20T00:00:00Z"), AddSample("v2", "2026-11-21T00:00:00Z"));
        Assert.Equal(3, await r.Svc.TopUpAsync(default));
        var ids = r.Work.Read(w => w.Subjects.Keys.OrderBy(x => x).ToList()); Assert.Equal(["ch-due", "v1", "v2"], ids);
        Assert.Equal("refresh", r.Work.Read(w => w.Subjects["ch-due"].Entries.Keys.Single()));
        // a downloader that lists only refresh claims the refresh item and nothing else
        var next = await r.Svc.NextAsync("dl1", ["refresh"], default); Assert.Equal(("ch-due", "refresh"), (next!.Subject, next.Stage));
        Assert.Equal(0, await r.Svc.TopUpAsync(default));   // not added twice
    }

    [Fact]
    public async Task A_finished_refresh_adds_sample_ready_subjects_follows_channels_and_sets_the_next_due_time()
    {
        var r = await Rig.Make(AddSource("ch-a", At, Protocol.RefreshNowPriority), AddSample("old", "2026-11-01T00:00:00Z"));
        await r.Proc.AppendLinesAsync([QLine.Add("inproc", "process", new JobDescriptor { Stage = "process", JobId = "inproc", AddedAt = At }, At)], default);
        await r.Svc.TopUpAsync(default);
        var body = Source("ch-a"); body.LastPolled = At; body.LastModified = "2026-11-22T11:00:00Z"; body.NextRefresh = Later; body.Run = "refresh";
        var follow = JobDescriptor.NewSource("channel", "UCfollowed00000000000000", "followed", "once", ["videos"], false, 0, 480);
        body.Result = new RunResult { Videos = [Video("new1"), Video("new2"), Video("old"), Video("inproc")], Follow = [follow], Modified = body.LastModified };
        await r.Finish("ch-a", "refresh", body, Later);
        Assert.Equal(1, await r.Svc.FoldRunsAsync(default));
        var ids = r.Full.Read(f => f.Subjects.Keys.OrderBy(x => x).ToList());
        Assert.Contains("new1", ids); Assert.Contains("new2", ids); Assert.DoesNotContain("inproc", ids);   // a subject processing knows is not re-added
        var n1 = r.Full.Read(f => f.Entry("new1", "sample"))!; Assert.Equal("pending", n1.State); Assert.Equal("2026-11-21T00:00:00Z", n1.Job!.Pub); Assert.Equal("sample", n1.Job.Stage);
        Assert.Equal("pending", r.Full.Read(f => f.Entry("old", "sample"))!.State); Assert.Equal(At, r.Full.Read(f => f.Entry("old", "sample"))!.Added);   // the known one is untouched
        Assert.NotNull(r.Full.Read(f => f.Entry("UCfollowed00000000000000".Insert(0, ""), "refresh")) ?? r.Full.Read(f => f.Entry(follow.JobId, "refresh")));
        var src = r.Full.Read(f => f.Entry("ch-a", "refresh"))!;
        Assert.Equal("pending", src.State); Assert.Equal(Later, src.Due); Assert.Equal("2026-11-22T11:00:00Z", src.Job!.LastModified); Assert.Null(src.Job.Result); Assert.Equal(0, src.Priority);
        Assert.False(r.Work.Read(w => w.Subjects.ContainsKey("ch-a")));   // dropped: the top-up adds it again when it is next due
        Assert.Equal(0, await r.Svc.FoldRunsAsync(default));   // a repeat does nothing
        await r.Svc.TopUpAsync(default); Assert.False(r.Work.Read(w => w.Subjects.ContainsKey("ch-a")));      // not due yet: it is not put back (the new samples are)
    }

    [Fact]
    public async Task A_large_refresh_result_goes_by_a_blob_that_is_read_and_deleted()
    {
        var r = await Rig.Make(AddSource("pl-big", At));
        await r.Svc.TopUpAsync(default);
        var vids = Enumerable.Range(0, 400).Select(i => Video("v" + i.ToString("000") + "aaaaaaaa")).ToList();
        var body = Source("pl-big"); body.LastPolled = At;
        var res = await RunResults.SpillAsync(r.S, P, "pl-big", new RunResult { Videos = vids, Modified = "2026-11-22T00:00:00Z" }, default);
        Assert.NotNull(res.Blob); Assert.Null(res.Videos); Assert.True(r.S.Exists(res.Blob!)); body.Result = res;
        await r.Finish("pl-big", "refresh", body, null);   // schedule once: done, not pending
        Assert.Equal(1, await r.Svc.FoldRunsAsync(default));
        Assert.Equal(400, r.Full.Read(f => f.Subjects.Values.Count(s => s.Entries.ContainsKey("sample"))));
        Assert.Equal("done", r.Full.Read(f => f.Entry("pl-big", "refresh"))!.State); Assert.False(r.S.Exists(res.Blob!));
    }

    [Fact]
    public async Task The_classify_singleton_goes_out_with_its_batch_and_its_decisions_come_back_into_the_full_queue()
    {
        var unc = (string id, string title) => QLine.Add(id, "download", new JobDescriptor { Stage = "download", JobId = id, AddedAt = At, Title = title, Duration = 3000, ChannelName = "c" }, At, 0, null, "unclassified");
        var r = await Rig.Make(QLine.Add("classify", "classify", new JobDescriptor { Stage = "classify", JobId = "all", Name = "classify all", AddedAt = At }, At), unc("u1", "good"), unc("u2", "bad"), unc("u3", "silent"));
        await r.S.PutAsync(P.Control, System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"classify\":\"on\"}"), null, null, false, default);
        await r.S.PutAsync(P.Control, System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"classify\":\"on\"}"), null, null, false, default);
        Assert.Equal(1, await r.Svc.TopUpAsync(default));
        var batch = r.Work.Read(w => w.Subjects["classify"].Entries["classify"].Job!.Entries!); Assert.Equal(["u1", "u2", "u3"], batch.Select(x => x.Id).OrderBy(x => x).ToList());
        var body = r.Work.Read(w => w.Subjects["classify"].Entries["classify"].Job!); body.Entries = null; body.LastPolled = At;
        body.Result = new RunResult { Decisions = [new() { Id = "u1", Candidate = true, Kind = "drama", Reason = "r" }, new() { Id = "u2", Candidate = false, Kind = "vlog", Reason = "no" }, new() { Id = "u3", Misses = 1 }] };
        await r.Finish("classify", "classify", body, Later);
        Assert.Equal(1, await r.Svc.FoldRunsAsync(default));
        Assert.Equal("pending", r.Full.Read(f => f.Entry("u1", "download"))!.State);
        var u2 = r.Full.Read(f => f.Entry("u2", "download"))!; Assert.Equal("failed", u2.State); Assert.Contains("classify: vlog", u2.LastError);
        var u3 = r.Full.Read(f => f.Entry("u3", "download"))!; Assert.Equal("unclassified", u3.State); Assert.Equal(1, u3.Job!.ClassifyMisses);
        var c = r.Full.Read(f => f.Entry("classify", "classify"))!; Assert.Equal(At, c.Due);   // an unclassified upload is left: due again at once
        Assert.False(r.Work.Read(w => w.Subjects.ContainsKey("classify")));
    }

    [Fact]
    public async Task Without_anything_to_classify_the_singleton_stays_in_the_full_queue()
    {
        var r = await Rig.Make(QLine.Add("classify", "classify", new JobDescriptor { Stage = "classify", JobId = "all", AddedAt = At }, At));
        Assert.Equal(0, await r.Svc.TopUpAsync(default)); Assert.Equal(0, r.Work.Read(w => w.Subjects.Count));
    }

    sealed class Fake { public Dictionary<string, string> Listings = new(); public Tools Tools() => new(new HttpClient()) { YtDlpOverride = (args, _, _) => Task.FromResult(new ProcResult(0, Listings.GetValueOrDefault(args[^1], ""), "")) }; }

    [Fact]
    public async Task The_refresh_stage_on_the_work_queue_writes_nothing_to_any_queue_and_hands_the_result_back()
    {
        var s = new FakeStore { Clock = () => T0 }; var work = new JobQueue(s, P.DownloadWork, "dl1", () => T0); await work.LoadAsync(default);
        var f = new Fake(); f.Listings["https://www.youtube.com/playlist?list=PLx"] = "aaaaaaaaaaa\tNA\tNA\n" + "bbbbbbbbbbb\tNA\tNA\n";
        var job = JobDescriptor.NewSource("playlist", "PLx", "pl", "6h", ["videos"], false, -1, 360); job.AddedAt = At;
        var before = s.Names("index/").OrderBy(x => x).ToList();
        var ctx = new JobContext { Job = job, Stage = "refresh", BlobName = "x", LeaseId = "l", Worker = 1, Store = s, Paths = P, NodeId = "dl1", Token = default, ThreadShare = () => 1, Report = (_, _) => { }, Queue = work };
        await new RefreshStage(f.Tools(), s, () => T0).RunAsync(ctx);
        Assert.Equal(["aaaaaaaaaaa", "bbbbbbbbbbb"], job.Result!.Videos!.Select(v => v.JobId).OrderBy(x => x).ToList());
        Assert.Equal(0, work.Read(w => w.Subjects.Count)); Assert.NotNull(ctx.NextDue);
        Assert.DoesNotContain(s.Names("index/").Except(before), n => n.Contains("queue") || n.Contains("/active/"));   // only the source's members file
    }

    static QLine AddAvail(string due) => QLine.Add("sweep", "availability", AvailabilityStage.NewSweep(), At, 0, due, "pending");
    static QLine AddDone(string id) => QLine.Add(id, "download", Video(id), At, 0, null, "done");

    [Fact]
    public async Task A_due_refresh_is_added_even_when_the_work_queue_is_full()
    {
        var samples = Enumerable.Range(0, 20).Select(i => AddSample("v" + i.ToString("D2"), "2026-11-" + (1 + i).ToString("D2") + "T00:00:00Z")).ToList();
        var r = await Rig.Make(samples.ToArray());
        await r.Svc.TopUpAsync(default); Assert.Equal(10, r.Work.Read(w => w.Subjects.Count));   // max 10: full
        await r.Full.AppendLinesAsync([AddSource("ch-due", At)], default);
        Assert.Equal(1, await r.Svc.TopUpAsync(default));
        Assert.Equal(11, r.Work.Read(w => w.Subjects.Count)); Assert.True(r.Work.Read(w => w.Subjects.ContainsKey("ch-due")));
    }

    [Fact]
    public async Task The_split_seeds_due_refresh_first_outside_the_max()
    {
        var lines = new List<QLine> { AddSource("ch-due", At), AddSource("pl-later", Later) };
        for (int i = 0; i < 12; i++) lines.Add(AddSample("v" + i.ToString("D2"), "2026-11-" + (1 + i).ToString("D2") + "T00:00:00Z"));
        var legacy = new QueueState(); foreach (var l in lines) legacy.Apply(l);
        var plan = QueueSplit.Plan(legacy, At, 10);
        Assert.Equal(11, plan.Work.Subjects.Count); Assert.True(plan.Work.Subjects.ContainsKey("ch-due")); Assert.False(plan.Work.Subjects.ContainsKey("pl-later"));
    }

    [Fact]
    public async Task An_admin_add_request_becomes_a_due_source_in_the_full_queue_without_youtube()
    {
        var r = await Rig.Make();
        await r.S.PutAsync(RefreshStage.RequestsDir(P) + "a.json", System.Text.Encoding.UTF8.GetBytes("{\"state\":\"pending\",\"url\":\"https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv\",\"schedule\":\"6h\"}"), null, null, false, default);
        await r.S.PutAsync(RefreshStage.RequestsDir(P) + "b.json", System.Text.Encoding.UTF8.GetBytes("{\"state\":\"pending\",\"url\":\"https://www.youtube.com/@somehandle\"}"), null, null, false, default);
        Assert.Equal(2, await r.Svc.ResolveRequestsAsync(default));
        await r.Full.PollAsync(default);
        var e = r.Full.Read(s => s.Entry("ch-UCabcdefghijklmnopqrstuv", "refresh")); Assert.NotNull(e); Assert.Equal("lookup", e!.Job!.Run); Assert.Equal(e.Job.AddedAt, e.Due); Assert.Equal(Protocol.RefreshNowPriority, e.Priority);
        Assert.Equal("done", JsonNode.Parse((await r.S.GetAsync(RefreshStage.RequestsDir(P) + "a.json", default))!.Data)!["state"]!.ToString());
        Assert.Equal("failed", JsonNode.Parse((await r.S.GetAsync(RefreshStage.RequestsDir(P) + "b.json", default))!.Data)!["state"]!.ToString());
    }

    [Fact]
    public async Task The_availability_sweep_runs_on_the_work_queue_with_an_input_blob_and_folds_back_its_due_time()
    {
        var r = await Rig.Make(AddAvail(At), AddDone("d1"));
        await r.Svc.TopUpAsync(default);
        var item = r.Work.Read(w => w.Subjects["sweep"].Entries["availability"]); Assert.NotNull(item.Job!.InputBlob);
        var input = JsonNode.Parse((await r.S.GetAsync(item.Job.InputBlob!, default))!.Data)!; Assert.Equal("d1", input["tracked"]![0]!.ToString());
        var body = item.Job; body.LastPolled = At; body.Result = new RunResult();
        await r.Finish("sweep", "availability", body, Later);
        Assert.Equal(1, await r.Svc.FoldRunsAsync(default));
        var f = r.Full.Read(s => s.Entry("sweep", "availability")); Assert.Equal(Later, f!.Due); Assert.Equal(At, f.Job!.LastPolled); Assert.Null(f.Job.Result);
        Assert.Null(await r.S.GetAsync(item.Job.InputBlob!, default));
    }

    static QLine AddDynamic(string id, string due, int pri = 0, string run = "refresh") { var j = Source(id); j.Schedule = "dynamic"; j.Every = null; j.Run = run; return QLine.Add(id, "refresh", j, At, pri, due, "pending"); }

    [Fact]
    public async Task Dynamic_sources_are_not_seeded_while_dynamicRefresh_is_off_and_are_when_it_is_on_explicit_manual_and_first_listings_always()
    {
        var r = await Rig.Make(AddDynamic("ch-dyn", At), AddDynamic("ch-manual", At, Protocol.RefreshNowPriority), AddDynamic("ch-new", At, 0, "lookup"), AddSource("ch-explicit", At));
        Assert.Equal(3, await r.Svc.TopUpAsync(default));
        Assert.Equal(["ch-explicit", "ch-manual", "ch-new"], r.Work.Read(w => w.Subjects.Keys.OrderBy(x => x).ToList()));
        await r.S.PutAsync(P.Control, System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"dynamicRefresh\":\"on\"}"), null, null, false, default);
        Assert.Equal(1, await r.Svc.TopUpAsync(default));
        Assert.True(r.Work.Read(w => w.Subjects.ContainsKey("ch-dyn")));
    }

    [Fact]
    public void The_split_seed_skips_dynamic_refreshes_unless_dynamicRefresh_is_on()
    {
        var legacy = new QueueState(); foreach (var l in new[] { AddDynamic("ch-dyn", At), AddSource("ch-explicit", At) }) legacy.Apply(l);
        Assert.Equal(["ch-explicit"], QueueSplit.Plan(legacy, At, 10).Work.Subjects.Keys.ToList());
        Assert.Equal(2, QueueSplit.Plan(legacy, At, 10, true).Work.Subjects.Count);
    }

    [Fact]
    public async Task Classify_is_not_seeded_while_control_classify_is_off_and_is_when_it_is_on()
    {
        var r = await Rig.Make(QLine.Add("classify", "classify", new JobDescriptor { Stage = "classify", JobId = "all", AddedAt = At }, At), QLine.Add("u1", "download", new JobDescriptor { Stage = "download", JobId = "u1", AddedAt = At, Title = "t", Duration = 3000 }, At, 0, null, Tags.Unclassified));
        Assert.Equal(0, await r.Svc.TopUpAsync(default)); Assert.False(r.Work.Read(w => w.Subjects.ContainsKey("classify")));
        await r.S.PutAsync(P.Control, System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"classify\":\"on\"}"), null, null, false, default);
        Assert.Equal(1, await r.Svc.TopUpAsync(default)); Assert.True(r.Work.Read(w => w.Subjects.ContainsKey("classify")));
    }

    [Fact]
    public void The_split_seed_skips_classify_unless_control_classify_is_on()
    {
        var legacy = new QueueState(); foreach (var l in new[] { QLine.Add("classify", "classify", new JobDescriptor { Stage = "classify", JobId = "all", AddedAt = At }, At), QLine.Add("u1", "download", new JobDescriptor { Stage = "download", JobId = "u1", AddedAt = At, Title = "t", Duration = 3000 }, At, 0, null, Tags.Unclassified) }) legacy.Apply(l);
        Assert.DoesNotContain("classify", QueueSplit.Plan(legacy, At, 10).Work.Subjects.Keys);
        Assert.Contains("classify", QueueSplit.Plan(legacy, At, 10, false, true).Work.Subjects.Keys);
    }

    [Fact]
    public async Task A_manual_classify_set_lands_in_the_queue_as_a_final_decision_and_writes_no_blob()
    {
        var r = await Rig.Make(QLine.Add("abcdefghijk", "download", new JobDescriptor { Stage = "download", JobId = "abcdefghijk", AddedAt = At, Title = "t", Duration = 3000 }, At, 0, null, Tags.Unclassified));
        await IndexNode.ClassifyCommand.SetAsync(r.S, r.Full, P, "abcdefghijk", "not", "owner says no", DateTimeOffset.Parse(At), default);
        var e = r.Full.Read(x => x.Entry("abcdefghijk", "download"))!; Assert.Equal("failed", e.State); Assert.True(e.Job!.Permanent);
        Assert.Empty(await r.S.ListAsync(P.Prefix + "classify/", false, false, default));
    }

    [Fact]
    public void The_split_applies_stored_classify_decisions_given_from_one_listing_only_to_unclassified_uploads()
    {
        var legacy = new QueueState();
        foreach (var l in new[] { QLine.Add("u1", "download", new JobDescriptor { Stage = "download", JobId = "u1", AddedAt = At, Title = "t", Duration = 3000 }, At, 0, null, Tags.Unclassified), QLine.Add("u2", "download", new JobDescriptor { Stage = "download", JobId = "u2", AddedAt = At, Title = "t", Duration = 3000 }, At, 0, null, Tags.Unclassified), QLine.Add("u3", "download", new JobDescriptor { Stage = "download", JobId = "u3", AddedAt = At, Title = "t", Duration = 3000 }, At, 0, null, Tags.Unclassified), QLine.Add("d1", "download", new JobDescriptor { Stage = "download", JobId = "d1", AddedAt = At }, At, 0, null, "pending") }) legacy.Apply(l);
        var stored = new Dictionary<string, System.Text.Json.Nodes.JsonObject> { ["u1"] = System.Text.Json.Nodes.JsonNode.Parse("{\"candidate\":true,\"kind\":\"drama\",\"reason\":\"y\"}")!.AsObject(), ["u2"] = System.Text.Json.Nodes.JsonNode.Parse("{\"candidate\":false,\"kind\":\"short\",\"reason\":\"x\"}")!.AsObject(), ["d1"] = System.Text.Json.Nodes.JsonNode.Parse("{\"candidate\":false}")!.AsObject() };
        var d = QueueSplit.Plan(legacy, At, 10, false, false, stored).Download.Subjects;
        Assert.Equal("pending", d["u1"].Entries["download"].State); Assert.Equal("failed", d["u2"].Entries["download"].State); Assert.Equal(Tags.Unclassified, d["u3"].Entries["download"].State); Assert.Equal("pending", d["d1"].Entries["download"].State);
    }
}
