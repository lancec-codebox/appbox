using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.42: the downloader node the desktop and the phone share (stages, handlers, the check server's two calls), and the phone setup that carries the check server.</summary>
public class DownloaderNodeTests
{
    sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Req, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((r, body));
            return answer(r, body);
        }
    }
    static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };

    [Fact]
    public void A_download_node_also_refreshes_unless_told_not_to()
    {
        Assert.Equal(new[] { "download", "refresh" }, DownloaderNode.WithRefresh(["download"], false));
        Assert.Equal(new[] { "download" }, DownloaderNode.WithRefresh(["download"], true));
        Assert.Equal(new[] { "download", "refresh" }, DownloaderNode.WithRefresh(["download", "refresh"], false));
        Assert.Equal(new[] { "process" }, DownloaderNode.WithRefresh(["process"], false));   // not a download node: untouched
    }

    [Fact]
    public void The_handlers_are_the_downloader_stages_and_the_sample_stage_gets_the_check_client_only_with_a_check_server()
    {
        using var http = new HttpClient(new Stub((_, _) => Json("{}")));
        var none = DownloaderNode.Handlers(null!, new FakeStore(), new DownloadGate(), http, new DownloaderSettings());
        Assert.Equal(DownloaderNode.StageNames.OrderBy(x => x), none.Keys.OrderBy(x => x));
        Assert.IsType<DownloadStage>(none["download"]); Assert.IsType<RefreshStage>(none["refresh"]); Assert.IsType<AvailabilityStage>(none["availability"]);
        Assert.Null(((SampleStage)none["sample"]).Inline);
        Assert.Null(DownloaderNode.FetchNext(http, new DownloaderSettings(), "n1"));
        Assert.Null(DownloaderNode.FetchNext(http, new DownloaderSettings { CheckUrl = " " }, "n1"));
        var with = DownloaderNode.Handlers(null!, new FakeStore(), new DownloadGate(), http, new DownloaderSettings { CheckUrl = "https://check.example.net/", CheckToken = "t" });
        Assert.Equal("https://check.example.net", ((SampleStage)with["sample"]).Inline!.Url);   // no trailing slash: the client adds /v1/check
    }

    [Fact]
    public async Task FetchNext_posts_the_node_and_its_stages_to_the_download_service_with_the_token()
    {
        var stub = new Stub((r, _) => Json("""{"subject":"abcdefghijk","stage":"download","epoch":3,"lease":"L1"}"""));
        using var http = new HttpClient(stub);
        var fetch = DownloaderNode.FetchNext(http, new DownloaderSettings { CheckUrl = "https://check.example.net/", CheckToken = "TOKEN-abc123" }, "android-pixel-abc123")!;
        var got = await fetch(["download", "refresh"], default);
        Assert.Equal(new NextClaim("abcdefghijk", "download", 3, "L1"), got);
        var (req, body) = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, req.Method); Assert.Equal("https://check.example.net/v1/download/next", req.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme); Assert.Equal("TOKEN-abc123", req.Headers.Authorization.Parameter);
        var j = JsonNode.Parse(body)!; Assert.Equal("android-pixel-abc123", (string)j["node"]!); Assert.Equal(new[] { "download", "refresh" }, j["stages"]!.AsArray().Select(x => (string)x!).ToArray());
        // nothing to hand over
        var idle = new Stub((_, _) => Json("""{"none":true,"retryAfterSeconds":60}"""));
        using var http2 = new HttpClient(idle);
        Assert.Null(await DownloaderNode.FetchNext(http2, new DownloaderSettings { CheckUrl = "https://check.example.net" }, "n1")!(["download"], default));
        Assert.Null(Assert.Single(idle.Calls).Req.Headers.Authorization);   // no token set: no header
    }

    [Fact]
    public void The_desktop_config_gives_the_same_settings()
    {
        var cfg = new NodeConfig { Check = new InlineCheckConfig { Url = "https://check.example.net", Token = "t1", TimeoutSeconds = 30 }, PreCheck = new PreCheckSettings() };
        var d = cfg.Downloader();
        Assert.True(d.HasCheck); Assert.Equal("https://check.example.net", d.CheckUrl); Assert.Equal("t1", d.CheckToken); Assert.Equal(30, d.CheckTimeoutSeconds); Assert.Same(cfg.PreCheck, d.PreCheck);
        Assert.False(new NodeConfig().Downloader().HasCheck);
    }

    [Fact]
    public void A_phone_setup_carries_the_check_server_and_no_sas_also_leaves_the_token_out()
    {
        const string sas = "https://acct.blob.core.windows.net/c?sv=2023-01-01&sig=SECRETSIG%2Babc123&se=2030-01-01";
        var o = new MedusaSetup.Options(sas, "phone-1", CheckUrl: "https://check.example.net/", CheckToken: "TOKEN-abc123");
        var j = MedusaSetup.Parse(MedusaSetup.Link(MedusaSetup.Build(o)));
        Assert.Equal("https://check.example.net", (string)j["checkUrl"]!); Assert.Equal("TOKEN-abc123", (string)j["checkToken"]!);
        var plain = MedusaSetup.Build(new MedusaSetup.Options(sas, "phone-1"));
        Assert.False(plain.ContainsKey("checkUrl")); Assert.False(plain.ContainsKey("checkToken"));   // a setup without a check server is what it was before
        Assert.Throws<ArgumentException>(() => MedusaSetup.Build(new MedusaSetup.Options(sas, "n", CheckUrl: "not a url")));
        var dir = Directory.CreateTempSubdirectory("setup").FullName; var file = Path.Combine(dir, "x.medusa-node.json");
        MedusaSetup.Run(new MedusaSetup.Options(null, null, CheckUrl: "https://check.example.net", CheckToken: "TOKEN-abc123"), false, file, dir, true);
        var shared = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal("https://check.example.net", (string)shared["checkUrl"]!); Assert.False(shared.ContainsKey("checkToken")); Assert.DoesNotContain("TOKEN-abc123", File.ReadAllText(file));
    }
}
