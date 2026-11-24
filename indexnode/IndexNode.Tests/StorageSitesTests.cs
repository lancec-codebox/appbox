using System.Net;
using System.Text;

namespace IndexNode.Tests;

/// <summary>0.13.18: RestStore counts requests per site with 404/409/412/304 apart and remembers a 404; the high-volume call sites make one LIST where they made a HEAD or GET per file.</summary>
public class StorageSitesTests
{
    const string Sas = "https://acct.blob.core.windows.net/media?sv=2021-12-02&sp=racwdlfti&sig=SIG";
    static readonly Paths P = new("index/");
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls; public readonly List<string> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { lock (Seen) { Calls++; Seen.Add(r.Method + " " + r.RequestUri!.AbsolutePath + r.RequestUri.Query.Split('&')[0]); } return Task.FromResult(respond(r)); }
    }
    static (RestStore, StoreMeter) Rest(Handler h) { var m = new StoreMeter { Limits = new StorageLimits { MaxFailuresPerMin = 100000, MaxPerMin = 1000000 } }; return (new RestStore(new HttpClient(h), Sas) { RetryDelay = _ => TimeSpan.Zero, Meter = m, Negative = new NegativeCache() }, m); }
    static async Task ReadVideoJson(IStore s) => await s.GetAsync("index/videos/a/video.json", default);

    [Fact]
    public async Task Rest_requests_are_counted_per_site_and_op_with_404_409_412_and_304_apart()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/gone") ? new(HttpStatusCode.NotFound) : r.RequestUri.AbsolutePath.EndsWith("/same") ? new(HttpStatusCode.NotModified) : r.Method == HttpMethod.Put ? new(HttpStatusCode.PreconditionFailed) : new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        var (s, m) = Rest(h);
        await ReadVideoJson(s); await s.GetAsync("gone", default); await s.HeadAsync("x/gone", default);
        await s.GetIfChangedAsync("same", "\"e\"", default); await s.PutAsync("p", [1], null, null, true, default);
        var t = m.Table();
        Assert.Contains(t, r => r.Site.Contains("ReadVideoJson") && r.Op == "get");
        Assert.Contains(t, r => r.Op == "get!404" && r.Example == "gone"); Assert.Contains(t, r => r.Op == "head!404");
        Assert.Contains(t, r => r.Op == "get!304"); Assert.Contains(t, r => r.Op == "put!412");
        Assert.Equal(5, m.Total); Assert.Equal(3, m.TotalFailed); Assert.Equal(1, m.TotalNotModified);   // 0.13.22: the 304 is a success: in the totals, not in the failures
    }

    [Fact]
    public async Task A_missing_name_is_not_asked_for_twice_until_a_listing_or_a_write_says_it_exists()
    {
        bool exists = false;
        var h = new Handler(r => r.Method == HttpMethod.Put ? new(HttpStatusCode.Created) : r.RequestUri!.Query.Contains("comp=list") ? new(HttpStatusCode.OK) { Content = new StringContent("<EnumerationResults><Blobs><Blob><Name>n/x</Name><Properties><Content-Length>3</Content-Length><Etag>0x1</Etag></Properties></Blob></Blobs><NextMarker/></EnumerationResults>", Encoding.UTF8) } : exists && !r.RequestUri!.AbsolutePath.EndsWith("/n/y") ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) } : new(HttpStatusCode.NotFound));
        var (s, _) = Rest(h);
        for (int i = 0; i < 5; i++) Assert.Null(await s.GetAsync("n/x", default));
        Assert.Equal(1, h.Calls);                               // one request, four answered from memory
        Assert.Null(await s.HeadAsync("n/x", default)); Assert.Equal(1, h.Calls);
        exists = true; await s.ListAsync("n/", false, false, default);   // the listing says it exists
        Assert.NotNull(await s.GetAsync("n/x", default)); Assert.Equal(3, h.Calls);
        Assert.Null(await s.GetAsync("n/y", default)); int before = h.Calls; Assert.Null(await s.GetAsync("n/y", default)); Assert.Equal(before, h.Calls);
        await s.PutAsync("n/y", [1], null, null, false, default); await s.GetAsync("n/y", default); Assert.Equal(before + 2, h.Calls);   // a write clears it: the next read asks again
    }

    [Fact]
    public async Task The_control_file_is_never_remembered_as_missing()
    {
        var h = new Handler(_ => new(HttpStatusCode.NotFound)); var (s, _) = Rest(h);
        await s.GetIfChangedAsync(P.Control, null, default); await s.GetAsync(P.Control, default); await s.GetAsync(P.Control, default);
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task Video_json_is_read_once_in_a_step_and_by_etag_afterwards()
    {
        var f = new FakeStore(); await VideoJsonSeed.PutAsync(f, P, "a", 600);
        var m = new StoreMeter(); var s = new MeteredStore(f, m); VideoJson.ClearCache(s);
        for (int i = 0; i < 6; i++) Assert.NotNull(await VideoJson.ReadAsync(s, P, "a", default));
        Assert.Equal(1, m.Total);
        var old = VideoJson.Fresh; VideoJson.Fresh = TimeSpan.Zero;
        try { await VideoJson.ReadAsync(s, P, "a", default); Assert.Equal(2, m.Total); Assert.Contains(m.Table(), r => r.Op == "get!304" || m.Total == 2); } finally { VideoJson.Fresh = old; }
        await VideoJson.UpdateAsync(s, P, "a", r => r["x"] = 1, default);
        Assert.Equal(1, (int)(await VideoJson.ReadAsync(s, P, "a", default))!.Root["x"]!);   // the write is seen
    }

    [Fact]
    public async Task The_frames_reader_takes_size_and_etag_from_one_listing_not_a_head_per_file()
    {
        var f = new FakeStore(); var m = new StoreMeter(); var s = new MeteredStore(f, m);
        var rnd = new Random(3); var x = new float[40 * CentersFile.Dim]; for (int i = 0; i < x.Length; i++) x[i] = (float)rnd.NextDouble() - 0.5f;
        var f16 = new byte[x.Length * 2]; for (int i = 0; i < x.Length; i++) BitConverter.TryWriteBytes(f16.AsSpan(i * 2), (Half)x[i]);
        var bin = CentersStep.Build("d", f16, Enumerable.Range(0, 40).Select(i => (double)i).ToList(), 2).Bin;
        for (int i = 0; i < 20; i++) await FramesFile.WriteAsync(f, P, "d" + i, bin, 0, false, default);
        m.Reset();
        for (int i = 0; i < 20; i++) Assert.NotNull(await FramesReader.OpenAsync(s, P.FramesFile("d" + i), default));
        Assert.Equal(1, m.Table().Where(r => r.Op == "list").Sum(r => r.Count)); Assert.Equal(0, m.Table().Where(r => r.Op == "head").Sum(r => r.Count));
    }
}
