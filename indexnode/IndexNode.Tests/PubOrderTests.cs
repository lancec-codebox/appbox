using System.Text.Json.Nodes;

namespace IndexNode.Tests;

/// <summary>0.13.16: checks and downloads go newest first (pub, descending, within a priority), pub is recorded, and `jobs sample-first` fills it in.</summary>
public class PubOrderTests
{
    static readonly Paths P = new("index/");
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-20T08:00:00Z");

    static QueueState State(params (string id, string stage, int pri, string? pub, string added)[] e)
    {
        var st = new QueueState();
        foreach (var x in e)
        {
            var job = new JobDescriptor { Stage = x.stage, JobId = x.id, Priority = x.pri, AddedAt = x.added, Pub = x.pub, PubSource = x.pub == null ? null : "info" };
            st.Apply(QLine.Add(x.id, x.stage, job, x.added, x.pri));
        }
        return st;
    }
    static List<string> Order(QueueState st, params string[] stages) => st.Candidates(Protocol.Utc(T0), s => stages.Contains(s)).Select(x => x.Subject.Id).ToList();

    [Fact]
    public void Within_a_priority_the_newest_upload_is_claimed_first_for_each_of_the_four_stages()
    {
        foreach (var stage in new[] { "sample", "check", "download", "process" })
        {
            var st = State(("old", stage, 0, "2026-01-01T00:00:00Z", "2026-11-01T00:00:00Z"), ("new", stage, 0, "2026-11-10T00:00:00Z", "2026-11-02T00:00:00Z"), ("mid", stage, 0, "2026-06-01T00:00:00Z", "2026-11-01T12:00:00Z"));
            Assert.Equal(["new", "mid", "old"], Order(st, stage));
        }
    }

    [Fact]
    public void A_higher_priority_still_goes_first_whatever_its_publish_time()
    {
        var st = State(("newest", "download", 0, "2026-11-19T00:00:00Z", "2026-11-01T00:00:00Z"), ("old-hi", "download", 5, "2020-01-01T00:00:00Z", "2026-11-02T00:00:00Z"));
        Assert.Equal(["old-hi", "newest"], Order(st, "download"));
    }

    [Fact]
    public void An_entry_without_pub_goes_after_every_dated_one_in_todays_order()
    {
        var st = State(("n1", "check", 0, null, "2026-11-01T00:00:00Z"), ("d1", "check", 0, "2019-01-01T00:00:00Z", "2026-11-05T00:00:00Z"), ("n2", "check", 0, null, "2026-11-02T00:00:00Z"), ("d2", "check", 0, "2025-01-01T00:00:00Z", "2026-11-06T00:00:00Z"));
        Assert.Equal(["d2", "d1", "n1", "n2"], Order(st, "check"));   // dated newest first, then undated by added time
    }

    [Fact]
    public void Group_and_search_index_ordering_is_unchanged()
    {
        var st = State(("g-new", "group", 0, "2026-11-19T00:00:00Z", "2026-11-03T00:00:00Z"), ("g-old", "group", 0, "2020-01-01T00:00:00Z", "2026-11-02T00:00:00Z"));
        Assert.Equal(["g-old", "g-new"], Order(st, "group"));   // by seq/added as before, not by pub
    }

    [Fact]
    public void Pub_comes_from_info_json_and_the_listing_estimate_keeps_the_listing_order()
    {
        Assert.Equal("2026-03-04T05:06:07Z", PubTime.FromInfo(JsonNode.Parse("{\"timestamp\":1772600767,\"upload_date\":\"20260301\"}")));
        Assert.Equal("2026-03-01T12:00:00Z", PubTime.FromInfo(JsonNode.Parse("{\"upload_date\":\"20260301\"}")));
        Assert.Null(PubTime.FromInfo(JsonNode.Parse("{\"title\":\"x\"}")));
        var listed = new[] { "aaaaaaaaaaa", "bbbbbbbbbbb", "ccccccccccc", "ddddddddddd" }.Select((id, i) => new RefreshStage.Entry(id, null, null, UploadDate: i == 1 ? "20260310" : null)).ToList();
        var p = RefreshStage.PubsOf(listed, T0);
        Assert.Equal("listing", p["bbbbbbbbbbb"].Source);
        Assert.Equal("estimate", p["aaaaaaaaaaa"].Source); Assert.Equal("estimate", p["ccccccccccc"].Source);
        Assert.True(string.CompareOrdinal(p["aaaaaaaaaaa"].Pub, p["bbbbbbbbbbb"].Pub) > 0);   // newer than the dated one below it
        Assert.True(string.CompareOrdinal(p["bbbbbbbbbbb"].Pub, p["ccccccccccc"].Pub) > 0);
        Assert.True(string.CompareOrdinal(p["ccccccccccc"].Pub, p["ddddddddddd"].Pub) > 0);   // listing order kept
    }

    [Fact]
    public async Task Sample_first_fills_pub_from_info_json_and_members_and_a_second_run_adds_nothing()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "dl1", Protocol.Utc(T0.AddDays(-1)));      // listing only
        await s.AddJob(P, "process", "pr1", Protocol.Utc(T0.AddDays(-1)));      // already downloaded: info.json
        await s.PutAsync(P.Video("pr1", "info.json"), System.Text.Encoding.UTF8.GetBytes("{\"timestamp\":1772600767}"), null, null, false, default);
        var q = new JobQueue(s, P, "t", () => T0); await q.LoadAsync(default);
        await q.AppendLinesAsync([new QLine { T = "add", S = "dl1", Stage = "download", At = Protocol.Utc(T0), Pri = 0, Job = new JobDescriptor { Stage = "download", JobId = "dl1", Source = "src1", AddedAt = Protocol.Utc(T0), Url = "https://www.youtube.com/watch?v=dl1xxxxxxxx" }, Force = true }], default);
        await s.PutAsync(P.SourceMembers("src1"), System.Text.Encoding.UTF8.GetBytes("{\"v\":2,\"members\":[{\"id\":\"dl1\",\"pub\":\"2026-11-15T00:00:00Z\",\"pubSource\":\"estimate\"}]}"), null, null, false, default);
        var dry = await SampleFirst.RunAsync(q, true, default, s, P);
        Assert.Equal(2, dry.Pubs);
        var run = await SampleFirst.RunAsync(q, false, default, s, P);
        Assert.Equal(2, run.Pubs);
        Assert.Equal("2026-11-15T00:00:00Z", q.Read(st => st.Entry("dl1", "sample")!.Job!.Pub)); Assert.Equal("estimate", q.Read(st => st.Entry("dl1", "sample")!.Job!.PubSource));
        Assert.Equal("2026-03-04T05:06:07Z", q.Read(st => st.Entry("pr1", "check")!.Job!.Pub)); Assert.Equal("info", q.Read(st => st.Entry("pr1", "check")!.Job!.PubSource));
        var again = await SampleFirst.RunAsync(q, false, default, s, P);
        Assert.Equal((0, 0, 0), (again.Downloads, again.Checks, again.Pubs));
    }

    [Fact]
    public async Task Pending_download_makes_no_per_video_store_op_and_a_downloaded_drama_one_read()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "dl1", Protocol.Utc(T0.AddDays(-1)));
        await s.AddJob(P, "process", "pr1", Protocol.Utc(T0.AddDays(-1)));
        await s.PutAsync(P.Video("pr1", "info.json"), System.Text.Encoding.UTF8.GetBytes("{\"timestamp\":1772600767}"), null, null, false, default);
        var q = new JobQueue(s, P, "t", () => T0); await q.LoadAsync(default);
        await q.AppendLinesAsync([new QLine { T = "add", S = "dl1", Stage = "download", At = Protocol.Utc(T0), Pri = 0, Job = new JobDescriptor { Stage = "download", JobId = "dl1", Source = "src1", AddedAt = Protocol.Utc(T0), Url = "https://www.youtube.com/watch?v=dl1xxxxxxxx" }, Force = true }], default);
        await s.PutAsync(P.SourceMembers("src1"), System.Text.Encoding.UTF8.GetBytes("{\"v\":2,\"members\":[{\"id\":\"dl1\",\"pub\":\"2026-11-15T00:00:00Z\",\"pubSource\":\"estimate\"}]}"), null, null, false, default);
        var meter = new StoreMeter(); var m = new MeteredStore(s, meter);
        var pubs = await SampleFirst.FindPubsAsync(q, m, P, default);
        Assert.Equal("2026-11-15T00:00:00Z", pubs["dl1"].Pub);
        Assert.Equal(2, meter.Total);   // members.json once + pr1's info.json once; nothing for dl1's own blobs
        Assert.Equal(0, meter.TotalFailed);
    }
}
