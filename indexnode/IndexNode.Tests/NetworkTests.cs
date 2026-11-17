using System.Text.Json;

namespace IndexNode.Tests;

public class NetworkTests
{
    private static readonly Paths P = new("index-net/");
    private const string T4 = "fl=1\nh=1.1.1.1\nip=203.0.113.7\nts=1\nvisit_scheme=https\nloc=US\ncolo=SEA\n";
    private const string T6 = "fl=1\nip=2001:db8:1:2:aaaa:bbbb:cccc:dddd\nloc=US\ncolo=SEA\n";

    private sealed class FuncHandler(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }

    [Fact]
    public void Trace_parsing_reads_ip_loc_and_colo()
    {
        var t = NetworkProbe.ParseTrace(T4);
        Assert.Equal(("203.0.113.7", "US", "SEA"), (t.Ip, t.Loc, t.Colo));
        Assert.Equal(new TraceInfo(null, null, null), NetworkProbe.ParseTrace(null));
        Assert.Null(NetworkProbe.ParseTrace("garbage\nip=\n").Ip);
    }

    [Fact]
    public void The_ipv6_prefix_is_the_first_64_bits()
    {
        Assert.Equal("2001:db8:1:2::/64", NetworkProbe.Ipv6Prefix64("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        Assert.Equal("2001:db8::/64", NetworkProbe.Ipv6Prefix64("2001:db8::1"));
        Assert.Null(NetworkProbe.Ipv6Prefix64("203.0.113.7")); Assert.Null(NetworkProbe.Ipv6Prefix64(null)); Assert.Null(NetworkProbe.Ipv6Prefix64("nope"));
    }

    [Fact]
    public async Task Both_families_are_looked_up_separately()
    {
        var urls = new List<string>();
        var n = await NetworkProbe.DetectAsync((u, _) => { lock (urls) urls.Add(u); return Task.FromResult<string?>(u == NetworkProbe.V4Url ? T4 : u == NetworkProbe.V6Url ? T6 : null); }, default);
        Assert.Equal("203.0.113.7", n.Ipv4); Assert.Equal("2001:db8:1:2:aaaa:bbbb:cccc:dddd", n.Ipv6); Assert.Equal("2001:db8:1:2::/64", n.Ipv6Prefix);
        Assert.Equal(("US", "SEA"), (n.Country, n.Colo)); Assert.DoesNotContain(NetworkProbe.FallbackUrl, urls);
    }

    [Fact]
    public async Task A_missing_family_is_null_and_the_fallback_fills_ipv4()
    {
        // no IPv6 route: the v6 endpoint fails, the fallback answers with an IPv4 (already known): ipv6 stays null
        var n = await NetworkProbe.DetectAsync((u, _) => u == NetworkProbe.V4Url ? Task.FromResult<string?>(T4) : throw new HttpRequestException("no route"), default);
        Assert.Equal("203.0.113.7", n.Ipv4); Assert.Null(n.Ipv6); Assert.Null(n.Ipv6Prefix);
        // 1.1.1.1 blocked: the fallback endpoint supplies the IPv4
        var m = await NetworkProbe.DetectAsync((u, _) => Task.FromResult<string?>(u == NetworkProbe.FallbackUrl ? T4 : null), default);
        Assert.Equal("203.0.113.7", m.Ipv4); Assert.Null(m.Ipv6);
        // nothing answers
        var none = await NetworkProbe.DetectAsync((_, _) => Task.FromResult<string?>(null), default);
        Assert.Null(none.Ipv4); Assert.Null(none.Ipv6); Assert.NotEqual("", none.CheckedAt);
    }

    [Fact]
    public async Task A_family_that_does_not_answer_in_time_is_null()
    {
        var n = await NetworkProbe.DetectAsync(async (u, ct) => { if (u == NetworkProbe.V6Url || u == NetworkProbe.FallbackUrl) { await Task.Delay(Timeout.Infinite, ct); } return T4; },
            default, timeout: TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("203.0.113.7", n.Ipv4); Assert.Null(n.Ipv6);
    }

    [Fact]
    public async Task Since_stays_while_the_address_pair_stays()
    {
        var first = await NetworkProbe.DetectAsync((u, _) => Task.FromResult<string?>(u == NetworkProbe.V4Url ? T4 : null), default);
        first.Since = "2026-11-01T00:00:00Z";
        var same = await NetworkProbe.DetectAsync((u, _) => Task.FromResult<string?>(u == NetworkProbe.V4Url ? T4 : null), default, first);
        Assert.Equal("2026-11-01T00:00:00Z", same.Since);
        var moved = await NetworkProbe.DetectAsync((u, _) => Task.FromResult<string?>(u == NetworkProbe.V4Url ? T4.Replace("203.0.113.7", "198.51.100.9") : null), default, first);
        Assert.NotEqual("2026-11-01T00:00:00Z", moved.Since);
    }

    // ---------------------------------------------------------------- the node record

    private static EngineOptions Opts(string id, Func<string, CancellationToken, Task<string?>>? fetch = null, bool once = true) => new()
    {
        NodeId = id, Name = id, Stages = ["download"], Jobs = new() { ["download"] = 1 }, Threads = 1, Once = once,
        PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(40), ControlInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(50),
        PlacementProbe = _ => Task.FromResult(new Placement { Provider = "unknown", Reason = "test", CheckedAt = Protocol.NowUtc() }),
        NetworkFetch = fetch ?? ((u, _) => Task.FromResult<string?>(u == NetworkProbe.V4Url ? T4 : u == NetworkProbe.V6Url ? T6 : null)),
    };

    private static NodeEngine Engine(FakeStore s, EngineOptions o, Func<JobContext, Task>? download = null) =>
        new(s, P, o, new Dictionary<string, IStageHandler> { ["download"] = new FuncHandler(download ?? (_ => Task.CompletedTask)) });

    [Fact]
    public async Task The_node_record_carries_the_network_and_keeps_its_other_fields()
    {
        var s = new FakeStore();
        var t = new Traffic(); t.Add(Traffic.OtherIn, 1000);
        var o = Opts("net-node"); var oo = new EngineOptions
        {
            NodeId = o.NodeId, Name = o.Name, Stages = o.Stages, Jobs = o.Jobs, Threads = 1, Once = true, Traffic = t, PollInterval = o.PollInterval, RenewInterval = o.RenewInterval,
            ControlInterval = o.ControlInterval, RegistryInterval = o.RegistryInterval, PlacementProbe = o.PlacementProbe, NetworkFetch = o.NetworkFetch,
        };
        await Engine(s, oo).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var raw = JsonDocument.Parse(s[P.Node("net-node")].Data).RootElement;
        var net = raw.GetProperty("network");
        Assert.Equal("203.0.113.7", net.GetProperty("ipv4").GetString()); Assert.Equal("2001:db8:1:2::/64", net.GetProperty("ipv6Prefix").GetString());
        Assert.Equal("US", net.GetProperty("country").GetString()); Assert.Equal("SEA", net.GetProperty("colo").GetString());
        Assert.True(net.TryGetProperty("checkedAt", out _)); Assert.True(net.TryGetProperty("since", out _));
        Assert.False(net.TryGetProperty("transport", out _)); Assert.False(net.TryGetProperty("vpn", out _)); // Medusa only
        // every existing field is still there
        foreach (var k in new[] { "nodeId", "name", "kind", "version", "platform", "stages", "jobs", "threads", "state", "control", "running", "traffic", "placement", "firstSeen", "instance", "lastSeen", "startedAt" })
            Assert.True(raw.TryGetProperty(k, out _), k);
        Assert.Equal(1000, raw.GetProperty("traffic").GetProperty("totals").GetProperty("otherIn").GetInt64());
    }

    [Fact]
    public async Task Since_carries_over_a_restart_with_the_same_address()
    {
        var s = new FakeStore();
        await Engine(s, Opts("net-node")).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var first = NodeInfo.Parse(s[P.Node("net-node")].Data)!.Network!;
        await Task.Delay(1100);
        await Engine(s, Opts("net-node")).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var second = NodeInfo.Parse(s[P.Node("net-node")].Data)!.Network!;
        Assert.Equal(first.Since, second.Since); Assert.NotEqual(first.CheckedAt, second.CheckedAt);
    }

    // ---------------------------------------------------------------- jobs record the network

    [Fact]
    public async Task A_failed_attempt_a_bot_block_and_a_success_all_record_the_network()
    {
        var s = new FakeStore();
        await s.AddJob(P, "download", "boom", "2026-11-13T19:00:00Z");
        await Engine(s, Opts("net-node"), _ => throw new InvalidOperationException("disk full")).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var j = s.Job(P.Job("download", "boom"));
        Assert.Equal("disk full", j.LastError); Assert.Equal(1, j.Attempts);
        Assert.Equal("203.0.113.7", j.Network!.Ipv4); Assert.Equal("2001:db8:1:2::/64", j.Network.Ipv6Prefix);

        var s2 = new FakeStore();
        await s2.AddJob(P, "download", "blocked", "2026-11-13T19:00:00Z");
        await Engine(s2, Opts("net-node"), _ => throw new BotBlocked("Sign in to confirm you're not a bot")).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var b = s2.Job(P.Job("download", "blocked"));
        Assert.Equal("pending", s2.State(P.Job("download", "blocked"))); Assert.Equal(0, b.Attempts); // no attempt counted
        Assert.Contains("not a bot", b.LastError); Assert.Equal("203.0.113.7", b.Network!.Ipv4);

        var s3 = new FakeStore();
        await s3.AddJob(P, "download", "ok", "2026-11-13T19:00:00Z");
        await Engine(s3, Opts("net-node")).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("done", s3.State(P.Job("download", "ok"))); Assert.Equal("203.0.113.7", s3.Job(P.Job("download", "ok")).Network!.Ipv4);
    }

    // ---------------------------------------------------------------- the per-IP cap

    private static NodeInfo Peer(string id, string? ip4, string? prefix, int downloads, DateTimeOffset seen, string state = "working", string stage = "download") => new()
    {
        NodeId = id, State = state, LastSeen = Protocol.Utc(seen), Network = new NetworkInfo { Ipv4 = ip4, Ipv6Prefix = prefix },
        Running = Enumerable.Range(0, downloads).Select(i => new RunningJob { JobId = id + i, Stage = stage }).ToList(),
    };

    [Fact]
    public void Cap_counts_the_same_v4_the_same_v6_64_and_nothing_else()
    {
        var now = DateTimeOffset.Parse("2026-11-17T12:00:00Z");
        var self = new NetworkInfo { Ipv4 = "203.0.113.7", Ipv6Prefix = "2001:db8:1:2::/64" };
        var peers = new[]
        {
            Peer("same-v4", "203.0.113.7", null, 1, now),
            Peer("same-v6", null, "2001:db8:1:2::/64", 2, now.AddSeconds(-30)),
            Peer("other-64", "198.51.100.9", "2001:db8:1:3::/64", 3, now),          // a different /64 and a different v4
            Peer("stale", "203.0.113.7", null, 4, now.AddMinutes(-4)),              // lastSeen over 3 minutes ago
            Peer("offline", "203.0.113.7", null, 5, now, state: "offline"),
            Peer("process-only", "203.0.113.7", null, 6, now, stage: "process"),    // not a download
            Peer("me", "203.0.113.7", null, 7, now),
        };
        Assert.Equal(3, NetworkCap.CountOthers(self, "me", peers, now));
        Assert.Equal(0, NetworkCap.CountOthers(new NetworkInfo { Ipv4 = "192.0.2.1", Ipv6Prefix = "2001:db8:9::/64" }, "me", peers, now));
        Assert.Equal(0, NetworkCap.CountOthers(null, "me", peers, now));
        Assert.Equal(0, NetworkCap.CountOthers(new NetworkInfo(), "me", peers, now)); // an unknown address matches nobody
    }

    [Fact]
    public void An_unset_cap_means_unlimited_and_a_set_cap_counts_own_downloads_too()
    {
        foreach (int? off in new int?[] { null, 0 }) Assert.True(NetworkCap.MayClaim(off, 99, 99));
        Assert.True(NetworkCap.MayClaim(2, 1, 0)); Assert.False(NetworkCap.MayClaim(2, 1, 1)); Assert.False(NetworkCap.MayClaim(2, 2, 0));
        Assert.Equal("Waiting: 2 downloads already running from this IP", NetworkCap.WaitText(2));
    }

    [Fact]
    public async Task A_capped_node_waits_without_an_attempt_or_a_backoff_and_an_uncapped_node_claims()
    {
        var now = DateTimeOffset.UtcNow;
        async Task<(FakeStore s, int runs)> Go(int? cap)
        {
            var s = new FakeStore();
            await s.AddJob(P, "download", "vid", Protocol.Utc(now.AddMinutes(-5)));
            if (cap != null) await ControlWatcher.SetMaxPerIpAsync(s, P, cap.Value.ToString(), "test", default);
            var peer = Peer("busy-peer", "203.0.113.7", null, 2, now);
            await s.PutAsync(P.Node("busy-peer"), peer.ToBytes(), new Dictionary<string, string> { [Tags.Kind] = "node" }, null, false, default);
            int runs = 0;
            await Engine(s, Opts("me-node"), _ => { runs++; return Task.CompletedTask; }).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            return (s, runs);
        }
        var capped = await Go(2);
        Assert.Equal(0, capped.runs); Assert.Equal("pending", capped.s.State(P.Job("download", "vid"))); Assert.Equal(0, capped.s.Job(P.Job("download", "vid")).Attempts);
        var roomy = await Go(3);
        Assert.Equal(1, roomy.runs); Assert.Equal("done", roomy.s.State(P.Job("download", "vid")));
        var off = await Go(null);
        Assert.Equal(1, off.runs); // no "downloads" in control.json: today's behaviour
    }

    [Fact]
    public async Task The_cap_setter_keeps_the_rest_of_control_json_and_off_removes_it()
    {
        var s = new FakeStore();
        await ControlWatcher.WriteAsync(s, P, "drain", null, "maintenance", "a", default);
        var c = await ControlWatcher.SetMaxPerIpAsync(s, P, "2", "owner", default);
        Assert.Equal(2, c.Downloads!.MaxPerIp); Assert.Equal("drain", c.Mode); Assert.Equal("maintenance", c.Message); Assert.Equal("owner", c.ChangedBy); Assert.NotNull(c.ChangedAt);
        var raw = JsonDocument.Parse(s[P.Control].Data).RootElement;
        Assert.Equal(2, raw.GetProperty("downloads").GetProperty("maxPerIp").GetInt32());
        var off = await ControlWatcher.SetMaxPerIpAsync(s, P, "off", "owner", default);
        Assert.Null(off.Downloads); Assert.False(JsonDocument.Parse(s[P.Control].Data).RootElement.TryGetProperty("downloads", out _));
        await Assert.ThrowsAsync<ArgumentException>(() => ControlWatcher.SetMaxPerIpAsync(s, P, "many", "owner", default));
        Assert.Null(ControlFile.Parse(System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"downloads\":{\"maxPerIp\":null}}")).Downloads!.MaxPerIp); // null = no cap
    }

    [Fact]
    public async Task Status_json_carries_the_network_object_and_the_cap()
    {
        var s = new FakeStore();
        await Engine(s, Opts("net-node")).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await ControlWatcher.SetMaxPerIpAsync(s, P, "2", "owner", default);
        var snap = await StatusSnapshot.BuildAsync(s, P, DateTimeOffset.UtcNow, default);
        var node = snap["nodes"]!.AsArray().Single()!;
        Assert.Equal("203.0.113.7", (string?)node["network"]!["ipv4"]); Assert.Equal("SEA", (string?)node["network"]!["colo"]); Assert.NotNull(node["placement"]);
        Assert.Equal(2, (int?)snap["control"]!["downloads"]!["maxPerIp"]);
    }
}
