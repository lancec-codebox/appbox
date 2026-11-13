using System.Net;
using System.Text;

namespace IndexNode.Tests;

public class RestStoreTests
{
    private const string Sas = "https://acct.blob.core.windows.net/media?sv=2021-12-02&sp=racwdlfti&sig=SECRETSIG%2Bvalue%3D";

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Req, string Body)> Seen = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Seen) Seen.Add((request, body));
            return respond(request, body);
        }
    }

    private static RestStore Store(Handler h) => new(new HttpClient(h), Sas) { RetryDelay = _ => TimeSpan.Zero };
    private static string Header(HttpRequestMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.First() : "";

    [Fact]
    public async Task AcquireLease_sends_action_duration_and_version_and_returns_lease_id()
    {
        var h = new Handler((r, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Created);
            resp.Headers.Add("x-ms-lease-id", "lease-123");
            return resp;
        });
        var id = await Store(h).AcquireLeaseAsync("index/jobs/download/abc.json", 60, default);
        Assert.Equal("lease-123", id);
        var req = h.Seen.Single().Req;
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Contains("comp=lease", req.RequestUri!.Query);
        Assert.Equal("acquire", Header(req, "x-ms-lease-action"));
        Assert.Equal("60", Header(req, "x-ms-lease-duration"));
        Assert.Equal("2021-12-02", Header(req, "x-ms-version"));
        Assert.Equal("/media/index/jobs/download/abc.json", req.RequestUri.AbsolutePath);
        Assert.Contains("sig=SECRETSIG", req.RequestUri.Query);
    }

    [Fact]
    public async Task AcquireLease_409_means_another_node_has_it()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Conflict));
        Assert.Null(await Store(h).AcquireLeaseAsync("x", 60, default));
    }

    [Fact]
    public async Task Renew_and_release_carry_the_lease_id_and_renew_409_is_lease_lost()
    {
        int n = 0;
        var h = new Handler((r, _) => new HttpResponseMessage(++n == 3 ? HttpStatusCode.Conflict : HttpStatusCode.OK));
        var s = Store(h);
        await s.RenewLeaseAsync("job", "L1", default);
        await s.ReleaseLeaseAsync("job", "L1", default);
        Assert.Equal("renew", Header(h.Seen[0].Req, "x-ms-lease-action"));
        Assert.Equal("release", Header(h.Seen[1].Req, "x-ms-lease-action"));
        Assert.All(h.Seen.Take(2), x => Assert.Equal("L1", Header(x.Req, "x-ms-lease-id")));
        await Assert.ThrowsAsync<LeaseLostException>(() => s.RenewLeaseAsync("job", "L1", default));
    }

    [Fact]
    public async Task FindByTags_encodes_the_where_clause_and_parses_tags()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?><EnumerationResults ServiceEndpoint="x"><Where>w</Where><Blobs>
            <Blob><Name>index/jobs/download/a.json</Name><ContainerName>media</ContainerName><Tags><TagSet><Tag><Key>idx_added</Key><Value>2026-11-13T19:00:00Z</Value></Tag><Tag><Key>idx_state</Key><Value>pending</Value></Tag></TagSet></Tags></Blob>
            <Blob><Name>index/jobs/download/b.json</Name><ContainerName>media</ContainerName></Blob>
            </Blobs><NextMarker /></EnumerationResults>
            """;
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) });
        var found = await Store(h).FindByTagsAsync(Tags.PendingWhere("download"), default);
        Assert.Equal(2, found.Count);
        Assert.Equal("pending", found[0].Tags["idx_state"]);
        var q = h.Seen.Single().Req.RequestUri!.Query;
        Assert.Contains("restype=container", q);
        Assert.Contains("comp=blobs", q);
        Assert.Contains("where=%22idx_stage%22%3D%27download%27%20AND%20%22idx_state%22%3D%27pending%27", q);
    }

    [Fact]
    public async Task FindByTags_403_is_a_forbidden_exception_without_the_signature()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied sig=SECRETSIG%2Bvalue%3D") });
        var e = await Assert.ThrowsAsync<ForbiddenException>(() => Store(h).FindByTagsAsync("x", default));
        Assert.DoesNotContain("SECRETSIG", e.Message);
    }

    [Fact]
    public async Task Put_sends_blob_type_tags_and_lease_and_if_none_match()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Created));
        var tags = Tags.Job("download", "pending", "2026-11-13T19:00:00Z");
        await Store(h).PutAsync("index/jobs/download/a.json", [1, 2], tags, "LEASE", false, default);
        var r = h.Seen.Single().Req;
        Assert.Equal("BlockBlob", Header(r, "x-ms-blob-type"));
        Assert.Equal("idx_stage=download&idx_state=pending&idx_added=2026-11-13T19%3A00%3A00Z&idx_due=2026-11-13T19%3A00%3A00Z", Header(r, "x-ms-tags"));
        Assert.Equal("LEASE", Header(r, "x-ms-lease-id"));

        var h2 = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Conflict));
        Assert.False(await Store(h2).PutAsync("a", [], null, null, true, default));
        Assert.Equal("*", Header(h2.Seen.Single().Req, "If-None-Match"));
    }

    [Fact]
    public async Task SetTags_and_metadata_use_comp_and_lease()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var s = Store(h);
        await s.SetTagsAsync("b", new Dictionary<string, string> { ["idx_state"] = "done" }, "L", default);
        await s.SetMetadataAsync("b", new Dictionary<string, string> { ["node"] = "n1", ["progress"] = "42%" }, "L", default);
        Assert.Contains("comp=tags", h.Seen[0].Req.RequestUri!.Query);
        Assert.Contains("<Key>idx_state</Key><Value>done</Value>", h.Seen[0].Body);
        Assert.Contains("comp=metadata", h.Seen[1].Req.RequestUri!.Query);
        Assert.Equal("n1", Header(h.Seen[1].Req, "x-ms-meta-node"));
        Assert.Equal("L", Header(h.Seen[1].Req, "x-ms-lease-id"));
    }

    private static string TempFile(long bytes)
    {
        var p = Path.GetTempFileName();
        using var fs = File.Create(p);
        var chunk = new byte[1024 * 1024];
        for (long w = 0; w < bytes; w += chunk.Length) fs.Write(chunk, 0, (int)Math.Min(chunk.Length, bytes - w));
        return p;
    }

    [Fact]
    public async Task PutFile_uses_8MiB_blocks_and_a_block_list_in_order_with_equal_length_ids()
    {
        var f = TempFile(20L * 1024 * 1024);
        try
        {
            var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Created));
            await Store(h).PutFileAsync("index/videos/x/video.mp4", f, "video/mp4", null, default);
            var blocks = h.Seen.Where(s => s.Req.RequestUri!.Query.Contains("comp=block&")).ToList();
            Assert.Equal(3, blocks.Count);
            var sizes = blocks.Select(b => b.Req.Content!.Headers.ContentLength).OrderByDescending(x => x).ToList();
            Assert.Equal([8L * 1024 * 1024, 8L * 1024 * 1024, 4L * 1024 * 1024], sizes);
            var ids = blocks.Select(b => Uri.UnescapeDataString(System.Web.HttpUtility.ParseQueryString(b.Req.RequestUri!.Query)["blockid"]!)).OrderBy(x => x).ToList();
            Assert.Equal(3, ids.Distinct().Count());
            Assert.Single(ids.Select(i => i.Length).Distinct());
            var list = h.Seen.Last();
            Assert.Contains("comp=blocklist", list.Req.RequestUri!.Query);
            Assert.Equal("video/mp4", Header(list.Req, "x-ms-blob-content-type"));
            var expected = "<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>" + string.Concat(ids.Select(i => $"<Latest>{i}</Latest>")) + "</BlockList>";
            Assert.Equal(expected, list.Body);
            Assert.Equal("blk00000000", Encoding.ASCII.GetString(Convert.FromBase64String(ids[0])));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public async Task PutFile_runs_at_most_4_blocks_in_parallel()
    {
        var f = TempFile(48L * 1024 * 1024); // 6 blocks
        try
        {
            int cur = 0, max = 0;
            var h = new DelayHandler(() =>
            {
                var c = Interlocked.Increment(ref cur);
                int m; while (c > (m = Volatile.Read(ref max))) Interlocked.CompareExchange(ref max, c, m);
            }, () => Interlocked.Decrement(ref cur));
            var s = new RestStore(new HttpClient(h), Sas) { RetryDelay = _ => TimeSpan.Zero };
            await s.PutFileAsync("v", f, "video/mp4", null, default);
            Assert.InRange(max, 2, RestStore.BlockParallelism);
        }
        finally { File.Delete(f); }
    }

    private sealed class DelayHandler(Action enter, Action exit) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Query.Contains("comp=block&")) { enter(); await Task.Delay(80, ct); exit(); }
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    [Fact]
    public async Task PutFile_retries_a_block_up_to_3_times_then_fails()
    {
        var f = TempFile(1024);
        try
        {
            int attempts = 0;
            var h = new Handler((r, _) =>
            {
                if (r.RequestUri!.Query.Contains("comp=block&")) return new HttpResponseMessage(++attempts <= 2 ? HttpStatusCode.InternalServerError : HttpStatusCode.Created);
                return new HttpResponseMessage(HttpStatusCode.Created);
            });
            await Store(h).PutFileAsync("v", f, "video/mp4", null, default);
            Assert.Equal(3, attempts);

            int always = 0;
            var h2 = new Handler((r, _) => { if (r.RequestUri!.Query.Contains("comp=block&")) { always++; return new HttpResponseMessage(HttpStatusCode.InternalServerError); } return new HttpResponseMessage(HttpStatusCode.Created); });
            await Assert.ThrowsAsync<StorageException>(() => Store(h2).PutFileAsync("v", f, "video/mp4", null, default));
            Assert.Equal(1 + RestStore.BlockRetries, always);
            Assert.DoesNotContain(h2.Seen, s => s.Req.RequestUri!.Query.Contains("comp=blocklist"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public async Task List_parses_metadata_tags_and_lease_state()
    {
        var xml = """
            <?xml version="1.0"?><EnumerationResults><Blobs><Blob><Name>index/jobs/process/a.json</Name><Properties><Content-Length>12</Content-Length><LeaseState>leased</LeaseState></Properties>
            <Metadata><node>n1</node><progress>sscd 40%</progress></Metadata><Tags><TagSet><Tag><Key>idx_state</Key><Value>pending</Value></Tag></TagSet></Tags></Blob></Blobs><NextMarker/></EnumerationResults>
            """;
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) });
        var l = await Store(h).ListAsync("index/jobs/", true, true, default);
        Assert.Equal("leased", l[0].LeaseState);
        Assert.Equal("sscd 40%", l[0].Metadata["progress"]);
        Assert.Equal("pending", l[0].Tags["idx_state"]);
        Assert.Contains("include=tags,metadata", h.Seen.Single().Req.RequestUri!.Query);
    }

    [Fact]
    public void Permissions_reads_only_the_sp_field()
    {
        Assert.Equal("racwdlfti", RestStore.Permissions(Sas));
    }
}
