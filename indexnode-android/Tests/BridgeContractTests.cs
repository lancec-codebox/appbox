using System.Text.RegularExpressions;
using IndexNode.Hosting;

namespace IndexNode.Android.Tests;

/// <summary>The page, the bridge allow-list and the activity's dispatch must agree: a method the page calls but the bridge refuses shows up on the phone as "unknown method" every few seconds.</summary>
public class BridgeContractTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Host", "Assets", "web", "index.html"))) d = d.Parent;
        return d?.FullName ?? throw new FileNotFoundException("indexnode-android root not found above " + AppContext.BaseDirectory);
    }

    static string[] PageCalls() => Regex.Matches(File.ReadAllText(Path.Combine(Root(), "Host", "Assets", "web", "index.html")), @"call\('([A-Za-z]+)'").Select(m => m.Groups[1].Value).Distinct().ToArray();
    static string[] Cases() => Regex.Matches(File.ReadAllText(Path.Combine(Root(), "Host", "MainActivity.cs")), @"case ""([A-Za-z]+)"":").Select(m => m.Groups[1].Value).Distinct().ToArray();

    [Fact]
    public void The_page_calls_something()
    {
        var calls = PageCalls();
        Assert.Contains("getState", calls); Assert.Contains("getDiagnostics", calls); Assert.Contains("clearDiagnostics", calls);
    }

    [Fact]
    public void Every_call_in_the_page_is_in_the_bridge_allow_list()
    {
        var missing = PageCalls().Where(c => !Bridge.Methods.Contains(c)).ToArray();
        Assert.True(missing.Length == 0, "index.html calls methods that Bridge.Methods refuses: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_call_in_the_page_has_a_case_in_MainActivity()
    {
        var cases = Cases();
        var missing = PageCalls().Where(c => !cases.Contains(c)).ToArray();
        Assert.True(missing.Length == 0, "index.html calls methods that MainActivity.Handle does not dispatch: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_allowed_method_has_a_case_in_MainActivity()
    {
        var cases = Cases();
        var missing = Bridge.Methods.Where(m => !cases.Contains(m)).ToArray();
        Assert.True(missing.Length == 0, "Bridge.Methods allows methods MainActivity.Handle does not dispatch: " + string.Join(", ", missing));
    }
}
