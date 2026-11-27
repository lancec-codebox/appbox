using System.Net;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using FluentAssertions;

namespace Nexis.Azure.Utilities.Tests;

public class HydrateBlobTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Query, string Body, string? Tags)> Requests = new();
        public long BlockSize = 5;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("x-ms-tags", out var tags);
            Requests.Add((request.Method, request.RequestUri!.Query, body, tags?.FirstOrDefault()));
            var q = request.RequestUri.Query;
            HttpResponseMessage resp;
            if (request.Method == HttpMethod.Get && q.Contains("comp=blocklist"))
            {
                var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList><UncommittedBlocks>"
                    + $"<Block><Name>gd+x+00001</Name><Size>{BlockSize}</Size></Block>"
                    + $"<Block><Name>gd+x+00000</Name><Size>{BlockSize}</Size></Block>"
                    + $"<Block><Name>other</Name><Size>99</Size></Block>"
                    + "</UncommittedBlocks></BlockList>";
                resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
            }
            else
            {
                resp = new HttpResponseMessage(HttpStatusCode.Created);
                resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"0x1\"");
            }
            return resp;
        }
    }

    [Fact]
    public async Task HydrateBlobAsync_commits_prefixed_blocks_in_order_and_sets_active_state()
    {
        var handler = new FakeHandler();
        var options = new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        options.Retry.MaxRetries = 0;
        var client = new BlockBlobClient(new Uri("https://acct.blob.core.windows.net/c/index/videos/x/video.mp4?sv=2021-12-02&sig=abc"), options);
        var tags = new Dictionary<string, string>
        {
            ["ghostd_state"] = "ghost",
            ["ghostd_block_prefix"] = "gd+x+",
            ["ghostd_size"] = "10",
            ["ghostd_last_access"] = "2026-11-13T19:00:00.0000000Z",
            ["idx_stage"] = "keepme",
        };

        await DehydrateOperation.HydrateBlobAsync(client, tags, null, null, CancellationToken.None);

        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        put.Query.Should().Contain("comp=blocklist");
        put.Body.IndexOf("gd+x+00000").Should().BeLessThan(put.Body.IndexOf("gd+x+00001"));
        put.Body.Should().NotContain("other");
        put.Tags.Should().Contain("ghostd_state=active").And.Contain("idx_stage=keepme");
        put.Tags.Should().NotContain("ghostd_block_prefix");
    }

    [Fact]
    public async Task A_proxy_form_sas_url_keeps_the_proxy_host_and_the_account_container_segment()
    {   // 0.13.80: ddindex-blob, https://<proxy>/<account>-<container>/<blob>?<sas>; the SDK on a non-Azure host takes the first segment as the container
        var handler = new UriHandler();
        var options = new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        options.Retry.MaxRetries = 0;
        var client = new BlockBlobClient(new Uri("https://ddindex-blob.example.io/acct-media/index/videos/x/video.mp4?sv=2021-12-02&sig=abc"), options);
        var tags = new Dictionary<string, string> { ["ghostd_state"] = "ghost", ["ghostd_block_prefix"] = "gd+x+", ["ghostd_size"] = "10" };
        await DehydrateOperation.HydrateBlobAsync(client, tags, null, null, CancellationToken.None);
        handler.Uris.Should().NotBeEmpty();
        handler.Uris.Should().OnlyContain(u => u.Host == "ddindex-blob.example.io" && u.AbsolutePath == "/acct-media/index/videos/x/video.mp4" && u.Query.Contains("sig=abc"));

        var rs = new IndexNode.RestStore(new HttpClient(), "https://ddindex-blob.example.io/acct-media?sv=2021-12-02&sig=abc");
        rs.BlobUri("index/videos/x/video.json").ToString().Should().Be("https://ddindex-blob.example.io/acct-media/index/videos/x/video.json?sv=2021-12-02&sig=abc");
        IndexNode.Program.ClientName("proc 18/x").Should().Be("proc-18-x");
    }

    private sealed class UriHandler : HttpMessageHandler
    {
        public List<Uri> Uris = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!);
            HttpResponseMessage resp = request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("comp=blocklist")
                ? new(HttpStatusCode.OK) { Content = new StringContent("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList><UncommittedBlocks><Block><Name>gd+x+00000</Name><Size>5</Size></Block><Block><Name>gd+x+00001</Name><Size>5</Size></Block></UncommittedBlocks></BlockList>", Encoding.UTF8, "application/xml") }
                : new(HttpStatusCode.Created);
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"0x1\"");
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task HydrateBlobAsync_fails_when_block_sizes_do_not_match_tag()
    {
        var handler = new FakeHandler { BlockSize = 4 };
        var options = new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        var client = new BlockBlobClient(new Uri("https://acct.blob.core.windows.net/c/v.mp4?sv=2021-12-02&sig=abc"), options);
        var tags = new Dictionary<string, string> { ["ghostd_block_prefix"] = "gd+x+", ["ghostd_size"] = "10" };

        var act = () => DehydrateOperation.HydrateBlobAsync(client, tags, null, null, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }
}
