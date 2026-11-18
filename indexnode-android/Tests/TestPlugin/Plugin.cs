using IndexNode.Hosting;

namespace TestPlugin;

/// <summary>Variants by compile constant: V2 (a newer good plugin), BAD (smoke test fails), default V1.</summary>
public sealed class P : IPlugin
{
#if V2
    const string Ver = "2";
#else
    const string Ver = "1";
#endif
    public Task<string> SmokeAsync(IHost host)
    {
#if BAD
        throw new InvalidOperationException("smoke test refuses");
#else
        return Task.FromResult("ok");
#endif
    }
    public Task<string> RunAsync(IHost host, string args, CancellationToken ct) => Task.FromResult("version " + Ver);
}
