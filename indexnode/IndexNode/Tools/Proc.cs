using System.Diagnostics;
using System.Text;

namespace IndexNode;

public sealed record ProcResult(int ExitCode, string Stdout, string Stderr);

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

    /// <summary>Runs a child process; cancellation kills the whole process tree (yt-dlp, ffmpeg and their children).</summary>
    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args, CancellationToken ct,
        Action<string>? onStdout = null, Action<string>? onStderr = null, string? workDir = null)
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
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (so) so.AppendLine(e.Data); onStdout?.Invoke(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (se) se.AppendLine(e.Data); onStderr?.Invoke(e.Data); } };
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var reg = ct.Register(() => Kill(p));
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return new ProcResult(p.ExitCode, so.ToString(), se.ToString());
    }

    public static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
