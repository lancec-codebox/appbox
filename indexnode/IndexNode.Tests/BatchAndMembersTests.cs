using System.Text.Json;

namespace IndexNode.Tests;

public class BatchAndMembersTests
{
    private static readonly Paths P = new("index-test/");
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static string Vid(int i) => "v" + i.ToString("D10");

    private sealed class Fake
    {
        public Dictionary<string, string> Listings = new();
        public Tools Tools() => new(new HttpClient()) { YtDlpOverride = (args, _, _) => Task.FromResult(new ProcResult(0, Listings.GetValueOrDefault(args[^1], ""), "")) };
    }

    private static string Listing(IEnumerable<string> ids) => string.Join("\n", ids.Select(i => i + "\tNA\tNA")) + "\n";

    private static async Task<(FakeStore Store, JobQueue Queue)> Setup()
    {
        var s = new FakeStore { Clock = () => Now }; var q = new JobQueue(s, P, "n1", () => Now); await q.LoadAsync(default); return (s, q);
    }

    private static async Task Run(FakeStore s, JobQueue q, Fake f, JobDescriptor job)
    {
        var ctx = new JobContext { Job = job, Stage = "refresh", Store = s, Paths = P, NodeId = "n1", Token = default, ThreadShare = () => 1, Report = (_, _) => { }, Queue = q };
        await new RefreshStage(f.Tools(), s, () => Now).RunAsync(ctx);
    }

    private static JobDescriptor Playlist(string id) => JobDescriptor.NewSource("playlist", id, "pl " + id, "1h", ["videos"], false, -1, 360);

    [Fact]
    public async Task A_lookup_of_200_new_videos_is_at_most_two_append_blocks_and_the_same_state_as_one_line_each()
    {
        var ids = Enumerable.Range(0, 200).Select(Vid).ToList();
        var (s, q) = await Setup(); var f = new Fake(); f.Listings["https://www.youtube.com/playlist?list=PLbatch"] = Listing(ids);
        int before = s.Ops.GetValueOrDefault("append");
        await Run(s, q, f, Playlist("PLbatch"));
        Assert.True(s.Ops.GetValueOrDefault("append") - before <= 2, "appends: " + (s.Ops.GetValueOrDefault("append") - before));

        // the same videos one at a time (the 0.13.1 behaviour)
        var (s2, q2) = await Setup(); int b2 = s2.Ops.GetValueOrDefault("append");
        foreach (var id in ids) { var d = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + id, 360, null); d.Source = "PLbatch"; d.Sources = ["PLbatch"]; d.AddedAt = Protocol.Utc(Now); await ClassifyJobs.CreateUnclassifiedAsync(q2, d); }
        Assert.True(s2.Ops.GetValueOrDefault("append") - b2 >= 200);
        string Sig(JobQueue x) => string.Join("|", x.Read(st => st.Subjects.Values.Where(v => v.Entries.ContainsKey("download")).OrderBy(v => v.Id, StringComparer.Ordinal).Select(v => v.Id + ":" + v.Entries["download"].State + ":" + v.Entries["download"].Added + ":" + (st.Entry("classify", "classify") != null))));
        Assert.Equal(Sig(q2), Sig(q));
        Assert.Equal(200, q.Read(st => st.Subjects.Values.Count(v => v.Entries.TryGetValue("download", out var e) && e.State == Tags.Unclassified)));
        Assert.NotNull(q.Read(st => st.Entry("classify", "classify")));
    }

    [Fact]
    public async Task A_batch_is_split_by_bytes_and_a_412_decides_the_whole_batch_again()
    {
        var (s, q) = await Setup();
        var ds = Enumerable.Range(0, 3000).Select(i => { var d = JobDescriptor.NewDownload("https://www.youtube.com/watch?v=" + Vid(i), 360, null); d.AddedAt = Protocol.Utc(Now); d.Title = new string('x', 2000); return d; }).ToList();
        int before = s.Ops.GetValueOrDefault("append");
        // someone else appends one of the videos first, between our read and our append: the batch is decided again and does not add it twice
        var other = new JobQueue(s, P, "n2", () => Now); await other.LoadAsync(default);
        s.BeforeAppend = _ => { var d = ds[5]; other.AppendLinesAsync([QLine.Add(d.JobId, "download", d, d.AddedAt)], default).GetAwaiter().GetResult(); };
        var made = await ClassifyJobs.CreateUnclassifiedBatchAsync(q, ds);
        Assert.Equal(2999, made.Count); Assert.DoesNotContain(ds[5].JobId, made);
        Assert.True(s.Ops.GetValueOrDefault("append") - before >= 3);   // 3000 lines of ~2 KB: more than one block, none over the limit
        Assert.Equal(3000, q.Read(st => st.Subjects.Values.Count(v => v.Entries.ContainsKey("download"))));
        Assert.All(JobQueue.Chunks(ds, d => d.ToBytes().Length), c => Assert.True(c.Sum(d => d.ToBytes().Length) < JobQueue.MaxAppendBytes));
    }

    [Fact]
    public async Task Refresh_finds_new_videos_from_members_alone_with_no_seen_file()
    {
        var (s, q) = await Setup(); var f = new Fake(); var url = "https://www.youtube.com/playlist?list=PLmem";
        f.Listings[url] = Listing([Vid(1), Vid(2)]);
        var job = Playlist("PLmem"); await Run(s, q, f, job);
        Assert.Null(await s.GetAsync(P.SourceSeen(job.JobId), default));
        var m = await SourceMembers.ReadAsync(s, P, job.JobId, default); Assert.Equal(2, m.Members.Count); Assert.All(m.Members, x => Assert.NotNull(x.FirstSeen));
        f.Listings[url] = Listing([Vid(3), Vid(1), Vid(2)]); job.Run = "refresh";
        await Run(s, q, f, job);
        Assert.Equal(3, q.Read(st => st.Subjects.Values.Count(v => v.Entries.ContainsKey("download"))));
        var m2 = await SourceMembers.ReadAsync(s, P, job.JobId, default); Assert.Equal(3, m2.Members.Count);
        Assert.Equal(m.Members.First(x => x.Id == Vid(1)).FirstSeen, m2.Members.First(x => x.Id == Vid(1)).FirstSeen);
    }

    [Fact]
    public async Task A_source_with_only_a_seen_file_migrates_without_adding_its_known_videos_again()
    {
        var (s, q) = await Setup(); var f = new Fake(); var url = "https://www.youtube.com/playlist?list=PLold";
        await s.PutAsync(P.SourceSeen("pl-PLold"), JsonSerializer.SerializeToUtf8Bytes(new[] { Vid(1), Vid(2) }), null, null, false, default);
        var job = Playlist("PLold"); job.Run = "refresh";
        f.Listings[url] = Listing([Vid(1), Vid(2), Vid(3)]);
        await Run(s, q, f, job);
        Assert.Null(await s.GetAsync(P.SourceSeen(job.JobId), default));   // seen.json went
        Assert.Equal([Vid(3)], q.Read(st => st.Subjects.Values.Where(v => v.Entries.ContainsKey("download")).Select(v => v.Id).ToList()));
        Assert.Equal(3, (await SourceMembers.ReadAsync(s, P, job.JobId, default)).Members.Count);
    }

    [Fact]
    public async Task Migrate_seen_dry_run_counts_and_writes_nothing_then_the_real_run_migrates()
    {
        var (s, _) = await Setup();
        await s.PutAsync(P.SourceSeen("pl-A"), JsonSerializer.SerializeToUtf8Bytes(new[] { Vid(1), Vid(2) }), null, null, false, default);
        Assert.Equal(2, await SourceMembers.MigrateSeenAsync(s, P, "pl-A", null, "2026-11-20T12:00:00Z", default, dryRun: true));
        Assert.NotNull(await s.GetAsync(P.SourceSeen("pl-A"), default)); Assert.Null(await s.GetAsync(P.SourceMembers("pl-A"), default));
        Assert.Equal(2, await SourceMembers.MigrateSeenAsync(s, P, "pl-A", null, "2026-11-20T12:00:00Z", default));
        Assert.Null(await s.GetAsync(P.SourceSeen("pl-A"), default)); Assert.Equal(2, (await SourceMembers.ReadAsync(s, P, "pl-A", default)).Members.Count);
        Assert.Equal(0, await SourceMembers.MigrateSeenAsync(s, P, "pl-A", [Vid(1)], "2026-11-20T12:00:00Z", default));   // nothing left to add
    }
}
