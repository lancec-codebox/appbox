using System.Text;
using System.Text.Json.Nodes;

namespace IndexNode.Hosting;

/// <summary>The owner's conditions for a run: Wi-Fi only and charging only (both default on), a daily cap. Pure logic so it is tested on Linux.</summary>
public sealed record RunConditions(bool WifiOnly = true, bool ChargingOnly = true, int DailyCap = 20)
{
    /// <summary>Why a run may not start now, or null when it may.</summary>
    public string? WhyNot(DeviceInfo d, int doneToday)
    {
        // precise verdicts: what the owner needs to see on the status screen
        if (d.Transport == "none") return "no network";
        if ((d.Transport == "wifi" || d.Transport == "ethernet") && !d.Validated) return "Wi-Fi connected, no internet";
        if (WifiOnly && d.Transport != "wifi" && d.Transport != "ethernet") return d.Transport == "cellular" ? "on mobile data" : "no Wi-Fi network";
        if (WifiOnly && d.Metered) return "Wi-Fi is metered";
        if (ChargingOnly && !d.Charging) return "not charging";
        if (DailyCap > 0 && doneToday >= DailyCap) return $"daily cap of {DailyCap} reached";
        return null;
    }

    /// <summary>How many jobs the next run may start (0 = no cap, i.e. unlimited, is reported as int.MaxValue).</summary>
    public int Remaining(int doneToday) => DailyCap <= 0 ? int.MaxValue : Math.Max(0, DailyCap - doneToday);

    public static RunConditions FromSettings(Func<string, string?> get) => new(
        get("wifiOnly") != "false", get("chargingOnly") != "false", int.TryParse(get("dailyCap"), out var c) ? c : 20);
}

/// <summary>The node-setup link or file of `indexnode medusa-setup` (kind medusa-node-settings): SAS, node name, conditions, cap, budget, and (0.1.4) the check server and its token.</summary>
public sealed record SetupSettings(string? Sas, string? NodeName, string Conditions, int DailyCap, int? Budget, string? Schedule, string? Window, int MaxHeight)
{
    /// <summary>0.1.4: the check server (`indexnode serve`) of the downloader: its URL and bearer token. Null when the setup has none.</summary>
    public string? CheckUrl { get; init; }
    public string? CheckToken { get; init; }

    public const string Kind = "medusa-node-settings", Scheme = "medusa://node-setup?d=", WebBase = "https://lancec-codebox.github.io/appbox/setup/#d=";

    /// <summary>Accepts the medusa:// link, the https hand-off page link, or the JSON of a .medusa-node.json file.</summary>
    public static SetupSettings Parse(string text)
    {
        text = text.Trim();
        string json;
        if (text.StartsWith(Scheme, StringComparison.Ordinal)) json = Decode(text[Scheme.Length..]);
        else if (text.StartsWith(WebBase, StringComparison.Ordinal)) json = Decode(text[WebBase.Length..]);
        else if (text.StartsWith('{')) json = text;
        else throw new FormatException("not a node-setup link or file");
        var j = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("empty setup");
        if ((string?)j["kind"] != Kind) throw new FormatException("not medusa node settings");
        var cond = (string?)j["conditions"] ?? "wifi+charging";
        if (cond is not ("wifi+charging" or "wifi")) throw new FormatException("unknown conditions " + cond);
        var sas = (string?)j["sas"];
        if (sas != null && !Uri.TryCreate(sas, UriKind.Absolute, out var u)) throw new FormatException("the SAS is not a URL");
        var checkUrl = (string?)j["checkUrl"];
        if (checkUrl != null && !(Uri.TryCreate(checkUrl, UriKind.Absolute, out var cu) && cu.Scheme is "https" or "http")) throw new FormatException("the check server is not a URL");
        return new SetupSettings(sas, (string?)j["nodeName"], cond, (int?)j["dailyCap"] ?? 20, (int?)j["budget"], (string?)j["schedule"], (string?)j["window"], (int?)j["maxHeight"] ?? 480) { CheckUrl = checkUrl, CheckToken = (string?)j["checkToken"] };
    }

    static string Decode(string b64)
    {
        b64 = b64.Replace('-', '+').Replace('_', '/'); b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    /// <summary>The container URL without the SAS (what the plugin may see), or null when no SAS.</summary>
    public string? StoreUrl => Sas == null ? null : new Uri(Sas).GetLeftPart(UriPartial.Path).TrimEnd('/');
}

/// <summary>The node id: android-&lt;model&gt;-&lt;6 hex&gt;, stable per install.</summary>
public static class NodeIdentity
{
    public static string Slug(string s)
    {
        var r = new string(s.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (r.Contains("--")) r = r.Replace("--", "-");
        return r.Length == 0 ? "phone" : r.Length > 24 ? r[..24].Trim('-') : r;
    }
    public static string NewId(string model) => "android-" + Slug(model) + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
}

/// <summary>Redacts what must never reach a log or the UI: SAS signatures, Medusa tokens and the check server token.</summary>
public static class HostRedact
{
    public static string Text(string? s, params string?[] secrets)
    {
        if (s == null) return "";
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?i)(sig=)[^&\s""'<>]+", "$1REDACTED");
        foreach (var x in secrets) if (!string.IsNullOrEmpty(x) && x.Length >= 6) s = s.Replace(x, "REDACTED");
        return s;
    }
}
