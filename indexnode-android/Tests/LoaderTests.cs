using System.Net;
using System.Text.Json;
using IndexNode.Hosting;

namespace IndexNode.Android.Tests;

public class LoaderTests
{
    static byte[] Dll(string v) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "tp", v + ".dll"));
    static Manifest Man(string version, byte[] dll, string? minHost = null) => new(version, PluginLoader.Sha256Hex(dll), "plugin.dll", minHost);

    sealed class Web(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(r.RequestUri!.AbsoluteUri, out var b) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) } : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    static (HttpClient, string) Server(Manifest m, byte[] dll)
    {
        var url = "https://example.test/manifest.json";
        return (new HttpClient(new Web(new() { [url] = JsonSerializer.SerializeToUtf8Bytes(m), ["https://example.test/plugin.dll"] = dll })), url);
    }

    static async Task<string> LoadedInfo(PluginManager pm, IHost host) { var p = await pm.EnsureLoadedAsync(host); return p.Version + "-" + p.Source; }

    static string Tmp() => Directory.CreateTempSubdirectory("pm").FullName;

    [Fact]
    public void Hash_mismatch_loads_nothing()
    {
        var m = Man("1", Dll("v1")) with { sha256 = new string('0', 64) };
        Assert.Throws<HashMismatchException>(() => PluginLoader.VerifyAndLoad(m, Dll("v1")));
    }

    [Fact]
    public async Task Verified_plugin_loads_and_runs()
    {
        var p = PluginLoader.VerifyAndLoad(Man("1", Dll("v1")), Dll("v1"));
        Assert.Equal("ok", await p.Plugin!.SmokeAsync(new FakeHost()));
        Assert.Equal("version 1", await p.Plugin.RunAsync(new FakeHost(), "{}", default));
    }

    [Fact]
    public async Task Update_with_a_hash_mismatch_keeps_the_running_plugin()
    {
        var pm = new PluginManager(Tmp(), () => (Man("1", Dll("v1")), Dll("v1")), "1.0.0");
        var host = new FakeHost();
        await pm.EnsureLoadedAsync(host);
        var (http, url) = Server(Man("2", Dll("v2")) with { sha256 = new string('a', 64) }, Dll("v2"));
        var r = await pm.CheckAndUpdateAsync(http, url, host);
        Assert.Equal("failed", r.Outcome);
        Assert.Contains("sha256 mismatch", r.Detail);
        Assert.Equal("1", pm.Current!.Version);
    }

    [Fact]
    public async Task Smoke_test_failure_falls_back_to_the_running_plugin()
    {
        var pm = new PluginManager(Tmp(), () => (Man("1", Dll("v1")), Dll("v1")), "1.0.0");
        var host = new FakeHost();
        await pm.EnsureLoadedAsync(host);
        var (http, url) = Server(Man("3", Dll("bad")), Dll("bad"));
        var r = await pm.CheckAndUpdateAsync(http, url, host);
        Assert.Equal("failed", r.Outcome);
        Assert.Contains("smoke test", r.Detail);
        Assert.Equal("1", pm.Current!.Version);
        Assert.Equal("version 1", await pm.Current.Plugin!.RunAsync(host, "{}", default));
    }

    [Fact]
    public async Task Good_update_swaps_keeps_the_last_good_and_the_old_context_is_collected()
    {
        var dir = Tmp();
        var host = new FakeHost();
        var pm = new PluginManager(dir, () => (Man("1", Dll("v1")), Dll("v1")), "1.0.0");
        await pm.EnsureLoadedAsync(host);
        Assert.Equal("bundled", pm.Current!.Source);
        var (http, url) = Server(Man("2", Dll("v2")), Dll("v2"));
        var r = await pm.CheckAndUpdateAsync(http, url, host);
        Assert.True(r.Switched);
        Assert.Equal("2", pm.Current!.Version);
        Assert.Equal("current", (await pm.CheckAndUpdateAsync(http, url, host)).Outcome);
        // a restart loads the downloaded version, not the bundled one
        var pm2 = new PluginManager(dir, () => (Man("1", Dll("v1")), Dll("v1")), "1.0.0");
        Assert.Equal("2-downloaded", await LoadedInfo(pm2, host));
        Assert.True(pm2.Unload());   // the collectible context is gone
        Assert.Null(pm2.Current);
    }

    [Fact]
    public async Task A_lower_manifest_version_is_current_and_downloads_nothing_a_higher_one_switches()
    {
        var host = new FakeHost();
        // the APK bundles 2; the rolling release still has 1: no downgrade, and the DLL is not even fetched
        var pm = new PluginManager(Tmp(), () => (Man("2", Dll("v2")), Dll("v2")), "1.0.0");
        await pm.EnsureLoadedAsync(host);
        var (lowHttp, url) = Server(Man("1", Dll("v1")), Dll("v1"));
        var low = new HttpClient(new Web(new() { [url] = JsonSerializer.SerializeToUtf8Bytes(Man("1", Dll("v1"))) }));   // no DLL on the server: a fetch would fail
        var r = await pm.CheckAndUpdateAsync(low, url, host);
        Assert.Equal("current", r.Outcome); Assert.False(r.Switched);
        Assert.Equal("2", pm.Current!.Version);
        // a higher one switches (3 is the plugin that is not a smoke failure: v3 is not a test DLL, so use a bundled 1 and manifest 2)
        var pm2 = new PluginManager(Tmp(), () => (Man("1", Dll("v1")), Dll("v1")), "1.0.0");
        await pm2.EnsureLoadedAsync(host);
        var (http, url2) = Server(Man("2", Dll("v2")), Dll("v2"));
        Assert.True((await pm2.CheckAndUpdateAsync(http, url2, host)).Switched);
        Assert.Equal("2", pm2.Current!.Version);
        // a downloaded 2 is running: the manifest's 1 is older, so it stays
        var (httpOld, urlOld) = Server(Man("1", Dll("v1")), Dll("v1"));
        Assert.Equal("current", (await pm2.CheckAndUpdateAsync(httpOld, urlOld, host)).Outcome);
        Assert.Equal("2", pm2.Current!.Version);
    }

    [Fact]
    public async Task When_the_downloaded_plugins_failed_the_bundled_one_takes_any_verified_manifest()
    {
        var dir = Tmp(); var host = new FakeHost();
        var pm = new PluginManager(dir, () => (Man("2", Dll("v2")), Dll("v2")), "1.0.0");
        // a downloaded 3 was good once; its file is damaged now, so the bundled 2 runs
        File.WriteAllText(Path.Combine(dir, "good.json"), """{"Current":"3","Previous":null,"CurrentSha":null,"PreviousSha":null}""");
        Assert.Equal("2-bundled", await LoadedInfo(pm, host));
        // the manifest offers 1, lower than the bundled 2: still taken, as before, because it verifies and passes its smoke test
        var (http, url) = Server(Man("1", Dll("v1")), Dll("v1"));
        Assert.True((await pm.CheckAndUpdateAsync(http, url, host)).Switched);
        Assert.Equal("1", pm.Current!.Version);
        // an unverifiable one is not taken
        var pm2 = new PluginManager(Tmp(), () => (Man("2", Dll("v2")), Dll("v2")), "1.0.0");
        File.WriteAllText(Path.Combine(((string)typeof(PluginManager).GetField("dir", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(pm2)!), "good.json"), """{"Current":"3","Previous":null,"CurrentSha":null,"PreviousSha":null}""");
        await pm2.EnsureLoadedAsync(host);
        var (h2, u2) = Server(Man("1", Dll("v1")) with { sha256 = new string('a', 64) }, Dll("v1"));
        Assert.Equal("failed", (await pm2.CheckAndUpdateAsync(h2, u2, host)).Outcome);
        Assert.Equal("2", pm2.Current!.Version);
    }

    [Fact]
    public async Task A_damaged_kept_file_falls_back_to_the_bundled_plugin()
    {
        var dir = Tmp(); var host = new FakeHost();
        var pm = new PluginManager(dir, () => (Man("1", Dll("v1")), Dll("v1")), "1.0.0");
        var (http, url) = Server(Man("2", Dll("v2")), Dll("v2"));
        await pm.EnsureLoadedAsync(host); await pm.CheckAndUpdateAsync(http, url, host);
        File.WriteAllBytes(Path.Combine(dir, "plugin-2.dll"), [1, 2, 3]);
        var pm2 = new PluginManager(dir, () => (Man("1", Dll("v1")), Dll("v1")), "1.0.0");
        var p = await pm2.EnsureLoadedAsync(host);
        Assert.Equal("1", p.Version); Assert.Equal("bundled", p.Source);
    }

    [Fact]
    public async Task A_plugin_that_needs_a_newer_host_is_not_loaded()
    {
        var pm = new PluginManager(Tmp(), () => (Man("1", Dll("v1")), Dll("v1")), "1.0.0");
        var host = new FakeHost(); await pm.EnsureLoadedAsync(host);
        var (http, url) = Server(Man("2", Dll("v2"), "9.0.0"), Dll("v2"));
        var r = await pm.CheckAndUpdateAsync(http, url, host);
        Assert.Equal("needs-newer-host", r.Outcome); Assert.Equal("1", pm.Current!.Version);
    }

    [Fact]
    public async Task The_built_plugin_is_one_dll_with_core_embedded_and_loads_through_the_loader()
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(IndexNode.Plugin.NodePlugin).Assembly.Location)!, "IndexNode.Plugin.dll");
        var dll = File.ReadAllBytes(path);
        // the resource the loader resolves IndexNode.Core from (one file, one hash)
        using (var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(dll)))
        {
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            Assert.Contains(md.ManifestResources.Select(r => md.GetString(md.GetManifestResource(r).Name)), n => n == "asm.IndexNode.Core.dll");
        }
        var p = PluginLoader.VerifyAndLoad(Man("0.1.0", dll), dll);
        Assert.Equal("ok", await p.Plugin!.SmokeAsync(new FakeHost()));
    }
}
