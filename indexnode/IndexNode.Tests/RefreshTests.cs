using System.Text.Json;

namespace IndexNode.Tests;

public class RefreshTests
{
    private static readonly Paths P = new("index-test/");
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static string Id(char c) => new string(c, 11);

    private sealed class Fake
    {
        public Dictionary<string, string> Listings = new();
        public Dictionary<string, string> WatchPages = new();
        public List<string> Calls = new();
        public Tools Tools() => new(new HttpClient()) { YtDlpOverride = (args, _, _) =>
        {
            var url = args[^1]; Calls.Add(string.Join(' ', args));
            if (url.Contains("watch?v=")) return Task.FromResult(new ProcResult(0, WatchPages.GetValueOrDefault(url.Split('=')[1], "NA\tNA") + "\n", ""));
            return Task.FromResult(new ProcResult(0, Listings.GetValueOrDefault(url, ""), ""));
        } };
    }

    private static string Lines(params string[] ids) => string.Join("\n", ids.Select(i => i + "\tNA\tNA")) + "\n";

    private static async Task<(JobContext Ctx, JobDescriptor Job)> Poll(FakeStore s, Fake f, JobDescriptor job, DateTimeOffset? now = null)
    {
        var name = P.Job("refresh", job.JobId);
        await s.PutAsync(name, job.ToBytes(), Tags.Job("refresh", "pending", job.AddedAt), null, false, default);
        var ctx = new JobContext { Job = job, Stage = "refresh", BlobName = name, LeaseId = "l", Worker = 1, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new RefreshStage(f.Tools(), s, () => now ?? Now).RunAsync(ctx);
        return (ctx, job);
    }

    private static JobDescriptor Playlist(string id, int backfill = -1, bool follow = false, string every = "1h") =>
        JobDescriptor.NewSource("playlist", id, "pl " + id, every, ["videos", "shorts"], follow, backfill, 360);

    private static string PlUrl(string id) => $"https://www.youtube.com/playlist?list={id}";

    [Fact]
    public async Task First_poll_with_backfill_0_records_what_exists_and_enqueues_nothing_then_a_new_id_is_downloaded()
    {
        var s = new FakeStore(); var f = new Fake();
        f.Listings[PlUrl("PLa")] = Lines(Id('a'), Id('b'), Id('c'));
        var (ctx, job) = await Poll(s, f, Playlist("PLa", backfill: 0));
        Assert.Empty(s.Blobs.Keys.Where(k => k.Contains("/jobs/download/")));
        Assert.Equal(3, job.Seen!.Count);
        Assert.Equal(0, job.Found);

        f.Listings[PlUrl("PLa")] = Lines(Id('d'), Id('a'), Id('b'), Id('c'));
        await Poll(s, f, job);
        var dl = Assert.Single(s.Blobs.Keys, k => k.Contains("/jobs/download/"));
        Assert.Equal(P.Job("download", Id('d')), dl);
        var d = s.Job(dl);
        Assert.Equal("pl-PLa", d.Source); Assert.Equal(360, d.MaxHeight);
        Assert.Equal("unclassified", s.State(dl));
        Assert.Equal(1, job.Found);
        Assert.Equal(Id('d'), job.Seen![0]);
    }

    [Fact]
    public async Task First_poll_with_backfill_3_enqueues_the_three_newest()
    {
        var s = new FakeStore(); var f = new Fake();
        f.Listings[PlUrl("PLb")] = Lines(Id('a'), Id('b'), Id('c'), Id('d'), Id('e'));
        await Poll(s, f, Playlist("PLb", backfill: 3));
        var made = s.Blobs.Keys.Where(k => k.Contains("/jobs/download/")).OrderBy(k => k).ToList();
        Assert.Equal([P.Job("download", Id('a')), P.Job("download", Id('b')), P.Job("download", Id('c'))], made);
        Assert.DoesNotContain(f.Calls, c => c.Contains("--playlist-end")); // a playlist lookup lists in full
    }

    [Fact]
    public async Task Backfill_asks_for_more_than_30_when_needed_and_uses_the_flat_listing_without_cookies()
    {
        var s = new FakeStore(); var f = new Fake();
        var cid = "UCeeeeeeeeeeeeeeeeeeeeee"; f.Listings[$"https://www.youtube.com/channel/{cid}/videos"] = Lines(Id('a'));
        await Poll(s, f, JobDescriptor.NewSource("channel", cid, "c", "once", ["videos"], false, 150, 480));
        var call = Assert.Single(f.Calls);
        Assert.Contains("--flat-playlist", call); Assert.Contains("--playlist-end 150", call); Assert.DoesNotContain("cookies", call);
    }

    [Fact]
    public async Task A_known_id_is_skipped_by_an_existing_download_job_or_video_folder()
    {
        var s = new FakeStore(); var f = new Fake();
        await s.AddJob(P, "download", Id('k'), "2026-01-01T00:00:00Z");
        await s.PutAsync(P.Video(Id('v'), "downloaded.json"), [1], null, null, false, default);
        var job = Playlist("PLd"); job.Seen = [];
        f.Listings[PlUrl("PLd")] = Lines(Id('n'), Id('k'), Id('v'));
        await Poll(s, f, job);
        Assert.Equal(1, job.Found);
        Assert.True(s.Exists(P.Job("download", Id('n'))));
        Assert.Equal(1, s.PutCount(P.Job("download", Id('k')))); // untouched
        Assert.Equal(2, s.Blobs.Keys.Count(k => k.Contains("/jobs/download/")));
    }

    [Fact]
    public async Task A_video_in_two_sources_is_downloaded_once()
    {
        var s = new FakeStore(); var f = new Fake();
        f.Listings[PlUrl("PL1")] = Lines(Id('x'));
        f.Listings[PlUrl("PL2")] = Lines(Id('x'));
        var a = Playlist("PL1"); a.Seen = []; var b = Playlist("PL2"); b.Seen = [];
        await Poll(s, f, a); await Poll(s, f, b);
        Assert.Single(s.Blobs.Keys, k => k.Contains("/jobs/download/"));
        Assert.Equal("pl-PL1", s.Job(P.Job("download", Id('x'))).Source);
        Assert.Equal(1, a.Found); Assert.Equal(0, b.Found);
        Assert.Contains(Id('x'), b.Seen!); // still recorded as seen
    }

    [Fact]
    public async Task FollowChannels_adds_each_channel_exactly_once_and_uses_channel_id_from_the_listing()
    {
        var s = new Fake(); var st = new FakeStore();
        const string c1 = "UCaaaaaaaaaaaaaaaaaaaaaa", c2 = "UCbbbbbbbbbbbbbbbbbbbbbb";
        s.Listings[PlUrl("PLdrop")] = $"{Id('a')}\t{c1}\tOne\n{Id('b')}\t{c1}\tOne\n{Id('c')}\t{c2}\tTwo\n";
        var job = Playlist("PLdrop", follow: true); job.Seen = [];
        await Poll(st, s, job);
        var chans = st.Blobs.Keys.Where(k => k.Contains("/jobs/refresh/ch-")).OrderBy(k => k).ToList();
        Assert.Equal([P.Job("refresh", "ch-" + c1), P.Job("refresh", "ch-" + c2)], chans);
        var ch = st.Job(chans[0]);
        Assert.Equal("channel", ch.Kind); Assert.Equal(0, ch.Backfill); Assert.False(ch.FollowChannels); Assert.Equal("One", ch.Name);
        Assert.Equal("pending", st.State(chans[0]));
        var puts = st.PutCount(chans[0]);
        // polling again finds nothing new: no second add, and an existing channel source is not overwritten
        await Poll(st, s, job);
        Assert.Equal(puts, st.PutCount(chans[0]));
        Assert.Equal(2, st.Blobs.Keys.Count(k => k.Contains("/jobs/refresh/ch-")));
    }

    [Fact]
    public async Task FollowChannels_resolves_a_missing_channel_id_with_one_non_flat_call_per_new_video()
    {
        var s = new Fake(); var st = new FakeStore();
        const string c1 = "UCcccccccccccccccccccccc";
        s.Listings[PlUrl("PLdrop2")] = Lines(Id('a'));
        s.WatchPages[Id('a')] = c1 + "\tResolved";
        var job = Playlist("PLdrop2", follow: true); job.Seen = [];
        await Poll(st, s, job);
        Assert.True(st.Exists(P.Job("refresh", "ch-" + c1)));
        Assert.Equal("Resolved", st.Job(P.Job("refresh", "ch-" + c1)).Name);
        Assert.Single(s.Calls, c => c.Contains("watch?v=") && c.Contains("--print") && !c.Contains("--flat-playlist"));
    }

    [Fact]
    public async Task An_admin_add_request_becomes_a_watch_job_on_the_next_poll_and_is_marked_done()
    {
        var s = new FakeStore(); var f = new Fake();
        const string url = "https://www.youtube.com/playlist?list=PLreq";
        f.Listings[url] = "My Dramas\tNA\tUCrrrrrrrrrrrrrrrrrrrrrr\tChan\n";
        f.Listings[PlUrl("PLa")] = Lines(Id('a'));
        await s.PutAsync(RefreshStage.RequestsDir(P) + "r1.json", System.Text.Encoding.UTF8.GetBytes($$"""{"v":1,"url":"{{url}}","mode":"ingest","followChannels":true,"state":"pending"}"""), null, null, false, default);
        await Poll(s, f, Playlist("PLa"));
        var w = s.Job(P.Job("refresh", "pl-PLreq"));
        Assert.Equal("My Dramas", w.Name); Assert.True(w.FollowChannels); Assert.Null(w.Backfill); Assert.Equal("once", w.Schedule);   // a playlist's lookup queues every video (no backfill limit); the schedule is once
        var req = System.Text.Json.Nodes.JsonNode.Parse(s.Blobs[RefreshStage.RequestsDir(P) + "r1.json"].Data)!;
        Assert.Equal("done", req["state"]!.ToString()); Assert.Equal("pl-PLreq", req["jobId"]!.ToString());
    }

    [Fact]
    public async Task A_channel_source_lists_its_tabs()
    {
        var s = new FakeStore(); var f = new Fake();
        const string c = "UCdddddddddddddddddddddd";
        f.Listings[$"https://www.youtube.com/channel/{c}/videos"] = Lines(Id('a'));
        f.Listings[$"https://www.youtube.com/channel/{c}/shorts"] = Lines(Id('b'));
        var job = JobDescriptor.NewSource("channel", c, "x", "1h", ["videos", "shorts"], false, 5, 480);
        await Poll(s, f, job);
        Assert.Equal(2, f.Calls.Count);
        // a channel's uploads become unclassified download jobs and no per-channel classify job: the singleton classify/all decides them
        Assert.Equal(2, s.Blobs.Keys.Count(k => k.Contains("/jobs/download/")));
        Assert.All(s.Blobs.Keys.Where(k => k.Contains("/jobs/download/")), k => Assert.Equal("unclassified", s.State(k)));
        Assert.Equal([P.Job("classify", "all")], s.Blobs.Keys.Where(k => k.Contains("/jobs/classify/")).ToList());
        Assert.Equal("ch-" + c, s.Job(P.Job("download", Id('a'))).Source);
    }

    [Fact]
    public async Task Seen_is_capped_at_500_newest()
    {
        var s = new FakeStore(); var f = new Fake();
        var job = Playlist("PLcap"); job.Seen = Enumerable.Range(0, 500).Select(i => "o" + i.ToString("D10")).ToList();
        f.Listings[PlUrl("PLcap")] = Lines(Id('n'));
        await Poll(s, f, job);
        Assert.Equal(500, job.Seen.Count); Assert.Equal(Id('n'), job.Seen[0]);
    }

    [Theory]
    [InlineData("1h", 60)] [InlineData("30m", 30)] [InlineData("1d", 1440)] [InlineData(null, 60)]
    public void Every_parses(string? every, int minutes) => Assert.Equal(TimeSpan.FromMinutes(minutes), RefreshStage.ParseEvery(every));

    [Fact]
    public async Task The_engine_reschedules_a_watch_job_as_pending_with_due_now_plus_every_and_lastPolled()
    {
        var s = new FakeStore(); var f = new Fake();
        f.Listings[PlUrl("PLr")] = Lines(Id('a'));
        var job = Playlist("PLr", every: "2h");
        await s.PutAsync(P.Job("refresh", job.JobId), job.ToBytes(), Tags.Job("refresh", "pending", "2026-01-01T00:00:00Z"), null, true, default);
        var o = new EngineOptions
        {
            NodeId = "n-1", Name = "n", Stages = ["refresh"], Jobs = new() { ["refresh"] = 1 }, Threads = 1, Once = true,
            PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(40), ControlInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromSeconds(30),
        };
        var before = DateTimeOffset.UtcNow;
        await new NodeEngine(s, P, o, new Dictionary<string, IStageHandler> { ["refresh"] = new RefreshStage(f.Tools(), s) }).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var name = P.Job("refresh", job.JobId);
        Assert.Equal("pending", s.State(name));
        var due = DateTimeOffset.Parse(s[name].Tags[Tags.Due]);
        Assert.InRange((due - before).TotalMinutes, 119, 121);
        Assert.Equal("refresh", s[name].Tags[Tags.Stage]);
        Assert.Equal("2026-01-01T00:00:00Z", s[name].Tags[Tags.Added]);
        var j = s.Job(name);
        Assert.NotNull(j.LastPolled); Assert.Equal(1, j.Seen!.Count);
    }

    // ---------------------------------------------------------------- sources

    [Fact]
    public void Classify_reads_playlist_and_channel_urls_and_refuses_Watch_Later()
    {
        Assert.Equal(("playlist", "PLabc_-1"), Sources.Classify("https://www.youtube.com/playlist?list=PLabc_-1"));
        Assert.Equal(("channel", "UCaaaaaaaaaaaaaaaaaaaaaa"), Sources.Classify("https://www.youtube.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa/videos"));
        Assert.Null(Sources.Classify("https://www.youtube.com/@someone"));
        Assert.Throws<ArgumentException>(() => Sources.Classify("https://www.youtube.com/playlist?list=WL"));
    }

    [Fact]
    public async Task Import_makes_one_watch_job_per_channel_and_skips_existing_ones()
    {
        var s = new FakeStore();
        var json = JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            new { channelId = "UCaaaaaaaaaaaaaaaaaaaaaa", channelName = "A", channelHandle = "@a", videos = 3, sampleTitles = new[] { "x" } },
            new { channelId = "UCbbbbbbbbbbbbbbbbbbbbbb", channelName = "B", channelHandle = "@b", videos = 1, sampleTitles = new string[0] },
            new { channelId = "UCcccccccccccccccccccccc", channelName = "C", channelHandle = "@c", videos = 9, sampleTitles = new[] { "y" } },
        });
        var existing = JobDescriptor.NewSource("channel", "UCbbbbbbbbbbbbbbbbbbbbbb", "B", "1h", ["videos"], false, 0, 480);
        await Sources.AddAsync(s, P, existing, default);
        var r = await Sources.ImportAsync(s, P, json, "1d", ["videos", "shorts"], 480, default);
        Assert.Equal(new Sources.ImportResult(2, 1, 0), r);
        var a = s.Job(P.Job("refresh", "ch-UCaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.Equal("A", a.Name); Assert.Equal("1d", a.Every); Assert.Equal("channel", a.Kind); Assert.Equal(0, a.Backfill);
        Assert.Equal("pending", s.State(P.Job("refresh", "ch-UCaaaaaaaaaaaaaaaaaaaaaa")));
        Assert.Equal("1h", s.Job(P.Job("refresh", "ch-UCbbbbbbbbbbbbbbbbbbbbbb")).Every); // not overwritten
        Assert.Equal(3, (await Sources.ListAsync(s, P, default)).Count);
        var r2 = await Sources.ImportAsync(s, P, json, "1d", ["videos"], 480, default);
        Assert.Equal(new Sources.ImportResult(0, 3, 0), r2);
    }
}
