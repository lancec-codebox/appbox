using System.Text.Json;
using Spike.Contract;
using Spike.Loader;
using Xunit;

public class LoaderTests
{
    sealed class FakeHost : IHost
    {
        public List<string> Logs = new();
        public string CacheDir => Path.GetTempPath();
        public void Log(string m) => Logs.Add(m);
        public void SetStatus(string t) { }
        public Task<HostResponse> GetAsync(string url, IDictionary<string, string>? h = null, int max = 1 << 20) => throw new NotSupportedException();
        public Task<HostResponse> PutAsync(string url, byte[] b, string ct = "") => throw new NotSupportedException();
        public Task<HostResponse> SendAsync(string m, string url, string? b, IDictionary<string, string>? h = null, int max = 1 << 20) => throw new NotSupportedException();
    }

    static byte[] Dll() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Plugin.V1.dll"));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static async Task<(string, WeakReference)> LoadRunUnload()
    {
        var dll = Dll();
        var m = new Manifest("1", PluginLoader.Sha256Hex(dll), "PluginV1.Plugin");
        var p = PluginLoader.VerifyAndLoad(m, dll);
        var json = await p.Plugin!.RunAsync(new FakeHost(), "x");
        return (json, p.Unload());
    }

    [Fact]
    public async Task Verifies_hash_then_loads_and_runs_then_unloads()
    {
        var (json, wr) = await LoadRunUnload();
        Assert.Equal("v1", JsonDocument.Parse(json).RootElement.GetProperty("plugin").GetString());
        for (int i = 0; i < 20 && wr.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(20); }
        Assert.False(wr.IsAlive);   // collectible context really went away
    }

    [Fact]
    public void Wrong_hash_is_rejected_and_nothing_is_loaded()
    {
        var dll = Dll();
        var before = AppDomain.CurrentDomain.GetAssemblies().Length + System.Runtime.Loader.AssemblyLoadContext.All.Count();
        var m = new Manifest("1", new string('0', 64), "");
        Assert.Throws<HashMismatchException>(() => PluginLoader.VerifyAndLoad(m, dll));
        var tampered = (byte[])dll.Clone(); tampered[tampered.Length / 2] ^= 1;
        Assert.Throws<HashMismatchException>(() => PluginLoader.VerifyAndLoad(new Manifest("1", PluginLoader.Sha256Hex(dll), ""), tampered));
        Assert.Equal(before, AppDomain.CurrentDomain.GetAssemblies().Length + System.Runtime.Loader.AssemblyLoadContext.All.Count());
    }
}
