using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IndexNode;

public sealed record FrameEntry(double T, string Kind);

public static partial class FrameMath
{
    public const double SceneThreshold = 0.3;
    public const int DecodeWidth = 360; // short side of the stored frames (FrameChunks.ShortSide)

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
        $"scale=w='if(lt(iw,ih),{DecodeWidth},-2)':h='if(lt(iw,ih),-2,{DecodeWidth})',select='gt(gt(scene,{SceneThreshold.ToString(CultureInfo.InvariantCulture)})+isnan(prev_selected_t)+gte(t-prev_selected_t,1),0)'";
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
                "-fps_mode", "vfr", "-q:v", "4", Path.Combine($"seg{i}", "%06d.jpg"),
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

    public sealed record Produced(int Sampled, int Kept, int Keyframes, long FrameBytes, long KeyframeBytes);

    /// <summary>The stored size: short side 360, long side even.</summary>
    public static (int W, int H) StoredSize(int w, int h)
    {
        int S = FrameChunks.ShortSide; int L = (int)Math.Round((double)Math.Max(w, h) * S / Math.Min(w, h) / 2) * 2;
        return w >= h ? (L, S) : (S, L);
    }

    /// <summary>
    /// Decodes the video once. Sampled frames: 1 fps, 360 px short side, colour-grid pruned against the kept frames of the last 30 s, then written as one AV1 file per 10 minutes
    /// (frames/NNNN.mp4 + .json with each frame's pts and spans). Keyframes: every key frame at full size, all-intra AV1 (keyframes/NNNN.mp4 + .json). Everything goes to <paramref name="put"/>
    /// (name relative to the job folder, bytes, content type); the manifests are put last.
    /// </summary>
    /// <summary>Reads the raw rgb file frame by frame (into <paramref name="buf"/>, whose length is the frame size) and calls <paramref name="onFrame"/> with the frame index until it returns false or the file ends. The file is closed and then deleted: Windows refuses to delete an open file.</summary>
    public static async Task ConsumeRawAsync(string file, byte[] buf, Func<int, Task<bool>> onFrame, CancellationToken ct)
    {
        await using (var fs = File.OpenRead(file))
        {
            for (int i = 0; ; i++)
            {
                int off = 0; while (off < buf.Length) { int r = await fs.ReadAsync(buf.AsMemory(off, buf.Length - off), ct); if (r == 0) break; off += r; }
                if (off < buf.Length) break;
                if (!await onFrame(i)) break;
            }
        }
        File.Delete(file);
    }

    public static async Task<Produced> ProduceAsync(Tools tools, string video, string work, FrameSettings st, int segments, double duration, Action<string> progress, Func<string, byte[], string, Task> put, CancellationToken ct)
    {
        var ffmpeg = await tools.FfmpegAsync(ct);
        var probe = await tools.FfprobeAsync(ct);
        var pr = await Proc.RunAsync(probe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", video], ct);
        var wh = pr.Stdout.Trim().Split('x'); var (W, H) = StoredSize(int.Parse(wh[0]), int.Parse(wh[1]));
        int nChunks = Math.Max(1, (int)Math.Ceiling(duration / FrameChunks.ChunkSeconds - 1e-9)); int frameBytes = W * H * 3;
        var pruner = new FramePruner(st); var index = new List<(string Chunk, double Start, List<ChunkFrame> Frames, long Bytes)>();
        var kept = new List<ChunkFrame>(); // every kept frame, to extend its spans later (frames of earlier chunks included)
        int sampled = 0, keptN = 0; long fb = 0; var absVideo = Path.GetFullPath(video);
        for (int c = 0; c < nChunks; c++)
        {
            int s0 = (int)(c * FrameChunks.ChunkSeconds), s1 = (int)Math.Min(Math.Ceiling(duration), (c + 1) * FrameChunks.ChunkSeconds);
            int parts = Math.Clamp(segments, 1, Math.Max(1, (s1 - s0) / 10)); var files = new string[parts];
            await Task.WhenAll(Enumerable.Range(0, parts).Select(async k =>
            {
                int a0 = s0 + (s1 - s0) * k / parts, a1 = s0 + (s1 - s0) * (k + 1) / parts; files[k] = Path.Combine(work, $"raw{c}_{k}.rgb");
                var r = await Proc.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1", "-ss", a0.ToString(CultureInfo.InvariantCulture), "-t", (a1 - a0).ToString(CultureInfo.InvariantCulture), "-i", absVideo, "-an", "-sn", "-filter_threads", "1",
                    "-vf", $"fps=1,scale={W}:{H}:flags=bicubic", "-pix_fmt", "rgb24", "-f", "rawvideo", files[k]], ct);
                if (r.ExitCode != 0) throw new InvalidOperationException($"ffmpeg frames {c}/{k} failed: {r.Stderr.Trim()}");
            }));
            var name = FrameChunks.Name(c); var enc = FrameChunks.Encoder.Start(ffmpeg, W, H, Path.Combine(work, name + ".mp4"), st.Crf, false);
            var frames = new List<ChunkFrame>();
            using (enc)
            {
                for (int k = 0; k < parts; k++)
                {
                    int a0 = s0 + (s1 - s0) * k / parts; var buf = new byte[frameBytes];
                    await ConsumeRawAsync(files[k], buf, async i =>
                    {
                        double t = a0 + i; if (t >= s1) return false; sampled++;
                        var feat = ColorGrid.Compute(new Rgb(buf, W, H));
                        int rep = pruner.Offer(t, feat);
                        if (rep >= 0) { FramePruner.AddSpan(kept[rep].Spans, t, t + FramePruner.FrameSeconds); return true; }
                        var f = new ChunkFrame(kept.Count, t, frames.Count, [[t, t + FramePruner.FrameSeconds]]);
                        pruner.Keep(f.I, t, feat); kept.Add(f); frames.Add(f); keptN++;
                        await enc.WriteAsync(buf, ct);
                        return true;
                    }, ct);
                }
                var bytes = await enc.FinishAsync(); fb += bytes.Length;
                await put($"frames/{name}.mp4", bytes, "video/mp4"); index.Add((name, c * FrameChunks.ChunkSeconds, frames, bytes.Length));
            }
            progress($"{c + 1}/{nChunks} chunks, {keptN}/{sampled} frames kept");
        }
        foreach (var (name, start, frames, _) in index) await put($"frames/{name}.json", FrameChunks.IndexJson(name, start, W, H, st.Crf, 10, frames, true), "application/json");
        await put("frames/manifest.json", FrameChunks.ManifestJson("frames", W, H, st.Crf, keptN, index.Select(x => (x.Chunk, x.Start, x.Frames.Count, x.Bytes)).ToList(), new { sampled, fps = 1, gop = 10, preset = 8, pruning = new { grid = st.GridThreshold, lookback = st.LookbackSeconds, kept = keptN, of = sampled } }), "application/json");

        // keyframes: full size, all-intra
        progress("keyframes");
        var kfFile = await CastStage.KeyframeFileAsync(tools, video, work, ct);
        var (kw, kh) = await OcrSubs.SizeAsync(tools, video, ct); kw &= ~1; kh &= ~1;
        var kIndex = new List<(string Chunk, double Start, List<ChunkFrame> Frames, long Bytes)>(); long kb = 0; int nk = 0;
        FrameChunks.Encoder? ke = null; int curChunk = -1; List<ChunkFrame>? kl = null; string? kName = null;
        async Task Close()
        {
            if (ke == null) return;
            var bytes = await ke.FinishAsync(); ke.Dispose(); ke = null; kb += bytes.Length;
            var f = Path.Combine(work, kName + ".mp4"); await File.WriteAllBytesAsync(f, bytes, ct);
            await FrameChunks.VerifyAllIntraAsync(tools, f, kl!.Count, ct);
            await put($"keyframes/{kName}.mp4", bytes, "video/mp4"); kIndex.Add((kName!, curChunk * FrameChunks.ChunkSeconds, kl, bytes.Length));
        }
        try
        {
            await CastStage.DecodeAsync(tools, kfFile, kw, kh, async (t, im, _) =>
            {
                int c = FrameChunks.ChunkOf(t);
                if (c != curChunk) { await Close(); curChunk = c; kName = FrameChunks.Name(c); kl = []; ke = FrameChunks.Encoder.Start(ffmpeg, kw, kh, Path.Combine(work, "k" + kName + ".mp4"), st.Crf, true); }
                kl!.Add(new ChunkFrame(nk, Math.Round(t, 3), kl.Count, [])); nk++;
                await ke!.WriteAsync(im.Data, ct);
            }, null, ct);
            await Close();
        }
        finally { ke?.Dispose(); try { File.Delete(kfFile); } catch { } }
        foreach (var (name, start, frames, _) in kIndex) await put($"keyframes/{name}.json", FrameChunks.IndexJson(name, start, kw, kh, st.Crf, 1, frames, false), "application/json");
        await put("keyframes/manifest.json", FrameChunks.ManifestJson("keyframes", kw, kh, st.Crf, nk, kIndex.Select(x => (x.Chunk, x.Start, x.Frames.Count, x.Bytes)).ToList(), new { allIntra = true }), "application/json");
        return new Produced(sampled, keptN, nk, fb, kb);
    }

    public static byte[] ToJson(
IEnumerable<FrameEntry> frames) =>
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
