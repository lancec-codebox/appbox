using System.Runtime.CompilerServices;
using IndexNode.Hosting;

namespace IndexNode.Android.Tests;

public class UnloadProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference LoadAndDrop(bool run)
    {
        var dll = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "tp", "v1.dll"));
        var p = PluginLoader.VerifyAndLoad(new Manifest("1", PluginLoader.Sha256Hex(dll)), dll);
        if (run) p.Plugin!.RunAsync(new FakeHost(), "{}", default).GetAwaiter().GetResult();
        return p.Unload();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bare_unload_is_collected(bool run)
    {
        var wr = LoadAndDrop(run);
        for (int i = 0; i < 20 && wr.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(wr.IsAlive);
    }
}
