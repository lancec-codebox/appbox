using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace IndexNode;

/// <summary>
/// A generic script runner (the GPU machine's whole job): scripts are queued as blobs, each with a time limit, and run one at a time.
///
///   indexnode scripts run-queue [--idle 2m] [--max 2h] [--prefix index/] [--node-name N]   take, run, record, repeat; exit when idle or at the cap
///   indexnode scripts submit --file run.sh [--timeout 300] [--id ID]                   queue a script (prints its id)
///   indexnode scripts wait <id> [--timeout 600]                                        wait for its result; prints the output, exits with its exit code
///
/// Queue: {prefix}gpu-scripts/pending/{id}.json = { v, timeoutSeconds, script, submittedAt }.
/// Result: {prefix}gpu-scripts/done/{id}/result.json = { exitCode, timedOut, seconds, node, startedAt, finishedAt } and output.txt (stdout+stderr, last 4 MB).
/// The runner knows nothing about IndexNode versions or stages: a script calls `$INDEXNODE ensure <version>` and `... stages run ...` itself.
/// </summary>
public static class ScriptsCommand
{
    const int MaxOutput = 4 << 20;

    public static async Task<int> RunAsync(Args a)
    {
        var sub = a.Positional.FirstOrDefault() ?? throw new ArgumentException("scripts run-queue|submit|wait");
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp();
        IStore store = new RestStore(http, cfg.Sas); var prefix = a.Get("prefix") ?? "index/";
        string Pending(string id) => $"{prefix}gpu-scripts/pending/{id}.json";
        string Done(string id, string f) => $"{prefix}gpu-scripts/done/{id}/{f}";
        switch (sub)
        {
            case "submit":
            {
                var file = a.Get("file") ?? throw new ArgumentException("--file <script> is required");
                var id = a.Get("id") ?? $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
                var job = new { v = 1, timeoutSeconds = a.Get("timeout") is { } t ? int.Parse(t) : 300, script = await File.ReadAllTextAsync(file), submittedAt = DateTime.UtcNow };
                if (!await store.PutAsync(Pending(id), JsonSerializer.SerializeToUtf8Bytes(job), null, null, true, CancellationToken.None)) throw new InvalidOperationException($"script {id} already exists");
                Console.WriteLine(id); return 0;
            }
            case "wait":
            {
                var id = a.Positional.ElementAtOrDefault(1) ?? throw new ArgumentException("scripts wait <id>");
                var until = DateTime.UtcNow.AddSeconds(a.Get("timeout") is { } t ? double.Parse(t) : 600);
                while (DateTime.UtcNow < until)
                {
                    var r = await store.GetAsync(Done(id, "result.json"), CancellationToken.None);
                    if (r != null)
                    {
                        var o = await store.GetAsync(Done(id, "output.txt"), CancellationToken.None);
                        if (o != null) Console.Write(Encoding.UTF8.GetString(o.Data));
                        var res = JsonDocument.Parse(r.Data).RootElement;
                        Console.Error.WriteLine($"script {id}: exit {res.GetProperty("exitCode").GetInt32()}{(res.GetProperty("timedOut").GetBoolean() ? " (TIMED OUT)" : "")} after {res.GetProperty("seconds").GetDouble():F1}s on {res.GetProperty("node").GetString()}");
                        return res.GetProperty("exitCode").GetInt32();
                    }
                    await Task.Delay(3000);
                }
                Console.Error.WriteLine($"script {id}: no result yet (gave up waiting)"); return 124;
            }
            case "run-queue":
                return await RunQueueAsync(store, prefix, Pending, Done, a);
            default: throw new ArgumentException($"unknown scripts command '{sub}'");
        }
    }

    static async Task<int> RunQueueAsync(IStore store, string prefix, Func<string, string> pending, Func<string, string, string> done, Args a)
    {
        var idle = WatchStage.ParseEvery(a.Get("idle") ?? "2m"); var cap = WatchStage.ParseEvery(a.Get("max") ?? "2h");
        var node = a.Get("node-name") ?? Environment.MachineName;
        var started = DateTime.UtcNow; var lastWork = DateTime.UtcNow; int ran = 0;
        Console.Error.WriteLine($"scripts: runner {node} watching {prefix}gpu-scripts/pending/ (idle exit {idle}, cap {cap})");
        while (DateTime.UtcNow - started < cap)
        {
            var list = await store.ListAsync($"{prefix}gpu-scripts/pending/", false, false, CancellationToken.None);
            var next = list.OrderBy(b => b.Name, StringComparer.Ordinal).FirstOrDefault();
            if (next == null)
            {
                if (DateTime.UtcNow - lastWork >= idle) { Console.Error.WriteLine($"scripts: idle {idle}, {ran} script(s) run, exiting"); return 0; }
                await Task.Delay(5000); continue;
            }
            var id = Path.GetFileNameWithoutExtension(next.Name);
            var lease = await store.AcquireLeaseAsync(next.Name, 60, CancellationToken.None);
            if (lease == null) { await Task.Delay(1000); continue; } // another runner has it
            try
            {
                var job = JsonDocument.Parse((await store.GetAsync(next.Name, CancellationToken.None))!.Data).RootElement;
                int timeout = job.TryGetProperty("timeoutSeconds", out var to) ? to.GetInt32() : 300;
                var script = job.GetProperty("script").GetString() ?? "";
                using var renew = new CancellationTokenSource();
                var renewer = Task.Run(async () => { while (!renew.IsCancellationRequested) { try { await Task.Delay(30000, renew.Token); await store.RenewLeaseAsync(next.Name, lease, CancellationToken.None); } catch (OperationCanceledException) { } catch (Exception e) { Console.Error.WriteLine($"scripts: lease renew: {e.Message}"); } } });
                Console.Error.WriteLine($"scripts: {id} start (limit {timeout}s)");
                var (exit, timedOut, secs, output, t0) = await RunScriptAsync(script, timeout, id);
                renew.Cancel(); await renewer;
                await store.PutAsync(done(id, "output.txt"), output, null, null, false, CancellationToken.None, "text/plain; charset=utf-8");
                await store.PutAsync(done(id, "result.json"), JsonSerializer.SerializeToUtf8Bytes(new { v = 1, id, exitCode = exit, timedOut, seconds = Math.Round(secs, 1), node, startedAt = t0, finishedAt = DateTime.UtcNow }), null, null, false, CancellationToken.None);
                Console.Error.WriteLine($"scripts: {id} exit {exit}{(timedOut ? " TIMED OUT" : "")} after {secs:F1}s");
                ran++;
            }
            finally
            {
                try { await store.ReleaseLeaseAsync(next.Name, lease, CancellationToken.None); } catch { }
                try { await store.DeleteAsync(next.Name, CancellationToken.None); } catch (Exception e) { Console.Error.WriteLine($"scripts: could not remove {next.Name}: {e.Message}"); }
                lastWork = DateTime.UtcNow;
            }
        }
        Console.Error.WriteLine($"scripts: reached the cap {cap}, {ran} script(s) run, exiting");
        return 0;
    }

    /// <summary>Runs the script with bash (pwsh on Windows when bash is missing) under its time limit; past it the whole process tree is killed.</summary>
    static async Task<(int Exit, bool TimedOut, double Secs, byte[] Output, DateTime Started)> RunScriptAsync(string script, int timeoutSeconds, string id)
    {
        var dir = Path.Combine(Path.GetTempPath(), "indexnode-scripts", id); Directory.CreateDirectory(dir);
        // Linux: bash. Windows (local tests): Git's bash (the "bash" on PATH there is often the WSL stub), else pwsh.
        string? bash = !OperatingSystem.IsWindows() ? "bash" : new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") }.FirstOrDefault(File.Exists);
        bool useBash = bash != null;
        var file = Path.Combine(dir, useBash ? "script.sh" : "script.ps1");
        await File.WriteAllTextAsync(file, script.Replace("\r\n", "\n"));
        var psi = new ProcessStartInfo(useBash ? bash! : "pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
        if (!useBash) { psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-File"); }
        psi.ArgumentList.Add(file);
        psi.Environment["INDEXNODE"] = SelfCommand(); // scripts call "$INDEXNODE ensure <version>" / "$INDEXNODE stages run ..."
        var outBuf = new StringBuilder(); var gate = new object();
        void Add(string? line) { if (line == null) return; lock (gate) { if (outBuf.Length < MaxOutput) outBuf.AppendLine(line); } Console.Error.WriteLine($"  [{id}] {line}"); }
        var sw = Stopwatch.StartNew(); var t0 = DateTime.UtcNow;
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => Add(e.Data); p.ErrorDataReceived += (_, e) => Add(e.Data);
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        bool timedOut = false;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { timedOut = true; try { p.Kill(true); } catch { } Add($"[runner] killed: past the {timeoutSeconds}s limit"); } // no wait here: WaitForExitAsync also waits for the output pipes
        }
        double secs = sw.Elapsed.TotalSeconds;
        if (timedOut) p.WaitForExit(2000); else p.WaitForExit(); // drain output; after a kill don't wait on a leftover child holding the pipe
        try { Directory.Delete(dir, true); } catch { }
        string text; lock (gate) text = outBuf.ToString();
        return (timedOut ? 124 : p.ExitCode, timedOut, secs, Encoding.UTF8.GetBytes(text), t0);
    }

    static string SelfCommand()
    {
        var p = Environment.ProcessPath ?? "indexnode";
        return Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? $"{p} {Path.Combine(AppContext.BaseDirectory, "indexnode.dll")}" : p;
    }

    static string? Which(string exe)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var ext in OperatingSystem.IsWindows() ? new[] { ".exe", "" } : [""])
                if (File.Exists(Path.Combine(d, exe + ext))) return Path.Combine(d, exe + ext);
        return null;
    }
}
