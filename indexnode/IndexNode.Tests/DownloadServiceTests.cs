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
    static QLine Add(string id, string stage, string pub, int pri = 0, string? state = null) => QLine.Add(id, stage, new JobDescriptor { Stage = stage, JobId = id, Url = "https://www.youtube.com/watch?v=" + id, AddedAt = At, Pub = pub }, At, pri, At, state);

    [Fact]
    public async Task A_prioritised_item_joins_a_full_work_queue_at_once_and_is_the_next_one_handed_out()
    {
        var lines = Enumerable.Range(0, 30).Select(i => Add("v" + i.ToString("00"), "sample", $"2026-11-{1 + i % 28:00}T00:00:00Z", i == 3 ? 9 : 0)).ToList();
        var r = await Rig.Make(10, lines.ToArray());
        Assert.Equal(10, await r.Svc.TopUpAsync(default));   // the work queue is full (10 of 10), its highest priority is 9
        await r.Full.AppendLinesAsync([Add("asked", "download", "2020-01-01T00:00:00Z", 1000), Add("same9", "sample", "2026-11-30T00:00:00Z", 9), Add("low", "sample", "2026-11-30T00:00:00Z", 1)], default);
        Assert.Equal(1, await r.Svc.TopUpAsync(default));    // only the item above every live priority jumps; an equal or lower one waits for the ordinary top-up
        var ids = r.Work.Read(w => w.Subjects.Values.Select(s => s.Id).ToList());
        Assert.Contains("asked", ids); Assert.DoesNotContain("same9", ids); Assert.DoesNotContain("low", ids); Assert.Equal(11, ids.Count);
        Assert.Equal(0, await r.Svc.TopUpAsync(default));    // idempotent: it is in the work queue now
        var next = await r.Svc.NextAsync("laptop", ["download", "sample"], default);
        Assert.Equal("asked", next!.Subject); Assert.Equal("download", next.Stage);   // and it is what a downloader gets next
        var many = Enumerable.Range(0, 80).Select(i => Add("hi" + i.ToString("00"), "sample", "2026-11-30T00:00:00Z", 5000)).ToArray();
        await r.Full.AppendLinesAsync(many, default);
        Assert.Equal(DownloadService.JumpMax, await r.Svc.TopUpAsync(default));   // bounded per pass
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
        await r.Svc.TopUpAsync(default);   // the new download item is topped up into work; the held one is not
        Assert.NotNull(r.Work.Read(w => w.Entry("n1", "download"))); Assert.Null(r.Work.Read(w => w.Entry("a1", "download")));
        var d1 = r.Proc.Read(p => p.Get("d1"))!; Assert.Equal("precheck", d1.Data); Assert.Equal("duplicate", d1.Entries["check"].State); Assert.Null(r.Proc.Read(p => p.Get("n1")));
        Assert.Equal(0, await r.Svc.CheckSamplesAsync(default));   // checked once
    }

    [Fact]
    public async Task A_downloaded_item_enters_processing_with_data_full_and_a_crash_before_enter_is_repaired()
    {
        var r = await Rig.Make(10, Add("n1", "download", "2026-11-01T00:00:00Z"));
        await r.Svc.TopUpAsync(default);
        var dl = new JobQueue(r.S, P.DownloadWork, "dl1", () => T0, downloader: true); await dl.LoadAsync(default);
        var h = await dl.TryClaimAsync("n1", s => ["download"], default); await h!.AppendAsync([new QLine { T = "done", Stage = "download", State = "done" }], true, default); await h.FinishAsync();
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
        var r = await Rig.Make(10, Add("v1", "download", "2026-11-01T00:00:00Z"));
        var got = await r.Svc.NextAsync("dl1", ["download"], default); Assert.NotNull(got);
        Assert.Equal("dl1", r.Work.Read(w => w.Get("v1"))!.ClaimBy);   // the claim line names the node
        var dl = new JobQueue(r.S, P.DownloadWork, "dl1", () => T0, downloader: true); await dl.LoadAsync(default);
        var h = dl.AdoptClaim(got!.Subject, got.Epoch, got.Lease);
        await r.S.RenewLeaseAsync(got.Active, got.Lease, default);   // what the heartbeat does
        await h.AppendAsync([new QLine { T = "done", Stage = "download", State = "done" }], true, default); await h.FinishAsync();
        Assert.Equal("done", dl.Read(w => w.Entry("v1", "download"))!.State); Assert.Null(dl.Read(w => w.Get("v1"))!.ClaimBy); Assert.False(r.S.Exists(got.Active));
        var other = await Rig.Make(10, Add("v2", "download", "2026-11-01T00:00:00Z")); var g2 = await other.Svc.NextAsync("dl1", [], default);
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
