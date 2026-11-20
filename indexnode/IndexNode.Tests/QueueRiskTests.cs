using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode.Tests;

/// <summary>Tests put back on the queue for behaviour with real risk: priority inheritance, classify batching and misses, comments inside the download.</summary>
[Collection("path-env")]
public class QueueRiskTests : IDisposable
{
    public void Dispose() => CommentsTests.RestorePath();
    static readonly Paths P = new("index-test/");
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T12:00:00Z");
    static string Id(int i) => "v" + i.ToString("D10");
    static async Task<(FakeStore, JobQueue)> Q() { var s = new FakeStore(); var q = new JobQueue(s, P, "t1", () => Now); await q.LoadAsync(default); return (s, q); }

    // ------------------------------------------------------------ priority
    [Fact]
    public async Task A_download_hands_its_priority_to_the_process_entry_it_adds()
    {
        var s = new FakeStore(); var d = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + Id(1), 480, null); d.JobId = Id(1); d.Priority = 7;
        var ctx = CommentsTests.Ctx(s, d, "download");
        await s.PutAsync(P.Video(Id(1), "downloaded.json"), "{}"u8.ToArray(), null, null, false, default);
        await s.PutAsync(P.Video(Id(1), "comments.zip"), [1], null, null, false, default);   // comments already there: only the follow-up is added
        await new DownloadStage(null!, s, CommentsTests.NoWait).EnsureFollowUpJobs(ctx);
        var add = Assert.Single(ctx.FollowUps, l => l.T == "add" && l.Stage == "process");
        Assert.Equal(7, add.Pri ?? add.Job!.Priority);
    }

    [Fact]
    public async Task A_refresh_gives_the_videos_it_finds_the_sources_priority_and_a_video_in_several_sources_takes_the_highest()
    {
        var (s, q) = await Q();
        foreach (var (id, pri) in new[] { ("ch-a", 3), ("ch-b", 9) })
        { var j = JobDescriptor.NewSource("channel", id, id, "1d", ["videos"], false, 0, 480); j.JobId = id; j.AddedAt = Protocol.Utc(Now); await Sources.AddAsync(q, j, default); await q.AppendLinesAsync([new QLine { T = "pri", S = id, Pri = pri }], default); }
        foreach (var (id, src) in new[] { ("only-a", "ch-a"), ("both", "ch-a") })
        { var d = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + id, 480, null); d.JobId = id; d.Source = src; d.Sources = [src]; d.AddedAt = Protocol.Utc(Now); await q.AppendLinesAsync([QLine.Add(id, "download", d, d.AddedAt, 3)], default); }
        // priority set on source ch-b with the listing that contains "both": the video now lists both sources and takes the highest
        var r = await PriorityTool.SetSourceAsync(q, "ch-b", 9, ["both"], default);
        Assert.Equal(1, r.Videos); Assert.Equal(1, r.Matched);
        Assert.Equal(9, q.Read(st => st.Entry("both", "download"))!.Priority); Assert.Equal(3, q.Read(st => st.Entry("only-a", "download"))!.Priority);
        Assert.Contains("ch-b", q.Read(st => st.Entry("both", "download"))!.Job!.Sources!);
        // lowering one source keeps the other's higher value
        await PriorityTool.SetSourceAsync(q, "ch-a", 1, null, default);
        Assert.Equal(9, q.Read(st => st.Entry("both", "download"))!.Priority); Assert.Equal(1, q.Read(st => st.Entry("only-a", "download"))!.Priority);
        Assert.Equal(5, PriorityTool.Effective(new JobDescriptor { Sources = ["x", "y"] }, new Dictionary<string, int> { ["x"] = 2, ["y"] = 5 }));
    }

    // ------------------------------------------------------------ classify
    sealed class Chat(Func<string, int, string> answer) : IChat
    {
        public readonly List<string> Prompts = [];
        public Task<string> ChatAsync(string model, JsonNode content, CancellationToken ct) { var p = content.GetValue<string>(); Prompts.Add(p); return Task.FromResult(answer(p, Prompts.Count)); }
    }
    static List<string> IdsIn(string p) => p.Split('\n').Where(l => Regex.IsMatch(l, @"^v\d{10} \|")).Select(l => l[..11]).ToList();
    static string Reply(IEnumerable<string> ids) => "{\"videos\":[" + string.Join(",", ids.Select(i => $"{{\"id\":\"{i}\",\"candidate\":true,\"kind\":\"full\",\"language\":\"zh\",\"reason\":\"r\"}}")) + "]}";
    /// <summary>An AI that answers every upload of a prompt except the ids in <paramref name="omit"/>.</summary>
    static Chat Omits(string[] omit, string[]? firstOnly = null) => new((p, n) => Reply(IdsIn(p).Where(i => !(omit.Contains(i) || n == 1 && firstOnly?.Contains(i) == true))));

    static async Task<JobContext> RunAll(FakeStore s, JobQueue q, IChat chat)
    {
        await ClassifyJobs.EnsureSingletonAsync(q, default);
        var job = q.Read(st => st.Entry(ClassifyJobs.Subject, "classify"))!.Job!;
        var ctx = new JobContext { Job = job, Stage = "classify", BlobName = ClassifyJobs.Subject, Worker = 1, Queue = q, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new ClassifyStage(s, () => Now) { Ai = () => (chat, "m") }.RunAllAsync(ctx);
        return ctx;
    }
    static async Task AddUnclassified(JobQueue q, int i)
    {
        var d = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + Id(i), 480, null); d.JobId = Id(i); d.Title = "A drama"; d.Duration = 90 * 60; d.ChannelName = "Chan"; d.AddedAt = $"2026-11-17T10:{i / 60:D2}:{i % 60:D2}Z";
        await ClassifyJobs.CreateUnclassifiedAsync(q, d, default);
    }

    [Fact]
    public async Task The_ai_gets_40_uploads_per_request_and_a_run_takes_the_oldest_400()
    {
        var (s, q) = await Q();
        for (int i = 0; i < 405; i++) await AddUnclassified(q, i);
        var chat = Omits([]);
        var ctx = await RunAll(s, q, chat);
        Assert.Equal(10, chat.Prompts.Count); Assert.All(chat.Prompts, p => Assert.Equal(40, IdsIn(p).Count));
        var left = ClassifyJobs.ListUnclassified(q);
        Assert.Equal([Id(400), Id(401), Id(402), Id(403), Id(404)], left.Select(x => x.Id));   // the newest five wait for the next round
        Assert.Equal(Protocol.Utc(Now), ctx.NextDue);   // more remain: due at once
    }

    [Fact]
    public async Task An_upload_the_ai_leaves_out_is_asked_again_in_a_smaller_batch_then_singly()
    {
        var (s, q) = await Q();
        for (int i = 1; i <= 40; i++) await AddUnclassified(q, i);
        // the first answer leaves out two; the retry (a batch of the two) answers one; the single ask answers the last
        var chat = new Chat((p, n) => { var ids = IdsIn(p); return n switch { 1 => Reply(ids.Where(i => i != Id(7) && i != Id(9))), 2 => Reply(ids.Where(i => i != Id(9))), _ => Reply(ids) }; });
        await RunAll(s, q, chat);
        Assert.Equal(3, chat.Prompts.Count);
        Assert.Equal([Id(7), Id(9)], IdsIn(chat.Prompts[1])); Assert.Equal([Id(9)], IdsIn(chat.Prompts[2]));
        Assert.Empty(ClassifyJobs.ListUnclassified(q));
        Assert.All(Enumerable.Range(1, 40), i => Assert.Equal("pending", q.Read(st => st.Entry(Id(i), "download"))!.State));
    }

    [Fact]
    public async Task An_upload_the_ai_never_answers_stays_unclassified_twice_and_is_given_up_on_at_the_third_miss()
    {
        var (s, q) = await Q();
        for (int i = 1; i <= 5; i++) await AddUnclassified(q, i);
        string bad = Id(3);
        await RunAll(s, q, Omits([bad]));
        var e = q.Read(st => st.Entry(bad, "download"))!; Assert.Equal(Tags.Unclassified, e.State); Assert.Equal(1, e.Job!.ClassifyMisses);
        Assert.Equal(4, Enumerable.Range(1, 5).Count(i => q.Read(st => st.Entry(Id(i), "download"))!.State == "pending"));
        await RunAll(s, q, Omits([bad]));
        e = q.Read(st => st.Entry(bad, "download"))!; Assert.Equal(Tags.Unclassified, e.State); Assert.Equal(2, e.Job!.ClassifyMisses);
        await RunAll(s, q, Omits([bad]));
        e = q.Read(st => st.Entry(bad, "download"))!; Assert.Equal("failed", e.State); Assert.True(e.Permanent); Assert.Contains("classify: no AI answer", e.LastError);
    }

    // ------------------------------------------------------------ comments inside the download
    static (FakeStore, StageZipStore, DownloadStage, string) Rig()
    {
        var dir = CommentsTests.Setup(out var cfg);
        var fake = new FakeStore(); var zs = new StageZipStore(fake, Path.Combine(dir, "zc"));
        return (fake, zs, new DownloadStage(new Tools(new HttpClient(), cfg), zs, CommentsTests.NoWait), dir);
    }

    [Fact]
    public async Task A_download_writes_the_comments_output_with_the_video_and_a_repeat_does_not_fetch_them_again()
    {
        if (OperatingSystem.IsWindows()) return;
        var (fake, zs, stage, dir) = Rig(); var id = "abcdefghij1";
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + id, 480, null);
        var ctx = CommentsTests.Ctx(zs, job, "download");
        await stage.RunAsync(ctx);
        Assert.True(fake.Exists(P.Video(id, "download.zip"))); Assert.True(fake.Exists(P.Video(id, "comments.zip")), string.Join(",", fake.Names("")));
        Assert.Single(ctx.FollowUps, l => l.T == "add" && l.Stage == "process");
        var calls = File.ReadAllLines(Path.Combine(dir, "calls.log")).Length;
        await stage.RunAsync(CommentsTests.Ctx(zs, job, "download"));   // a resume after a lost done line: nothing is downloaded or fetched again
        Assert.Equal(calls, File.ReadAllLines(Path.Combine(dir, "calls.log")).Length);
    }

    [Fact]
    public async Task A_video_that_is_already_downloaded_but_has_no_comments_gets_them_on_its_next_download_claim_as_a_duplicates_metadata_entry_does()
    {
        if (OperatingSystem.IsWindows()) return;
        var (fake, zs, stage, dir) = Rig(); var id = "abcdefghij2";
        var job = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + id, 480, null);
        await stage.RunAsync(CommentsTests.Ctx(zs, job, "download"));   // a complete download
        fake.Blobs.Remove(P.Video(id, "comments.zip"));                   // ... that has no comments (e.g. stored before comments were part of the download)
        var zip = fake[P.Video(id, "download.zip")].Data.ToArray(); var zipPuts = fake.PutCount(P.Video(id, "download.zip"));
        var zs2 = new StageZipStore(fake, Path.Combine(dir, "zc2")); var stage2 = new DownloadStage(new Tools(new HttpClient(), new NodeConfig { Ytdlp = new YtDlpConfig { Command = ["bash", Path.Combine(dir, "yt-dlp.sh")] } }), zs2, CommentsTests.NoWait);
        var before = File.ReadAllLines(Path.Combine(dir, "calls.log")).Length;
        var ctx = CommentsTests.Ctx(zs2, job, "download");
        await stage2.RunAsync(ctx);
        Assert.True(fake.Exists(P.Video(id, "comments.zip")));
        Assert.True(zip.SequenceEqual(fake[P.Video(id, "download.zip")].Data)); Assert.Equal(zipPuts, fake.PutCount(P.Video(id, "download.zip")));   // the download is not redone or rewritten
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(dir, "calls.log")).Skip(before), l => l.Contains("video.%(ext)s"));
        Assert.Single(ctx.FollowUps, l => l.T == "add" && l.Stage == "process");

    }
}
