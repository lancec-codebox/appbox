using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IndexNode.Hosting;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.1.8: the page updates like the plugin (Loader/UiManager.cs), and the page's service call takes any /v1/ route (ServiceCalls' general form).</summary>
public class UiManagerTests
{
    const string Url = "https://example.test/rel/ui.json";
    static string Dir() => Path.Combine(Path.GetTempPath(), "ui-" + Guid.NewGuid().ToString("N")[..8]);
    static byte[] Page(string version, string body = "") => Encoding.UTF8.GetBytes($"<!doctype html>\n<html><head><meta name=\"ui-version\" content=\"{version}\"><meta name=\"ui-min-host\" content=\"0.1.8\"></head><body>{body}</body></html>");
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    static string Manifest(string version, byte[] page, string minHost = "0.1.8", string? sha = null) => $"{{\"version\":\"{version}\",\"sha256\":\"{sha ?? Sha(page)}\",\"page\":\"ui.html\",\"minHost\":\"{minHost}\"}}";

    /// <summary>A fake release: ui.json and ui.html beside it; counts the page downloads.</summary>
    sealed class Rel
    {
        public string ManifestJson = ""; public byte[] PageBytes = []; public int PageGets;
        public Task<byte[]> Get(string url, CancellationToken ct)
        {
            if (url == Url) return Task.FromResult(Encoding.UTF8.GetBytes(ManifestJson));
            Assert.Equal("https://example.test/rel/ui.html", url); PageGets++; return Task.FromResult(PageBytes);
        }
    }

    [Fact]
    public async Task A_newer_page_is_fetched_checked_kept_and_loaded_and_the_one_before_stays()
    {
        var m = new UiManager(Dir(), "0.1.8.0", "0.1.8"); var rel = new Rel();
        Assert.Null(m.PageToLoad()); Assert.Equal(("0.1.8.0", "bundled"), m.Active);
        // the release has the same page as the app: nothing is downloaded
        rel.PageBytes = Page("0.1.8.0"); rel.ManifestJson = Manifest("0.1.8.0", rel.PageBytes);
        Assert.Equal("current", (await m.CheckAsync(rel.Get, Url)).Outcome); Assert.Equal(0, rel.PageGets);
        // a newer one: verified, kept, and the one to load
        rel.PageBytes = Page("0.1.8.1", "one"); rel.ManifestJson = Manifest("0.1.8.1", rel.PageBytes);
        var r = await m.CheckAsync(rel.Get, Url);
        Assert.True(r.Switched); Assert.Equal("0.1.8.1", r.Version); Assert.Equal(("0.1.8.1", "page-0.1.8.1.html"), m.PageToLoad());
        Assert.Equal(rel.PageBytes, File.ReadAllBytes(Path.Combine(m.WwwDir, "page-0.1.8.1.html"))); Assert.Equal(("0.1.8.1", "downloaded"), m.Active);
        Assert.Equal("current", (await m.CheckAsync(rel.Get, Url)).Outcome); Assert.Equal(1, rel.PageGets);                  // asked again: no second download
        // the next one: 0.1.8.1 stays on disk as the one before, older files go
        rel.PageBytes = Page("0.1.8.2", "two"); rel.ManifestJson = Manifest("0.1.8.2", rel.PageBytes);
        Assert.True((await m.CheckAsync(rel.Get, Url)).Switched); Assert.Equal("0.1.8.2", m.PageToLoad()!.Value.Version);
        Assert.Equal(["page-0.1.8.1.html", "page-0.1.8.2.html"], Directory.GetFiles(m.WwwDir).Select(Path.GetFileName).OrderBy(x => x).ToArray());
        // a new process (the app restarted) finds the same page
        Assert.Equal("0.1.8.2", new UiManager(Path.GetDirectoryName(m.WwwDir)!, "0.1.8.0", "0.1.8").PageToLoad()!.Value.Version);
        // an APK that carries a newer page than the downloaded one wins
        Assert.Null(new UiManager(Path.GetDirectoryName(m.WwwDir)!, "0.1.9.0", "0.1.9").PageToLoad());
    }

    [Fact]
    public async Task A_page_that_does_not_verify_or_needs_a_newer_app_changes_nothing()
    {
        var m = new UiManager(Dir(), "0.1.8.0", "0.1.8"); var rel = new Rel { PageBytes = Page("0.1.8.5") };
        rel.ManifestJson = Manifest("0.1.8.5", rel.PageBytes, sha: new string('0', 64));                                      // the hash is not the page's
        var bad = await m.CheckAsync(rel.Get, Url); Assert.Equal("failed", bad.Outcome); Assert.Contains("hash", bad.Detail); Assert.Null(m.PageToLoad()); Assert.NotNull(m.LastError);
        rel.ManifestJson = Manifest("0.1.8.6", rel.PageBytes);                                                                // the file says 0.1.8.5, the manifest 0.1.8.6
        Assert.Equal("failed", (await m.CheckAsync(rel.Get, Url)).Outcome); Assert.Null(m.PageToLoad());
        rel.ManifestJson = Manifest("0.1.8.5", rel.PageBytes, minHost: "0.2.0");                                              // made for a newer app
        var need = await m.CheckAsync(rel.Get, Url); Assert.Equal("needs-newer-host", need.Outcome); Assert.Null(m.PageToLoad());
        rel.ManifestJson = "{\"version\":\"9\",\"sha256\":\"zz\",\"page\":\"../x\"}";                                            // not a manifest we take
        Assert.Equal("failed", (await m.CheckAsync(rel.Get, Url)).Outcome);
        Assert.Empty(Directory.GetFiles(m.WwwDir));
        // a file changed on disk after it was verified is not loaded
        rel.ManifestJson = Manifest("0.1.8.5", rel.PageBytes); Assert.True((await m.CheckAsync(rel.Get, Url)).Switched);
        File.WriteAllText(Path.Combine(m.WwwDir, "page-0.1.8.5.html"), "<!doctype html>tampered"); Assert.Null(m.PageToLoad());
    }

    [Fact]
    public async Task A_page_that_never_comes_alive_is_marked_bad_and_the_one_before_is_used()
    {
        var m = new UiManager(Dir(), "0.1.8.0", "0.1.8"); var rel = new Rel();
        foreach (var v in new[] { "0.1.8.1", "0.1.8.2" }) { rel.PageBytes = Page(v); rel.ManifestJson = Manifest(v, rel.PageBytes); Assert.True((await m.CheckAsync(rel.Get, Url)).Switched); }
        m.MarkBad("0.1.8.2");
        Assert.Equal("0.1.8.1", m.PageToLoad()!.Value.Version); Assert.False(File.Exists(Path.Combine(m.WwwDir, "page-0.1.8.2.html")));
        var again = await m.CheckAsync(rel.Get, Url); Assert.Equal("failed", again.Outcome); Assert.Contains("did not work", again.Detail);   // the same version is not fetched again
        m.MarkBad("0.1.8.1"); Assert.Null(m.PageToLoad()); Assert.Equal(("0.1.8.0", "bundled"), m.Active);                                    // nothing downloaded is left: the page in the app
        rel.PageBytes = Page("0.1.8.3"); rel.ManifestJson = Manifest("0.1.8.3", rel.PageBytes);
        Assert.True((await m.CheckAsync(rel.Get, Url)).Switched); Assert.Equal("0.1.8.3", m.PageToLoad()!.Value.Version);                      // a newer one is taken as usual
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Host", "Assets", "web", "index.html"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("indexnode-android not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_page_in_the_app_carries_its_version_and_runs_on_this_app()
    {
        var html = File.ReadAllText(Path.Combine(Root(), "Host", "Assets", "web", "index.html"));
        var host = Regex.Match(File.ReadAllText(Path.Combine(Root(), "Host", "Host.csproj")), "<ApplicationDisplayVersion>([^<]+)<").Groups[1].Value;
        Assert.True(Version.TryParse(UiManager.Meta(html, "ui-version"), out var ui), "index.html needs <meta name=\"ui-version\" content=\"a.b.c.d\">");
        Assert.True(Version.TryParse(UiManager.Meta(html, "ui-min-host"), out var min), "index.html needs <meta name=\"ui-min-host\" content=\"a.b.c\">");
        Assert.True(min <= Version.Parse(host), $"the page needs app {min} but the app is {host}");
        Assert.StartsWith("<!doctype html", html.TrimStart(), StringComparison.OrdinalIgnoreCase);   // what UiManager requires of a downloaded page
        Assert.True(ui!.Major >= 0);
    }

    // ---- the general form of the page's service call

    [Fact]
    public void Any_route_under_v1_and_nothing_else()
    {
        static JsonObject A(string json) => JsonNode.Parse(json)!.AsObject();
        Assert.Equal(("GET", "/v1/ai/holds?x=1&unit=a%23b"), (ServiceCalls.Build(A("{\"path\":\"/v1/ai/holds?x=1&unit=a%23b\"}")).Method, ServiceCalls.Build(A("{\"path\":\"/v1/ai/holds?x=1&unit=a%23b\"}")).Path));
        var post = ServiceCalls.Build(A("{\"method\":\"post\",\"path\":\"/v1/ai/verdict\",\"json\":{\"upload\":\"abc\",\"verdict\":\"real\"}}"));
        Assert.Equal(("POST", "/v1/ai/verdict", "application/json"), (post.Method, post.Path, post.ContentType)); Assert.Equal("real", (string)JsonNode.Parse(Encoding.UTF8.GetString(post.Body!))!["verdict"]!);
        var pic = ServiceCalls.Build(A("{\"method\":\"POST\",\"path\":\"/v1/search\",\"image\":\"" + Convert.ToBase64String([0xFF, 0xD8, 0xFF, 1]) + "\",\"fields\":{\"trim\":\"true\",\"top\":40}}"));
        Assert.StartsWith("multipart/form-data; boundary=", pic.ContentType); var body = Encoding.Latin1.GetString(pic.Body!);
        Assert.Contains("name=\"trim\"\r\n\r\ntrue", body); Assert.Contains("name=\"top\"\r\n\r\n40", body); Assert.Contains("filename=\"picture.jpg\"", body);
        foreach (var bad in new[] { "{\"path\":\"/v2/x\"}", "{\"path\":\"https://evil.example/v1/x\"}", "{\"path\":\"/v1/../admin\"}", "{\"path\":\"/v1//x\"}", "{\"path\":\"/v1/x y\"}", "{\"path\":\"/v1/x@evil\"}",
            "{\"method\":\"DELETE\",\"path\":\"/v1/x\"}", "{\"path\":\"/v1/x\",\"json\":{}}", "{\"path\":\"/v1/x\",\"image\":\"AAAA\"}" })
            Assert.Throws<ArgumentException>(() => ServiceCalls.Build(A(bad)));
    }
}
