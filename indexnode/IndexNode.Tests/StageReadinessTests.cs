using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.24: stages that become ready on their own (actors after group, the actors-index singleton), the AI hold, seq order, and the one-time import. Fakes only.</summary>
public class StageReadinessTests
{
    static readonly Paths P = new("index/");
    sealed class Clock { public DateTimeOffset T = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero); public DateTimeOffset Now() => T; public void Add(TimeSpan d) => T += d; }
    static (FakeStore, Clock) NewStore() { var c = new Clock(); return (new FakeStore { Clock = c.Now }, c); }
    static async Task<JobQueue> Node(FakeStore s, Clock c, string id) { var q = new JobQueue(s, P, id, c.Now); await q.LoadAsync(default); return q; }
    static readonly ReadyOptions Ro = new(3, 10);
    static bool Ok(QSubject s, QEntry e) => !StageDeps.Held(e);

    static QLine GroupDone(string id, string at, long? seq = null) => QLine.Add(id, "group", new JobDescriptor { Stage = "group", JobId = id, AddedAt = at }, at, null, null, "done");
    /// <summary>0.13.32: the cast step of the upload's process entry finished at the given version (a done line with step "cast" and sv).</summary>
    static async Task CastAt(JobQueue q, string id, int ver) { await q.AppendLinesAsync([QLine.Add(id, "process", new JobDescriptor { Stage = "process", JobId = id }, q.NowUtc())], default); await q.AppendLinesAsync([new QLine { T = "done", S = id, Stage = "process", Step = "cast", StepVer = ver }], default); }
    static async Task Group(JobQueue q, string id, long? seq = null, int castVer = 2) { await q.AppendLinesAsync([GroupDone(id, q.NowUtc())], default); if (seq != null) await q.AppendLinesAsync([new QLine { T = "seq", S = id, Seq = seq }], default); if (castVer > 0) await CastAt(q, id, castVer); }
    static List<string> Ready(JobQueue q, Clock c, string stage = "actors") => q.Read(st => st.Candidates(Protocol.Utc(c.T), x => true, Ok, null, Ro)).Where(x => x.Runnable.Any(e => e.Stage == stage)).Select(x => x.Subject.Id).ToList();
    static string[]? Pick(QueueState st, QSubject s, Clock c) => StageDeps.Derived.Where(d => StageDeps.ReadyEntry(st, s, d, Protocol.Utc(c.T), Ro) != null).ToArray() is { Length: > 0 } r ? r : null;
    static async Task Finish(JobQueue q, Clock c, string id)   // a claim of the ready actors stage, then its done line
    {
        var h = (await q.TryClaimAsync(id, (st, s) => Pick(st, s, c)?.Where(x => x == "actors").ToArray() is { Length: > 0 } r ? r : null, default))!; Assert.NotNull(h);
        c.Add(TimeSpan.FromSeconds(1)); await h.AppendAsync([new QLine { T = "done", Stage = "actors" }], true, default); await h.FinishAsync();
    }

    [Fact]
    public async Task A_group_done_makes_actors_ready_with_no_add_line_and_the_claim_creates_the_entry()
    {
        var (s, c) = NewStore(); var q = await Node(s, c, "a");
        await q.AppendLinesAsync([QLine.Add("v1", "process", new JobDescriptor { Stage = "process", JobId = "v1" }, q.NowUtc())], default);
        Assert.Empty(Ready(q, c));                                    // no group yet: not ready
        await Group(q, "v1"); Assert.Null(q.Read(st => st.Entry("v1", "actors")));
        Assert.Equal(["v1"], Ready(q, c));                            // ready on its own: nobody added anything
        var h = (await q.TryClaimAsync("v1", (st, sub) => Pick(st, sub, c), default))!; Assert.NotNull(h);
        Assert.Equal("pending", q.Read(st => st.Entry("v1", "actors"))!.State);   // the claim line created the entry
        await h.AppendAsync([new QLine { T = "done", Stage = "actors" }], true, default); await h.FinishAsync();
        Assert.Empty(Ready(q, c));                                    // final: not ready again
        Assert.DoesNotContain(s.AppendText("index/jobs/queue.log").Split('\n'), l => l.Contains("\"st\":\"actors\"") && l.Contains("\"t\":\"add\""));
        var b = await Node(s, c, "b"); Assert.Equal("done", b.Read(st => st.Entry("v1", "actors"))!.State);   // another node replays the same state
    }

    [Fact]
    public async Task An_AI_hold_keeps_actors_unready_and_a_manual_add_releases_it()
    {
        var (s, c) = NewStore(); var q = await Node(s, c, "a");
        await Group(q, "ai1"); await Group(q, "ok1");
        await q.AppendLinesAsync([new QLine { T = "add", S = "ai1", Stage = "actors", Job = new JobDescriptor { Stage = "actors", JobId = "ai1" }, At = q.NowUtc(), Hold = "ai" }], default);
        Assert.Equal(["ok1"], Ready(q, c));                           // the held one is never ready
        Assert.Null(Pick(q.Read(st => st), q.Read(st => st.Get("ai1")!), c));
        await q.AppendLinesAsync([ActorsAdd.ManualLine("ai1", q.Read(st => st.Entry("ai1", "actors")), 0)], default);
        c.T = DateTimeOffset.UtcNow.AddMinutes(1);   // ManualLine stamps the real clock as the due time
        Assert.Null(q.Read(st => st.Entry("ai1", "actors"))!.Hold);
        Assert.Equal(["ai1", "ok1"], Ready(q, c).OrderBy(x => x, StringComparer.Ordinal).ToList());   // the manual reason released it
        Assert.Null(q.Read(st => st.Entry("ai1", "actors"))!.Hold);
    }

    [Fact]
    public async Task Ready_actors_jobs_come_in_grouping_seq_order_with_no_window()
    {
        var (s, c) = NewStore(); var q = await Node(s, c, "a");
        foreach (var (id, sq) in new[] { ("c", 30), ("a", 10), ("d", 40), ("b", 20) }) await Group(q, id, sq);
        Assert.Equal(["a", "b", "c", "d"], q.Read(st => st.Candidates(Protocol.Utc(c.T), x => true, Ok, null, Ro)).Select(x => x.Subject.Id).ToList());
        Assert.Equal(["a", "b", "c", "d"], (await q.LookAsync(x => true, Ok, default, Ro)).Candidates.Select(x => x.Subject.Id).ToList());
        for (int i = 0; i < 200; i++) await Group(q, "z" + i.ToString("D3"), 100 + i);
        Assert.Equal(204, Ready(q, c).Count);                          // all of them at once: no batch limit
    }

    [Fact]
    public async Task The_actors_index_is_ready_at_N_unindexed_uploads_or_when_the_oldest_waited_T_and_only_one_node_builds()
    {
        var (s, c) = NewStore(); var q = await Node(s, c, "a"); var other = await Node(s, c, "b");
        foreach (var id in new[] { "u1", "u2" }) { await Group(q, id); await Finish(q, c, id); }
        Assert.Empty(Ready(q, c, "actors-index"));                    // 2 < N=3, and the oldest has not waited 10 minutes
        c.Add(TimeSpan.FromMinutes(9)); Assert.Empty(Ready(q, c, "actors-index"));
        c.Add(TimeSpan.FromMinutes(2)); Assert.Equal(["actors-index"], Ready(q, c, "actors-index"));   // T reached: a tail is never left
        await Group(q, "u3"); await Finish(q, c, "u3");
        Assert.Equal(["actors-index"], Ready(q, c, "actors-index"));
        // one builder: two nodes race for the singleton, one lease
        await other.PollAsync(default);
        Func<QueueState, QSubject, string[]?> pick = (st, sub) => StageDeps.ReadyEntry(st, sub, "actors-index", Protocol.Utc(c.T), Ro) != null || sub.Entries.TryGetValue("actors-index", out var e) && e.State == "pending" ? ["actors-index"] : null;
        var won = (await Task.WhenAll(q.TryClaimAsync("actors-index", pick, default), other.TryClaimAsync("actors-index", pick, default))).Where(h => h != null).ToList();
        Assert.Single(won);
        c.Add(TimeSpan.FromSeconds(1)); await won[0]!.AppendAsync([new QLine { T = "done", Stage = "actors-index" }], true, default); await won[0]!.FinishAsync();
        Assert.Empty(Ready(q, c, "actors-index"));                    // the build took them in: nothing unindexed now
        c.Add(TimeSpan.FromMinutes(30)); Assert.Empty(Ready(q, c, "actors-index"));
        foreach (var id in new[] { "u4", "u5", "u6" }) { await Group(q, id); await Finish(q, c, id); }
        Assert.Equal(["actors-index"], Ready(q, c, "actors-index"));  // N new ones: the next build is ready again
    }

    [Fact]
    public async Task The_import_dry_run_counts_and_a_real_run_then_a_rerun_add_nothing_twice()
    {
        var (s, c) = NewStore();
        foreach (var (id, sq, ai) in new[] { ("i1", 1, false), ("i2", 2, true), ("i3", 3, false), ("i4", 4, false) }) await UpdateGrouping(s, id, sq, ai);
        var q = await Node(s, c, "a"); await Group(q, "i4", 4, 0);       // i4 is already in the queue with group done and its seq
        var ops0 = s.Gets.Count; var dry = await QueueCommand.ImportDoneGroupAsync(q, s, P, true, default);
        Assert.Equal(4, dry.Grouped); Assert.Equal(3, dry.GroupDone); Assert.Equal(3, dry.Seqs); Assert.Equal(1, dry.Holds); Assert.Equal(0, dry.Appends);
        Assert.Null(q.Read(st => st.Get("i1")));                       // a dry run writes nothing
        var real = await QueueCommand.ImportDoneGroupAsync(q, s, P, false, default);
        Assert.Equal(3, real.GroupDone); Assert.True(real.Appends >= 1);
        Assert.Equal("done", q.Read(st => st.Entry("i1", "group"))!.State); Assert.Equal(2, q.Read(st => st.Get("i2")!.Seq)); Assert.Equal("ai", q.Read(st => st.Entry("i2", "actors"))!.Hold);
        Assert.Empty(Ready(q, c));   // 0.13.32: a done group alone is not enough: actors also needs the cast step at v2
        foreach (var id in new[] { "i1", "i3", "i4" }) await CastAt(q, id, 2);
        Assert.Equal(["i1", "i3", "i4"], Ready(q, c).OrderBy(x => x, StringComparer.Ordinal).ToList());   // imported done groups with their cast repaired make actors ready; the AI one is held
        var again = await QueueCommand.ImportDoneGroupAsync(q, s, P, false, default);
        Assert.Equal(0, again.GroupDone); Assert.Equal(0, again.Seqs); Assert.Equal(0, again.Holds);
    }

    static async Task UpdateGrouping(FakeStore fs, string id, long seq, bool ai) => await VideoJson.UpdateAsync(fs, P, id, r =>
    {
        r["grouping"] = new System.Text.Json.Nodes.JsonObject { ["v"] = 1, ["proposal"] = new System.Text.Json.Nodes.JsonObject { ["kind"] = "new", ["mixed"] = false }, ["seq"] = seq };
        if (ai) r["ai"] = new System.Text.Json.Nodes.JsonObject { ["score"] = 0.9, ["flag"] = true, ["source"] = "test" };
    }, default);
}
