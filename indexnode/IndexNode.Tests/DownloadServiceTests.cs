using System.Text.Json.Nodes;
namespace IndexNode.Tests;

public class DownloadServiceTests
{
    static readonly Paths P = new("index/");
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
    static string At => Protocol.Utc(T0);
    sealed class Rig
    {
        public FakeStore S = new() { Clock = () => T0 }; public JobQueue Full = null!, Work = null!, Proc = null!; public DownloadService Svc = null!; public Dictionary<string, JsonObject> Decisions = new(); public int Checks;
        public static async Task<Rig> Make(int workMax = 10, params QLine[] fullLines)
        {
            var r = new Rig();
            r.Full = new JobQueue(r.S, P.DownloadQueue, "svc", () => T0); await r.Full.LoadAsync(default);
            r.Work = new JobQueue(r.S, P.DownloadWork, "svc", () => T0); await r.Work.LoadAsync(default);
            r.Proc = new JobQueue(r.S, P.ProcessQueue, "svc", () => T0); await r.Proc.LoadAsync(default);
            if (fullLines.Length > 0) await r.Full.AppendLinesAsync(fullLines, default);
            r.Svc = new DownloadService(r.Full, r.Work, r.Proc, (id, ct) => { r.Checks++; return Task.FromResult<JsonObject?>(r.Decisions.TryGetValue(id, out var d) ? d : new JsonObject { ["decision"] = "new" }); }, workMax);
            return r;
        }
    }
    /// <summary>0.13.52: a download the service may hand out belongs to an upload that was sampled (a pending download of a never-sampled upload becomes a sample entry): the finished sample that goes with a download in these tests.</summary>
    static QLine Sampled(string id) => Add(id, "sample", "2020-01-01T00:00:00Z", 0, "done");
    static QLine Add(string id, string stage, string pub, int pri = 0, string? state = null) => QLine.Add(id, stage, new JobDescriptor { Stage = stage, JobId = id, Url = "https://www.youtube.com/watch?v=" + id, AddedAt = At, Pub = pub }, At, pri, At, state);

    [Fact]
    public async Task A_pending_download_of_an_upload_never_sampled_becomes_a_sample_first_and_no_node_is_handed_such_a_download()
    {
        // plain: a classify decision or `enqueue` made it; checked: its sample is done and the check added the download; uncl: not a drama yet; held: an AI hold; inwork/busy: plain ones already in the work queue
        var r = await Rig.Make(10, Add("plain", "download", "2026-11-05T00:00:00Z", 7), Sampled("checked"), Add("checked", "download", "2026-11-04T00:00:00Z"), Add("uncl", "download", "2026-11-03T00:00:00Z", 0, "unclassified"),
            new QLine { T = "add", S = "held", Stage = "download", Job = new JobDescriptor { Stage = "download", JobId = "held", AddedAt = At }, At = At, Due = At, Hold = "ai" },
            Add("inwork", "download", "2026-11-02T00:00:00Z"), Add("busy", "download", "2026-11-01T00:00:00Z"));
        await r.Work.AppendLinesAsync([Add("inwork", "download", "2026-11-02T00:00:00Z"), Add("busy", "download", "2026-11-01T00:00:00Z")], default);   // topped up by an older service
        var node = new JobQueue(r.S, P.DownloadWork, "dl1", () => T0); await node.LoadAsync(default);
        var mine = await node.TryClaimAsync("busy", s => ["download"], default); Assert.NotNull(mine);   // a node is downloading this one right now
        Assert.Equal(2, await r.Svc.SampleFirstAsync(default));   // plain and inwork; busy is left to finish, the others are not its business
        foreach (var id in new[] { "plain", "inwork" })
        {
            Assert.Null(r.Full.Read(f => f.Entry(id, "download"))); Assert.Equal("pending", r.Full.Read(f => f.Entry(id, "sample"))!.State);
            Assert.Equal("https://www.youtube.com/watch?v=" + id, r.Full.Read(f => f.Entry(id, "sample"))!.Job!.Url);
        }
        Assert.Equal(7, r.Full.Read(f => f.Entry("plain", "sample"))!.Priority);                       // priority kept
        Assert.Null(r.Work.Read(w => w.Entry("inwork", "download")));                                  // its idle copy left the work queue
        Assert.Equal("pending", r.Full.Read(f => f.Entry("checked", "download"))!.State); Assert.Equal("unclassified", r.Full.Read(f => f.Entry("uncl", "download"))!.State);
        Assert.Equal("ai", r.Full.Read(f => f.Entry("held", "download"))!.Hold); Assert.Null(r.Full.Read(f => f.Entry("held", "sample")));
        Assert.Equal("pending", r.Full.Read(f => f.Entry("busy", "download"))!.State); Assert.NotNull(r.Work.Read(w => w.Entry("busy", "download")));
        Assert.Equal(0, await r.Svc.SampleFirstAsync(default));   // idempotent
        // what nodes are handed: samples for the two, the download only for the upload that was checked
        var got = new List<(string, string)>();
        for (int i = 0; i < 4; i++) if (await r.Svc.NextAsync("n" + i, ["sample", "download"], default) is { } c) got.Add((c.Subject, c.Stage));
        Assert.Equal([("checked", "download"), ("inwork", "sample"), ("plain", "sample")], got.OrderBy(x => x.Item1).ToList());
        // a plain download added later (classify set, enqueue) is converted by the very next hand-over, before it can be given out
        await r.Full.AppendLinesAsync([Add("later", "download", "2026-11-06T00:00:00Z", 1000)], default);
        var next = await r.Svc.NextAsync("n9", ["sample", "download"], default);
        Assert.Equal(("later", "sample"), (next!.Subject, next.Stage));
        // the node that was downloading finishes; it folds back and enters processing as the download it was
        await mine!.AppendAsync([new QLine { T = "done", Stage = "download", State = "done" }], true, default); await mine.FinishAsync();
        await r.Svc.RunOnceAsync(default);
        Assert.Equal("done", r.Full.Read(f => f.Entry("busy", "download"))!.State); Assert.Null(r.Full.Read(f => f.Entry("busy", "sample")));
    }

    static QLine[] Checked(string id, int pri = 0, string pub = "2026-11-01T00:00:00Z") => [Sampled(id), Add(id, "check", pub, pri, "done"), Add(id, "download", pub, pri)];

    [Fact]
    public async Task A_download_sampled_before_the_index_was_built_is_checked_again_on_its_stored_clips_before_it_is_handed_out()
    {
        // 0.13.55 (owner, 2026-11-25: sampling is part of the download): stillnew and nowdup were sampled and checked at T0; the published index was built an hour later
        var r = await Rig.Make(10, [.. Checked("stillnew", 5), .. Checked("nowdup", 9)]);
        r.Svc.IndexBuiltAt = _ => Task.FromResult<DateTimeOffset?>(T0.AddHours(1));
        r.Decisions["nowdup"] = new JsonObject { ["decision"] = "duplicate", ["matchUpload"] = "orig7", ["top"] = new JsonArray(new JsonObject { ["upload"] = "orig7", ["share"] = 0.91 }) };
        var got = await r.Svc.NextAsync("dl1", ["sample", "download"], default);
        Assert.Equal(("stillnew", "download"), (got!.Subject, got.Stage)); Assert.Equal(2, r.Checks);   // nowdup had the higher priority: checked first, found to be a copy, not handed out; then stillnew, checked and handed out
        Assert.Equal("duplicate", r.Full.Read(f => f.Entry("nowdup", "check"))!.State); Assert.Equal("orig7", r.Full.Read(f => f.Entry("nowdup", "check"))!.Job!.DuplicateOf);
        Assert.Equal("duplicate", r.Full.Read(f => f.Entry("nowdup", "download"))!.State); Assert.Null(r.Work.Read(w => w.Get("nowdup")));   // nothing was put on the work queue for it
        var d = r.Proc.Read(p => p.Get("nowdup"))!; Assert.Equal("precheck", d.Data); Assert.Equal("duplicate", d.Entries["check"].State);  // it enters the catalog as a duplicate, like one found at its first check
        Assert.Equal("done", r.Full.Read(f => f.Entry("stillnew", "check"))!.State); Assert.Equal("dl1", r.Work.Read(w => w.Get("stillnew"))!.ClaimBy);
        Assert.Equal(0, (await r.Svc.RunOnceAsync(default)).Entered);   // a duplicate found this way is never entered as a download
        Assert.Null(await r.Svc.NextAsync("dl2", ["sample", "download"], default)); Assert.Equal(2, r.Checks);   // nothing left, and nothing checked twice

        // an index built BEFORE the check, or no index time at all: the verdict is current, no check runs
        foreach (var built in new DateTimeOffset?[] { T0.AddHours(-1), null })
        {
            var f = await Rig.Make(10, Checked("fresh"));
            if (built != null) f.Svc.IndexBuiltAt = _ => Task.FromResult(built);
            var g = await f.Svc.NextAsync("dl1", ["download"], default); Assert.Equal("fresh", g!.Subject); Assert.Equal(0, f.Checks);
        }
    }

    [Fact]
    public async Task A_check_that_cannot_run_keeps_its_download_back_and_is_not_tried_again_for_half_an_hour()
    {
        var r = await Rig.Make(10, [.. Checked("broken"), Add("s1", "sample", "2026-11-02T00:00:00Z")]);
        var now = T0; r.Svc.Clock = () => now; r.Svc.IndexBuiltAt = _ => Task.FromResult<DateTimeOffset?>(T0.AddHours(1));
        int calls = 0; r.Svc = new DownloadService(r.Full, r.Work, r.Proc, (id, ct) => { calls++; throw new InvalidOperationException("the sample has no frames"); }, 10) { Clock = () => now, IndexBuiltAt = _ => Task.FromResult<DateTimeOffset?>(T0.AddHours(1)) };
        var got = await r.Svc.NextAsync("dl1", ["sample", "download"], default);
        Assert.Equal(("s1", "sample"), (got!.Subject, got.Stage)); Assert.Equal(1, calls);   // the download is not handed out on a verdict that could not be renewed; the node gets the sample instead
        Assert.Equal("pending", r.Full.Read(f => f.Entry("broken", "download"))!.State);
        Assert.Null(await r.Svc.NextAsync("dl2", ["sample", "download"], default)); Assert.Equal(1, calls);   // asked again a moment later: no second attempt
        now = T0.AddMinutes(31);
        Assert.Null(await r.Svc.NextAsync("dl2", ["sample", "download"], default)); Assert.Equal(2, calls);   // half an hour on it is tried once more
    }

    [Fact]
    public async Task One_call_checks_at_most_four_stale_downloads_then_hands_out_a_sample_and_a_tie_goes_to_the_download()
    {
        var lines = new List<QLine>(); for (int i = 0; i < 6; i++) lines.AddRange(Checked("dup" + i, 3, $"2026-11-0{9 - i}T00:00:00Z"));
        lines.Add(Add("s1", "sample", "2026-11-20T00:00:00Z", 3)); lines.AddRange(Checked("tie", 0)); lines.Add(Add("s0", "sample", "2026-11-21T00:00:00Z", 0));
        var r = await Rig.Make(10, lines.ToArray());
        r.Svc.IndexBuiltAt = _ => Task.FromResult<DateTimeOffset?>(T0.AddHours(1));
        for (int i = 0; i < 6; i++) r.Decisions["dup" + i] = new JsonObject { ["decision"] = "duplicate", ["matchUpload"] = "orig" };
        var a = await r.Svc.NextAsync("dl1", ["sample", "download"], default);
        Assert.Equal(("s1", "sample"), (a!.Subject, a.Stage)); Assert.Equal(DownloadService.MaxChecksPerCall, r.Checks);   // four copies settled, then the caller is not kept waiting: the sample of the same priority
        var b = await r.Svc.NextAsync("dl2", ["sample", "download"], default);
        Assert.Equal(("tie", "download"), (b!.Subject, b.Stage)); Assert.Equal(7, r.Checks);   // the other two copies, then priority 0: the download before the sample of the same priority (and it was checked: 7)
        var c = await r.Svc.NextAsync("dl3", ["sample", "download"], default); Assert.Equal(("s0", "sample"), (c!.Subject, c.Stage));
        Assert.All(Enumerable.Range(0, 6), i => Assert.Equal("duplicate", r.Full.Read(f => f.Entry("dup" + i, "download"))!.State));
    }

    [Fact]
    public async Task A_call_does_not_wait_behind_another_callers_check_and_stops_checking_after_one_that_cannot_run_or_after_its_time()
    {
        // found by the shadow dry run of 2026-11-25: a check takes about 12 s, and every node asked for the same first download
        var r = await Rig.Make(10, [.. Checked("slow", 9), .. Checked("other", 5)]);
        var gate = new TaskCompletionSource(); var entered = new TaskCompletionSource(); var seen = new List<string>();
        var svc = new DownloadService(r.Full, r.Work, r.Proc, async (id, ct) => { lock (seen) seen.Add(id); if (id == "slow") { entered.TrySetResult(); await gate.Task; } return new JsonObject { ["decision"] = "new" }; }, 10) { IndexBuiltAt = _ => Task.FromResult<DateTimeOffset?>(T0.AddHours(1)) };
        var first = svc.NextAsync("dl1", ["sample", "download"], default); await entered.Task;
        var second = await svc.NextAsync("dl2", ["sample", "download"], default);
        Assert.Equal(("other", "download"), (second!.Subject, second.Stage)); Assert.False(first.IsCompleted);   // dl2 did not wait for the check dl1 is in: it took the next download
        gate.SetResult(); var got = await first; Assert.Equal(("slow", "download"), (got!.Subject, got.Stage));
        Assert.Equal(["slow", "other"], seen);   // each checked once

        // a check that cannot run (a store limit, the index): the same call tries no other download, so one bad minute does not send every ready download into its half hour
        var b = await Rig.Make(10, [.. Checked("b1", 9), .. Checked("b2", 5), Add("s1", "sample", "2026-11-02T00:00:00Z")]);
        int calls = 0; var bs = new DownloadService(b.Full, b.Work, b.Proc, (id, ct) => { calls++; throw new InvalidOperationException("storage: a step made 25 store ops"); }, 10) { IndexBuiltAt = _ => Task.FromResult<DateTimeOffset?>(T0.AddHours(1)) };
        var bg = await bs.NextAsync("dl1", ["sample", "download"], default);
        Assert.Equal(("s1", "sample"), (bg!.Subject, bg.Stage)); Assert.Equal(1, calls);

        // the time limit: after 25 s of checking, the call starts no further check and the node gets a sample
        var t = await Rig.Make(10, [.. Checked("d1", 9), .. Checked("d2", 8), .. Checked("d3", 7), Add("s1", "sample", "2026-11-02T00:00:00Z")]);
        var now = T0; int n = 0;
        var ts = new DownloadService(t.Full, t.Work, t.Proc, (id, ct) => { n++; now = now.AddSeconds(30); return Task.FromResult<JsonObject?>(new JsonObject { ["decision"] = "duplicate", ["matchUpload"] = "orig" }); }, 10) { Clock = () => now, IndexBuiltAt = _ => Task.FromResult<DateTimeOffset?>(T0.AddHours(1)) };
        var tg = await ts.NextAsync("dl1", ["sample", "download"], default);
        Assert.Equal(("s1", "sample"), (tg!.Subject, tg.Stage)); Assert.Equal(1, n);
    }

    [Fact]
    public async Task While_downloads_are_off_in_control_the_service_hands_nothing_out()
    {
        // 0.13.60: on 2026-11-26 two nodes that had not seen download=stop were handed 15 more downloads
        var r = await Rig.Make(10, [.. Checked("ready"), Add("s1", "sample", "2026-11-02T00:00:00Z")]);
        bool on = false; r.Svc.StageOn = st => on;
        Assert.Null(await r.Svc.NextAsync("dl1", ["sample", "download"], default));                       // neither the download nor the sample
        Assert.Null(r.Work.Read(w => w.Get("ready")?.ClaimBy)); Assert.Null(r.Work.Read(w => w.Get("s1")?.ClaimBy));
        on = true;
        var got = await r.Svc.NextAsync("dl1", ["sample", "download"], default); Assert.Equal(("ready", "download"), (got!.Subject, got.Stage));
    }

    [Fact]
    public async Task A_download_nobody_holds_does_not_stay_on_the_work_queue()
    {
        var r = await Rig.Make(10, Checked("v1"));
        var dl = new JobQueue(r.S, P.DownloadWork, "dl1", () => T0, downloader: true); await dl.LoadAsync(default);
        var got = await r.Svc.NextAsync("dl1", ["download"], default); await dl.PollAsync(default);
        var h = dl.AdoptClaim(got!.Subject, got.Epoch, got.Lease);
        await h!.AppendAsync([new QLine { T = "failed", Stage = "download", State = "pending", Attempt = 1, Error = "HTTP 403", Due = At }], true, default); await h.FinishAsync();   // the node's download failed: it will be tried again
        await r.Svc.RunOnceAsync(default);
        Assert.Null(r.Work.Read(w => w.Entry("v1", "download")));                                       // its attempt is folded back, and it is not left where a node could claim it by itself
        Assert.Equal(1, r.Full.Read(f => f.Entry("v1", "download"))!.Attempts); Assert.Equal("pending", r.Full.Read(f => f.Entry("v1", "download"))!.State);
        var again = await r.Svc.NextAsync("dl2", ["download"], default); Assert.Equal("v1", again!.Subject);   // handed out again from the full queue
    }

    [Fact]
    public async Task A_prioritised_item_joins_a_full_work_queue_at_once_and_is_the_next_one_handed_out()
    {
        var lines = Enumerable.Range(0, 30).Select(i => Add("v" + i.ToString("00"), "sample", $"2026-11-{1 + i % 28:00}T00:00:00Z", i == 3 ? 9 : 0)).ToList();
        var r = await Rig.Make(10, lines.ToArray());
        Assert.Equal(10, await r.Svc.TopUpAsync(default));   // the work queue is full (10 of 10), its highest priority is 9
        await r.Full.AppendLinesAsync([Sampled("asked"), Add("asked", "download", "2020-01-01T00:00:00Z", 1000), Add("same9", "sample", "2026-11-30T00:00:00Z", 9), Add("low", "sample", "2026-11-30T00:00:00Z", 1)], default);
        Assert.Equal(0, await r.Svc.TopUpAsync(default));    // 0.13.55: a download is never put on the work queue ahead of time; an equal or lower sample waits for the ordinary top-up
        var ids = r.Work.Read(w => w.Subjects.Values.Select(s => s.Id).ToList());
        Assert.DoesNotContain("asked", ids); Assert.DoesNotContain("same9", ids); Assert.DoesNotContain("low", ids); Assert.Equal(10, ids.Count);
        var next = await r.Svc.NextAsync("laptop", ["download", "sample"], default);
        Assert.Equal("asked", next!.Subject); Assert.Equal("download", next.Stage);   // and it is what a downloader gets next: its priority is above every sample's
        Assert.Equal("laptop", r.Work.Read(w => w.Get("asked"))!.ClaimBy);            // on the work queue only now, claimed for the node that asked
        var many = Enumerable.Range(0, 80).Select(i => Add("hi" + i.ToString("00"), "sample", "2026-11-30T00:00:00Z", 5000)).ToArray();
        await r.Full.AppendLinesAsync(many, default);
        Assert.Equal(DownloadService.JumpMax, await r.Svc.TopUpAsync(default));   // bounded per pass
    }

    [Fact]
    public async Task A_claimed_item_does_not_keep_equal_priority_samples_out_of_a_full_work_queue()
    {
        // 0.13.77 (2026-11-26): a full work queue of priority-0 samples, one priority-200 download handed out (claimed); 200 samples in the full queue must still get in
        var lines = Enumerable.Range(0, 12).Select(i => Add("old" + i.ToString("00"), "sample", $"2026-11-{1 + i:00}T00:00:00Z", 0)).ToList();
        lines.AddRange(Checked("pick0")); lines.Add(new QLine { T = "pri", S = "pick0", Pri = 200 });
        var r = await Rig.Make(10, lines.ToArray());
        Assert.Equal(10, await r.Svc.TopUpAsync(default));   // full: ten priority-0 samples
        var dl = await r.Svc.NextAsync("node1", ["download"], default); Assert.Equal("pick0", dl!.Subject);   // the 200 download is handed out and claimed
        await r.Full.AppendLinesAsync([Add("pick1", "sample", "2026-11-30T00:00:00Z", 200), Add("pick2", "sample", "2026-11-30T00:00:00Z", 200)], default);
        Assert.Equal(2, await r.Svc.TopUpAsync(default));     // before 0.13.77: 0 (the claimed download set the bar at 200)
        var ids = r.Work.Read(w => w.Subjects.Values.Select(s => s.Id).ToHashSet()); Assert.Contains("pick1", ids); Assert.Contains("pick2", ids);
        await r.Full.AppendLinesAsync([Add("pick3", "sample", "2026-11-30T00:00:00Z", 200)], default);
        Assert.Equal(0, await r.Svc.TopUpAsync(default));     // idle samples at 200 are waiting now: an equal one waits for them, as before
    }

    [Fact]
    public async Task Top_up_stops_at_the_max_goes_newest_first_keeps_priority_and_leaves_listed_and_hold_items()
    {
        var lines = Enumerable.Range(0, 30).Select(i => Add("v" + i.ToString("00"), "sample", $"2026-11-{1 + i % 28:00}T00:00:00Z", i == 3 ? 9 : 0)).ToList();
        lines.Add(Add("u1", "download", "2026-11-30T00:00:00Z", 0, "unclassified")); lines.Add(Add("ch-1", "refresh", "2026-11-30T00:00:00Z", 0, "done"));
        lines.Add(new QLine { T = "add", S = "h1", Stage = "download", Job = new JobDescriptor { Stage = "download", JobId = "h1", AddedAt = At, Pub = "2026-11-29T00:00:00Z" }, At = At, Due = At, Hold = "ai" });
        var r = await Rig.Make(10, lines.ToArray());
        Assert.Equal(10, await r.Svc.TopUpAsync(default));
        var ids = r.Work.Read(w => w.Subjects.Values.Select(s => s.Id).ToList()); Assert.Equal(10, ids.Count); Assert.DoesNotContain("u1", ids); Assert.DoesNotContain("ch-1", ids); Assert.DoesNotContain("h1", ids);
        Assert.Contains("v03", ids);   // priority 9 first although its pub is not the newest
        var rest = r.Work.Read(w => w.Subjects.Values.Where(s => s.Id != "v03").OrderByDescending(s => s.Entries["sample"].Job!.Pub, StringComparer.Ordinal).Select(s => s.Entries["sample"].Job!.Pub).ToList());
        var all = Enumerable.Range(0, 30).Where(i => i != 3).Select(i => $"2026-11-{1 + i % 28:00}T00:00:00Z").OrderByDescending(x => x, StringComparer.Ordinal).Take(9).ToList(); Assert.Equal(all, rest);
        Assert.Equal(0, await r.Svc.TopUpAsync(default));   // 10 live of 10: not under half
    }

    [Fact]
    public async Task Fold_back_and_clear_out_leave_only_live_items_after_a_rollover()
    {
        var r = await Rig.Make(10, Enumerable.Range(0, 6).Select(i => Add("v" + i, "sample", $"2026-11-0{i + 1}T00:00:00Z")).ToArray());
        await r.Svc.TopUpAsync(default);
        // downloaders finish three items in the work queue: two done, one failed with an error
        var dl = new JobQueue(r.S, P.DownloadWork, "dl1", () => T0, downloader: true); await dl.LoadAsync(default);
        foreach (var (id, st) in new[] { ("v5", "done"), ("v4", "done"), ("v3", "failed") })
        {
            var h = await dl.TryClaimAsync(id, s => ["sample"], default); Assert.NotNull(h);
            await h!.AppendAsync([new QLine { T = st == "done" ? "done" : "failed", Stage = "sample", State = st, Error = st == "failed" ? "boom" : null, Attempt = st == "failed" ? 3 : null, Permanent = st == "failed" ? true : null }], true, default); await h.FinishAsync();
        }
        Assert.Equal(3, await r.Svc.FoldBackAsync(default));
        Assert.Equal("done", r.Full.Read(f => f.Entry("v5", "sample"))!.State); Assert.Equal("failed", r.Full.Read(f => f.Entry("v3", "sample"))!.State); Assert.Equal("boom", r.Full.Read(f => f.Entry("v3", "sample"))!.LastError);
        Assert.Equal(3, await r.Svc.ClearOutAsync(default));
        await r.Work.RollOverAsync(default);
        var fresh = new JobQueue(r.S, P.DownloadWork, "x", () => T0); await fresh.LoadAsync(default);
        var left = fresh.Read(w => w.Subjects.Values.Select(s => s.Id).OrderBy(x => x).ToList()); Assert.Equal(["v0", "v1", "v2"], left);   // only the live ones
    }

    [Fact]
    public async Task A_finished_sample_is_checked_then_a_full_download_goes_to_work_or_the_upload_enters_as_metadata_only()
    {
        var r = await Rig.Make(10, Add("n1", "sample", "2026-11-01T00:00:00Z"), Add("d1", "sample", "2026-11-02T00:00:00Z"), Add("a1", "sample", "2026-11-03T00:00:00Z"));
        r.Decisions["d1"] = new JsonObject { ["decision"] = "duplicate", ["matchUpload"] = "orig1", ["top"] = new JsonArray(new JsonObject { ["upload"] = "orig1", ["share"] = 0.93 }) };
        r.Decisions["a1"] = new JsonObject { ["decision"] = "new", ["ai"] = new JsonObject { ["flag"] = true, ["score"] = 0.6 } };
        await r.Svc.TopUpAsync(default);
        var dl = new JobQueue(r.S, P.DownloadWork, "dl1", () => T0, downloader: true); await dl.LoadAsync(default);
        foreach (var id in new[] { "n1", "d1", "a1" }) { var h = await dl.TryClaimAsync(id, s => ["sample"], default); await h!.AppendAsync([new QLine { T = "done", Stage = "sample", State = "done" }], true, default); await h.FinishAsync(); }
        await r.Svc.FoldBackAsync(default); Assert.Equal(3, await r.Svc.CheckSamplesAsync(default)); Assert.Equal(3, r.Checks);
        Assert.Equal("done", r.Full.Read(f => f.Entry("n1", "check"))!.State); Assert.Equal("pending", r.Full.Read(f => f.Entry("n1", "download"))!.State);
        Assert.Equal("duplicate", r.Full.Read(f => f.Entry("d1", "check"))!.State); Assert.Null(r.Full.Read(f => f.Entry("d1", "download")));
        Assert.Equal("ai", r.Full.Read(f => f.Entry("a1", "download"))!.Hold);   // the AI hold stays in the full queue
        await r.Svc.TopUpAsync(default);   // 0.13.55: neither download is topped up; the new one is handed out when a node asks, the held one never
        Assert.Null(r.Work.Read(w => w.Entry("n1", "download"))); Assert.Null(r.Work.Read(w => w.Entry("a1", "download")));
        var handed = await r.Svc.NextAsync("dl1", ["download"], default); Assert.Equal(("n1", "download"), (handed!.Subject, handed.Stage));
        Assert.NotNull(r.Work.Read(w => w.Entry("n1", "download"))); Assert.Null(await r.Svc.NextAsync("dl2", ["download"], default));   // a1 stays held, n1 is taken
        var d1 = r.Proc.Read(p => p.Get("d1"))!; Assert.Equal("precheck", d1.Data); Assert.Equal("duplicate", d1.Entries["check"].State); Assert.Null(r.Proc.Read(p => p.Get("n1")));
        Assert.Equal(0, await r.Svc.CheckSamplesAsync(default));   // checked once
    }

    [Fact]
    public async Task A_node_that_sampled_an_upload_is_handed_its_download_at_once_and_is_told_why_when_there_is_none()
    {
        // 0.13.49 (owner, 2026-11-24): "Sampling a sample should immediately proceed with its download ... batched onto a node, like processing"
        var r = await Rig.Make(10, Add("n1", "sample", "2026-11-01T00:00:00Z", 200), Add("d1", "sample", "2026-11-02T00:00:00Z", 200), Add("a1", "sample", "2026-11-03T00:00:00Z", 200), Add("other", "sample", "2026-11-04T00:00:00Z", 200));
        r.Decisions["d1"] = new JsonObject { ["decision"] = "duplicate", ["matchUpload"] = "orig1", ["top"] = new JsonArray(new JsonObject { ["upload"] = "orig1", ["share"] = 0.93 }) };
        r.Decisions["a1"] = new JsonObject { ["decision"] = "new", ["ai"] = new JsonObject { ["flag"] = true, ["score"] = 0.6 } };
        await r.Svc.TopUpAsync(default);
        var dl = new JobQueue(r.S, P.DownloadWork, "node-a", () => T0, downloader: true); await dl.LoadAsync(default);
        async Task Sample(string id) { var h = await dl.TryClaimAsync(id, s => ["sample"], default); await h!.AppendAsync([new QLine { T = "done", Stage = "sample", State = "done" }], true, default); await h.FinishAsync(); }

        Assert.Equal("its sample is not recorded as done", (await r.Svc.SampledAsync("node-a", "n1", default)).Outcome);   // asked too early: nothing is checked or handed over
        Assert.Equal(0, r.Checks);

        await Sample("n1");
        var got = await r.Svc.SampledAsync("node-a", "n1", default);
        Assert.Equal("new", got.Outcome); Assert.Equal(("n1", "download"), (got.Claim!.Subject, got.Claim.Stage)); Assert.Equal(1, r.Checks);
        Assert.Equal("done", r.Full.Read(f => f.Entry("n1", "check"))!.State); Assert.Equal(200, r.Full.Read(f => f.Entry("n1", "download"))!.Priority);
        await dl.PollAsync(default);
        Assert.Equal("node-a", dl.Read(w => w.Get("n1"))!.ClaimBy);                      // claimed for the node that asked
        var adopted = dl.AdoptClaim(got.Claim.Subject, got.Claim.Epoch, got.Claim.Lease);
        await adopted.AppendAsync([new QLine { T = "done", Stage = "download", State = "done" }], true, default); await adopted.FinishAsync();   // the node runs it and reports
        await r.Svc.RunOnceAsync(default);
        Assert.Equal("done", r.Full.Read(f => f.Entry("n1", "download"))!.State); Assert.Equal("full", r.Proc.Read(p => p.Get("n1"))!.Data);   // and it enters processing like any download
        Assert.Equal(1, r.Checks);                                                       // the pass does not check it a second time

        await Sample("d1");
        var dup = await r.Svc.SampledAsync("node-a", "d1", default);
        Assert.Null(dup.Claim); Assert.Equal("duplicate of orig1: nothing to download", dup.Outcome);
        Assert.Equal("duplicate", r.Proc.Read(p => p.Get("d1"))!.Entries["check"].State);  // settled exactly as the pass settles it

        await Sample("a1");
        var ai = await r.Svc.SampledAsync("node-a", "a1", default);
        Assert.Null(ai.Claim); Assert.Equal("its download is held (ai)", ai.Outcome); Assert.Equal("ai", r.Full.Read(f => f.Entry("a1", "download"))!.Hold);

        // an upload the pass already checked (the node asked a moment later): no second check, the download is still handed over, and to one node only
        await Sample("other"); await r.Svc.FoldBackAsync(default); Assert.Equal(1, await r.Svc.CheckSamplesAsync(default)); int before = r.Checks;
        var late = await r.Svc.SampledAsync("node-b", "other", default);
        Assert.Equal(before, r.Checks); Assert.Equal("other", late.Claim!.Subject);
        Assert.Equal("its download was taken by another node", (await r.Svc.SampledAsync("node-c", "other", default)).Outcome);
    }

    [Fact]
    public async Task A_nodes_call_does_not_wait_for_the_passes_other_checks_and_one_sample_is_checked_once_when_both_want_it()
    {
        // 0.13.50: 0.13.49 made the pass and a node's call take turns on one lock: the call waited behind the pass's whole batch (90 s live) and the node's timeout fired
        var r = await Rig.Make(10, Add("slow", "sample", "2026-11-01T00:00:00Z", 200), Add("mine", "sample", "2026-11-02T00:00:00Z", 200));
        var gate = new TaskCompletionSource(); int checks = 0; var started = new TaskCompletionSource();
        r.Svc = new DownloadService(r.Full, r.Work, r.Proc, async (id, ct) => { Interlocked.Increment(ref checks); if (id == "slow") { started.TrySetResult(); await gate.Task; } return new JsonObject { ["decision"] = "new" }; }, 10);
        await r.Svc.TopUpAsync(default);
        var dl = new JobQueue(r.S, P.DownloadWork, "node-a", () => T0, downloader: true); await dl.LoadAsync(default);
        async Task Sample(string id) { var h = await dl.TryClaimAsync(id, s => ["sample"], default); await h!.AppendAsync([new QLine { T = "done", Stage = "sample", State = "done" }], true, default); await h.FinishAsync(); }
        await Sample("slow"); await r.Svc.FoldBackAsync(default);
        var pass = r.Svc.CheckSamplesAsync(default);                                   // the pass is busy with a slow check
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Sample("mine");
        var got = await r.Svc.SampledAsync("node-a", "mine", default).WaitAsync(TimeSpan.FromSeconds(10));   // the node's call is answered while the pass is still inside its check
        Assert.Equal("mine", got.Claim!.Subject); Assert.False(pass.IsCompleted);
        var both = r.Svc.SampledAsync("node-b", "slow", default);                       // a node asks for the very sample the pass is checking: it waits for that one check, no second one
        await Task.Delay(100); Assert.False(both.IsCompleted);
        gate.SetResult();
        Assert.Equal("slow", (await both.WaitAsync(TimeSpan.FromSeconds(10))).Claim!.Subject); await pass.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, checks);                                                       // "slow" once, "mine" once
        Assert.Equal(1, r.Full.Read(f => f.Get("slow")!.Entries.Count(e => e.Key == "check")));
    }

    [Fact]
    public async Task A_downloaded_item_enters_processing_with_data_full_and_a_crash_before_enter_is_repaired()
    {
        var r = await Rig.Make(10, Sampled("n1"), Add("n1", "download", "2026-11-01T00:00:00Z"));
        var dl = new JobQueue(r.S, P.DownloadWork, "dl1", () => T0, downloader: true); await dl.LoadAsync(default);
        var got1 = await r.Svc.NextAsync("dl1", ["download"], default); await dl.PollAsync(default);
        var h = dl.AdoptClaim(got1!.Subject, got1.Epoch, got1.Lease); await h!.AppendAsync([new QLine { T = "done", Stage = "download", State = "done" }], true, default); await h.FinishAsync();
        Assert.Null(r.Proc.Read(p => p.Get("n1")));
        var pass = await r.Svc.RunOnceAsync(default); Assert.Equal(1, pass.Entered);
        Assert.Equal("full", r.Proc.Read(p => p.Get("n1"))!.Data); Assert.Equal("done", r.Full.Read(f => f.Entry("n1", "download"))!.State);
        Assert.Equal(0, (await r.Svc.RunOnceAsync(default)).Entered);   // enter for an existing subject is a no-op
        // the crash: done download in the full log, enter lost -> the next pass enters it
        await r.Full.AppendLinesAsync([Add("n2", "download", "2026-11-02T00:00:00Z", 0, "done")], default); Assert.Null(r.Proc.Read(p => p.Get("n2")));
        Assert.Equal(1, (await r.Svc.RunOnceAsync(default)).Entered); Assert.NotNull(r.Proc.Read(p => p.Get("n2")));
    }

    [Fact]
    public async Task The_fallback_claims_for_the_caller_two_callers_never_get_the_same_item_and_none_when_empty()
    {
        var r = await Rig.Make(10, Enumerable.Range(0, 3).Select(i => Add("v" + i, "sample", $"2026-11-0{i + 1}T00:00:00Z")).ToArray());
        var calls = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => r.Svc.NextAsync("node" + i, ["sample", "download"], default))));
        var got = calls.Where(c => c != null).Select(c => c!.Subject).ToList(); Assert.Equal(3, got.Count); Assert.Equal(3, got.Distinct().Count());
        var c0 = calls.First(c => c != null)!; Assert.Equal("index/jobs/download/work/active/" + c0.Subject + ".json", c0.Active);
        Assert.Equal(c0.Subject, r.Work.Read(w => w.Get(c0.Subject))!.Subjects().Id); 
    }

    [Fact]
    public async Task A_claim_the_service_made_for_a_node_is_adopted_renewed_and_finished_by_that_node()
    {
        var r = await Rig.Make(10, Sampled("v1"), Add("v1", "download", "2026-11-01T00:00:00Z"));
        var got = await r.Svc.NextAsync("dl1", ["download"], default); Assert.NotNull(got);
        Assert.Equal("dl1", r.Work.Read(w => w.Get("v1"))!.ClaimBy);   // the claim line names the node
        var dl = new JobQueue(r.S, P.DownloadWork, "dl1", () => T0, downloader: true); await dl.LoadAsync(default);
        var h = dl.AdoptClaim(got!.Subject, got.Epoch, got.Lease);
        await r.S.RenewLeaseAsync(got.Active, got.Lease, default);   // what the heartbeat does
        await h.AppendAsync([new QLine { T = "done", Stage = "download", State = "done" }], true, default); await h.FinishAsync();
        Assert.Equal("done", dl.Read(w => w.Entry("v1", "download"))!.State); Assert.Null(dl.Read(w => w.Get("v1"))!.ClaimBy); Assert.False(r.S.Exists(got.Active));
        var other = await Rig.Make(10, Sampled("v2"), Add("v2", "download", "2026-11-01T00:00:00Z")); var g2 = await other.Svc.NextAsync("dl1", [], default);
        var wrong = new JobQueue(other.S, P.DownloadWork, "dl2", () => T0, downloader: true); await wrong.LoadAsync(default);
        await Assert.ThrowsAsync<LeaseLostException>(() => wrong.AdoptClaim(g2!.Subject, g2.Epoch, g2.Lease).AppendAsync([new QLine { T = "done", Stage = "download", State = "done" }], true, default));   // another node cannot use it
    }

    [Fact]
    public async Task A_downloader_never_reads_the_full_queue_drains_the_work_queue_and_gets_none_when_the_service_has_nothing()
    {
        var r = await Rig.Make(10, Enumerable.Range(0, 2).Select(i => Add("v" + i, "sample", $"2026-11-0{i + 1}T00:00:00Z")).ToArray());
        await r.Svc.TopUpAsync(default); await r.S.PutAsync(P.Moved, System.Text.Encoding.UTF8.GetBytes("{\"v\":2}"), null, null, false, default); while (r.S.Seen.TryDequeue(out _)) { }
        var dl = new JobQueue(r.S, P, "dl1", () => T0, downloader: true); await dl.LoadAsync(default);   // the downloader opens the queue by the normal paths and lands in work/
        Assert.Equal("index/jobs/download/work/", dl.Paths.QueueDir);
        var look = await dl.LookAsync(st => st is "sample" or "download", null, default); Assert.Equal(2, look.Candidates.Count);
        foreach (var (s, _) in look.Candidates) { var h = await dl.TryClaimAsync(s.Id, x => ["sample"], default); await h!.AppendAsync([new QLine { T = "done", Stage = "sample", State = "done" }], true, default); await h.FinishAsync(); }
        look = await dl.LookAsync(st => st is "sample" or "download", null, default); Assert.Empty(look.Candidates);   // drained: the service is the only thing that can refill it
        Assert.All(r.S.Seen.ToList(), n => Assert.True(n.StartsWith("index/jobs/download/work/") || n == P.Moved || !n.StartsWith("index/jobs/"), n));
        var empty = await Rig.Make(10); Assert.Null(await empty.Svc.NextAsync("node1", ["sample"], default));
    }
}

static class QSubjectExt { public static QSubject Subjects(this QSubject s) => s; }
