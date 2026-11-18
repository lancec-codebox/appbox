using System.Text;
using System.Text.RegularExpressions;

namespace IndexNode.Hosting.Medusa;

/// <summary>ITools over Medusa's /v2 capability API: a workspace on the phone, runs of yt-dlp, ffmpeg and ffprobe in it, files read with Range into the app sandbox only when asked.</summary>
public sealed class MedusaV2Tools(MedusaClient client, string sandboxDir) : ITools
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(700);
    public int TimeoutSec { get; set; } = 3600;
    /// <summary>"background" for the index node (the spec default is interactive).</summary>
    public string Priority { get; set; } = "background";
    /// <summary>Called with the queue message ("Waiting for index-node download 12ab") while a run waits, and with null once it runs.</summary>
    public Action<string?>? OnQueue { get; set; }
    public const int RangeChunk = 4 << 20;

    public async Task<IToolWorkspace> CreateWorkspaceAsync(string name, CancellationToken ct)
    {
        var id = await client.CreateWorkspaceAsync(name, ct);
        var local = Path.Combine(sandboxDir, "ws-" + Regex.Replace(id, "[^A-Za-z0-9_-]", "_"));
        Directory.CreateDirectory(local);
        return new V2Workspace(client, id, name, local);
    }

    public async Task<ProcResult> RunAsync(IToolWorkspace ws, string tool, IReadOnlyList<string> args, CancellationToken ct, Action<string>? onStdout = null)
    {
        var w = (V2Workspace)ws;
        var runId = await client.StartRunAsync(tool, args, w.Id, TimeoutSec, Priority, ct);
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        int from = 0;
        try
        {
            while (true)
            {
                var st = await client.GetRunAsync(runId, ct);
                var (next, lines) = await client.GetLogAsync(runId, from, ct);
                from = next;
                OnQueue?.Invoke(st.State == "queued" ? (st.QueueMessage ?? "Waiting for Medusa") : null);
                foreach (var (stream, text) in lines)
                {
                    if (stream == "stderr") stderr.AppendLine(text); else { stdout.AppendLine(text); onStdout?.Invoke(text); }
                }
                if (st.Finished)
                {
                    int code = st.ExitCode ?? (st.Succeeded ? 0 : 1);
                    if (code != 0 && stderr.Length == 0 && !string.IsNullOrEmpty(st.Tail)) stderr.Append(st.Tail);
                    return new ProcResult(code, stdout.ToString(), stderr.ToString());
                }
                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            try { await client.CancelRunAsync(runId, CancellationToken.None); } catch { /* best effort */ }
            throw;
        }
    }

    public async Task<ProcResult> RunYtDlpAsync(IEnumerable<string> args, CancellationToken ct, Action<string>? onStdout = null)
    {
        await using var ws = await CreateWorkspaceAsync("yt-dlp-" + Guid.NewGuid().ToString("N")[..8], ct);
        return await RunAsync(ws, "yt-dlp", args.ToList(), ct, onStdout);
    }
}

public sealed class V2Workspace(MedusaClient client, string id, string name, string localDir) : IToolWorkspace
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    private readonly Dictionary<string, string> fetched = new();

    private async Task<Dictionary<string, long>> FilesAsync(CancellationToken ct) =>
        (await client.ListFilesAsync(Id, ct)).GroupBy(f => f.Path.Replace('\\', '/').TrimStart('/')).ToDictionary(g => g.Key, g => g.First().Size);

    public async Task<bool> ExistsAsync(string path, CancellationToken ct) => (await FilesAsync(ct)).ContainsKey(path.Replace('\\', '/'));

    public async Task<IReadOnlyList<string>> ListAsync(string dir, string glob, CancellationToken ct)
    {
        dir = dir.Trim('/');
        var rx = new Regex("^" + Regex.Escape(glob).Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$", RegexOptions.IgnoreCase);
        return (await FilesAsync(ct)).Keys
            .Where(p => { var d = p.Contains('/') ? p[..p.LastIndexOf('/')] : ""; return d == dir && rx.IsMatch(p[(p.LastIndexOf('/') + 1)..]); })
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        var (data, _) = await client.GetRangeAsync(Id, path, 0, null, ct);
        return Encoding.UTF8.GetString(data);
    }

    public Task WriteTextAsync(string path, string text, CancellationToken ct) => client.PutFileAsync(Id, path, Encoding.UTF8.GetBytes(text), ct);

    public async Task<long> TotalBytesAsync(CancellationToken ct) => (await FilesAsync(ct)).Values.Where(v => v > 0).Sum();

    public async Task<long> SizeAsync(string path, CancellationToken ct)
    {
        var f = await FilesAsync(ct);
        if (!f.TryGetValue(path, out var s)) throw new FileNotFoundException(path);
        return s;
    }

    /// <summary>Range-reads the file into the sandbox in 4 MB pieces (resumable by size) and returns the local path.</summary>
    public async Task<string> LocalPathAsync(string path, CancellationToken ct)
    {
        if (fetched.TryGetValue(path, out var done)) return done;
        var local = Path.Combine(localDir, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await using (var fs = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            long pos = 0, total = long.MaxValue;
            while (pos < total)
            {
                var (data, t) = await client.GetRangeAsync(Id, path, pos, pos + MedusaV2Tools.RangeChunk - 1, ct);
                if (t is { } tt) total = tt;
                if (data.Length == 0) break;
                await fs.WriteAsync(data, ct);
                pos += data.Length;
            }
        }
        fetched[path] = local;
        return local;
    }

    public async ValueTask DisposeAsync()
    {
        try { await client.DeleteWorkspaceAsync(Id, CancellationToken.None); } catch { /* the phone may be gone */ }
        try { Directory.Delete(localDir, true); } catch { }
    }
}
