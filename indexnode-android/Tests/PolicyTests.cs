using System.Text;
using IndexNode.Hosting;

namespace IndexNode.Android.Tests;

public class PolicyTests
{
    static DeviceInfo Dev(string t = "wifi", bool charging = true, bool metered = false) => new() { Transport = t, Charging = charging, Metered = metered };

    [Fact]
    public void Wifi_and_charging_are_both_required_by_default()
    {
        var c = new RunConditions();
        Assert.Null(c.WhyNot(Dev(), 0));
        Assert.Contains("Wi-Fi", c.WhyNot(Dev("cellular"), 0));
        Assert.Contains("Wi-Fi", c.WhyNot(Dev("wifi", metered: true), 0));
        Assert.Contains("charger", c.WhyNot(Dev(charging: false), 0));
        Assert.Null(new RunConditions(true, false).WhyNot(Dev(charging: false), 0));
        Assert.Null(new RunConditions(false, false).WhyNot(Dev("cellular", false), 0));
        Assert.Equal("no network", new RunConditions(false, false).WhyNot(Dev("none"), 0));
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
