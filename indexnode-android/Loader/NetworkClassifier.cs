namespace IndexNode.Hosting;

/// <summary>One Android network as the host sees it: its transports and capabilities, and whether it is the active (default) one.</summary>
public sealed record NetSnapshot(bool Wifi, bool Ethernet, bool Cellular, bool Vpn, bool Internet, bool Validated, bool NotMetered, bool IsActive);

/// <summary>What the device is connected to, from all of its networks. Pure, so it is tested on Linux; the Android host only maps ConnectivityManager into NetSnapshot.</summary>
public static class NetworkClassifier
{
    /// <summary>Transport: wifi, ethernet, cellular, vpn or none. Validated: the network that decided it has working internet. Metered comes from that network. ActiveTransport is the active network's own transport.</summary>
    public sealed record Result(string Transport, bool Vpn, bool Metered, string? Via, string ActiveTransport = "none", bool Validated = false);

    public static Result Classify(IReadOnlyList<NetSnapshot> nets)
    {
        var r = Decide(nets);
        var a = nets.FirstOrDefault(n => n.IsActive);
        return r with { ActiveTransport = a == null ? "none" : a.Vpn ? "vpn" : a.Wifi ? "wifi" : a.Ethernet ? "ethernet" : a.Cellular ? "cellular" : "other" };
    }

    private static string Name(NetSnapshot n) => n.Wifi ? "wifi" : n.Ethernet ? "ethernet" : n.Cellular ? "cellular" : "none";

    /// <summary>
    /// A VPN is the active network on a phone that uses one; it may carry no Wi-Fi transport and is metered by default on Android 10+. So when the active network is a VPN the networks under it decide:
    /// a non-VPN network with Wi-Fi or ethernet means Wi-Fi (a validated one preferred), with Metered and Validated taken from that network and Via "vpn". Cellular under a VPN is cellular, never Wi-Fi.
    /// A Wi-Fi network that is up but not validated is still reported as wifi with Validated=false, so the verdict can say "connected, no internet".
    /// </summary>
    private static Result Decide(IReadOnlyList<NetSnapshot> nets)
    {
        var active = nets.FirstOrDefault(n => n.IsActive);
        if (active == null) return new("none", false, false, null);
        bool vpn = active.Vpn; string? via = vpn ? "vpn" : null;
        if (!vpn) return new(Name(active), false, !active.NotMetered, null, Validated: active.Validated);
        var under = nets.Where(n => !n.Vpn && n.Internet).ToList();
        var lan = under.FirstOrDefault(n => (n.Wifi || n.Ethernet) && n.Validated) ?? under.FirstOrDefault(n => n.Wifi || n.Ethernet);
        if (lan != null) return new(Name(lan), true, !lan.NotMetered, via, Validated: lan.Validated);
        var cell = under.FirstOrDefault(n => n.Cellular && n.Validated) ?? under.FirstOrDefault(n => n.Cellular);
        if (cell != null) return new("cellular", true, !cell.NotMetered, via, Validated: cell.Validated);
        // a VPN with nothing known underneath: what the VPN itself reports
        return new(active.Wifi || active.Ethernet || active.Cellular ? Name(active) : "vpn", true, !active.NotMetered, via, Validated: active.Validated);
    }
}
