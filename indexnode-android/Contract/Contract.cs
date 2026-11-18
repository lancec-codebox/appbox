using System.Collections.Generic;
using System.Threading.Tasks;

namespace Spike.Contract;

/// <summary>Result of a host HTTP call. Body is capped at the requested maxBytes.</summary>
public sealed class HostResponse
{
    public int Status { get; set; }
    public string ContentType { get; set; } = "";
    public string Headers { get; set; } = "";   // "Name: value" lines
    public byte[] Body { get; set; } = System.Array.Empty<byte>();
    public string Text => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>Everything a plugin may ask of the host. The host holds no node logic.</summary>
public interface IHost
{
    Task<HostResponse> GetAsync(string url, IDictionary<string, string>? headers = null, int maxBytes = 1 << 20);
    Task<HostResponse> PutAsync(string url, byte[] body, string contentType = "application/octet-stream");
    /// <summary>Any method (POST for Medusa jobs).</summary>
    Task<HostResponse> SendAsync(string method, string url, string? body, IDictionary<string, string>? headers = null, int maxBytes = 1 << 20);
    /// <summary>The app's private cache folder; plugin files live below it.</summary>
    string CacheDir { get; }
    void Log(string message);
    void SetStatus(string text);
}

public interface IPlugin
{
    Task<string> RunAsync(IHost host, string args);
}
