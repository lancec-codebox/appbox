using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace IndexNode;

/// <summary>Where a node runs and how close it is to the storage account (registry field "placement").</summary>
public sealed class Placement
{
    public string Provider { get; set; } = "unknown";
    public string? Region { get; set; }
    public string? VmSize { get; set; }
    public string? Zone { get; set; }
    /// <summary>"imds", "env" (INDEXNODE_REGION) or null.</summary>
    public string? Source { get; set; }
    /// <summary>Why detection found nothing: timeout, refused, 404 ...</summary>
    public string? Reason { get; set; }
    public double? StorageRttMs { get; set; }
    /// <summary>True when the region equals the storage region: reads are free, otherwise billed egress. Null when the region is unknown.</summary>
    public bool? SameRegionAsStorage { get; set; }
    public string CheckedAt { get; set; } = "";

    public const string ImdsUrl = "http://169.254.169.254/metadata/instance?api-version=2021-02-01";
    public static readonly TimeSpan Refresh = TimeSpan.FromHours(24);

    public static string NormRegion(string? r) => (r ?? "").Replace(" ", "").Replace("-", "").ToLowerInvariant();

    /// <summary>Parses the IMDS answer: compute.location, vmSize, zone (when present).</summary>
    public static Placement? ParseImds(string json)
    {
        try
        {
            var c = JsonDocument.Parse(json).RootElement.GetProperty("compute");
            string? S(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            return new Placement { Provider = "azure", Region = S("location"), VmSize = S("vmSize"), Zone = S("zone"), Source = "imds" };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public static double Median(IReadOnlyList<double> v)
    {
        var s = v.Order().ToList();
        return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    /// <summary>Median TCP connect time over <paramref name="tries"/> connects; null when none succeeded.</summary>
    public static async Task<double?> StorageRttAsync(string host, int tries, Func<string, CancellationToken, Task>? connect, CancellationToken ct)
    {
        connect ??= async (h, t) => { using var tcp = new TcpClient(); await tcp.ConnectAsync(h, 443, t); };
        var times = new List<double>();
        for (int i = 0; i < tries; i++)
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(TimeSpan.FromSeconds(3));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await connect(host, to.Token); times.Add(sw.Elapsed.TotalMilliseconds); } catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }
        return times.Count == 0 ? null : Math.Round(Median(times), 1);
    }

    /// <summary>IMDS (2 s, no proxy), else the INDEXNODE_REGION override, plus the storage connect time and the same-region flag.</summary>
    public static async Task<Placement> DetectAsync(string? storageHost, string storageRegion, HttpMessageHandler? imds, string? envRegion, Func<string, CancellationToken, Task>? connect, CancellationToken ct)
    {
        Placement p;
        try
        {
            using var http = imds != null ? new HttpClient(imds) : new HttpClient(new SocketsHttpHandler { UseProxy = false });
            http.Timeout = TimeSpan.FromSeconds(2);
            using var req = new HttpRequestMessage(HttpMethod.Get, ImdsUrl);
            req.Headers.TryAddWithoutValidation("Metadata", "true");
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) p = new Placement { Reason = ((int)res.StatusCode).ToString() };
            else p = ParseImds(await res.Content.ReadAsStringAsync(ct)) ?? new Placement { Reason = "unparsable" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { p = new Placement { Reason = "timeout" }; }
        catch (HttpRequestException e) { p = new Placement { Reason = e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused } ? "refused" : e.InnerException is SocketException s ? s.SocketErrorCode.ToString().ToLowerInvariant() : "unreachable" }; }
        if (!string.IsNullOrWhiteSpace(envRegion)) { p.Region = envRegion.Trim(); p.Source = "env"; }
        if (p.Region != null) p.Reason = null;
        if (storageHost != null) p.StorageRttMs = await StorageRttAsync(storageHost, 5, connect, ct);
        p.SameRegionAsStorage = p.Region == null ? null : NormRegion(p.Region) == NormRegion(storageRegion);
        p.CheckedAt = Protocol.NowUtc();
        return p;
    }
}
