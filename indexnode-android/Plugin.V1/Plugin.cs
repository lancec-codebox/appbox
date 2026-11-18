using Spike.Contract;
namespace PluginV1;

public sealed class Plugin : IPlugin
{
    public Task<string> RunAsync(IHost host, string args)
    {
        host.Log("v1 running, args=" + args);
        host.SetStatus("plugin v1");
        return Task.FromResult("{\"plugin\":\"v1\",\"message\":\"hello from version 1\",\"args\":" + System.Text.Json.JsonSerializer.Serialize(args) + "}");
    }
}
