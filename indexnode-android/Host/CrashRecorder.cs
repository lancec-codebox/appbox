using Android.Content;
using Android.OS;
using Android.Runtime;
using IndexNode.Hosting;

namespace IndexNode.Host;

/// <summary>Writes last-crash.txt (and keeps a rolling log file) in the app's files dir for every unhandled failure: managed, unobserved task and Java. Nothing here may throw.</summary>
public static class CrashRecorder
{
    public const string CrashFile = "last-crash.txt", LogFile = "indexnode-log.txt";
    static string dir = "";
    static Func<string?[]> secrets = () => [];
    static Func<string?> pluginVersion = () => null;
    static bool installed;
    static readonly object gate = new();

    public static string CrashPath => Path.Combine(dir, CrashFile);
    public static string LogPath => Path.Combine(dir, LogFile);

    public static void Install(Context ctx, Func<string?[]> secretsFn, Func<string?> pluginVersionFn)
    {
        lock (gate)
        {
            dir = ctx.FilesDir!.AbsolutePath; secrets = secretsFn; pluginVersion = pluginVersionFn;
            if (installed) return; installed = true;
        }
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Record("AndroidEnvironment.UnhandledExceptionRaiser", e.Exception);   // not marked handled: the app still ends as before, but with a record
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("AppDomain.UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) => Record("TaskScheduler.UnobservedTaskException", e.Exception);
        var previous = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
        Java.Lang.Thread.DefaultUncaughtExceptionHandler = new JavaHandler(previous);
    }

    sealed class JavaHandler(Java.Lang.Thread.IUncaughtExceptionHandler? previous) : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        public void UncaughtException(Java.Lang.Thread? t, Java.Lang.Throwable? e)
        {
            try { Record("Java uncaught exception (thread " + t?.Name + ")", e == null ? new Exception("null throwable") : new Exception(e.GetType().FullName + ": " + e.Message + "\n" + Android.Util.Log.GetStackTraceString(e), null)); } catch { }
            previous?.UncaughtException(t, e);
        }
    }

    public static void Record(string source, Exception e)
    {
        try
        {
            var info = new CrashInfo(DateTime.UtcNow, NodeRuntime.HostVersion, Try(pluginVersion), (Build.Manufacturer + " " + Build.Model).Trim(), Build.VERSION.Release + " (API " + (int)Build.VERSION.SdkInt + ")", source, e.GetType().FullName ?? "Exception", e.Message, Diagnostics.Describe(e));
            var text = Diagnostics.FormatCrash(info, secrets());
            lock (gate) File.WriteAllText(CrashPath, text);
            try { AppendLog("CRASH " + source + ": " + e.GetType().Name + ": " + e.Message); } catch { }
        }
        catch { }
    }

    static string? Try(Func<string?> f) { try { return f(); } catch { return null; } }

    /// <summary>Rolling log: appended on every line, cut to the last 500 lines when it grows past twice that.</summary>
    public static void AppendLog(string line)
    {
        try
        {
            if (dir == "") return;
            lock (gate)
            {
                File.AppendAllText(LogPath, line.Replace("\r", " ").Replace("\n", " ") + "\n");
                if (new FileInfo(LogPath).Length > 400_000)
                {
                    var keep = Diagnostics.TrimLog(File.ReadAllLines(LogPath));
                    File.WriteAllLines(LogPath, keep);
                }
            }
        }
        catch { }
    }

    public static string? LastCrash() { try { lock (gate) return File.Exists(CrashPath) ? File.ReadAllText(CrashPath) : null; } catch { return null; } }
    public static string[] LogFileLines() { try { lock (gate) return File.Exists(LogPath) ? Diagnostics.TrimLog(File.ReadAllLines(LogPath)) : []; } catch { return []; } }
    public static void Clear() { try { lock (gate) { if (File.Exists(CrashPath)) File.Delete(CrashPath); if (File.Exists(LogPath)) File.Delete(LogPath); } } catch { } }
}
