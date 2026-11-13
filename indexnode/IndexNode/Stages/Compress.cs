using System.Globalization;

namespace IndexNode;

public static class Compress
{
    public static List<string> BuildArgs(string input, string output, CompressSettings s, int threads) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats",
        "-i", input, "-map", "0:v:0", "-map", "0:a:0?", "-sn",
        "-c:v", "libsvtav1", "-crf", s.Crf.ToString(), "-preset", s.Preset.ToString(), "-svtav1-params", $"lp={Math.Max(1, threads)}",
        "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", $"{s.AudioKbps}k", "-movflags", "+faststart", output,
    ];

    /// <summary>Encodes, then verifies the duration (within 1 s) and a full decode.</summary>
    public static async Task RunAsync(Tools tools, string input, string output, CompressSettings s, int threads, double originalDuration, Action<string> progress, CancellationToken ct)
    {
        var ffmpeg = await tools.FfmpegAsync(ct);
        var args = BuildArgs(input, output, s, threads);
        var r = await Proc.RunAsync(ffmpeg, args, ct, onStdout: line =>
        {
            if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us) && originalDuration > 0)
                progress($"{Math.Min(99, us / 1e6 / originalDuration * 100):F0}%");
        });
        if (r.ExitCode != 0) throw new InvalidOperationException("ffmpeg compress failed: " + r.Stderr.Trim());
        await VerifyAsync(tools, output, originalDuration, ct);
    }

    public static async Task VerifyAsync(Tools tools, string file, double originalDuration, CancellationToken ct)
    {
        var d = await Frames.DurationAsync(tools, file, ct);
        if (Math.Abs(d - originalDuration) > 1.0) throw new InvalidOperationException($"compact duration {d:F2}s differs from original {originalDuration:F2}s by more than 1 s");
        var ffmpeg = await tools.FfmpegAsync(ct);
        var dec = await Proc.RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-i", file, "-f", "null", "-"], ct);
        if (dec.ExitCode != 0 || dec.Stderr.Trim().Length > 0) throw new InvalidOperationException("compact.mp4 does not decode cleanly: " + dec.Stderr.Trim());
    }
}
