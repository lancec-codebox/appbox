using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


namespace IndexNode;

public sealed record ProcResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// A scratch folder a tool run works in. Tool arguments name files relative to it (the run's working directory is the workspace).
/// Desktop: a local folder. Android: a Medusa /v2 workspace; files are copied into the app sandbox only when <see cref="LocalPathAsync"/> asks.
/// </summary>
public interface IToolWorkspace : IAsyncDisposable
{
    string Name { get; }
    Task<bool> ExistsAsync(string path, CancellationToken ct);
    /// <summary>Files directly in <paramref name="dir"/> ("" = the root) whose name matches the glob (* and ?), as paths relative to the workspace.</summary>
    Task<IReadOnlyList<string>> ListAsync(string dir, string glob, CancellationToken ct);
    Task<string> ReadTextAsync(string path, CancellationToken ct);
    Task WriteTextAsync(string path, string text, CancellationToken ct);
    /// <summary>Sum of the sizes of all files in the workspace.</summary>
    Task<long> TotalBytesAsync(CancellationToken ct);
    Task<long> SizeAsync(string path, CancellationToken ct);
    /// <summary>A local file with the content, for uploads and reads (desktop: the file itself; Android: fetched with Range reads into the sandbox). It lives until the workspace is disposed.</summary>
    Task<string> LocalPathAsync(string path, CancellationToken ct);
}

/// <summary>What the node logic may ask of yt-dlp, ffmpeg and ffprobe. Desktop starts local processes; the Android host asks Medusa.</summary>
public interface ITools
{
    Task<IToolWorkspace> CreateWorkspaceAsync(string name, CancellationToken ct);

    /// <summary>
    /// Runs "yt-dlp", "ffmpeg" or "ffprobe" with the workspace as the working directory. For yt-dlp the implementation adds the JavaScript runtime,
    /// the ffmpeg location and the cookies the node is configured with. Cancelling stops the tool. <paramref name="onStdout"/> sees each stdout line (progress).
    /// </summary>
    Task<ProcResult> RunAsync(IToolWorkspace ws, string tool, IReadOnlyList<string> args, CancellationToken ct, Action<string>? onStdout = null);

    /// <summary>yt-dlp without a workspace (listings, availability): stdout and stderr are the result.</summary>
    Task<ProcResult> RunYtDlpAsync(IEnumerable<string> args, CancellationToken ct, Action<string>? onStdout = null);
}

public static class ToolsProbe
{
    public static async Task<double> DurationAsync(ITools tools, IToolWorkspace ws, string file, CancellationToken ct)
    {
        var r = await tools.RunAsync(ws, "ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file], ct);
        if (r.ExitCode != 0 || !double.TryParse(r.Stdout.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) throw new InvalidOperationException("ffprobe could not read duration: " + r.Stderr.Trim());
        return d;
    }

    public static async Task<int> HeightAsync(ITools tools, IToolWorkspace ws, string file, CancellationToken ct)
    {
        var r = await tools.RunAsync(ws, "ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=height", "-of", "csv=p=0", file], ct);
        return int.TryParse(r.Stdout.Trim(), out var h) ? h : 0;
    }
}
