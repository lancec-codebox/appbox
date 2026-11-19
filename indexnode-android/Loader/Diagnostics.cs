using System.Text;

namespace IndexNode.Hosting;

/// <summary>One unhandled failure, as written to last-crash.txt. Strings only, so the writer is testable off the phone.</summary>
public sealed record CrashInfo(DateTime Utc, string HostVersion, string? PluginVersion, string Device, string AndroidVersion, string Source, string ExceptionType, string Message, string Stack);

/// <summary>Text for last-crash.txt and the diagnostics report. Everything goes through HostRedact: no SAS, no sig=.</summary>
public static class Diagnostics
{
    public const int MaxLogLines = 500, MaxLineChars = 600, MaxReportLogLines = 200, MaxCrashChars = 20000;

    public static string FormatCrash(CrashInfo c, params string?[] secrets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("time (UTC): " + c.Utc.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("app: IndexNode host " + c.HostVersion + ", plugin " + (c.PluginVersion ?? "(not loaded)"));
        sb.AppendLine("device: " + c.Device + ", Android " + c.AndroidVersion);
        sb.AppendLine("source: " + c.Source);
        sb.AppendLine("exception: " + c.ExceptionType);
        sb.AppendLine("message: " + c.Message);
        sb.AppendLine("stack:");
        sb.AppendLine(c.Stack);
        return Cap(HostRedact.Text(sb.ToString(), secrets), MaxCrashChars);
    }

    /// <summary>Managed type, message and the full chain (inner exceptions included) of an exception.</summary>
    public static string Describe(Exception e) => e.ToString();

    /// <summary>The newest lines, each cut to a length, redacted.</summary>
    public static string[] TrimLog(IEnumerable<string> lines, int maxLines = MaxLogLines, int maxLineChars = MaxLineChars, params string?[] secrets)
    {
        var all = lines.Select(l => HostRedact.Text(l, secrets)).Select(l => l.Length > maxLineChars ? l[..maxLineChars] + "..." : l).ToList();
        return all.Count > maxLines ? all.GetRange(all.Count - maxLines, maxLines).ToArray() : all.ToArray();
    }

    /// <summary>The text behind "Copy diagnostics": versions, device, settings (the SAS never), node state, last crash, recent log.</summary>
    public static string Report(string hostVersion, string? pluginVersion, string device, string androidVersion, IReadOnlyDictionary<string, string?> settings, IReadOnlyDictionary<string, string?> state, string? lastCrash, IEnumerable<string> log, params string?[] secrets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("IndexNode diagnostics");
        sb.AppendLine("host " + hostVersion + ", plugin " + (pluginVersion ?? "(not loaded)"));
        sb.AppendLine("device " + device + ", Android " + androidVersion);
        sb.AppendLine().AppendLine("== settings (no store signature) ==");
        foreach (var (k, v) in settings) sb.AppendLine(k + ": " + (v ?? "-"));
        sb.AppendLine().AppendLine("== node state ==");
        foreach (var (k, v) in state) sb.AppendLine(k + ": " + (v ?? "-"));
        sb.AppendLine().AppendLine("== last crash ==");
        sb.AppendLine(string.IsNullOrWhiteSpace(lastCrash) ? "(none)" : Cap(lastCrash!, MaxCrashChars));
        sb.AppendLine().AppendLine("== recent log ==");
        foreach (var l in TrimLog(log, MaxReportLogLines, MaxLineChars, secrets)) sb.AppendLine(l);
        return HostRedact.Text(sb.ToString(), secrets);
    }

    static string Cap(string s, int max) => s.Length > max ? s[..max] + "\n...(truncated)" : s;
}
