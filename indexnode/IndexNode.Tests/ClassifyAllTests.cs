using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode.Tests;

public class ClassifyAllTests
{
    private static readonly Paths P = new("index-test/");
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static string Id(int i) => "v" + i.ToString("D10");

    private sealed class Chat(Func<string, string> answer) : IChat
    {
        public readonly List<string> Prompts = [];
        public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) { var p = content.GetValue<string>(); Prompts.Add(p); return Task.FromResult(answer(p)); }
    }

    /// <summary>An AI that calls every upload of the prompt a drama, except ids listed in <paramref name="not"/>.</summary>
    private static Chat Yes(params string[] not) => new(p =>
    {
        var ids = p.Split('\n').Where(l => Regex.IsMatch(l, @"^v\d{10} \|")).Select(l => l[..11]).ToList();
        return "{\"videos\":[" + string.Join(",", ids.Select(i => $"{{\"id\":\"{i}\",\"candidate\":{(not.Contains(i) ? "false" : "true")},\"kind\":\"{(not.Contains(i) ? "recap" : "full")}\",\"language\":\"zh\",\"reason\":\"r\"}}")) + "]}";
    });

    private static JobDescriptor Dl(string id, double? minutes, string title = "A drama", string? channel = "Chan", string? availability = null, string added = "2026-11-17T10:00:00Z")
    {
        var d = JobDescriptor.NewDownload($"https://www.youtube.com/watch?v={id}", 480, null); d.JobId = id; d.AddedAt = added;
        d.Title = title; d.Duration = minutes * 60; d.ChannelName = channel; d.ChannelId = "UC" + new string('a', 22); d.Source = "ch-x"; d.Availability = availability;
        return d;
    }

    private static Task Add(FakeStore s, JobDescriptor d) => ClassifyJobs.CreateUnclassifiedAsync(s, P, d, default);

    private static async Task<JobContext> RunAll(FakeStore s, IChat? chat, DateTimeOffset? now = null)
    {
        await ClassifyJobs.EnsureSingletonAsync(s, P, default);
        var job = s.Job(ClassifyJobs.SingletonBlob(P));
        var ctx = new JobContext { Job = job, Stage = "classify", BlobName = ClassifyJobs.SingletonBlob(P), LeaseId = "l", Worker = 1, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new ClassifyStage(s, () => now ?? Now) { Ai = () => chat == null ? null : (chat, "m") }.RunAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task A_new_download_job_is_unclassified_with_its_classification_fields_and_creates_the_singleton()
    {
        var s = new FakeStore();
        Assert.True(await ClassifyJobs.CreateUnclassifiedAsync(s, P, Dl(Id(1), 90, "Big Drama", "Chan")));
        Assert.False(await ClassifyJobs.CreateUnclassifiedAsync(s, P, Dl(Id(1), 90)));
        var name = P.Job("download", Id(1));
        Assert.Equal("unclassified", s.State(name));
        var d = s.Job(name);
        Assert.Equal("Big Drama", d.Title); Assert.Equal(5400, d.Duration); Assert.Equal("Chan", d.ChannelName); Assert.Equal("ch-x", d.Source); Assert.NotNull(d.ChannelId);
        Assert.Equal("pending", s.State(ClassifyJobs.SingletonBlob(P)));
    }

    [Fact]
    public void A_listing_line_carries_the_classification_fields()
    {
        var e = WatchStage.ParseLine("aaaaaaaaaaa\tUCbbbbbbbbbbbbbbbbbbbbbb\tChan\t3600\tNA\tTitle A\twith a tab")!;
        Assert.Equal(("Chan", 3600d, "Title A with a tab", null), (e.ChannelName, e.Duration, e.Title, e.Availability));
        var dead = WatchStage.ParseLine("aaaaaaaaaaa\tNA\tNA\tNA\tprivate\t[Private video]")!;
        Assert.Equal(("private", null), (dead.Availability, dead.Duration));
    }

    [Fact]
    public async Task No_claim_query_matches_unclassified_jobs_and_batches_never_pick_them()
    {
        var s = new FakeStore();
        await Add(s, Dl(Id(1), 90));
        foreach (var where in new[] { Tags.PendingWhere("download"), Tags.PendingWhere("download", "2099-01-01T00:00:00Z") })
            Assert.DoesNotContain(await s.FindByTagsAsync(where, default), b => b.Name == P.Job("download", Id(1)));
        Assert.Contains(await s.FindByTagsAsync(Tags.UnclassifiedWhere, default), b => b.Name == P.Job("download", Id(1)));
        // the 0.9.0 batches pick dramas from the CPU step stages only: neither download nor classify is one of them
        Assert.DoesNotContain("download", StageOrder.PassOrder); Assert.DoesNotContain("classify", StageOrder.PassOrder);
        var picks = await StageOrder.DramasAsync(StageOrder.PassOrder, (st, ct) => Task.FromResult<IReadOnlyList<BlobEntry>>(s.FindByTagsAsync(Tags.PendingWhere(st), ct).Result), (_, _, _) => Task.FromResult(true), default);
        Assert.Empty(picks);
        // the classify job holds nothing: a hold blob is per drama and the singleton's id is not a video
        Assert.DoesNotContain(s.Names(P.HoldsDir), n => n.Contains("all"));
        // the Medusa claim queries (BlobClient.kt, IndexStore.kt, NodeCore.kt) match idx_state == pending only
        var root = AppContext.BaseDirectory; while (root != null && !Directory.Exists(Path.Combine(root, "medusa"))) root = Path.GetDirectoryName(root);
        if (root != null)
            foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "medusa", "app", "src", "main"), "*.kt", SearchOption.AllDirectories))
                Assert.DoesNotContain("unclassified", File.ReadAllText(f));
    }

    [Fact]
    public async Task The_singleton_is_created_once_under_concurrent_creators()
    {
        var s = new FakeStore();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => ClassifyJobs.CreateUnclassifiedAsync(s, P, Dl(Id(i), 90)))));
        Assert.Single(s.Blobs.Keys, k => k.Contains("/jobs/classify/"));
        Assert.Equal(1, s[ClassifyJobs.SingletonBlob(P)].Version);                              // written once, never rewritten while it is pending
        Assert.Equal(40, s.Blobs.Keys.Count(k => k.Contains("/jobs/download/")));
    }

    [Fact]
    public async Task A_run_applies_stored_then_length_then_unavailable_then_ai_and_sets_pending_or_failed_with_the_reason()
    {
        var s = new FakeStore();
        await Add(s, Dl(Id(1), 90, added: "2026-11-17T10:00:01Z"));                                   // stored: not a drama, although 90 min
        await ClassifyStage.SaveDecisionAsync(s, P, Id(1), null, null, null, null, false, "recap", "stored one", "owner", null, "2026-11-16T00:00:00Z", default);
        await Add(s, Dl(Id(2), 5, added: "2026-11-17T10:00:02Z"));                                    // short
        await Add(s, Dl(Id(3), null, "[Private video]", availability: null, added: "2026-11-17T10:00:03Z")); // dead by title
        await Add(s, Dl(Id(4), 120, availability: "private", added: "2026-11-17T10:00:04Z"));          // dead by availability, with a duration
        await Add(s, Dl(Id(5), 100, added: "2026-11-17T10:00:05Z"));                                  // ai: yes
        await Add(s, Dl(Id(6), 100, "Recap", added: "2026-11-17T10:00:06Z"));                         // ai: no
        var chat = Yes(Id(6));
        var ctx = await RunAll(s, chat);
        Assert.Single(chat.Prompts);
        Assert.Contains(Id(5), chat.Prompts[0]); Assert.Contains(Id(6), chat.Prompts[0]); Assert.Contains("Chan", chat.Prompts[0]);
        foreach (var i in new[] { 1, 2, 3, 4 }) Assert.DoesNotContain(Id(i), chat.Prompts[0]);
        string St(int i) => s.State(P.Job("download", Id(i)));
        Assert.Equal(["failed", "failed", "failed", "failed", "pending", "failed"], Enumerable.Range(1, 6).Select(St).ToArray());
        string Why(int i) => s.Job(P.Job("download", Id(i))).Reason!;
        Assert.Equal("classify: recap stored one", Why(1));
        Assert.StartsWith("classify: short ", Why(2));
        Assert.StartsWith("classify: unavailable ", Why(3)); Assert.StartsWith("classify: unavailable ", Why(4));
        Assert.StartsWith("classify: recap ", Why(6));
        Assert.True(s.Job(P.Job("download", Id(2))).Permanent);
        Assert.Equal(["rule", "rule", "rule", "ai", "ai"], Enumerable.Range(2, 5).Select(i => JsonNode.Parse(s.Blobs[ClassifyStage.Blob(P, Id(i))].Data)!["by"]!.ToString()).ToArray());
        Assert.Equal("unavailable", JsonNode.Parse(s.Blobs[ClassifyStage.Blob(P, Id(3))].Data)!["kind"]!.ToString());
        Assert.Equal(6, ctx.Job.Classified); Assert.Equal(5, ctx.Job.Rejected);
        Assert.Empty(await ClassifyJobs.ListUnclassifiedAsync(s, P, default));
    }

    [Fact]
    public async Task The_ai_gets_batches_of_40_and_a_run_takes_the_oldest_400()
    {
        var s = new FakeStore();
        for (int i = 0; i < 405; i++) await Add(s, Dl(Id(i), 90, added: $"2026-11-17T10:{i / 60:D2}:{i % 60:D2}Z"));
        var chat = Yes();
        var ctx = await RunAll(s, chat);
        Assert.Equal(10, chat.Prompts.Count);                                                          // 400 / 40
        var left = await ClassifyJobs.ListUnclassifiedAsync(s, P, default);
        Assert.Equal(5, left.Count); Assert.Equal(Id(400), Path.GetFileNameWithoutExtension(left[0].Name)); // the newest five stay
        Assert.Equal(Protocol.Utc(Now), ctx.NextDue);                                                 // more remain: due at once
    }

    [Fact]
    public async Task Due_in_ten_minutes_when_nothing_is_left_and_at_once_when_more_remain()
    {
        var s = new FakeStore();
        var idle = await RunAll(s, null);                                                              // nothing to do: no AI needed either
        Assert.Equal(Protocol.Utc(Now.AddMinutes(10)), idle.NextDue);
        await Add(s, Dl(Id(1), 90));
        var ctx = await RunAll(s, Yes());
        Assert.Equal(Protocol.Utc(Now.AddMinutes(10)), ctx.NextDue);                                  // this run left nothing unclassified
        for (int i = 10; i < 411; i++) await Add(s, Dl(Id(i), 5));
        Assert.Equal(Protocol.Utc(Now), (await RunAll(s, null)).NextDue);                              // 401 left over the cap of 400: again at once
    }

    [Fact]
    public async Task A_node_without_ai_applies_the_rules_and_skips_the_rest_without_spending_an_attempt()
    {
        var s = new FakeStore();
        await Add(s, Dl(Id(1), 5)); await Add(s, Dl(Id(2), 90));
        await Assert.ThrowsAsync<JobSkipped>(() => RunAll(s, null));
        Assert.Equal("failed", s.State(P.Job("download", Id(1)))); Assert.Equal("unclassified", s.State(P.Job("download", Id(2))));
    }

    [Fact]
    public async Task Old_per_channel_classify_jobs_still_complete_once()
    {
        var s = new FakeStore();
        var job = JobDescriptor.NewClassify(JobDescriptor.NewWatch("channel", "UCaaaaaaaaaaaaaaaaaaaaaa", "Old", "1h", ["videos"], false, 0, 480),
            [new ClassifyEntry { Id = "aaaaaaaaaaa", Title = "Drama", Duration = 5400 }, new ClassifyEntry { Id = "bbbbbbbbbbb", Title = "clip", Duration = 60 }], "2026-11-14T12:00:00Z");
        var ctx = new JobContext { Job = job, Stage = "classify", BlobName = P.Job("classify", job.JobId), LeaseId = "l", Worker = 1, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        var chat = new Chat(_ => """{"videos":[{"id":"aaaaaaaaaaa","candidate":true,"kind":"full","reason":"x"}]}""");
        await new ClassifyStage(s) { Ai = () => (chat, "m") }.RunAsync(ctx);
        Assert.Null(ctx.NextDue);                                                                      // done: the engine does not bring it back
        Assert.Equal("pending", s.State(P.Job("download", "aaaaaaaaaaa")));
        Assert.False(s.Exists(P.Job("download", "bbbbbbbbbbb")));
    }

    [Fact]
    public async Task Set_overrides_a_decision_in_both_directions_and_a_stored_decision_beats_the_rules()
    {
        var s = new FakeStore();
        await Add(s, Dl(Id(1), 5));                                                                    // the length rule would reject
        Assert.Contains("drama", await ClassifyCommand.SetAsync(s, P, Id(1), "drama", "it is a drama", Now, default));
        Assert.Equal("pending", s.State(P.Job("download", Id(1))));
        Assert.Equal("owner", JsonNode.Parse(s.Blobs[ClassifyStage.Blob(P, Id(1))].Data)!["by"]!.ToString());
        await Add(s, Dl(Id(2), 5));
        await ClassifyCommand.SetAsync(s, P, Id(2), "drama", null, Now, default);
        await Add(s, Dl(Id(3), 90));
        await RunAll(s, Yes());
        Assert.Equal("pending", s.State(P.Job("download", Id(2))));                                    // not rejected as short
        await ClassifyCommand.SetAsync(s, P, Id(3), "not", "wrong call", Now, default);
        Assert.Equal("failed", s.State(P.Job("download", Id(3))));
        Assert.Equal("classify: not-drama wrong call", s.Job(P.Job("download", Id(3))).Reason);
        Assert.True(s.Job(P.Job("download", Id(3))).Permanent);
        await ClassifyCommand.SetAsync(s, P, Id(3), "drama", null, Now, default);                       // a rejection can be overruled
        Assert.Equal("pending", s.State(P.Job("download", Id(3)))); Assert.Null(s.Job(P.Job("download", Id(3))).Permanent);
        await Assert.ThrowsAsync<ArgumentException>(() => ClassifyCommand.SetAsync(s, P, Id(3), "maybe", null, Now, default));
        var done = Dl(Id(4), 90); await s.PutAsync(P.Job("download", Id(4)), done.ToBytes(), Tags.Job("download", "done", done.AddedAt), null, true, default);
        Assert.Contains("left alone", await ClassifyCommand.SetAsync(s, P, Id(4), "not", null, Now, default));
        Assert.Equal("done", s.State(P.Job("download", Id(4))));
    }

    [Fact]
    public async Task Rejections_are_listed_with_their_reasons_and_status_counts_today()
    {
        var s = new FakeStore();
        await Add(s, Dl(Id(1), 5, "Tiny clip")); await Add(s, Dl(Id(2), 90));
        var ran = await RunAll(s, Yes());
        var row = Assert.Single(await ClassifyCommand.RejectedAsync(s, P, Now.AddDays(-1), default));
        Assert.Contains(Id(1), row); Assert.Contains("classify: short", row); Assert.Contains("Tiny clip", row);
        Assert.Empty(await ClassifyCommand.RejectedAsync(s, P, Now.AddDays(1), default));
        s.Blobs[ClassifyJobs.SingletonBlob(P)].Data = ran.Job.ToBytes();                               // what the engine does after the run
        Assert.Equal(("2026-11-17", 2, 1), await ClassifyCommand.TodayAsync(s, P, Now, default));
        Assert.Equal(("2026-11-18", 0, 0), await ClassifyCommand.TodayAsync(s, P, Now.AddDays(1), default));
    }

    [Fact]
    public async Task Requeue_sets_a_sources_pending_downloads_back_to_unclassified_and_nothing_else()
    {
        var s = new FakeStore();
        var a = Dl(Id(1), 90); a.Source = "pl-A"; var b = Dl(Id(2), 90); b.Source = "pl-B"; var c = Dl(Id(3), 90); c.Source = "pl-A";
        foreach (var d in new[] { a, b, c }) await s.PutAsync(P.Job("download", d.JobId), d.ToBytes(), Tags.Job("download", d == c ? "done" : "pending", d.AddedAt, null, 7), null, true, default);
        Assert.Equal(1, await ClassifyCommand.RequeueSourceAsync(s, P, "pl-A", default));
        Assert.Equal("unclassified", s.State(P.Job("download", Id(1)))); Assert.Equal("7", s.Blobs[P.Job("download", Id(1))].Tags[Tags.Priority]);
        Assert.Equal("pending", s.State(P.Job("download", Id(2)))); Assert.Equal("done", s.State(P.Job("download", Id(3))));
        Assert.Equal("pending", s.State(ClassifyJobs.SingletonBlob(P)));
    }

    [Fact]
    public async Task A_failed_singleton_is_made_pending_again_by_the_next_creator()
    {
        var s = new FakeStore();
        await ClassifyJobs.EnsureSingletonAsync(s, P, default);
        var j = s.Job(ClassifyJobs.SingletonBlob(P)); j.Attempts = 3;
        await s.PutAsync(ClassifyJobs.SingletonBlob(P), j.ToBytes(), Tags.Job("classify", "failed", j.AddedAt), null, false, default);
        await Add(s, Dl(Id(1), 90));
        Assert.Equal("pending", s.State(ClassifyJobs.SingletonBlob(P)));
        Assert.Equal(0, s.Job(ClassifyJobs.SingletonBlob(P)).Attempts);
    }

    [Fact]
    public void Dead_video_detection_uses_the_availability_or_a_placeholder_title_without_duration()
    {
        Assert.True(ClassifyJobs.IsUnavailable(Dl("aaaaaaaaaaa", 60, availability: "private")));
        Assert.True(ClassifyJobs.IsUnavailable(Dl("aaaaaaaaaaa", null, "[Deleted video]")));
        Assert.False(ClassifyJobs.IsUnavailable(Dl("aaaaaaaaaaa", 60, "[Private video]")));             // a real duration: the title alone proves nothing
        Assert.False(ClassifyJobs.IsUnavailable(Dl("aaaaaaaaaaa", null, "Some title", availability: "public")));
    }
}
