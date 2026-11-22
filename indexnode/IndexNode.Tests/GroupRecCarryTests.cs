namespace IndexNode.Tests;

/// <summary>0.13.23: the grouping records ride in uploads.yaml with the video.json ETag (no GroupBuild read per build), and an unchanged build publishes nothing.</summary>
public class GroupRecCarryTests
{
    static readonly Paths P = new("index/");
    static async Task<(FakeStore F, MeteredStore S, StoreMeter M)> Seed(int n)
    {
        var f = new FakeStore(); var m = new StoreMeter(); var s = new MeteredStore(f, m);
        var rnd = new Random(5); var x = new float[40 * CentersFile.Dim]; for (int i = 0; i < x.Length; i++) x[i] = (float)rnd.NextDouble() - 0.5f;
        var f16 = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(f16.AsSpan(i * 2), (Half)x[i]);
        var bin = CentersStep.Build("d", f16, Enumerable.Range(0, 40).Select(i => (double)i).ToList(), 2).Bin;
        for (int i = 0; i < n; i++) { await FramesFile.WriteAsync(f, P, "d" + i, bin, 0, false, default); await VideoJsonSeed.PutAsync(f, P, "d" + i); }
        VideoJson.ClearCache(s); return (f, s, m);
    }
    static long Gets(StoreMeter m, string site) => m.Table().Where(r => r.Op == "get" && r.Site.Contains(site)).Sum(r => r.Count);

    [Fact]
    public async Task Group_record_reads_per_build_before_after_and_after_one_change()
    {
        const int N = 30; var (f, s, m) = await Seed(N);
        var ups = await SearchIndexBuild.ListCentersAsync(s, P, default);
        var v1 = await SearchIndexBuild.BuildAsync(s, P, ups, null, null, 1, 1, 2, _ => { }, default);
        Assert.All(v1.Uploads, u => Assert.NotNull(u.GroupRec));
        async Task<SearchData> Next(SearchData head)
        {
            VideoJson.ClearCache(s); var n = await SearchIndexBuild.BuildAsync(s, P, ups, head.Mean, head, head.Version + 1, 1, 2, _ => { }, default);
            await GroupBuild.ApplyAsync(s, P, head, n, default, null, 2, null, requireGroups: true, foldOnly: true, speechFresh: true, write: false); return n;
        }
        // BEFORE: a head from 0.13.22 has the ETags but no group records: every upload's video.json is read for them
        var legacy = new SearchData { Version = 1, MeanVersion = 1, MeanFrames = v1.MeanFrames, Mean = v1.Mean, Bits = v1.Bits, RefUpload = v1.RefUpload, RefCenter = v1.RefCenter, Uploads = v1.Uploads.Select(u => u with { GroupRec = null }).ToList() };
        long b0 = Gets(m, "VideoJson"); await Next(legacy); long before = Gets(m, "VideoJson") - b0;
        // AFTER: the head carries them: no video.json read at all, GroupBuild's included
        long a0 = Gets(m, "VideoJson"); var v2 = await Next(v1); long after = Gets(m, "VideoJson") - a0;
        // one change: only that video.json is read
        await VideoJsonSeed.PutAsync(f, P, "d3", language: "en", dubbed: false);
        long c0 = Gets(m, "VideoJson"); await Next(v1); long changed = Gets(m, "VideoJson") - c0;
        Assert.Equal(N, before); Assert.Equal(0, after); Assert.Equal(1, changed);
        Console.WriteLine($"video.json reads per build over {N} uploads: before {before}, after {after}, one changed {changed}");
        var back = SearchData.ParseUploadsYaml(v2.UploadsYaml()).Uploads; Assert.Equal(v2.Uploads.Select(u => u.GroupRec), back.Select(u => u.GroupRec));
    }

    [Fact]
    public async Task Nothing_changed_publishes_no_version_and_a_change_publishes_one()
    {
        var (f, s, m) = await Seed(5);
        string Head() => System.Text.Encoding.UTF8.GetString(f.Blobs["index/search/head.yaml"].Data);
        async Task<string> Run()
        {
            var w = new StringWriter(); var old = Log.Out; Log.Out = w;
            try
            {
                var job = AvailabilityStage.NewSweep();
                var ctx = new JobContext { Job = job, Stage = "search-index", Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 2, Report = (_, _) => { } };
                await new SearchIndexStage(s).RunAsync(ctx);
            }
            finally { Log.Out = old; }
            return w.ToString();
        }
        Assert.Contains("published v1", await Run()); var head1 = Head();
        var names = f.Blobs.Keys.OrderBy(k => k).ToList();
        VideoJson.ClearCache(s); long g0 = Gets(m, "GroupBuild");
        var log = await Run();
        Assert.Contains("search-index: up to date (v1)", log); Assert.DoesNotContain("published", log);
        Assert.Equal(head1, Head()); Assert.Equal(names, f.Blobs.Keys.OrderBy(k => k).ToList()); Assert.Equal(g0, Gets(m, "GroupBuild"));
        await VideoJsonSeed.PutAsync(f, P, "d2", language: "en", dubbed: false); VideoJson.ClearCache(s);
        Assert.Contains("published v2", await Run()); Assert.Contains("version: 2", Head());
    }

    [Fact]
    public async Task Real_store_shape_two_builds_in_a_row_read_no_group_records_and_publish_once_even_with_unrelated_video_json_changes()
    {
        var (f, s, m) = await Seed(5);
        // non-indexed subjects: a pre-checked short-circuit (has precheck, no frames file) and plain samples
        var pc = new System.Text.Json.Nodes.JsonObject { ["decision"] = "skip", ["at"] = "2026-01-01T00:00:00Z", ["group"] = GroupProposal.None.ToJson() };
        for (int i = 0; i < 20; i++) await VideoJsonSeed.PutAsync(f, P, "x" + i, precheck: i % 2 == 0 ? pc : null);
        string Head() => System.Text.Encoding.UTF8.GetString(f.Blobs["index/search/head.yaml"].Data);
        async Task<(string Log, long Recs)> Run()
        {
            VideoJson.ClearCache(s); long g0 = Gets(m, "ReadRec");
            var w = new StringWriter(); var old = Log.Out; Log.Out = w;
            try { await new SearchIndexStage(s).RunAsync(new JobContext { Job = AvailabilityStage.NewSweep(), Stage = "search-index", Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 2, Report = (_, _) => { } }); }
            finally { Log.Out = old; }
            return (w.ToString(), Gets(m, "ReadRec") - g0);
        }
        var r1 = await Run(); Assert.Contains("published v1", r1.Log); Assert.True(r1.Recs >= 20, "first build reads the non-indexed records: " + r1.Recs);
        var names = f.Blobs.Keys.OrderBy(k => k).ToList(); var head = Head();
        var r2 = await Run();   // second build, store unchanged
        Assert.Equal(0, r2.Recs); Assert.Contains("up to date (v1)", r2.Log); Assert.Equal(head, Head()); Assert.Equal(names, f.Blobs.Keys.OrderBy(k => k).ToList());
        // one unrelated video.json changes (a non-indexed subject, title only): read once, still no publish
        await VideoJsonSeed.PutAsync(f, P, "x3", title: "other", precheck: null);
        var r3 = await Run(); Assert.Equal(1, r3.Recs); Assert.Contains("up to date (v1)", r3.Log); Assert.Equal(names, f.Blobs.Keys.OrderBy(k => k).ToList());
        var r4 = await Run(); Assert.Equal(1, r4.Recs);   // nothing was written, so the head still has the old ETag: re-read until the next publish records the new one
         Assert.Contains("up to date (v1)", r4.Log);
        // an indexed upload whose video.json changed only outside the index's fields: read, not published
        await VideoJsonSeed.PutAsync(f, P, "d1", title: "renamed");
        var r5 = await Run(); Assert.Contains("up to date (v1)", r5.Log);
        // a non-indexed subject that gains a precheck record is a grouping change: published
        await VideoJsonSeed.PutAsync(f, P, "x3", precheck: pc);
        var r6 = await Run(); Assert.Contains("published v2", r6.Log);
    }

    [Fact]
    public async Task Catalog_pre_check_reads_per_build_before_after_and_after_one_change()
    {
        const int N = 5, M = 20; var (f, s, m) = await Seed(N);
        var pc = new System.Text.Json.Nodes.JsonObject { ["decision"] = "skip", ["at"] = "2026-01-01T00:00:00Z", ["group"] = GroupProposal.None.ToJson() };
        for (int i = 0; i < M; i++) await VideoJsonSeed.PutAsync(f, P, "x" + i, precheck: i % 2 == 0 ? pc : null);   // half pre-checked (in the catalog), half downloaded only (left out)
        var ups = await SearchIndexBuild.ListCentersAsync(s, P, default);
        var idx = await SearchIndexBuild.BuildAsync(s, P, ups, null, null, 1, 1, 2, _ => { }, default);
        async Task<(Catalog C, long Reads)> Build(Catalog? prev)
        {
            VideoJson.ClearCache(s); long r0 = Gets(m, "ReadPrecheckAsync");
            var c = await CatalogBuild.BuildAsync(s, P, 2, idx.Uploads, prev, 0.5, null, default);
            return (c, Gets(m, "ReadPrecheckAsync") - r0);
        }
        var first = await Build(null);                                              // BEFORE: no carried fields (a 0.13.22 catalog has none): everything is read
        var legacy = new Catalog { Uploads = first.C.Uploads.Select(u => u with { VideoEtag = null }).ToList() };
        var before = await Build(legacy);
        var after = await Build(first.C);                                           // AFTER: carried by ETag: nothing is read
        await VideoJsonSeed.PutAsync(f, P, "x2", title: "changed", precheck: pc);   // one pre-checked subject changes
        await VideoJsonSeed.PutAsync(f, P, "x5", title: "changed");                 // and one left-out one
        var changed = await Build(first.C);
        Assert.Equal(N + M, before.Reads); Assert.Equal(0, after.Reads); Assert.Equal(2, changed.Reads);
        Assert.Equal(N + M / 2, first.C.Uploads.Count); Assert.Equal(M / 2, first.C.NotCataloged.Count);
        Assert.Equal(first.C.Uploads.Select(u => u.Id), after.C.Uploads.Select(u => u.Id)); Assert.Equal("changed", changed.C.Find("x2")!.Title);
        Console.WriteLine($"catalog pre-check reads per build over {N + M} video.json: before {before.Reads}, after {after.Reads}, two changed {changed.Reads}");
        var back = Catalog.FromGzip(first.C.ToGzip()); Assert.Equal(first.C.NotCataloged, back.NotCataloged); Assert.Equal(first.C.Uploads.Select(u => u.VideoEtag), back.Uploads.Select(u => u.VideoEtag));
    }
}
