using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode.Hosting;

/// <summary>A newer app (the host APK) than the installed one, as the rolling release's host.json describes it.</summary>
public sealed record HostOffer(string Version, string ApkUrl, string Sha256, long Bytes, string? Notes);

/// <summary>
/// 0.1.5: the app updates itself from inside the app, like Medusa. The rules without Android (tested on Linux): what host.json must say, when it is an update,
/// how the downloaded file is verified, and what an install result means. CI writes host.json next to the plugin manifest when it publishes a new host version:
/// {"version":"0.1.5","apk":"https://github.com/lancec-codebox/appbox/releases/download/indexnode-android-v0.1.5/indexnode-android.apk","sha256":"&lt;64 hex&gt;","bytes":N,"notes":"..."}
/// </summary>
public static partial class HostUpdate
{
    public const string RollingUrl = "https://github.com/lancec-codebox/appbox/releases/download/indexnode-android-plugin/host.json";
    /// <summary>An APK is only ever taken from this repository's releases.</summary>
    public const string AllowedPrefix = "https://github.com/lancec-codebox/appbox/releases/download/";

    [GeneratedRegex("^[0-9a-fA-F]{64}$")] private static partial Regex Hex64();

    /// <summary>True when <paramref name="candidate"/> is a higher x.y.z than <paramref name="installed"/>. Anything that is not a version is not newer.</summary>
    public static bool Newer(string? candidate, string? installed) =>
        Version.TryParse(candidate, out var c) && Version.TryParse(installed, out var i) && c > i;

    /// <summary>The offer in host.json when it is newer than the installed app, else null. A file that is not a host.json (no version, an APK outside the repository's releases, no hash) throws FormatException.</summary>
    public static HostOffer? Parse(string json, string installed)
    {
        JsonObject j;
        try { j = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("host.json is not an object"); }
        catch (System.Text.Json.JsonException) { throw new FormatException("host.json is not JSON"); }
        var version = (string?)j["version"]; var apk = (string?)j["apk"]; var sha = (string?)j["sha256"];
        if (!Version.TryParse(version, out _)) throw new FormatException("host.json has no version");
        if (apk == null || !apk.StartsWith(AllowedPrefix, StringComparison.Ordinal) || !apk.EndsWith(".apk", StringComparison.Ordinal)) throw new FormatException("host.json names an APK outside this repository's releases");
        if (sha == null || !Hex64().IsMatch(sha)) throw new FormatException("host.json has no SHA-256");
        if (!Newer(version, installed)) return null;
        return new HostOffer(version!, apk, sha.ToLowerInvariant(), (long?)j["bytes"] ?? -1, (string?)j["notes"]);
    }

    public static string Sha256Hex(Stream s) => Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();

    /// <summary>True when the file is exactly the APK the offer names.</summary>
    public static bool Verify(string file, HostOffer offer)
    {
        using var f = File.OpenRead(file);
        if (offer.Bytes > 0 && f.Length != offer.Bytes) return false;
        return Sha256Hex(f) == offer.Sha256;
    }

    /// <summary>
    /// What a PackageInstaller session result means (status = PackageInstaller.STATUS_*). Kind: "confirm" (Android shows its own prompt), "done", "cancelled" or "failed".
    /// </summary>
    public static (string Kind, string Message) Outcome(int status, string? systemMessage)
    {
        var extra = string.IsNullOrWhiteSpace(systemMessage) ? "" : " (" + systemMessage.Trim() + ")";
        return status switch
        {
            -1 => ("confirm", "Android asks you to confirm the update"),
            0 => ("done", "The app was updated"),
            3 => ("cancelled", "The update was cancelled"),
            2 => ("failed", "Android blocked the install (a policy or another app)" + extra),
            4 => ("failed", "Android rejected the update file as invalid" + extra),
            5 => ("failed", "The update conflicts with the installed app, probably a different signing key" + extra),
            6 => ("failed", "Not enough storage to install the update" + extra),
            7 => ("failed", "The update is not compatible with this phone" + extra),
            _ => ("failed", "The install failed" + extra),
        };
    }
}
