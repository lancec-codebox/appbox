using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace IndexNode;

/// <summary>
/// `indexnode ensure <version|latest|current> [--variant cpu|gpu|fdd] [--dir <cache>]`: makes that IndexNode release available on this machine and prints
/// the command to run it (stdout, one line), so a script can do `IX=$(indexnode ensure 0.7.0); $IX stages run ...`. Downloads the release asset once
/// (public GitHub releases, no credentials), checks its .sha256, caches it. The running build is reused when it already is that version and variant.
/// </summary>
public static class EnsureCommand
{
    const string Repo = "lancec-codebox/appbox";

    public static async Task<int> RunAsync(Args a)
    {
        var want = a.Positional.FirstOrDefault() ?? throw new ArgumentException("ensure <version|latest|current> [--variant cpu|gpu|fdd] [--dir <cache>]");
        var mine = typeof(EnsureCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        var variant = a.Get("variant") ?? (HasCuda() ? "gpu" : "cpu");
        var ct = CancellationToken.None;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("indexnode-ensure");

        var version = want switch { "current" => mine, "latest" => await LatestAsync(http, ct), _ => want.TrimStart('v') };
        bool dev = version == "dev"; // the indexnode-dev prerelease: rebuilt in place, so its checksum decides whether the cache is current
        if (version == mine && variant == (HasCuda() ? "gpu" : "cpu")) { Console.WriteLine(SelfCommand()); return 0; }

        bool win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var rid = (win ? "win" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux") + "-" + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");
        var suffix = variant switch { "cpu" => "", "fdd" => "-fdd", "gpu" => "-gpu", _ => throw new ArgumentException($"unknown variant '{variant}' (cpu, gpu, fdd)") };
        if (variant == "gpu" && win) throw new ArgumentException("the gpu variant is linux-x64 only");
        var asset = $"indexnode-{version}-{rid}{suffix}{(win ? ".exe" : "")}";
        var dir = a.Get("dir") ?? Path.Combine(Environment.GetEnvironmentVariable("INDEXNODE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "indexnode"), "versions", $"{version}{suffix}");
        var exe = Path.Combine(dir, win ? "indexnode.exe" : "indexnode");
        if (!dev && File.Exists(exe) && File.Exists(exe + ".ok")) { Console.WriteLine(exe); return 0; }

        Directory.CreateDirectory(dir);
        var baseUrl = $"https://github.com/{Repo}/releases/download/{(dev ? "indexnode-dev" : $"indexnode-v{version}")}/{asset}";
        
        var sumResp = await http.GetAsync(baseUrl + ".sha256", ct);
        if (!sumResp.IsSuccessStatusCode) { Console.Error.WriteLine($"ensure: release indexnode-v{version} has no {asset} (HTTP {(int)sumResp.StatusCode})"); return 3; }
        var expected = (await sumResp.Content.ReadAsStringAsync(ct)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim().ToLowerInvariant();
        if (dev && File.Exists(exe) && File.Exists(exe + ".ok") && (await File.ReadAllTextAsync(exe + ".ok", ct)).Trim() == expected) { Console.WriteLine(exe); return 0; }
        var tmp = exe + ".part";
        Console.Error.WriteLine($"ensure: downloading {asset}");
        await using (var s = await http.GetStreamAsync(baseUrl, ct)) await using (var f = File.Create(tmp)) await s.CopyToAsync(f, ct);
        string actual; await using (var f = File.OpenRead(tmp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (actual != expected) { File.Delete(tmp); Console.Error.WriteLine($"ensure: {asset} sha256 {actual} != published {expected}"); return 2; }
        File.Move(tmp, exe, true);
        if (!win) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        await File.WriteAllTextAsync(exe + ".ok", expected, ct);
        Console.Error.WriteLine($"ensure: {asset} verified ({new FileInfo(exe).Length / 1048576} MB)");
        Console.WriteLine(exe);
        return 0;
    }

    /// <summary>The command that runs this very build: its own path, or `dotnet <dll>` when it runs as a framework-dependent dll (the GPU image).</summary>
    static string SelfCommand()
    {
        var p = Environment.ProcessPath ?? "indexnode";
        var name = Path.GetFileNameWithoutExtension(p);
        return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? $"{p} {Path.Combine(AppContext.BaseDirectory, "indexnode.dll")}" : p;
    }

    static bool HasCuda()
    {
        try { return Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider"); }
        catch { return false; }
    }

    /// <summary>The highest indexnode-v* release (the repo's "latest" release is Medusa's, so it can't be used).</summary>
    static async Task<string> LatestAsync(HttpClient http, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=50", ct));
        var versions = doc.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean())
            .Select(r => r.GetProperty("tag_name").GetString() ?? "").Where(t => t.StartsWith("indexnode-v", StringComparison.Ordinal))
            .Select(t => t["indexnode-v".Length..]).Where(v => Version.TryParse(v, out _)).ToList();
        if (versions.Count == 0) throw new InvalidOperationException("no indexnode releases found");
        return versions.OrderByDescending(Version.Parse).First();
    }
}
