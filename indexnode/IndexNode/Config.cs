using System.Text.Json;
using System.Text.Json.Serialization;

namespace IndexNode;

public sealed class NodeConfig
{
    public string? Sas { get; set; }
    public string? NodeId { get; set; }
    public string? NodeName { get; set; }
    public Dictionary<string, int>? Jobs { get; set; }
    public int? Threads { get; set; }
    public string? ModelUrl { get; set; }
    public string? ModelPath { get; set; }
    public BrowserConfig? Browser { get; set; }
    public YtDlpConfig? Ytdlp { get; set; }

    [JsonIgnore] public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string ConfigDir()
    {
        var env = Environment.GetEnvironmentVariable("INDEXNODE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(env)) return env;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "indexnode");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var home = string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg;
        return Path.Combine(home, "indexnode");
    }

    public static string ConfigPath() => Path.Combine(ConfigDir(), "config.json");
    public static string ToolsDir() => Path.Combine(ConfigDir(), "tools");

    public static NodeConfig Load()
    {
        var p = ConfigPath();
        var c = File.Exists(p) ? JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(p), Json) ?? new() : new();
        var envSas = Environment.GetEnvironmentVariable("INDEXNODE_SAS_URL");
        if (!string.IsNullOrWhiteSpace(envSas)) c.Sas = envSas.Trim();
        return c;
    }

    /// <summary>Writes the config user-only (mode 600 on Linux). Creates the node id on first save.</summary>
    public void Save()
    {
        var dir = ConfigDir();
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var p = ConfigPath();
        if (!File.Exists(p))
        {
            File.WriteAllText(p, "");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.WriteAllText(p, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Stable per install: name plus 6 random hex characters, stored in the config.</summary>
    public string EnsureNodeId()
    {
        if (!string.IsNullOrEmpty(NodeId)) return NodeId;
        NodeName ??= SanitizeName(Environment.MachineName);
        NodeId = NodeName + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        // persist without the env-supplied SAS leaking into the file
        var onDisk = File.Exists(ConfigPath()) ? JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(ConfigPath()), Json) ?? new() : new();
        onDisk.NodeId = NodeId; onDisk.NodeName = NodeName;
        onDisk.Save();
        return NodeId;
    }

    public static string SanitizeName(string s)
    {
        var r = new string(s.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        return r.Length == 0 ? "node" : r;
    }

    public static int PhysicalCores()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                var set = new HashSet<string>();
                string phys = "0";
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("physical id")) phys = line.Split(':')[1].Trim();
                    else if (line.StartsWith("core id")) set.Add(phys + "/" + line.Split(':')[1].Trim());
                }
                if (set.Count > 0) return set.Count;
            }
            else if (OperatingSystem.IsWindows())
            {
                var r = Proc.RunQuick("powershell", ["-NoProfile", "-Command", "(Get-CimInstance Win32_Processor | Measure-Object NumberOfCores -Sum).Sum"]);
                if (int.TryParse(r.Trim(), out var n) && n > 0) return n;
            }
        }
        catch { }
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    /// <summary>"process=2,download=1" or a bare number for every stage.</summary>
    public static Dictionary<string, int> ParseJobs(string? spec)
    {
        var d = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(spec)) return d;
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=');
            if (kv.Length == 1 && int.TryParse(kv[0], out var all)) { d["download"] = all; d["process"] = all; }
            else if (kv.Length == 2 && Protocol.Stages.Contains(kv[0]) && int.TryParse(kv[1], out var n) && n >= 0) d[kv[0]] = n;
            else throw new ArgumentException($"bad --jobs entry '{part}' (use e.g. process=2,download=1)");
        }
        return d;
    }

    public int JobsFor(string stage) => Jobs != null && Jobs.TryGetValue(stage, out var n) ? n : 1;
    public int EffectiveThreads => Threads is > 0 ? Threads.Value : PhysicalCores();
}

public sealed class YtDlpConfig
{
    /// <summary>A full command prefix, e.g. ["python", ".../yt-dlp.pyz"]. Null: the downloaded yt-dlp executable.</summary>
    public string[]? Command { get; set; }
}

public sealed class BrowserConfig
{
    public string? Exe { get; set; }
    public string? ProfileDir { get; set; }
    public int? Port { get; set; }

    public const int DefaultPort = 9333;

    public static string DefaultProfileDir() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "indexnode", "brave-profile")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "indexnode", "brave-profile");

    public static string[] DefaultExeCandidates() => OperatingSystem.IsWindows()
        ?
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
        ]
        : ["brave-browser"];

    public string ResolveExe()
    {
        if (!string.IsNullOrWhiteSpace(Exe)) return Exe;
        foreach (var c in DefaultExeCandidates())
            if (!Path.IsPathRooted(c) || File.Exists(c)) return c;
        return DefaultExeCandidates()[0];
    }

    public string ResolveProfileDir() => string.IsNullOrWhiteSpace(ProfileDir) ? DefaultProfileDir() : ProfileDir;
    public int ResolvePort() => Port ?? DefaultPort;
}
