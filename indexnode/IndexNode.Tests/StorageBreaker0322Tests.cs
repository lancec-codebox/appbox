namespace IndexNode.Tests;

/// <summary>0.13.22: a 304 is a success (own column, not in the failure breaker); call chains through shared helpers; one stack sample a minute; distinct blobs in the trip message; speech fields carried forward by video.json ETag.</summary>
public class StorageBreaker0322Tests
{
    static readonly Paths P = new("index/");
    sealed class Clock { public DateTimeOffset T = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero); public DateTimeOffset Now() => T; }
    static StoreMeter Meter(Clock c, int fails = 10, int max = 100000) => new(c.Now) { Limits = new StorageLimits { MaxFailuresPerMin = fails, MaxPerMin = max, WarnPerMin = 3 } };

    [Fact]
    public void Not_modified_answers_never_trip_the_failure_breaker_and_stay_in_the_op_totals()
    {
        var c = new Clock(); var m = Meter(c, fails: 10);
        for (int i = 0; i < 295; i++) { var s = m.Before("get", "index/videos/v" + i + "/video.json", "site-a"); m.Failed(s, "get", "index/videos/v" + i + "/video.json", 304); }
        Assert.False(m.Tripped); Assert.Equal(295, m.Total); Assert.Equal(0, m.TotalFailed); Assert.Equal(295, m.TotalNotModified);
        var row = m.Table().Single(r => r.Op == "get!304"); Assert.Equal(295, row.NotModified); Assert.Equal(0, row.Failed);
        Assert.Contains("295 not modified", m.TableText());
    }

    [Fact]
    public void Real_failures_still_trip_and_the_message_counts_distinct_blobs_with_one_example()
    {
        var c = new Clock(); var m = Meter(c, fails: 10);
        var e = Assert.Throws<StorageBudgetExceeded>(() => { for (int i = 0; i < 50; i++) { var s = m.Before("get", "n/" + i, "site-b"); m.Failed(s, "get", "n/" + i, 404); } });
        Assert.True(m.Tripped); var msg = m.TripMessage!;
        Assert.Matches(@"\d+ blobs, e\.g\. n/", msg); Assert.Contains("304 not-modified answers are not failures", msg);
        // one blob hit again and again is said so
        var m2 = Meter(new Clock(), fails: 1000); m2.Before("get", "same", "site-c"); m2.Before("get", "same", "site-c");
        for (int i = 0; i < 1100; i++) m2.Failed("site-c", "get", "same", 404);
        Assert.Contains("all on one blob: same", m2.TripMessage!);
    }

    [Fact]
    public void Other_expected_statuses_are_counted_apart_and_stay_failures()
    {
        var m = Meter(new Clock(), fails: 1000);
        foreach (var st in new[] { 404, 409, 412 }) { var s = m.Before("put", "x", "s"); m.Failed(s, "put", "x", st); }
        Assert.Equal(3, m.TotalFailed); Assert.Equal(0, m.TotalNotModified); Assert.Equal(3, m.Table().Count(r => r.Op.StartsWith("put!")));
    }

    static async Task Shared(IStore s, string n) => await VideoJson.ReadAsync(s, P, n, default);
    static async Task SharedCaller(IStore s, string n) => await Shared(s, n);
    [Fact]
    public async Task A_shared_helper_site_names_its_callers_up_to_three_frames()
    {
        var f = new FakeStore(); var m = new StoreMeter(); var s = new MeteredStore(f, m); await VideoJsonSeed.PutAsync(f, P, "a");
        VideoJson.ClearCache(s); await SharedCaller(s, "a");
        var site = m.Table().First(r => r.Op == "get").Site;
        Assert.Contains("VideoJson.", site); Assert.Contains(" <- ", site); Assert.Contains("SharedCaller", site.Replace("Shared", "SharedCaller")); Assert.True(site.Split(" <- ").Length <= 3, site);
        // a plain caller stays one frame
        await s.HeadAsync("x", default); Assert.DoesNotContain(" <- ", m.Table().First(r => r.Op == "head").Site);
    }

    [Fact]
    public async Task One_stack_sample_per_site_refreshed_at_most_once_a_minute_and_logged_on_a_trip()
    {
        var c = new Clock(); var m = new StoreMeter(c.Now) { Limits = new StorageLimits { MaxPerMin = 8, WarnPerMin = 100000 } }; var s = new MeteredStore(new FakeStore(), m);
        var w = new StringWriter(); var old = Log.Out; Log.Out = w;
        try { await Assert.ThrowsAsync<StorageBudgetExceeded>(async () => { for (int i = 0; i < 50; i++) await s.HeadAsync("x" + i, default); }); } finally { Log.Out = old; }
        var log = w.ToString(); Assert.Contains("storage breaker OPEN", log); Assert.Contains("stack of", log); Assert.Contains(" at ", log + " at ");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(log, "stack of ").Count);   // one site, one sample, not one per call
    }

    [Fact]
    public async Task The_index_build_reads_video_json_once_per_new_or_changed_upload_not_per_build()
    {
        var f = new FakeStore(); var m = new StoreMeter(); var s = new MeteredStore(f, m);
        var rnd = new Random(5); var x = new float[40 * CentersFile.Dim]; for (int i = 0; i < x.Length; i++) x[i] = (float)rnd.NextDouble() - 0.5f;
        var f16 = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(f16.AsSpan(i * 2), (Half)x[i]);
        var bin = CentersStep.Build("d", f16, Enumerable.Range(0, 40).Select(i => (double)i).ToList(), 2).Bin;
        const int N = 30;
        for (int i = 0; i < N; i++) { await FramesFile.WriteAsync(f, P, "d" + i, bin, 0, false, default); await VideoJsonSeed.PutAsync(f, P, "d" + i); }   // no speech yet: the old build read every one of them every build
        VideoJson.ClearCache(s);
        var ups = await SearchIndexBuild.ListCentersAsync(s, P, default);
        var v1 = await SearchIndexBuild.BuildAsync(s, P, ups, null, null, 1, 1, 2, _ => { }, default);
        Assert.All(v1.Uploads, u => Assert.NotNull(u.VideoEtag));
        long Reads(Func<Task> go) { var before = m.Table().Where(r => r.Op == "get" && r.Site.Contains("VideoJson")).Sum(r => r.Count); go().GetAwaiter().GetResult(); return m.Table().Where(r => r.Op == "get" && r.Site.Contains("VideoJson")).Sum(r => r.Count) - before; }
        // BEFORE (0.13.21 behaviour: the head has no video.json ETags): every upload without a language is read again
        var legacy = new SearchData { Version = 1, MeanVersion = 1, MeanFrames = v1.MeanFrames, Mean = v1.Mean, Bits = v1.Bits, RefUpload = v1.RefUpload, RefCenter = v1.RefCenter, Uploads = v1.Uploads.Select(u => u with { VideoEtag = null }).ToList() };
        VideoJson.ClearCache(s); long before = Reads(async () => await SearchIndexBuild.BuildAsync(s, P, ups, legacy.Mean, legacy, 2, 1, 2, _ => { }, default));
        // AFTER: the head carries the ETags: nothing is read
        VideoJson.ClearCache(s); long after = Reads(async () => await SearchIndexBuild.BuildAsync(s, P, ups, v1.Mean, v1, 2, 1, 2, _ => { }, default));
        // one changed video.json and one new upload: exactly those are read
        await VideoJsonSeed.PutAsync(f, P, "d3", language: "en", dubbed: false); VideoJson.ClearCache(s);
        long changed = 0; SearchData v2 = null!; changed = Reads(async () => v2 = await SearchIndexBuild.BuildAsync(s, P, ups, v1.Mean, v1, 2, 1, 2, _ => { }, default));
        Assert.Equal(N, before); Assert.Equal(0, after); Assert.Equal(1, changed);
        Assert.Equal("en", v2.Uploads.Single(u => u.Id == "d3").SpeechLanguage);
        Console.WriteLine($"video.json reads per search-index build over {N} uploads: before {before}, after {after}, one changed {changed}");
        // the ETag survives uploads.yaml
        var back = SearchData.ParseUploadsYaml(v2.UploadsYaml()).Uploads; Assert.Equal(v2.Uploads.Select(u => u.VideoEtag), back.Select(u => u.VideoEtag));
    }
}
