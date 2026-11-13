using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>The SAS URL is a secret: every log line and exception passes through here.</summary>
public static partial class Redact
{
    [GeneratedRegex(@"(?i)(sig=)[^&\s""'<>]+")]
    private static partial Regex SigRegex();

    public static string Sas(string? text) => text == null ? "" : SigRegex().Replace(text, "$1REDACTED");

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
