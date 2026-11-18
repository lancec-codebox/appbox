using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace IndexNode.Hosting;

/// <summary>Result of a host HTTP call. Body is capped at the requested maxBytes.</summary>
public sealed class HostResponse
{
    public int Status { get; set; }
    public string ContentType { get; set; } = "";
    public string Headers { get; set; } = "";   // "Name: value" lines
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public string Text => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>What the phone is doing right now (the conditions of a run are checked against it).</summary>
public sealed class DeviceInfo
{
    /// <summary>"wifi", "cellular", "ethernet" or "none".</summary>
    public string Transport { get; set; } = "none";
    public bool Vpn { get; set; }
    /// <summary>"vpn" when the transport is the one under an active VPN (Wi-Fi via VPN), else null.</summary>
    public string? Via { get; set; }
    /// <summary>Informational only: the rule "Wi-Fi only" ignores it (Medusa does too).</summary>
    public bool Metered { get; set; }
    /// <summary>Raw facts for the status screen: the active network's own transport ("wifi", "vpn", ...) and whether the network used for the decision is validated.</summary>
    public string ActiveTransport { get; set; } = "none";
    public bool Validated { get; set; }
    public bool Charging { get; set; }
    public int BatteryPercent { get; set; } = -1;
    public long FreeBytes { get; set; }
    public string Model { get; set; } = "";
}

/// <summary>Everything a plugin may ask of the host. The host holds no node logic.</summary>
public interface IHost
{
    /// <summary>"android-&lt;model&gt;-&lt;hex&gt;", stable per install.</summary>
    string NodeId { get; }
    string NodeName { get; }
    /// <summary>The registry kind of this node ("android").</summary>
    string Kind { get; }
    /// <summary>The store container URL without any credential, e.g. https://acct.blob.core.windows.net/index. Empty before setup.</summary>
    string StoreUrl { get; }
    /// <summary>
    /// A request to the store. <paramref name="pathAndQuery"/> is relative to the container ("/index/jobs/x.json?comp=metadata"); the host adds the SAS natively
    /// (the SAS never reaches the plugin, its logs or its exceptions) and refuses anything that is not under <see cref="StoreUrl"/>.
    /// </summary>
    Task<HostResponse> StoreAsync(string method, string pathAndQuery, IReadOnlyDictionary<string, string>? headers, byte[]? body, int maxBytes, CancellationToken ct);
    /// <summary>Any other HTTP request (the public-address lookup). No credentials are added.</summary>
    Task<HostResponse> HttpAsync(string method, string url, IReadOnlyDictionary<string, string>? headers, byte[]? body, int maxBytes, CancellationToken ct);
    /// <summary>A folder in the app's own sandbox for the plugin's files (scratch space, state).</summary>
    string FilesDir { get; }
    void Log(string message);
    /// <summary>Status line, current job and progress (0..1) for the UI and the notification.</summary>
    void SetStatus(string text, string? job = null, double? progress = null);
    string? GetSetting(string key);
    void SetSetting(string key, string? value);
    DeviceInfo GetDeviceInfo();
    /// <summary>yt-dlp, ffmpeg and ffprobe (the host backs them with Medusa).</summary>
    ITools Tools { get; }
}

public interface IPlugin
{
    /// <summary>The host calls this on a freshly loaded plugin before it switches to it; throw (or return not "ok") to refuse the plugin. No network access.</summary>
    Task<string> SmokeAsync(IHost host);
    /// <summary>One node run: args is JSON ({"stages":[...],"maxJobs":N,"once":true}); returns JSON with the counts. Cancelling releases the running jobs.</summary>
    Task<string> RunAsync(IHost host, string args, CancellationToken ct);
}
