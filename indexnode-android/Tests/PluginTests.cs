using System.Text;
using System.Text.Json.Nodes;
using IndexNode.Hosting;
using IndexNode.Hosting.Medusa;
using IndexNode.Plugin;
using IndexNode.Tests;

namespace IndexNode.Android.Tests;

/// <summary>An ITools that plays yt-dlp, ffprobe and ffmpeg in a local folder: the plugin only sees ITools.</summary>
public sealed class FakeTools : ITools
{
    public readonly List<string> Urls = new();
    public readonly string Root = Directory.CreateTempSubdirectory("fakewp").FullName;
    public Task<IToolWorkspace> CreateWorkspaceAsync(string name, CancellationToken ct)
    {
        var d = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..4]); Directory.CreateDirectory(d);
        return Task.FromResult<IToolWorkspace>(new LocalWorkspace(name, d));
    }
    public readonly List<string[]> Listings = new();
    /// <summary>What a flat listing prints (one tab-separated line per video, RefreshStage.ListPrint).</summary>
    public string ListingOutput = "";
    public Task<ProcResult> RunYtDlpAsync(IEnumerable<string> args, CancellationToken ct, Action<string>? onStdout = null) { Listings.Add(args.ToArray()); return Task.FromResult(new ProcResult(0, ListingOutput, "")); }
    public Task<ProcResult> RunAsync(IToolWorkspace ws, string tool, IReadOnlyList<string> args, CancellationToken ct, Action<string>? onStdout = null)
    {
        var d = ((LocalWorkspace)ws).Dir;
        if (tool != "yt-dlp") return Task.FromResult(new ProcResult(0, "12.5\n", ""));
        var url = args.Last(a => a.StartsWith("http")); Urls.Add(url);
        var o = args[args.ToList().IndexOf("-o") + 1];
        if (args.Contains("--skip-download")) return Task.FromResult(new ProcResult(0, "", ""));
        File.WriteAllText(Path.Combine(d, o.Replace("%(ext)s", "mp4")), "VIDEO");
        File.WriteAllText(Path.Combine(d, o.Replace("%(ext)s", "jpg")), "JPG");
        File.WriteAllText(Path.Combine(d, o.Replace("%(ext)s", "info.json")), """{"id":"x","description":"desc","height":480,"duration":12.5,"format_id":"18","vcodec":"avc1","acodec":"aac","comments":[]}""");
        onStdout?.Invoke("[download]  50.0% of 1MiB");
        return Task.FromResult(new ProcResult(0, "", ""));
    }
}

public class PluginTests
{
    static readonly Paths P = new("index/");

    /// <summary>A pending download entry in the queue log (0.13.0: no job blobs).</summary>
    static Task Dl(FakeStore s, string id, string added, int priority) => s.AddJob(P, "download", id, added, null, priority);
    static QueueState Q(FakeStore s) => s.Replay(P.Prefix);

    static FakeHost Host(FakeTools t)
    {
        var h = new FakeHost(t);
        h.Http = (m, url) => new HostResponse { Status = 200, Body = Encoding.UTF8.GetBytes("ip=203.0.113.9\nloc=US\ncolo=SEA\n") };
        return h;
    }

    [Fact]
    public async Task Smoke_test_passes_without_a_network()
    {
        Assert.Equal("ok", await new NodePlugin().SmokeAsync(new FakeHost()));
    }

    [Fact]
    public async Task A_download_runs_by_priority_and_writes_zip_metadata_follow_up_jobs_and_the_network()
    {
        var s = new FakeStore(); var tools = new FakeTools(); var host = Host(tools);
        await Dl(s, "olderzero01", "2026-11-10T00:00:00Z", 0);
        await Dl(s, "highprio001", "2026-11-17T00:00:00Z", 100);
        var res = JsonNode.Parse(await new NodePlugin().RunWithStoreAsync(host, s, """{"stages":["download"],"maxJobs":1,"once":true}""", default))!;
        Assert.Equal(1, (int)res["jobsStarted"]!);
        // the priority job went first, the older priority-0 job is untouched
        Assert.Equal(["https://www.youtube.com/watch?v=highprio001"], tools.Urls.Distinct().ToList());
        Assert.Equal("pending", Q(s).Entry("olderzero01", "download")!.State);
        Assert.Equal("done", Q(s).Entry("highprio001", "download")!.State);
        var id = "highprio001";
        // video.mp4 and download.zip are blobs; the small files are members of the zip, read back through the same zip-aware store the plugin wrote with
        Assert.True(s.Exists(P.Video(id, "video.mp4"))); Assert.True(s.Exists(P.Video(id, "download.zip")));
        var rd = StageZipStore.Wrap(s, Directory.CreateTempSubdirectory("rd").FullName);
        foreach (var f in new[] { "info.json", "thumb.jpg", "description.txt", "downloaded.json" })
            Assert.NotNull(await rd.GetAsync(P.Video(id, f), default));
        var marker = JsonNode.Parse(Encoding.UTF8.GetString((await rd.GetAsync(P.Video(id, "downloaded.json"), default))!.Data))!;
        Assert.Equal("android-pixel-abc123", (string)marker["nodeId"]!);
        Assert.Equal(480, (int)marker["height"]!);
        // the download only downloads and uploads: its done line adds the process follow-up (no comments or step jobs, no pre-check here)
        Assert.Equal("pending", Q(s).Entry(id, "process")!.State); Assert.Null(Q(s).Entry(id, "comments"));
        // the network report: the node record carries the address, transport and kind
        var node = JsonNode.Parse(Encoding.UTF8.GetString(s[P.Node("android-pixel-abc123")].Data))!;
        Assert.Equal("android", (string)node["kind"]!);
        Assert.Equal("203.0.113.9", (string)node["network"]!["ipv4"]!);
        Assert.Equal("wifi", (string)node["network"]!["transport"]!);
        // the job records the network it started on, so the marker does too
        Assert.Equal("203.0.113.9", (string)marker["network"]!["ipv4"]!);
    }

    [Fact]
    public async Task A_refresh_run_lists_the_source_writes_members_and_queues_new_videos_unclassified()
    {
        var s = new FakeStore(); var tools = new FakeTools(); var host = Host(tools);
        tools.ListingOutput = "aaaaaaaaaaa\tUCchan\tChan\t120\tpublic\tOne\nbbbbbbbbbbb\tUCchan\tChan\t60\tpublic\tTwo\n";
        var src = JobDescriptor.NewSource("playlist", "PLtest", "Test list", "once", ["videos"], false, -1, 480);   // a new source starts as a lookup
        var q = new JobQueue(s, P, "seed", () => DateTimeOffset.UtcNow); await q.LoadAsync(default);
        Assert.True(await Sources.AddAsync(q, src, default));
        // a download node (no stages given) runs refresh as well, like the desktop one
        var res = JsonNode.Parse(await new NodePlugin().RunWithStoreAsync(host, s, """{"maxJobs":1,"once":true}""", default))!;
        Assert.Contains("refresh", res["stages"]!.AsArray().Select(x => (string)x!));
        // the listing call is a flat playlist listing
        Assert.Contains(tools.Listings, a => a.Contains("--flat-playlist"));
        // members.json for the catalog
        var mem = JsonNode.Parse(Encoding.UTF8.GetString(s[P.SourceMembers("pl-PLtest")].Data))!;
        Assert.Equal(["aaaaaaaaaaa", "bbbbbbbbbbb"], mem["members"]!.AsArray().Select(m => (string)m!["id"]!).ToArray());
        // every new video is a download job, unclassified (the classify job decides)
        foreach (var v in new[] { "aaaaaaaaaaa", "bbbbbbbbbbb" })
        {
            Assert.Equal("unclassified", Q(s).Entry(v, "download")!.State);
        }
        // the source job is done (schedule once)
        Assert.Equal("done", Q(s).Entry("pl-PLtest", "refresh")!.State);
        // noRefresh leaves the stage out
        var res2 = JsonNode.Parse(await new NodePlugin().RunWithStoreAsync(host, new FakeStore(), """{"stages":["download"],"noRefresh":true,"once":true}""", default))!;
        Assert.DoesNotContain("refresh", res2["stages"]!.AsArray().Select(x => (string)x!));
    }

    [Fact]
    public async Task With_a_check_server_in_the_settings_an_idle_node_asks_the_download_service_with_its_token()
    {
        var s = new FakeStore(); var tools = new FakeTools(); var host = Host(tools);
        var ip = host.Http!;
        host.Http = (m, url) => url.EndsWith("/v1/download/next") ? new HostResponse { Status = 200, ContentType = "application/json", Body = Encoding.UTF8.GetBytes("""{"none":true,"retryAfterSeconds":60}""") } : ip(m, url);
        host.Settings["checkUrl"] = "https://check.example.net/"; host.Settings["checkToken"] = "TOKEN-abc123";
        var res = JsonNode.Parse(await new NodePlugin().RunWithStoreAsync(host, s, """{"stages":["download"],"noRefresh":true,"once":true}""", default))!;
        Assert.True((bool)res["checkServer"]!); Assert.Equal(0, (int)res["jobsStarted"]!);
        var call = Assert.Single(host.HttpCalls, c => c.Url.EndsWith("/v1/download/next"));
        Assert.Equal("POST", call.Method); Assert.Equal("https://check.example.net/v1/download/next", call.Url);
        Assert.Equal("Bearer TOKEN-abc123", call.Headers!["Authorization"]);
        var body = JsonNode.Parse(call.Body)!; Assert.Equal("android-pixel-abc123", (string)body["node"]!); Assert.Contains("download", body["stages"]!.AsArray().Select(x => (string)x!));
        Assert.DoesNotContain(host.Logs, l => l.Contains("TOKEN-abc123"));
        // no check server in the settings: no call, and the result says so
        var plain = Host(tools);
        var res2 = JsonNode.Parse(await new NodePlugin().RunWithStoreAsync(plain, new FakeStore(), """{"stages":["download"],"noRefresh":true,"once":true}""", default))!;
        Assert.False((bool)res2["checkServer"]!); Assert.DoesNotContain(plain.HttpCalls, c => c.Url.Contains("/v1/download/"));
    }

    [Fact]
    public async Task A_stage_a_downloader_has_no_handler_for_is_refused()
    {
        var host = Host(new FakeTools());
        var e = await Assert.ThrowsAsync<ArgumentException>(() => new NodePlugin().RunWithStoreAsync(host, new FakeStore(), """{"stages":["process"],"once":true}""", default));
        Assert.Contains("does not run stage process", e.Message);
    }

    [Fact]
    public async Task Control_stop_keeps_the_node_from_claiming()
    {
        var s = new FakeStore(); var tools = new FakeTools(); var host = Host(tools);
        await Dl(s, "stoppedjob1", "2026-11-17T00:00:00Z", 0);
        await s.PutAsync(P.Control, Encoding.UTF8.GetBytes("""{"mode":"stop"}"""), null, null, false, default);   // index/jobs/control.json
        await new NodePlugin().RunWithStoreAsync(host, s, """{"stages":["download"],"once":true}""", default);
        Assert.Empty(tools.Urls);
        Assert.Equal("pending", Q(s).Entry("stoppedjob1", "download")!.State);
    }
}
