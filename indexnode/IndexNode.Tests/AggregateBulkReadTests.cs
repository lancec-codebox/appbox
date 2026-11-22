using System.Net;
namespace IndexNode.Tests;

/// <summary>Bulk readers (search-index build, GroupBuild) read the aggregate once; videos.agg never goes through nexis hydration.</summary>
public class AggregateBulkReadTests
{
    static readonly Paths P = new("index/");
    sealed class Counting : HttpMessageHandler
    {
        public List<string> Urls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Urls.Add(r.Method + " " + r.RequestUri!.AbsolutePath + r.RequestUri.Query); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
    }

    [Fact]
    public async Task Aggregate_never_touches_nexis()
    {
        var h = new Counting(); var rs = new RestStore(new HttpClient(h), "https://acct.blob.core.windows.net/c?sv=1&sig=x");
        await rs.PrepareReadAsync(VideoAggregate.Name(P), default);
        Assert.Empty(h.Urls);   // no touch, no tags, no hydrate
        Assert.True(VideoAggregate.IsAggregate(VideoAggregate.Name(P)));
        Assert.False(VideoAggregate.IsAggregate(P.Video("x", "video.json")));
    }

    static async Task<(FakeStore F, MeteredStore S, StoreMeter M)> Seed(int n)
    {
        var f = new FakeStore(); var m = new StoreMeter(); var s = new MeteredStore(f, m);
        var rnd = new Random(5); var x = new float[40 * CentersFile.Dim]; for (int i = 0; i < x.Length; i++) x[i] = (float)rnd.NextDouble() - 0.5f;
        var f16 = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(f16.AsSpan(i * 2), (Half)x[i]);
        var bin = CentersStep.Build("d", f16, Enumerable.Range(0, 40).Select(i => (double)i).ToList(), 2).Bin;
        for (int i = 0; i < n; i++)
        {
            await FramesFile.WriteAsync(f, P, "d" + i, bin, 0, false, default);
            await VideoJson.UpdateAsync(f, P, "d" + i, r => { VideoJson.SetSpeech(r, "en", false, "precheck"); r["precheck"] = new System.Text.Json.Nodes.JsonObject { ["at"] = "2024-01-01T00:00:00Z" }; }, default);
        }
        VideoJson.ClearCache(s); return (f, s, m);
    }
    static long Reads(StoreMeter m) => m.Table().Where(r => r.Op is "get" or "range" or "head").Sum(r => r.Count);
    static long VideoReads(StoreMeter m) => m.Table().Where(r => r.Op is "get" or "head" && r.Site.Contains("VideoJson")).Sum(r => r.Count);

    [Fact]
    public async Task Search_index_build_store_ops_before_and_after()
    {
        const int N = 30; var (f, s, m) = await Seed(N);
        var ups = await SearchIndexBuild.ListCentersAsync(s, P, default);
        m.Reset(); VideoAggregate.Enabled = false;
        long before, after;
        try
        {
            var a = await SearchIndexBuild.BuildAsync(s, P, ups, null, null, 1, 1, 2, _ => { }, default); before = Reads(m);
            Assert.All(a.Uploads, u => Assert.Equal("en", u.SpeechLanguage));
        }
        finally { VideoAggregate.Enabled = true; }
        m.Reset(); VideoJson.ClearCache(s);
        var b = await SearchIndexBuild.BuildAsync(s, P, ups, null, null, 1, 1, 2, _ => { }, default); after = Reads(m);
        Assert.All(b.Uploads, u => Assert.Equal("en", u.SpeechLanguage));
        var vj = m.Table().Where(r => r.Op == "get" && r.Site.Contains("ReadSpeechEtag")).Sum(r => r.Count);
        Console.WriteLine($"SEARCH-INDEX BUILD N={N}: read ops before={before} after={after}; per-upload video.json gets after={vj}");
        Assert.True(before - after >= N - 3, $"before {before} after {after}");   // the N per-upload reads are replaced by one list + one GET
        Assert.Equal(0, vj);
    }

    [Fact]
    public async Task Group_build_reads_aggregate_and_falls_back_for_an_upload_it_lacks()
    {
        var (f, s, m) = await Seed(5);
        VideoJson.ClearCache(s);
        // an upload with only a per-upload video.json (written before the aggregate)
        await f.PutAsync(P.Video("old", "video.json"), System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"old\",\"speech\":{\"language\":\"de\"}}"), null, null, false, default);
        var view = await VideoAggregate.OpenViewAsync(s, P, default);
        Assert.NotNull(view); Assert.True(view!.Has("d1")); Assert.False(view.Has("old"));
        var a = await VideoJson.ReadViaAsync(view, s, P, "d1", default); var o = await VideoJson.ReadViaAsync(view, s, P, "old", default);
        Assert.Equal("en", (string?)a!.Root["speech"]!["language"]); Assert.Equal("de", (string?)o!.Root["speech"]!["language"]);
    }
}
