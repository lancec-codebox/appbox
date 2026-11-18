using IndexNode.Hosting;

namespace IndexNode.Android.Tests;

/// <summary>An IHost for tests: the store is a plain handler, settings a dictionary, tools whatever the test passes.</summary>
public sealed class FakeHost(ITools? tools = null, string? dir = null) : IHost
{
    public string NodeId { get; set; } = "android-pixel-abc123";
    public string NodeName { get; set; } = "android-pixel";
    public string Kind => "android";
    public string StoreUrl { get; set; } = "https://acct.blob.core.windows.net/index";
    public string FilesDir { get; } = dir ?? Directory.CreateTempSubdirectory("fakehost").FullName;
    public ITools Tools { get; } = tools!;
    public readonly List<string> Logs = new();
    public readonly Dictionary<string, string?> Settings = new();
    public readonly List<string> Statuses = new();
    public DeviceInfo Device { get; set; } = new() { Transport = "wifi", Vpn = false, Charging = true, BatteryPercent = 80, Model = "Pixel" };
    public Func<string, string, HostResponse>? Http;
    public Func<string, string, HostResponse>? Store;
    public Task<HostResponse> StoreAsync(string method, string pq, IReadOnlyDictionary<string, string>? h, byte[]? body, int max, CancellationToken ct) => Task.FromResult(Store!(method, pq));
    public Task<HostResponse> HttpAsync(string method, string url, IReadOnlyDictionary<string, string>? h, byte[]? body, int max, CancellationToken ct) => Task.FromResult(Http != null ? Http(method, url) : new HostResponse { Status = 404 });
    public void Log(string m) { lock (Logs) Logs.Add(m); }
    public void SetStatus(string t, string? job = null, double? p = null) { lock (Statuses) Statuses.Add(t); }
    public string? GetSetting(string k) => Settings.GetValueOrDefault(k);
    public void SetSetting(string k, string? v) => Settings[k] = v;
    public DeviceInfo GetDeviceInfo() => Device;
}
