using System.Text;
using IndexNode.Hosting;

namespace IndexNode.Android.Tests;

public class PolicyTests
{
    static DeviceInfo Dev(string t = "wifi", bool charging = true, bool metered = false, bool validated = true) => new() { Transport = t, Charging = charging, Metered = metered, Validated = validated };

    [Fact]
    public void Wifi_and_charging_are_both_required_by_default()
    {
        var c = new RunConditions();
        Assert.Null(c.WhyNot(Dev(), 0));
        Assert.Equal("on mobile data", c.WhyNot(Dev("cellular"), 0));
        Assert.Equal("Wi-Fi is metered", c.WhyNot(Dev("wifi", metered: true), 0));
        Assert.Equal("not charging", c.WhyNot(Dev(charging: false), 0));
        Assert.Null(new RunConditions(true, false).WhyNot(Dev(charging: false), 0));
        Assert.Null(new RunConditions(false, false).WhyNot(Dev("cellular", false), 0));
        Assert.Equal("no network", new RunConditions(false, false).WhyNot(Dev("none"), 0));
        Assert.Equal("no network", c.WhyNot(Dev("none"), 0));
        Assert.Equal("Wi-Fi connected, no internet", c.WhyNot(Dev("wifi", validated: false), 0));   // the owner's internet was down
        Assert.Null(new RunConditions(false, true).WhyNot(Dev("wifi", metered: true), 0));         // metered only matters for Wi-Fi only
    }

    // a network as ConnectivityManager reports it: (transports, capabilities, isActive)
    static NetSnapshot Wifi(bool active = false, bool metered = false, bool validated = true) => new(true, false, false, false, true, validated, !metered, active);
    static NetSnapshot Cell(bool active = false) => new(false, false, true, false, true, true, false, active);
    static NetSnapshot Vpn(bool active = true, bool metered = true) => new(false, false, false, true, true, true, !metered, active);   // Android 10+ VPNs are metered by default

    static DeviceInfo Nets(params NetSnapshot[] nets) { var r = NetworkClassifier.Classify(nets); return new() { Transport = r.Transport, Vpn = r.Vpn, Metered = r.Metered, Via = r.Via, Validated = r.Validated, Charging = true }; }

    [Fact]
    public void A_vpn_with_wifi_underneath_gives_a_run()
    {
        var d = Nets(Vpn(), Wifi());   // the VPN is the active network, metered, without TRANSPORT_WIFI
        Assert.Equal("wifi", d.Transport); Assert.True(d.Vpn); Assert.False(d.Metered); Assert.Equal("vpn", d.Via);
        Assert.Null(new RunConditions().WhyNot(d, 0));
    }

    [Fact]
    public void A_vpn_with_cellular_underneath_waits()
    {
        var d = Nets(Vpn(), Cell());
        Assert.Equal("cellular", d.Transport); Assert.True(d.Vpn);
        Assert.Equal("on mobile data", new RunConditions().WhyNot(d, 0));
        // Wi-Fi that is up under the VPN but has no internet: its own verdict
        Assert.Equal("Wi-Fi connected, no internet", new RunConditions().WhyNot(Nets(Vpn(), Cell(), Wifi(validated: false)), 0));
    }

    [Fact]
    public void Metered_wifi_waits_unmetered_runs_and_a_wifi_without_internet_says_so()
    {
        var d = Nets(Wifi(active: true, metered: true));
        Assert.Equal("wifi", d.Transport); Assert.True(d.Metered); Assert.True(d.Validated); Assert.Null(d.Via);
        Assert.Equal("Wi-Fi is metered", new RunConditions().WhyNot(d, 0));
        Assert.Null(new RunConditions().WhyNot(Nets(Wifi(active: true)), 0));
        Assert.Equal("on mobile data", new RunConditions().WhyNot(Nets(Cell(active: true)), 0));
        Assert.Equal("Wi-Fi connected, no internet", new RunConditions().WhyNot(Nets(Wifi(active: true, validated: false)), 0));
        Assert.Equal("no network", new RunConditions().WhyNot(Nets(), 0));
        var r = NetworkClassifier.Classify([Wifi(active: true, metered: true)]);
        Assert.Equal(("wifi", true), (r.ActiveTransport, r.Validated));   // the raw facts the status screen shows
        Assert.Equal("vpn", NetworkClassifier.Classify([Vpn(), Wifi()]).ActiveTransport);
    }

    [Fact]
    public void The_wifi_under_a_vpn_decides_metered_not_the_vpn_and_ethernet_counts()
    {
        Assert.True(Nets(Vpn(metered: false), Wifi(metered: true)).Metered);            // metered Wi-Fi under an unmetered VPN: still metered
        Assert.Equal("Wi-Fi is metered", new RunConditions().WhyNot(Nets(Vpn(), Wifi(metered: true)), 0));
        var eth = Nets(Vpn(), new NetSnapshot(false, true, false, false, true, true, true, false));
        Assert.Equal("ethernet", eth.Transport); Assert.Null(new RunConditions().WhyNot(eth, 0));
        Assert.Equal("none", Nets().Transport);
        Assert.Equal("cellular", Nets(Cell(active: true)).Transport);
        // wifi + cellular both up and the active one is Wi-Fi: Wi-Fi
        Assert.Equal("wifi", Nets(Cell(), Wifi(active: true)).Transport);
    }

    [Fact]
    public void The_daily_cap_counts_down_and_stops()
    {
        var c = new RunConditions(DailyCap: 3);
        Assert.Equal(3, c.Remaining(0)); Assert.Equal(1, c.Remaining(2)); Assert.Equal(0, c.Remaining(5));
        Assert.Contains("daily cap", c.WhyNot(Dev(), 3));
        Assert.Null(new RunConditions(DailyCap: 0).WhyNot(Dev(), 999));
    }

    static string Link(string json) => SetupSettings.Scheme + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    const string Json = """{"v":1,"kind":"medusa-node-settings","sas":"https://acct.blob.core.windows.net/index?sv=2024&sp=racwdlf&sig=SECRETSIG","nodeName":"pixel-office","conditions":"wifi","dailyCap":7,"budget":50,"maxHeight":360}""";

    [Fact]
    public void A_setup_link_a_page_link_and_a_file_all_parse()
    {
        foreach (var text in new[] { Link(Json), SetupSettings.WebBase + Link(Json)[SetupSettings.Scheme.Length..], Json })
        {
            var s = SetupSettings.Parse(text);
            Assert.Equal("pixel-office", s.NodeName); Assert.Equal("wifi", s.Conditions); Assert.Equal(7, s.DailyCap); Assert.Equal(50, s.Budget); Assert.Equal(360, s.MaxHeight);
            Assert.Equal("https://acct.blob.core.windows.net/index", s.StoreUrl);   // no query, so no SAS
            Assert.DoesNotContain("sig", s.StoreUrl);
        }
    }

    [Fact]
    public void Other_json_and_odd_conditions_are_refused()
    {
        Assert.Throws<FormatException>(() => SetupSettings.Parse("""{"kind":"something-else"}"""));
        Assert.Throws<FormatException>(() => SetupSettings.Parse("hello"));
        Assert.Throws<FormatException>(() => SetupSettings.Parse(Json.Replace("\"wifi\"", "\"always\"")));
    }

    [Fact]
    public void Node_ids_and_redaction()
    {
        var id = NodeIdentity.NewId("Pixel 8 Pro");
        Assert.Matches("^android-pixel-8-pro-[0-9a-f]{6}$", id);
        Assert.Equal("phone", NodeIdentity.Slug("***"));
        var t = HostRedact.Text("GET https://a/b?sv=1&sig=abc123%3D&x=1 token tok-secret-1", "tok-secret-1");
        Assert.DoesNotContain("abc123", t); Assert.DoesNotContain("tok-secret-1", t);
    }

    [Fact]
    public void The_store_gate_adds_the_sas_only_under_the_container()
    {
        const string store = "https://acct.blob.core.windows.net/index";
        var u = StoreGate.Build(store, "sv=1&sig=S", "/index/index/jobs/download/x.json?comp=metadata");
        Assert.Equal("https://acct.blob.core.windows.net/index/index/jobs/download/x.json?comp=metadata&sv=1&sig=S", u.ToString());
        Assert.Equal("https://acct.blob.core.windows.net/index?restype=container&sv=1&sig=S", StoreGate.Build(store, "sv=1&sig=S", "/index?restype=container").ToString());
        Assert.Throws<UnauthorizedAccessException>(() => StoreGate.Build(store, "sv=1&sig=S", "/other/x.json"));
        Assert.Throws<UnauthorizedAccessException>(() => StoreGate.Build(store, "sv=1&sig=S", "/index/../other/x"));
        Assert.Throws<UnauthorizedAccessException>(() => StoreGate.Build(store, "sv=1&sig=S", "/index/x?sig=mine"));
        Assert.Throws<UnauthorizedAccessException>(() => StoreGate.Build(store, "sv=1&sig=S", "/indexed/x"));
        Assert.Throws<InvalidOperationException>(() => StoreGate.Build(store, "", "/index/x"));
    }
}
