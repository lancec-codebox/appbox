using Xunit;

namespace IndexNode.Tests;

public class FfmpegResolveTests
{
    private const string Essentials = " V....D libx264              libx264 H.264\n V....D libx265              libx265 HEVC\n";
    private const string Full = Essentials + " V....D libsvtav1             SVT-AV1\n";
    private const string Dec = " V....D av1                  Alliance for Open Media AV1\n V....D libdav1d             dav1d AV1\n";

    [Fact]
    public void A_build_without_libsvtav1_is_named_as_missing_for_the_frames_stage()
    {
        var m = Tools.MissingFfmpegCapabilities(["frames"], Essentials, Dec);
        Assert.Equal(["encoder libsvtav1"], m);
        Assert.Empty(Tools.MissingFfmpegCapabilities(["frames"], Full, Dec));
    }

    [Fact]
    public void Stages_that_do_not_need_the_encoder_do_not_ask_for_it_and_a_missing_decoder_is_reported()
    {
        Assert.Empty(Tools.MissingFfmpegCapabilities(["sscd"], Essentials, Dec));
        Assert.Equal(["an AV1 decoder (libdav1d)"], Tools.MissingFfmpegCapabilities(["sscd"], Full, " V....D h264  H.264\n"));
        Assert.Empty(Tools.MissingFfmpegCapabilities(["comments", "watch"], "", ""));
    }

    [Fact]
    public async Task The_tools_folder_ffmpeg_wins_over_PATH_and_a_bad_one_fails_fast_naming_its_path()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ffres-" + Guid.NewGuid().ToString("N")[..8]);
        string Fake(string dir, string encoders)
        {
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, "ffmpeg");
            File.WriteAllText(p, "#!/bin/sh\nif [ \"$2\" = \"-encoders\" ]; then printf '" + encoders.Replace("\n", "\\n") + "'; else printf '" + Dec.Replace("\n", "\\n") + "'; fi\n");
            File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Copy(p, Path.Combine(dir, "ffprobe"), true);
            return p;
        }
        var onPath = Fake(Path.Combine(root, "path"), Full);      // would be fine, but must not win
        var own = Fake(Path.Combine(root, "cfg", "tools"), Essentials);  // IndexNode's own, lacking libsvtav1
        var oldPath = Environment.GetEnvironmentVariable("PATH"); var oldCfg = Environment.GetEnvironmentVariable("INDEXNODE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "path") + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("INDEXNODE_CONFIG_DIR", Path.Combine(root, "cfg"));
            var tools = new Tools(new HttpClient(), new NodeConfig());
            Assert.Equal(own, await tools.FfmpegAsync(default));
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.CheckFfmpegAsync(["frames"], default));
            Assert.Contains(own, e.Message); Assert.Contains("libsvtav1", e.Message);
            await tools.CheckFfmpegAsync(["comments", "watch"], default);   // no ffmpeg stages: nothing checked
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); Environment.SetEnvironmentVariable("INDEXNODE_CONFIG_DIR", oldCfg); try { Directory.Delete(root, true); } catch { } }
    }
}
