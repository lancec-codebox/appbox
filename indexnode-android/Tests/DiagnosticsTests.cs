using IndexNode.Hosting;

namespace IndexNode.Android.Tests;

public class DiagnosticsTests
{
    const string Sas = "https://acct.blob.core.windows.net/c?sv=2022&sig=SECRETSIGVALUE%2Fabc&se=2030";

    static CrashInfo Crash(string msg = "boom", string stack = "at A.B()") =>
        new(new DateTime(2026, 11, 19, 8, 30, 0, DateTimeKind.Utc), "0.1.2", "0.1.1", "Google Pixel 7", "14", "AppDomain.UnhandledException", "System.InvalidOperationException", msg, stack);

    [Fact]
    public void Crash_record_has_time_versions_device_exception_and_stack()
    {
        var t = Diagnostics.FormatCrash(Crash());
        Assert.Contains("2026-11-19 08:30:00", t);
        Assert.Contains("host 0.1.2, plugin 0.1.1", t);
        Assert.Contains("Google Pixel 7, Android 14", t);
        Assert.Contains("System.InvalidOperationException", t);
        Assert.Contains("message: boom", t);
        Assert.Contains("at A.B()", t);
    }

    [Fact]
    public void Crash_record_never_contains_the_sas_or_a_signature()
    {
        var t = Diagnostics.FormatCrash(Crash("failed " + Sas, "at X() " + Sas), Sas, "tokentoken123");
        Assert.DoesNotContain("SECRETSIGVALUE", t);
        Assert.DoesNotContain("sig=SECRET", t);
        Assert.Contains("REDACTED", t);
        Assert.DoesNotContain("tokentoken123", Diagnostics.FormatCrash(Crash("t tokentoken123"), "tokentoken123"));
    }

    [Fact]
    public void A_huge_crash_is_cut()
    {
        var t = Diagnostics.FormatCrash(Crash(stack: new string('x', 100000)));
        Assert.True(t.Length < Diagnostics.MaxCrashChars + 100);
        Assert.Contains("truncated", t);
    }

    [Fact]
    public void Log_keeps_the_newest_lines_and_cuts_long_ones()
    {
        var lines = Enumerable.Range(0, 1200).Select(i => "line " + i).Append(new string('y', 5000)).ToList();
        var r = Diagnostics.TrimLog(lines);
        Assert.Equal(Diagnostics.MaxLogLines, r.Length);
        Assert.Equal("line 701", r[0]);
        Assert.True(r[^1].Length <= Diagnostics.MaxLineChars + 3);
        Assert.Equal(new[] { "a", "b" }, Diagnostics.TrimLog(new[] { "a", "b" }));
    }

    [Fact]
    public void Report_has_every_section_and_no_sas_anywhere()
    {
        var settings = new Dictionary<string, string?> { ["wifiOnly"] = "true", ["store"] = Sas, ["note"] = null };
        var state = new Dictionary<string, string?> { ["state"] = "idle", ["lastError"] = "GET " + Sas + " failed" };
        var log = Enumerable.Range(0, 400).Select(i => "log " + i + (i == 399 ? " " + Sas : "")).ToList();
        var r = Diagnostics.Report("0.1.2", null, "Pixel 7", "14", settings, state, Diagnostics.FormatCrash(Crash(Sas), Sas), log, Sas);
        Assert.Contains("host 0.1.2, plugin (not loaded)", r);
        Assert.Contains("wifiOnly: true", r); Assert.Contains("state: idle", r);
        Assert.Contains("== last crash ==", r); Assert.Contains("== recent log ==", r);
        Assert.DoesNotContain("SECRETSIGVALUE", r); Assert.DoesNotContain("sig=SEC", r);
        Assert.DoesNotContain("log 100\n", r.Replace("\r", ""));   // only the newest 200 lines
        Assert.Contains("log 399", r);
    }

    [Fact]
    public void Report_without_a_crash_says_none()
    {
        var r = Diagnostics.Report("0.1.2", "0.1.2", "P", "13", new Dictionary<string, string?>(), new Dictionary<string, string?>(), null, new string[0]);
        Assert.Contains("(none)", r);
    }
}
