using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>The SAS URL and the browser's cookies are secrets: every log line and exception passes through here.</summary>
public static partial class Redact
{
    [GeneratedRegex(@"(?i)(sig=)[^&\s""'<>]+")]
    private static partial Regex SigRegex();

    // a Cookie / Set-Cookie header, and a Netscape cookie-file line (domain, flag, path, flag, expiry, name, value)
    [GeneratedRegex(@"(?i)((?:set-)?cookie:\s*)[^\r\n]+")]
    private static partial Regex CookieHeaderRegex();

    [GeneratedRegex(@"(?m)^(?:#HttpOnly_)?\.?[\w.-]+\t(?:TRUE|FALSE)\t[^\t\r\n]*\t(?:TRUE|FALSE)\t\d+\t[^\t\r\n]*\t[^\r\n]*$")]
    private static partial Regex NetscapeLineRegex();

    [GeneratedRegex(@"(?i)(authorization:\s*bearer\s+|\bsk-)[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex BearerRegex();

    private static readonly ConcurrentDictionary<string, byte> KnownSecrets = new();

    /// <summary>Registers an exact secret value (a cookie value): it is replaced wherever it shows up, until <see cref="ClearKnown"/>.</summary>
    public static void Register(string? secret) { if (!string.IsNullOrEmpty(secret) && secret.Length >= 6) KnownSecrets[secret] = 0; }
    public static void ClearKnown() => KnownSecrets.Clear();

    public static string Sas(string? text)
    {
        if (text == null) return "";
        var r = SigRegex().Replace(text, "$1REDACTED");
        r = CookieHeaderRegex().Replace(r, "$1REDACTED");
        r = BearerRegex().Replace(r, "$1REDACTED");
        r = NetscapeLineRegex().Replace(r, "[cookie line REDACTED]");
        foreach (var k in KnownSecrets.Keys) r = r.Replace(k, "REDACTED");
        return r;
    }

    /// <summary>Redacts the text and, when known, the exact SAS string (covers odd encodings).</summary>
    public static string Sas(string? text, string? knownSas)
    {
        var r = Sas(text);
        if (!string.IsNullOrEmpty(knownSas)) r = r.Replace(knownSas, "REDACTED");
        return r;
    }
}

public static class Log
{
    public static TextWriter Out = Console.Out;
    private static readonly object Gate = new();

    public static void Info(string message)
    {
        lock (Gate) Out.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {Redact.Sas(message)}");
    }
}
