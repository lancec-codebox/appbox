namespace IndexNode.Tests;

public class LauncherTests
{
    [Fact]
    public void Executable_only_prefix_puts_args_after_it()
    {
        var (file, args) = YtDlpLauncher.Compose(["/tools/yt-dlp"], ["--no-playlist", "https://x"]);
        Assert.Equal("/tools/yt-dlp", file);
        Assert.Equal(["--no-playlist", "https://x"], args);
    }

    [Fact]
    public void Zipapp_prefix_runs_through_python()
    {
        var (file, args) = YtDlpLauncher.Compose(["python", "C:/Users/u/AppData/Local/indexnode/tools/yt-dlp.pyz"], ["-f", "b", "https://x"]);
        Assert.Equal("python", file);
        Assert.Equal(["C:/Users/u/AppData/Local/indexnode/tools/yt-dlp.pyz", "-f", "b", "https://x"], args);
    }

    [Fact]
    public void Empty_prefix_is_an_error() => Assert.Throws<ArgumentException>(() => YtDlpLauncher.Compose([], ["x"]));

    [Theory]
    [InlineData("An error occurred trying to start process: Application Control policy has blocked this file", 0, true)]
    [InlineData("Failed to load Python DLL 'C:\\Temp\\_MEI1\\python310.dll'", 1, true)]
    [InlineData("", 4551, true)]
    [InlineData("", -1073741515, true)]
    [InlineData("ERROR: Video unavailable", 1, false)]
    [InlineData("", 1, false)]
    public void Detects_a_policy_blocked_start(string text, int code, bool blocked) => Assert.Equal(blocked, YtDlpLauncher.LooksBlocked(text, code));

    [Fact]
    public void Blocked_hint_names_the_zipapp_command() => Assert.Contains("indexnode tools install --ytdlp-zipapp", Tools.BlockedHint);

    [Fact]
    public async Task Configured_command_prefix_is_used_and_a_missing_default_exe_suggests_the_zipapp()
    {
        // configured prefix: bash <script> <args...>
        var dir = Path.Combine(Path.GetTempPath(), "launcher-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "fake.sh");
        File.WriteAllText(script, "echo \"args: $@\"\n");
        // Windows has no bash for a Windows-path script; cmd echoes the same line there.
        string[] prefix = OperatingSystem.IsWindows() ? ["cmd", "/c", "echo args:"] : ["bash", script];
        var tools = new Tools(new HttpClient(), new NodeConfig { Ytdlp = new YtDlpConfig { Command = prefix } });
        var r = await tools.RunYtDlpAsync(["--version", "u"], default);
        Assert.Equal("args: --version u", r.Stdout.Trim());

        // a configured command that cannot start: a clear message with the fix, not a bare Win32 error
        var broken = new Tools(new HttpClient(), new NodeConfig { Ytdlp = new YtDlpConfig { Command = ["/nonexistent/python", "x.pyz"] } });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => broken.RunYtDlpAsync(["--version"], default));
        Assert.Contains("indexnode tools install --ytdlp-zipapp", ex.Message);
    }

    [Fact]
    public void Config_command_parses_json_array_or_single_path()
    {
        Assert.Equal(["python", "C:/t/yt-dlp.pyz"], Commands.ParseCommand("[\"python\",\"C:/t/yt-dlp.pyz\"]"));
        Assert.Equal(["C:/t/yt-dlp.exe"], Commands.ParseCommand("C:/t/yt-dlp.exe"));
    }

    [Fact]
    public void Config_round_trips_browser_and_ytdlp()
    {
        var c = new NodeConfig { Ytdlp = new YtDlpConfig { Command = ["python", "p.pyz"] }, Browser = new BrowserConfig { Port = 9444, ProfileDir = "/p" } };
        var back = System.Text.Json.JsonSerializer.Deserialize<NodeConfig>(System.Text.Json.JsonSerializer.Serialize(c, NodeConfig.Json), NodeConfig.Json)!;
        Assert.Equal(["python", "p.pyz"], back.Ytdlp!.Command);
        Assert.Equal(9444, back.Browser!.Port);
    }
}
