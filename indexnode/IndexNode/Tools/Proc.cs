using System.Diagnostics;
using System.Text;

namespace IndexNode;

/// <summary>Limits for one child invocation: a wall-clock Timeout and an Idle limit (no output on stdout or stderr). Null or zero: no limit.</summary>
public sealed record ChildLimits(TimeSpan? Timeout = null, TimeSpan? Idle = null)
{
    public static string Say(TimeSpan t) => t.TotalSeconds % 60 == 0 ? $"{(int)t.TotalMinutes} min" : $"{t.TotalSeconds:0.#} s";
}

/// <summary>A child that ran past its wall-clock limit or went silent for too long and was killed (tree included). An ordinary exception: the engine counts it as a transient failure with its back-off, and after the attempt limit the job stays failed with this message.</summary>
public sealed class ChildTimeoutException(string message) : Exception(message);

public static class Proc
{
    public static string RunQuick(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15000);
        return o;
    }

    /// <summary>Runs a child process; cancellation kills the whole process tree (yt-dlp, ffmpeg and their children). With limits, a child past its wall-clock limit or silent for the idle limit is killed the same way and ChildTimeoutException is thrown.</summary>
    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args, CancellationToken ct,
        Action<string>? onStdout = null, Action<string>? onStderr = null, string? workDir = null, ChildLimits? limits = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        var so = new StringBuilder(); var se = new StringBuilder();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        long lastOut = Environment.TickCount64; string? why = null;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { Interlocked.Exchange(ref lastOut, Environment.TickCount64); lock (so) so.AppendLine(e.Data); onStdout?.Invoke(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { Interlocked.Exchange(ref lastOut, Environment.TickCount64); lock (se) se.AppendLine(e.Data); onStderr?.Invoke(e.Data); } };
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var reg = ct.Register(() => Kill(p));
        using var done = new CancellationTokenSource();
        var timeout = limits?.Timeout is { Ticks: > 0 } t ? t : (TimeSpan?)null; var idle = limits?.Idle is { Ticks: > 0 } i ? i : (TimeSpan?)null;
        Task watch = Task.CompletedTask;
        if (timeout != null || idle != null)
        {
            var started = Environment.TickCount64;
            var poll = TimeSpan.FromMilliseconds(Math.Clamp(Math.Min((timeout ?? TimeSpan.FromHours(1)).TotalMilliseconds, (idle ?? TimeSpan.FromHours(1)).TotalMilliseconds) / 5, 20, 1000));
            watch = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(poll, done.Token);
                        var now = Environment.TickCount64;
                        if (timeout != null && now - started >= timeout.Value.TotalMilliseconds) why = "timed out after " + ChildLimits.Say(timeout.Value);
                        else if (idle != null && now - Interlocked.Read(ref lastOut) >= idle.Value.TotalMilliseconds) why = "no output for " + ChildLimits.Say(idle.Value) + " (hung); killed";
                        if (why != null) { Kill(p); return; }
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        await p.WaitForExitAsync(CancellationToken.None);
        done.Cancel(); await watch;
        if (why != null) throw new ChildTimeoutException($"{Path.GetFileNameWithoutExtension(file)} {why}");
        ct.ThrowIfCancellationRequested();
        return new ProcResult(p.ExitCode, so.ToString(), se.ToString());
    }

    public static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
