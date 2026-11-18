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

    /// <summary>The exception type, message and stack trace (inner exceptions too) for stderr, with SAS signatures and cookies removed; nothing else is left out.</summary>
    public static string Trace(Exception e) => Sas(e.ToString());

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
    private static RollingFile? file;

    /// <summary>Also writes every log line to a rolling file (2 files of at most 20 MB).</summary>
    public static void UseFile(RollingFile f) { lock (Gate) file = f; }

    public static void Info(string message)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss} {Redact.Sas(message)}";
        lock (Gate) { Out.WriteLine(line); try { file?.WriteLine(line); } catch { /* the log file is a convenience */ } }
    }
}

/// <summary>A log file that rolls over at <see cref="MaxBytes"/>: name.log and name.1.log, so at most two files.</summary>
public sealed class RollingFile
{
    private readonly string path, older;
    public long MaxBytes { get; }
    private long size;

    public RollingFile(string path, long maxBytes = 20L * 1024 * 1024)
    {
        this.path = path; MaxBytes = maxBytes;
        older = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".1" + Path.GetExtension(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        size = File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public void WriteLine(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
        if (size > 0 && size + bytes > MaxBytes) { File.Move(path, older, true); size = 0; }
        File.AppendAllText(path, line + Environment.NewLine);
        size += bytes;
    }

    public static string DefaultDir() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "indexnode", "logs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "indexnode", "logs");

    public static string FileFor(string dir, string nodeName) => Path.Combine(dir, "run-" + new string(nodeName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()) + ".log");
}
