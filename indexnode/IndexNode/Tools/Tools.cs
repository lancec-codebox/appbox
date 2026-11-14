using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace IndexNode;

/// <summary>Finds yt-dlp, ffmpeg and ffprobe on PATH, or downloads pinned releases into the tools folder beside the config.</summary>
public sealed class Tools(HttpClient http, NodeConfig? config = null)
{
    public const string YtDlpVersion = "2026.10.26";

    /// <summary>sha256 of the yt-dlp release assets of <see cref="YtDlpVersion"/> (from its SHA2-256SUMS).</summary>
    public static readonly Dictionary<string, string> YtDlpSha256 = new()
    {
        ["yt-dlp"] = "e51e69e89489f199530f13d32a4342da3ddb2cf34842f364603a066db05afbf3",
        ["yt-dlp.exe"] = "f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d",
        ["yt-dlp_linux"] = "767ba9dce7052aa196f8b12652f433d76de128ba168966fa84b6394d5e6c6795",
        ["yt-dlp_linux_aarch64"] = "e87e9533e89fe5889abaa21f5114dfe7756d6aa6219b83551add04d48b18e483",
        ["yt-dlp_macos"] = "0f922c15ccc0b83eefa1c8bc9104e434318c4bf16e408e1ee93b7745a6efe0aa",
    };

    /// <summary>The pinned ffmpeg build (BtbN autobuild of the n9.0 branch, GPL, with libsvtav1): one asset per platform, with its sha256.</summary>
    public const string FfmpegVersion = "n9.0.2-57-gd838f9f9ae";
    public sealed record FfmpegAsset(string Url, string Sha256, bool Zip);
    private const string FfmpegBase = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-11-13-12-43/ffmpeg-n9.0.2-57-gd838f9f9ae-";

    public static FfmpegAsset FfmpegFor(bool windows, bool arm) => windows
        ? new(FfmpegBase + "win64-gpl-9.0.zip", "ae2b8d3f88cf9a1dd22d4e29a27d933dc51abff28a39172b8282488a3030b3e3", true)
        : arm ? new(FfmpegBase + "linuxarm64-gpl-9.0.tar.xz", "ad525931f3a90c185c41fe6c978db125324658f12773e7c547f25911b8ce2786", false)
              : new(FfmpegBase + "linux64-gpl-9.0.tar.xz", "01f1a5a6721de2d4e2e49dbadf6eb30bb750934429f685f6f5a389168ac27e23", false);

    /// <summary>Set by tests: replaces the yt-dlp process (same arguments as the real run, after the JS-runtime flags).</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Action<string>?, Task<ProcResult>>? YtDlpOverride { get; set; }

    /// <summary>
    /// yt-dlp needs a JavaScript runtime for YouTube. The config's ytdlp.jsRuntime wins ("node", "deno:/path/to/deno", ...);
    /// otherwise node on PATH, else deno. Null when none is available.
    /// </summary>
    public static string? ChooseJsRuntime(string? configured, Func<string, string?> onPath)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? null : configured.Trim();
        if (onPath("node") is { } n) return "node:" + n;
        if (onPath("deno") is { } d) return "deno:" + d;
        return null;
    }

    public string? JsRuntime => ChooseJsRuntime(config?.Ytdlp?.JsRuntime, OnPath);

    /// <summary>One line per tool for `indexnode tools status`.</summary>
    public IReadOnlyList<string> Status()
    {
        var lines = new List<string>();
        var cmd = config?.Ytdlp?.Command;
        lines.Add("yt-dlp:     " + (cmd is { Length: > 0 } ? string.Join(' ', cmd) + " (configured)" : OnPath("yt-dlp") ?? InTools("yt-dlp") ?? "not installed (downloaded on first use)") + $"  [pinned {YtDlpVersion}]");
        lines.Add("js runtime: " + (JsRuntime is { } js ? js + (string.IsNullOrWhiteSpace(config?.Ytdlp?.JsRuntime) ? " (found on PATH)" : " (configured)") : "NONE: YouTube downloads need node or deno on PATH, or: indexnode config set ytdlp.js-runtime node:/path/to/node"));
        lines.Add("ffmpeg:      " + (OnPath("ffmpeg") ?? InTools("ffmpeg") ?? $"not installed (indexnode tools install --ffmpeg fetches the pinned build {FfmpegVersion})"));
        lines.Add("ffprobe:     " + (OnPath("ffprobe") ?? InTools("ffprobe") ?? "not installed"));
        return lines;
    }
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
        var withJs = JsRuntime is { } js ? ["--js-runtimes", js, ..args] : args.ToArray();
        if (YtDlpOverride != null) return await YtDlpOverride(withJs, ct, onStdout);
        var (file, full) = YtDlpLauncher.Compose(await YtDlpCommandAsync(ct), withJs);
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

    private async Task Download(string url, string dest, CancellationToken ct, string? sha256 = null)
    {
        Log.Info($"tools: downloading {url}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (sha256 != null) { await Fetch.VerifiedAsync(http, url, dest, sha256, ct); return; }
        using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        await using var fs = File.Create(dest);
        await r.Content.CopyToAsync(fs, ct);
        Traffic.Current.Add(Traffic.OtherIn, fs.Length);
    }

    /// <summary>Downloads the pinned yt-dlp (sha256-checked) into the tools folder, unless it is already there.</summary>
    public async Task<string> InstallYtDlpPinnedAsync(CancellationToken ct) => InTools("yt-dlp") ?? await InstallYtDlpAsync(ct);

    private async Task<string> InstallYtDlpAsync(CancellationToken ct)
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        var asset = OperatingSystem.IsWindows() ? "yt-dlp.exe" : OperatingSystem.IsMacOS() ? "yt-dlp_macos" : arm ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";
        var dest = Path.Combine(NodeConfig.ToolsDir(), Exe("yt-dlp"));
        await Download($"https://github.com/yt-dlp/yt-dlp/releases/download/{YtDlpVersion}/{asset}", dest, ct, YtDlpSha256.GetValueOrDefault(asset));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dest, (UnixFileMode)0b111_101_101);
        return dest;
    }

    public Task InstallFfmpegPinnedAsync(CancellationToken ct) => InstallFfmpegAsync(ct);

    private async Task InstallFfmpegAsync(CancellationToken ct)
    {
        var asset = FfmpegFor(OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture == Architecture.Arm64);
        var tools = NodeConfig.ToolsDir();
        Directory.CreateDirectory(tools);
        var tmp = Path.Combine(tools, "dl");
        Directory.CreateDirectory(tmp);
        if (OperatingSystem.IsWindows())
        {
            var zip = Path.Combine(tmp, "ffmpeg.zip");
            await Download(asset.Url, zip, ct, asset.Sha256);
            using var za = ZipFile.OpenRead(zip);
            foreach (var e in za.Entries.Where(e => e.FullName.EndsWith("/bin/ffmpeg.exe") || e.FullName.EndsWith("/bin/ffprobe.exe")))
                e.ExtractToFile(Path.Combine(tools, e.Name), true);
        }
        else
        {
            var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            var archive = Path.Combine(tmp, "ffmpeg.tar.xz");
            // BtbN's Linux build, because the johnvansickle static builds have no libsvtav1
            await Download(asset.Url, archive, ct, asset.Sha256);
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
