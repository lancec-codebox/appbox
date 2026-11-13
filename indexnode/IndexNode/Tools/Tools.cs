using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace IndexNode;

/// <summary>Finds yt-dlp, ffmpeg and ffprobe on PATH, or downloads pinned releases into the tools folder beside the config.</summary>
public sealed class Tools(HttpClient http, NodeConfig? config = null)
{
    public const string YtDlpVersion = "2026.10.26";
    private string? ytdlp, ffmpeg, ffprobe;
    private readonly SemaphoreSlim gate = new(1, 1);

    private static string Exe(string n) => OperatingSystem.IsWindows() ? n + ".exe" : n;

    public static string? OnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var f = Path.Combine(dir, Exe(name)); if (File.Exists(f)) return f; } catch { }
        }
        return null;
    }

    private static string? InTools(string name) { var f = Path.Combine(NodeConfig.ToolsDir(), Exe(name)); return File.Exists(f) ? f : null; }

    public const string BlockedHint = "yt-dlp could not start (blocked by Windows App Control / policy?). Run: indexnode tools install --ytdlp-zipapp  (uses the yt-dlp zipapp through your Python)";

    /// <summary>The configured command prefix (ytdlp.command), or the yt-dlp executable.</summary>
    public async Task<string[]> YtDlpCommandAsync(CancellationToken ct) =>
        config?.Ytdlp?.Command is { Length: > 0 } c ? c : [await YtDlpAsync(ct)];

    /// <summary>Runs yt-dlp through the launcher; a start failure that looks like a policy block says so and names the fix.</summary>
    public async Task<ProcResult> RunYtDlpAsync(IEnumerable<string> args, CancellationToken ct, Action<string>? onStdout = null)
    {
        var (file, full) = YtDlpLauncher.Compose(await YtDlpCommandAsync(ct), args);
        ProcResult r;
        try { r = await Proc.RunAsync(file, full, ct, onStdout); }
        catch (System.ComponentModel.Win32Exception e) when (YtDlpLauncher.LooksBlocked(e.Message, e.NativeErrorCode)) { throw new InvalidOperationException(BlockedHint + " [" + e.Message + "]"); }
        catch (System.ComponentModel.Win32Exception e) when (config?.Ytdlp?.Command is null) { throw new InvalidOperationException($"yt-dlp could not start ({e.Message}). If App Control blocks it, run: indexnode tools install --ytdlp-zipapp"); }
        catch (System.ComponentModel.Win32Exception e) { throw new InvalidOperationException($"the configured ytdlp.command could not start ({e.Message}); check it, or run: indexnode tools install --ytdlp-zipapp"); }
        if (r.ExitCode != 0 && YtDlpLauncher.LooksBlocked(r.Stderr, r.ExitCode)) throw new InvalidOperationException(BlockedHint);
        return r;
    }

    /// <summary>Downloads the official yt-dlp zipapp (release asset "yt-dlp", no extension) and returns the command to store.</summary>
    public async Task<string[]> InstallYtDlpZipappAsync(CancellationToken ct)
    {
        var dest = Path.Combine(NodeConfig.ToolsDir(), "yt-dlp.pyz");
        await Download($"https://github.com/yt-dlp/yt-dlp/releases/download/{YtDlpVersion}/yt-dlp", dest, ct);
        return [OnPath("python") ?? OnPath("python3") ?? (OperatingSystem.IsWindows() ? "python" : "python3"), dest];
    }

    public async Task<string> YtDlpAsync(CancellationToken ct)
    {
        if (ytdlp != null) return ytdlp;
        await gate.WaitAsync(ct);
        try
        {
            ytdlp ??= OnPath("yt-dlp") ?? InTools("yt-dlp") ?? await InstallYtDlpAsync(ct);
            return ytdlp;
        }
        finally { gate.Release(); }
    }

    public async Task<string> FfmpegAsync(CancellationToken ct) { await EnsureFfAsync(ct); return ffmpeg!; }
    public async Task<string> FfprobeAsync(CancellationToken ct) { await EnsureFfAsync(ct); return ffprobe!; }

    private async Task EnsureFfAsync(CancellationToken ct)
    {
        if (ffmpeg != null && ffprobe != null) return;
        await gate.WaitAsync(ct);
        try
        {
            ffmpeg = OnPath("ffmpeg") ?? InTools("ffmpeg");
            ffprobe = OnPath("ffprobe") ?? InTools("ffprobe");
            if (ffmpeg == null || ffprobe == null) { await InstallFfmpegAsync(ct); ffmpeg = InTools("ffmpeg"); ffprobe = InTools("ffprobe"); }
            if (ffmpeg == null || ffprobe == null) throw new InvalidOperationException("ffmpeg/ffprobe not found and could not be installed");
        }
        finally { gate.Release(); }
    }

    private async Task Download(string url, string dest, CancellationToken ct)
    {
        Log.Info($"tools: downloading {url}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        await using var fs = File.Create(dest);
        await r.Content.CopyToAsync(fs, ct);
    }

    private async Task<string> InstallYtDlpAsync(CancellationToken ct)
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        var asset = OperatingSystem.IsWindows() ? "yt-dlp.exe" : OperatingSystem.IsMacOS() ? "yt-dlp_macos" : arm ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";
        var dest = Path.Combine(NodeConfig.ToolsDir(), Exe("yt-dlp"));
        await Download($"https://github.com/yt-dlp/yt-dlp/releases/download/{YtDlpVersion}/{asset}", dest, ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dest, (UnixFileMode)0b111_101_101);
        return dest;
    }

    private async Task InstallFfmpegAsync(CancellationToken ct)
    {
        var tools = NodeConfig.ToolsDir();
        Directory.CreateDirectory(tools);
        var tmp = Path.Combine(tools, "dl");
        Directory.CreateDirectory(tmp);
        if (OperatingSystem.IsWindows())
        {
            var zip = Path.Combine(tmp, "ffmpeg.zip");
            await Download("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", zip, ct);
            using var za = ZipFile.OpenRead(zip);
            foreach (var e in za.Entries.Where(e => e.FullName.EndsWith("/bin/ffmpeg.exe") || e.FullName.EndsWith("/bin/ffprobe.exe")))
                e.ExtractToFile(Path.Combine(tools, e.Name), true);
        }
        else
        {
            var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            var archive = Path.Combine(tmp, "ffmpeg.tar.xz");
            // BtbN's Linux build, because the johnvansickle static builds have no libsvtav1
            await Download($"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-{(arm ? "linuxarm64" : "linux64")}-gpl.tar.xz", archive, ct);
            var r = await Proc.RunAsync("tar", ["-xJf", archive, "-C", tmp, "--wildcards", "*/bin/ffmpeg", "*/bin/ffprobe"], ct);
            if (r.ExitCode != 0) throw new InvalidOperationException("could not unpack ffmpeg: " + r.Stderr);
            foreach (var f in Directory.GetFiles(tmp, "ff*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) is "ffmpeg" or "ffprobe"))
            {
                var dest = Path.Combine(tools, Path.GetFileName(f));
                File.Move(f, dest, true);
                File.SetUnixFileMode(dest, (UnixFileMode)0b111_101_101);
            }
        }
        try { Directory.Delete(tmp, true); } catch { }
    }
}

public static class YtDlpLauncher
{
    /// <summary>The first element of the prefix is the program; the rest of the prefix goes before the yt-dlp arguments.</summary>
    public static (string File, string[] Args) Compose(string[] prefix, IEnumerable<string> args)
    {
        if (prefix.Length == 0) throw new ArgumentException("empty yt-dlp command");
        return (prefix[0], prefix.Skip(1).Concat(args).ToArray());
    }

    /// <summary>Windows 4551 (policy block), STATUS_DLL_NOT_FOUND-style exits, or text naming the block.</summary>
    public static bool LooksBlocked(string text, int code) =>
        code is 4551 or -1073741515 or -1073740791 or unchecked((int)0xC0000022)
        || System.Text.RegularExpressions.Regex.IsMatch(text ?? "", @"(?i)blocked by (policy|your organization)|application control|App Control|Failed to load Python DLL|python3\d+\.dll");
}
