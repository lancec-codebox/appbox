using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IndexNode;

public sealed record FrameEntry(double T, string Kind);

public static partial class FrameMath
{
    public const double SceneThreshold = 0.3;
    public const int DecodeWidth = 288;

    /// <summary>Splits [0,duration] into up to <paramref name="segments"/> equal parts, none shorter than minSegment seconds.</summary>
    public static List<(double Start, double End)> Plan(double duration, int segments, double minSegment = 10)
    {
        int n = Math.Clamp(segments, 1, Math.Max(1, (int)Math.Floor(duration / minSegment)));
        var list = new List<(double, double)>();
        for (int i = 0; i < n; i++)
            list.Add((duration * i / n, i == n - 1 ? duration : duration * (i + 1) / n));
        return list;
    }

    /// <summary>A frame's time is the segment start plus its pts_time.</summary>
    public static double AbsoluteTime(double segmentStart, double ptsTime) => segmentStart + ptsTime;

    [GeneratedRegex(@"pts_time:(?<t>[0-9.eE+-]+)")]
    private static partial Regex PtsRegex();
    [GeneratedRegex(@"lavfi\.scene_score=(?<s>[0-9.eE+-]+)")]
    private static partial Regex SceneRegex();

    /// <summary>Parses the output of the ffmpeg metadata=print filter: pts_time and (when present) the scene score per frame.</summary>
    public static List<(double Pts, double? Scene)> ParseMetadata(string text)
    {
        var list = new List<(double, double?)>();
        double? pts = null; double? scene = null;
        foreach (var line in text.Split('\n'))
        {
            var m = PtsRegex().Match(line);
            if (m.Success)
            {
                if (pts != null) list.Add((pts.Value, scene));
                pts = double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture); scene = null;
                continue;
            }
            var s = SceneRegex().Match(line);
            if (s.Success) scene = double.Parse(s.Groups["s"].Value, CultureInfo.InvariantCulture);
        }
        if (pts != null) list.Add((pts.Value, scene));
        return list;
    }

    public static string Kind(double? scene) => scene is > SceneThreshold ? "scene" : "tick";

    public static string SelectFilter() =>
        $"scale={DecodeWidth}:-2,select='gt(gt(scene,{SceneThreshold.ToString(CultureInfo.InvariantCulture)})+isnan(prev_selected_t)+gte(t-prev_selected_t,1),0)'";
}

public static class Frames
{
    /// <summary>
    /// One ffmpeg per segment (the caller passes budget ÷ running jobs), each with its own -ss/-to. Frames are written, in time order, to
    /// <paramref name="framesDir"/> as 000000.jpg, ... and the time list is returned.
    /// </summary>
    public static async Task<List<FrameEntry>> ExtractAsync(Tools tools, string video, string workDir, string framesDir, int segments, double duration, Action<string> progress, CancellationToken ct)
    {
        var ffmpeg = await tools.FfmpegAsync(ct);
        var plan = FrameMath.Plan(duration, segments);
        Directory.CreateDirectory(framesDir);
        int done = 0;
        var results = new List<(double T, string Kind, string File)>[plan.Count];
        var absVideo = Path.GetFullPath(video);
        await Task.WhenAll(plan.Select(async (seg, i) =>
        {
            var dir = Path.Combine(workDir, $"seg{i}");
            Directory.CreateDirectory(dir);
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1",
                "-ss", seg.Start.ToString("F3", CultureInfo.InvariantCulture), "-to", seg.End.ToString("F3", CultureInfo.InvariantCulture),
                "-i", absVideo, "-an", "-sn", "-filter_threads", "1",
                "-vf", FrameMath.SelectFilter() + $",metadata=mode=print:file=meta{i}.txt",
                "-fps_mode", "vfr", "-q:v", "3", Path.Combine($"seg{i}", "%06d.jpg"),
            };
            var r = await Proc.RunAsync(ffmpeg, args, ct, workDir: workDir);
            if (r.ExitCode != 0) throw new InvalidOperationException($"ffmpeg frames segment {i} failed: {r.Stderr.Trim()}");
            var meta = FrameMath.ParseMetadata(File.ReadAllText(Path.Combine(workDir, $"meta{i}.txt")));
            var files = Directory.GetFiles(dir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            if (files.Length != meta.Count) throw new InvalidOperationException($"segment {i}: {files.Length} images but {meta.Count} metadata entries");
            results[i] = meta.Select((m, k) => (FrameMath.AbsoluteTime(seg.Start, m.Pts), FrameMath.Kind(m.Scene), files[k])).ToList();
            progress($"{Interlocked.Increment(ref done)}/{plan.Count} segments");
        }));

        var all = results.SelectMany(x => x).OrderBy(x => x.T).ToList();
        var list = new List<FrameEntry>(all.Count);
        for (int k = 0; k < all.Count; k++)
        {
            File.Move(all[k].File, Path.Combine(framesDir, $"{k:D6}.jpg"), true);
            list.Add(new FrameEntry(Math.Round(all[k].T, 3), all[k].Kind));
        }
        return list;
    }

    public static byte[] ToJson(IEnumerable<FrameEntry> frames) =>
        JsonSerializer.SerializeToUtf8Bytes(frames.Select(f => new { t = f.T, kind = f.Kind }));

    public static async Task<int> HeightAsync(Tools tools, string file, CancellationToken ct)
    {
        var probe = await tools.FfprobeAsync(ct);
        var r = await Proc.RunAsync(probe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=height", "-of", "csv=p=0", file], ct);
        return int.TryParse(r.Stdout.Trim(), out var h) ? h : 0;
    }

    public static async Task<double> DurationAsync(Tools tools, string file, CancellationToken ct)
    {
        var probe = await tools.FfprobeAsync(ct);
        var r = await Proc.RunAsync(probe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file], ct);
        if (r.ExitCode != 0 || !double.TryParse(r.Stdout.Trim(), CultureInfo.InvariantCulture, out var d)) throw new InvalidOperationException("ffprobe could not read duration: " + r.Stderr.Trim());
        return d;
    }
}
