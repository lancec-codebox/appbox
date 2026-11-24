using System.Text;
using Android.Content;
using Android.Net;
using Android.OS;
using IndexNode.Hosting;
using Uri = System.Uri;

namespace IndexNode.Host;

/// <summary>What the plugin may ask of the phone. No node logic here: HTTP to the store (SAS added natively), sandbox files, settings, status, device info, tools.</summary>
public sealed class AndroidHost(Context ctx, Prefs prefs, Func<ITools> tools, Action<string, string?, double?> status) : IHost
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly List<string> log = new();
    private readonly object gate = new();

    public string NodeId => prefs.Get("nodeId") ?? "";
    public string NodeName => prefs.Get("nodeName") ?? NodeId;
    public string Kind => "android";
    public string StoreUrl => prefs.Sas is { Length: > 0 } s && Uri.TryCreate(s, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path).TrimEnd('/') : "";
    public string FilesDir { get; } = Path.Combine(ctx.FilesDir!.AbsolutePath, "node");
    public ITools Tools => tools();

    public async Task<HostResponse> StoreAsync(string method, string pathAndQuery, IReadOnlyDictionary<string, string>? headers, byte[]? body, int maxBytes, CancellationToken ct)
    {
        var sas = prefs.Sas ?? throw new InvalidOperationException("the node is not set up");
        var uri = StoreGate.Build(StoreUrl, new Uri(sas).Query, pathAndQuery);
        return await SendAsync(method, uri, headers, body, maxBytes, ct, sas);
    }

    public Task<HostResponse> HttpAsync(string method, string url, IReadOnlyDictionary<string, string>? headers, byte[]? body, int maxBytes, CancellationToken ct) =>
        SendAsync(method, new Uri(url), headers, body, maxBytes, ct, null);

    private async Task<HostResponse> SendAsync(string method, Uri uri, IReadOnlyDictionary<string, string>? headers, byte[]? body, int maxBytes, CancellationToken ct, string? secret)
    {
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), uri);
            if (body != null) req.Content = new ByteArrayContent(body);
            if (headers != null)
                foreach (var (k, v) in headers)
                    if (!req.Headers.TryAddWithoutValidation(k, v)) req.Content?.Headers.TryAddWithoutValidation(k, v);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var ms = new MemoryStream(); var buf = new byte[81920];
            while (ms.Length < maxBytes) { int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, maxBytes - ms.Length)), ct); if (n == 0) break; ms.Write(buf, 0, n); }
            var hdr = string.Join("\n", resp.Headers.Concat(resp.Content.Headers).Select(h => h.Key + ": " + string.Join(",", h.Value)));
            return new HostResponse { Status = (int)resp.StatusCode, ContentType = resp.Content.Headers.ContentType?.ToString() ?? "", Headers = hdr, Body = ms.ToArray() };
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            // the exception text can carry the request URL: never with the signature
            throw new HttpRequestException(HostRedact.Text(e.Message, [secret, .. prefs.Secrets]));
        }
    }

    public void Log(string message)
    {
        var line = DateTime.Now.ToString("MM-dd HH:mm:ss") + " " + HostRedact.Text(message, prefs.Secrets);
        lock (gate) { log.Add(line); if (log.Count > 1000) log.RemoveAt(0); }
        Android.Util.Log.Info("indexnode", line);
        CrashRecorder.AppendLog(line);
    }
    public string[] LogLines() { lock (gate) return log.ToArray(); }
    public void ClearLog() { lock (gate) log.Clear(); }

    public void SetStatus(string text, string? job = null, double? progress = null) => status(text, job, progress);
    public string? GetSetting(string key) => prefs.Get(key);
    public void SetSetting(string key, string? value) => prefs.Set(key, value);

    public DeviceInfo GetDeviceInfo()
    {
        var d = new DeviceInfo { Model = Build.Model ?? "phone" };
        try
        {
            var cm = (ConnectivityManager)ctx.GetSystemService(Context.ConnectivityService)!;
            // a thin mapping of every network into NetworkClassifier (the active one may be a metered VPN that does not report Wi-Fi)
            var active = cm.ActiveNetwork; var snaps = new List<NetSnapshot>();
            foreach (var n in cm.GetAllNetworks() ?? [])
            {
                var c = cm.GetNetworkCapabilities(n); if (c == null) continue;
                snaps.Add(new NetSnapshot(c.HasTransport(TransportType.Wifi), c.HasTransport(TransportType.Ethernet), c.HasTransport(TransportType.Cellular), c.HasTransport(TransportType.Vpn),
                    c.HasCapability(NetCapability.Internet), c.HasCapability(NetCapability.Validated), c.HasCapability(NetCapability.NotMetered), active != null && n.Equals(active)));
            }
            var cls = NetworkClassifier.Classify(snaps);
            d.Transport = cls.Transport; d.Vpn = cls.Vpn; d.Metered = cls.Metered; d.Via = cls.Via; d.ActiveTransport = cls.ActiveTransport; d.Validated = cls.Validated;
            var bm = (BatteryManager)ctx.GetSystemService(Context.BatteryService)!;
            d.BatteryPercent = bm.GetIntProperty((int)BatteryProperty.Capacity);
            var intent = ctx.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
            var plugged = intent?.GetIntExtra(BatteryManager.ExtraPlugged, 0) ?? 0;
            d.Charging = plugged != 0;
            d.FreeBytes = new StatFs(ctx.FilesDir!.AbsolutePath).AvailableBytes;
        }
        catch (Exception e) { Log("device info: " + e.Message); }
        return d;
    }
}
