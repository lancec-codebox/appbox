using System.Net;
using System.Net.Sockets;

namespace IndexNode;

/// <summary>The node's public network as seen from outside (registry field "network"): the public IPv4 and IPv6, where Cloudflare sees it, and (Medusa only) the transport and VPN flag.</summary>
public sealed class NetworkInfo
{
    public string? Ipv4 { get; set; }
    public string? Ipv6 { get; set; }
    /// <summary>The IPv6 /64 as "2001:db8:1:2::/64": hosts of one home share it.</summary>
    public string? Ipv6Prefix { get; set; }
    public string? Country { get; set; }
    public string? Colo { get; set; }
    /// <summary>Medusa only: "wifi", "cellular" or "ethernet".</summary>
    public string? Transport { get; set; }
    /// <summary>Medusa only: true when the active network is a VPN.</summary>
    public bool? Vpn { get; set; }
    public string CheckedAt { get; set; } = "";
    /// <summary>When this ipv4/ipv6 pair was first seen.</summary>
    public string Since { get; set; } = "";

    public bool SamePair(NetworkInfo? o) => o != null && Ipv4 == o.Ipv4 && Ipv6 == o.Ipv6;
}

/// <summary>The network a download attempt started on (jobs and downloaded.json, field "network").</summary>
public sealed class JobNetwork
{
    public string? Ipv4 { get; set; }
    public string? Ipv6Prefix { get; set; }
    public bool? Vpn { get; set; }
    public string? Transport { get; set; }

    public static JobNetwork? From(NetworkInfo? n) => n == null ? null : new JobNetwork { Ipv4 = n.Ipv4, Ipv6Prefix = n.Ipv6Prefix, Vpn = n.Vpn, Transport = n.Transport };
}

public sealed record TraceInfo(string? Ip, string? Loc, string? Colo);

/// <summary>Looks up the public addresses with Cloudflare's trace endpoint: IPv4 and IPv6 separately, since YouTube may be reached over either.</summary>
public static class NetworkProbe
{
    public const string V4Url = "https://1.1.1.1/cdn-cgi/trace";
    public const string V6Url = "https://[2606:4700:4700::1111]/cdn-cgi/trace";
    public const string FallbackUrl = "https://www.cloudflare.com/cdn-cgi/trace";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    /// <summary>A node re-checks every 30 minutes, and before claiming a download when the last check is older than 10.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ClaimStale = TimeSpan.FromMinutes(10);

    /// <summary>Parses "key=value" lines: ip, loc, colo.</summary>
    public static TraceInfo ParseTrace(string? text)
    {
        string? ip = null, loc = null, colo = null;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim(); var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line[..eq]; var v = line[(eq + 1)..].Trim();
            if (v.Length == 0) continue;
            if (k == "ip") ip = v; else if (k == "loc") loc = v; else if (k == "colo") colo = v;
        }
        return new TraceInfo(ip, loc, colo);
    }

    /// <summary>"2001:db8:1:2:aaaa::1" -> "2001:db8:1:2::/64"; null for anything that is not an IPv6 address.</summary>
    public static string? Ipv6Prefix64(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetworkV6) return null;
        var b = a.GetAddressBytes();
        for (int k = 8; k < 16; k++) b[k] = 0;
        var s = new IPAddress(b).ToString(); // canonical, zeros compressed: 2001:db8:1:2:: or 2001:db8::
        return (s.EndsWith("::") ? s : s + "::") + "/64";
    }

    private static AddressFamily? FamilyOf(string? ip) => IPAddress.TryParse(ip, out var a) ? a.AddressFamily : null;

    /// <summary>One lookup. [fetch] returns the trace text or null/throws; a family that does not answer within 5 s is null.
    /// A family that failed is filled from the fallback endpoint when that answers with an address of this family.</summary>
    public static async Task<NetworkInfo> DetectAsync(Func<string, CancellationToken, Task<string?>> fetch, CancellationToken ct, NetworkInfo? previous = null, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Timeout;
        async Task<TraceInfo?> One(string url)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(limit);
                var text = await fetch(url, cts.Token).WaitAsync(limit, ct);
                var t = ParseTrace(text);
                return t.Ip == null ? null : t;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }
        var t4 = One(V4Url); var t6 = One(V6Url);
        var r4 = await t4; var r6 = await t6;
        if (FamilyOf(r4?.Ip) != AddressFamily.InterNetwork) r4 = null;
        if (FamilyOf(r6?.Ip) != AddressFamily.InterNetworkV6) r6 = null;
        if (r4 == null || r6 == null)
        {
            var f = await One(FallbackUrl);
            if (f != null)
            {
                var fam = FamilyOf(f.Ip);
                if (r4 == null && fam == AddressFamily.InterNetwork) r4 = f;
                else if (r6 == null && fam == AddressFamily.InterNetworkV6) r6 = f;
            }
        }
        var now = Protocol.NowUtc();
        var n = new NetworkInfo
        {
            Ipv4 = r4?.Ip, Ipv6 = r6?.Ip, Ipv6Prefix = Ipv6Prefix64(r6?.Ip),
            Country = r4?.Loc ?? r6?.Loc, Colo = r4?.Colo ?? r6?.Colo, CheckedAt = now, Since = now,
        };
        if (previous != null && n.SamePair(previous) && previous.Since.Length > 0) n.Since = previous.Since;
        return n;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>The real fetch: a plain GET, no credentials.</summary>
    public static async Task<string?> HttpFetchAsync(string url, CancellationToken ct)
    {
        using var r = await Http.GetAsync(url, ct);
        return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(ct) : null;
    }
}

/// <summary>The optional per-IP download cap (control.json "downloads": { "maxPerIp": N }); off unless set above 0.</summary>
public static class NetworkCap
{
    /// <summary>Same IPv4, or the same IPv6 /64.</summary>
    public static bool Shares(NetworkInfo? a, NetworkInfo? b)
    {
        if (a == null || b == null) return false;
        if (!string.IsNullOrEmpty(a.Ipv4) && a.Ipv4 == b.Ipv4) return true;
        return !string.IsNullOrEmpty(a.Ipv6Prefix) && a.Ipv6Prefix == b.Ipv6Prefix;
    }

    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(3);

    /// <summary>Downloads running right now on OTHER nodes that share this node's public address: running download jobs of nodes with a lastSeen under 3 minutes old that are not offline.</summary>
    public static int CountOthers(NetworkInfo? self, string selfId, IEnumerable<NodeInfo> nodes, DateTimeOffset now)
    {
        int n = 0;
        foreach (var o in nodes)
        {
            if (o.NodeId == selfId || o.State == "offline" || !Shares(self, o.Network)) continue;
            if (!DateTimeOffset.TryParse(o.LastSeen, out var seen) || now - seen >= Fresh) continue;
            n += o.Running.Count(r => r.Stage == "download");
        }
        return n;
    }

    /// <summary>True when a download may start: no cap, or others + own running downloads are under the cap.</summary>
    public static bool MayClaim(int? maxPerIp, int others, int own) => maxPerIp is not > 0 || others + own < maxPerIp;

    public static string WaitText(int running) => $"Waiting: {running} downloads already running from this IP";
}
