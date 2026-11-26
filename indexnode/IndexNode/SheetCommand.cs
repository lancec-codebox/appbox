using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndexNode;

/// <summary>
/// 0.13.47: `indexnode sheet`: contact sheets of real frames for a person to look at (owner, 2026-11-24). Reads the store through the node's own layers (a ghosted file is made readable the usual way),
/// writes JPEGs locally, at most --max-kb each (default 100). Nothing is written to the queue; the store sees only the usual read touch of a ghost.
///   sheet &lt;id&gt;...        one sheet per upload: its stored frames when it was processed, otherwise its sample segments
///   sheet pair &lt;dup&gt;...  one two-row sheet per duplicate: top its sample, bottom the upload it matched at the corresponding moments
/// </summary>
public static class SheetCommand
{
    /// <summary>One frame of a sheet: the time it was asked for, the time of the frame that was found, and the picture.</summary>
    public sealed record Shot(double Wanted, double T, Rgb Im);

    public const string Usage = "indexnode sheet <id>... [--at s,s,...] [--count 12] | sheet pair <duplicate id>... [--match <id>] [--match-times s,s] [--per-segment 2] [--align 20]   common: [--cols 4] [--width 240] [--max-kb 100] [--output-local <dir, default ./sheets>]";

    // ---- drawing (pure)

    static readonly Dictionary<char, string[]> Font = new()
    {
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"], ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"], ['3'] = ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"], ['5'] = ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        ['6'] = ["00110", "01000", "10000", "11110", "10001", "10001", "01110"], ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"], ['9'] = ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
        [':'] = ["00000", "00100", "00100", "00000", "00100", "00100", "00000"],
    };

    /// <summary>h:mm:ss of a video time.</summary>
    public static string Clock(double t) { int s = (int)Math.Max(0, Math.Round(t)); return $"{s / 3600}:{s / 60 % 60:D2}:{s % 60:D2}"; }

    /// <summary>Digits and colons in a 5x7 font, white on a dark box, top-left at (x, y); characters outside the font are skipped.</summary>
    public static void DrawText(Rgb im, int x, int y, string text, int scale)
    {
        int w = text.Length * 6 * scale + scale, h = 9 * scale;
        for (int yy = Math.Max(0, y); yy < Math.Min(im.H, y + h); yy++) for (int xx = Math.Max(0, x); xx < Math.Min(im.W, x + w); xx++) { int p = (yy * im.W + xx) * 3; im.Data[p] = im.Data[p + 1] = im.Data[p + 2] = 0; }
        for (int k = 0; k < text.Length; k++)
        {
            if (!Font.TryGetValue(text[k], out var g)) continue;
            for (int r = 0; r < 7; r++) for (int c = 0; c < 5; c++)
            {
                if (g[r][c] != '1') continue;
                for (int dy = 0; dy < scale; dy++) for (int dx = 0; dx < scale; dx++)
                {
                    int px = x + scale + (k * 6 + c) * scale + dx, py = y + scale + r * scale + dy;
                    if (px < 0 || py < 0 || px >= im.W || py >= im.H) continue;
                    int p = (py * im.W + px) * 3; im.Data[p] = im.Data[p + 1] = im.Data[p + 2] = 255;
                }
            }
        }
    }

    /// <summary>The grid: every cell <paramref name="cellW"/> wide, a row as tall as its tallest scaled frame, frames centred on a dark background, each with its own time in the corner. A missing frame leaves its cell dark.</summary>
    public static Rgb Compose(IReadOnlyList<IReadOnlyList<Shot?>> rows, int cellW)
    {
        const int gap = 2;
        int cols = Math.Max(1, rows.Max(r => r.Count));
        int ScaledH(Rgb im) => Math.Max(1, (int)Math.Round((double)im.H * Math.Min(cellW, im.W) / im.W));
        var rowH = rows.Select(r => r.Where(s => s != null).Select(s => ScaledH(s!.Im)).DefaultIfEmpty(cellW / 2).Max()).ToList();
        int W = cols * cellW + (cols + 1) * gap, H = rowH.Sum() + (rows.Count + 1) * gap;
        var canvas = new Rgb(new byte[W * H * 3], W, H); Array.Fill(canvas.Data, (byte)24);
        int y = gap;
        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Count; c++)
            {
                if (rows[r][c] is not { } s) continue;
                int w = Math.Min(cellW, s.Im.W), h = ScaledH(s.Im);
                var small = w == s.Im.W && h == s.Im.H ? s.Im : Thumbs.BoxResize(s.Im, w, h);
                int x0 = gap + c * (cellW + gap) + (cellW - w) / 2, y0 = y + (rowH[r] - h) / 2;
                for (int yy = 0; yy < h; yy++) Buffer.BlockCopy(small.Data, yy * w * 3, canvas.Data, ((y0 + yy) * W + x0) * 3, w * 3);
                int scale = cellW >= 200 ? 2 : 1;
                DrawText(canvas, x0, y0 + h - 9 * scale, Clock(s.T), scale);
            }
            y += rowH[r] + gap;
        }
        return canvas;
    }

    /// <summary>The sheet as a JPEG of at most <paramref name="maxBytes"/>: the quality goes down first (80 to 32), then the cells get narrower. Returns the smallest it reached when nothing fits.</summary>
    public static (byte[] Jpeg, int Quality, int CellW) Fit(IReadOnlyList<IReadOnlyList<Shot?>> rows, int cellW, int maxBytes)
    {
        (byte[] Jpeg, int Quality, int CellW) best = default;
        for (int w = cellW; w >= 64; w = (int)(w * 0.85))
        {
            var im = Compose(rows, w);
            foreach (int q in new[] { 80, 70, 60, 50, 40, 32 })
            {
                var j = Imaging.EncodeJpeg(im, q);
                if (best.Jpeg == null || j.Length < best.Jpeg.Length) best = (j, q, w);
                if (j.Length <= maxBytes) return (j, q, w);
            }
        }
        return best;
    }

    /// <summary>How alike two frames look, -1 to 1: both reduced to 24x24 grey over their central 70% (so side bars and edge logos count little), mean removed, normalised, multiplied.</summary>
    public static double Similarity(Rgb a, Rgb b)
    {
        static double[] Sig(Rgb im)
        {
            int x0 = (int)(im.W * 0.15), y0 = (int)(im.H * 0.15), w = Math.Max(1, im.W - 2 * x0), h = Math.Max(1, im.H - 2 * y0);
            var crop = new byte[w * h * 3]; for (int y = 0; y < h; y++) Buffer.BlockCopy(im.Data, ((y0 + y) * im.W + x0) * 3, crop, y * w * 3, w * 3);
            var s = Thumbs.BoxResize(new Rgb(crop, w, h), 24, 24); var g = new double[24 * 24]; double mean = 0;
            for (int i = 0; i < g.Length; i++) { g[i] = s.Data[i * 3] * 0.299 + s.Data[i * 3 + 1] * 0.587 + s.Data[i * 3 + 2] * 0.114; mean += g[i]; }
            mean /= g.Length; double n = 0; for (int i = 0; i < g.Length; i++) { g[i] -= mean; n += g[i] * g[i]; }
            n = Math.Sqrt(n); if (n > 1e-9) for (int i = 0; i < g.Length; i++) g[i] /= n;
            return g;
        }
        var sa = Sig(a); var sb = Sig(b); double d = 0; for (int i = 0; i < sa.Length; i++) d += sa[i] * sb[i];
        return d;
    }

    /// <summary>Evenly spread times inside [start, end]: the middles of <paramref name="n"/> equal parts.</summary>
    public static List<double> Spread(double start, double end, int n) => Enumerable.Range(0, Math.Max(0, n)).Select(i => start + (end - start) * (i + 0.5) / n).ToList();

    /// <summary>"... times 2771/6407 s ..." of a duplicate decision: the time in the matched upload each of the two sample segments lines up with.</summary>
    public static double[]? MatchTimes(string? reason) =>
        reason != null && Regex.Match(reason, @"times (\d+(?:\.\d+)?)/(\d+(?:\.\d+)?) s") is { Success: true } m ? [double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)] : null;

    // ---- where frames come from

    public interface ISource : IAsyncDisposable { string Kind { get; } Task<Shot?> AtAsync(double t, CancellationToken ct); }

    /// <summary>A processed upload's stored frames (videos/&lt;id&gt;/frames/: manifest, NNNN.json, NNNN.mjpeg or NNNN.mp4). Only the chunks that hold a wanted time are read.</summary>
    public sealed class FramesSource : ISource
    {
        readonly IStore store; readonly Paths paths; readonly string id; readonly Tools? tools; readonly string container; readonly List<(string Chunk, double Start)> chunks;
        readonly Dictionary<string, ChunkInfo> infos = new(); readonly Dictionary<string, byte[]> datas = new(); readonly string tmp = Path.Combine(Path.GetTempPath(), "indexnode-sheet-" + Guid.NewGuid().ToString("N")[..8]);
        public string Kind => "frames"; public double Duration { get; private set; }
        FramesSource(IStore store, Paths paths, string id, Tools? tools, string container, List<(string, double)> chunks, double duration) { this.store = store; this.paths = paths; this.id = id; this.tools = tools; this.container = container; this.chunks = chunks; Duration = duration; }

        public static async Task<FramesSource?> OpenAsync(IStore store, Paths paths, string id, Tools? tools, CancellationToken ct)
        {
            var name = paths.Video(id, "frames/manifest.json");
            if (await store.GetAsync(name, ct) is not { } man) return null;
            var j = StoredJson.Doc(man.Data, name).RootElement;
            var chunks = j.GetProperty("chunks").EnumerateArray().Where(c => !c.TryGetProperty("frames", out var f) || f.GetInt32() > 0).Select(c => (c.GetProperty("chunk").GetString()!, c.GetProperty("start").GetDouble())).OrderBy(c => c.Item2).ToList();
            if (chunks.Count == 0) return null;
            var container = j.TryGetProperty("container", out var co) ? co.GetString() ?? "mp4" : "mp4";
            var src = new FramesSource(store, paths, id, tools, container, chunks, 0);
            var last = await src.InfoAsync(chunks[^1].Item1, ct);
            src.Duration = last.Frames.Count > 0 ? last.Frames.Max(f => f.T) : chunks[^1].Item2;
            return src;
        }

        async Task<ChunkInfo> InfoAsync(string chunk, CancellationToken ct)
        {
            if (infos.TryGetValue(chunk, out var ci)) return ci;
            var name = paths.Video(id, $"frames/{chunk}.json");
            var b = await store.GetAsync(name, ct) ?? throw new InvalidOperationException($"{id}: frames/{chunk}.json is missing");
            return infos[chunk] = FrameChunks.ParseChunk(chunk, b.Data, name);
        }

        /// <summary>Every stored frame within <paramref name="window"/> seconds of the time (JPEG chunks only; a video-chunk upload gives the one nearest frame).</summary>
        public async Task<List<Shot>> NearAsync(double t, double window, CancellationToken ct)
        {
            var r = new List<Shot>();
            if (container != "mjpeg") { if (await AtAsync(t, ct) is { } one) r.Add(one); return r; }
            for (int kk = 0; kk < chunks.Count; kk++)
            {
                if (chunks[kk].Start > t + window || (kk + 1 < chunks.Count && chunks[kk + 1].Start < t - window)) continue;
                var info = await InfoAsync(chunks[kk].Chunk, ct); byte[]? data = null; List<(int Off, int Len)>? split = null;
                for (int row = 0; row < info.Frames.Count; row++)
                {
                    var f = info.Frames[row]; if (Math.Abs(f.T - t) > window) continue;
                    data ??= await DataAsync(info.Chunk, "mjpeg", ct);
                    var (off, len) = f.Off >= 0 && f.Len > 0 ? ((int)f.Off, f.Len) : (split ??= FrameChunks.SplitJpegs(data))[row];
                    r.Add(new Shot(t, f.T, Imaging.DecodeJpeg(data.AsSpan(off, len).ToArray())));
                }
            }
            return r;
        }

        async Task<byte[]> DataAsync(string chunk, string ext, CancellationToken ct) =>
            datas.TryGetValue(chunk, out var d) ? d : datas[chunk] = (await store.GetAsync(paths.Video(id, $"frames/{chunk}.{ext}"), ct))?.Data ?? throw new InvalidOperationException($"{id}: frames/{chunk}.{ext} is missing");

        public async Task<Shot?> AtAsync(double t, CancellationToken ct)
        {
            int k = chunks.FindLastIndex(c => c.Start <= t); if (k < 0) k = 0;
            // the nearest stored frame in the chunk that holds the time and its two neighbours (a pruned stretch can leave the nearest frame next door)
            (ChunkInfo Info, int Row)? best = null; double bestD = double.MaxValue;
            foreach (int kk in new[] { k, k - 1, k + 1 })
            {
                if (kk < 0 || kk >= chunks.Count) continue;
                if (kk != k && bestD <= 30) continue;
                var info = await InfoAsync(chunks[kk].Chunk, ct);
                for (int r = 0; r < info.Frames.Count; r++) { double d = Math.Abs(info.Frames[r].T - t); if (d < bestD) { bestD = d; best = (info, r); } }
            }
            if (best is not { } b) return null;
            var f = b.Info.Frames[b.Row]; var ext = container == "mjpeg" ? "mjpeg" : "mp4";
            if (container == "mjpeg" && f.Off >= 0 && f.Len > 0 && !datas.ContainsKey(b.Info.Chunk))
            {   // 0.13.62: one frame is one ranged read of its own bytes, not the whole ten-minute chunk (tens of MB)
                var rr = await store.GetRangeAsync(paths.Video(id, $"frames/{b.Info.Chunk}.mjpeg"), f.Off, f.Len, null, ct);
                if (rr.Data is { } one && one.Length == f.Len && one.Length > 3 && one[0] == 0xFF && one[1] == 0xD8) return new Shot(t, f.T, Imaging.DecodeJpeg(one));
            }
            var data = await DataAsync(b.Info.Chunk, ext, ct);
            if (container == "mjpeg")
            {
                var (off, len) = f.Off >= 0 && f.Len > 0 ? ((int)f.Off, f.Len) : FrameChunks.SplitJpegs(data)[b.Row];
                return new Shot(t, f.T, Imaging.DecodeJpeg(data.AsSpan(off, len).ToArray()));
            }
            if (tools == null) throw new InvalidOperationException("this upload's frames are video chunks: ffmpeg is needed");
            Directory.CreateDirectory(tmp); var file = Path.Combine(tmp, b.Info.Chunk + ".mp4"); if (!File.Exists(file)) await File.WriteAllBytesAsync(file, data, ct);
            return await Cut(tools, file, f.Pts, tmp, ct) is { } im ? new Shot(t, f.T, im) : null;
        }
        public ValueTask DisposeAsync() { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } return ValueTask.CompletedTask; }
    }

    /// <summary>An upload's sample (videos/&lt;id&gt;/sample/: sample.json and its segments). A wanted time outside every segment gives the nearest moment that was sampled.</summary>
    public sealed class SampleSource : ISource
    {
        readonly IStore store; readonly Paths paths; readonly string id; readonly Tools tools; readonly string tmp = Path.Combine(Path.GetTempPath(), "indexnode-sheet-" + Guid.NewGuid().ToString("N")[..8]);
        public string Kind => "sample"; public List<(string File, double Start, double End)> Segments { get; } public double Duration { get; }
        SampleSource(IStore store, Paths paths, string id, Tools tools, List<(string, double, double)> segs, double duration) { this.store = store; this.paths = paths; this.id = id; this.tools = tools; Segments = segs; Duration = duration; }

        public static async Task<SampleSource?> OpenAsync(IStore store, Paths paths, string id, Tools tools, CancellationToken ct)
        {
            var name = paths.Video(id, "sample/sample.json");
            if (await store.GetAsync(name, ct) is not { } b) return null;
            var j = StoredJson.Doc(b.Data, name).RootElement;
            var segs = j.GetProperty("segments").EnumerateArray().Select(s => (s.GetProperty("file").GetString()!, s.GetProperty("start").GetDouble(), s.GetProperty("end").GetDouble())).ToList();
            return segs.Count == 0 ? null : new SampleSource(store, paths, id, tools, segs, j.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : segs[^1].Item3);
        }

        public async Task<Shot?> AtAsync(double t, CancellationToken ct)
        {
            var seg = Segments.OrderBy(s => t < s.Start ? s.Start - t : t > s.End ? t - s.End : 0).First();
            double at = Math.Clamp(t, seg.Start, Math.Max(seg.Start, seg.End - 1));
            Directory.CreateDirectory(tmp); var file = Path.Combine(tmp, seg.File);
            if (!File.Exists(file))
            {
                var blob = paths.Video(id, "sample/" + seg.File);
                var data = await FrameChunks.ReadAsync(store, blob, ct) ?? throw new InvalidOperationException($"{id}: sample/{seg.File} is missing");
                if (data.Length == 0) throw new InvalidOperationException($"{id}: sample/{seg.File} reads as 0 bytes (a ghost that could not be made readable)");
                await File.WriteAllBytesAsync(file, data, ct);
            }
            return await Cut(tools, file, at - seg.Start, tmp, ct) is { } im ? new Shot(t, at, im) : null;
        }
        public ValueTask DisposeAsync() { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } return ValueTask.CompletedTask; }
    }

    /// <summary>0.13.48: an upload that only has its downloaded video (a duplicate found after the full download has no sample and no stored frames): the video is fetched once to a temporary file and frames are cut from it.</summary>
    public sealed class VideoSource : ISource
    {
        readonly IStore store; readonly Paths paths; readonly string id; readonly Tools tools; readonly string tmp = Path.Combine(Path.GetTempPath(), "indexnode-sheet-" + Guid.NewGuid().ToString("N")[..8]); string? file;
        public string Kind => "video"; public double Duration { get; private set; }
        VideoSource(IStore store, Paths paths, string id, Tools tools) { this.store = store; this.paths = paths; this.id = id; this.tools = tools; }

        public static async Task<VideoSource?> OpenAsync(IStore store, Paths paths, string id, Tools tools, CancellationToken ct)
        {
            if (await store.HeadAsync(paths.Video(id, "video.mp4"), ct) == null) return null;
            var v = new VideoSource(store, paths, id, tools); await v.FetchAsync(ct); return v;
        }

        async Task FetchAsync(CancellationToken ct)
        {
            var data = await FrameChunks.ReadAsync(store, paths.Video(id, "video.mp4"), ct) ?? throw new InvalidOperationException($"{id}: video.mp4 is missing");
            if (data.Length == 0) throw new InvalidOperationException($"{id}: video.mp4 reads as 0 bytes (a ghost that could not be made readable)");
            Directory.CreateDirectory(tmp); file = Path.Combine(tmp, "video.mp4"); await File.WriteAllBytesAsync(file, data, ct);
            var r = await Proc.RunAsync(await tools.FfprobeAsync(ct), ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file], ct);
            Duration = double.TryParse(r.Stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        public async Task<Shot?> AtAsync(double t, CancellationToken ct)
        {
            double at = Math.Clamp(t, 0, Math.Max(0, Duration - 1));
            return await Cut(tools, file!, at, tmp, ct) is { } im ? new Shot(t, at, im) : null;
        }
        public ValueTask DisposeAsync() { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } return ValueTask.CompletedTask; }
    }

    /// <summary>One frame of a local video file at <paramref name="offset"/> seconds (ffmpeg, PNG, decoded in-process); null when ffmpeg gives none.</summary>
    static async Task<Rgb?> Cut(Tools tools, string file, double offset, string tmp, CancellationToken ct)
    {
        var png = Path.Combine(tmp, "cut-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            var r = await Proc.RunAsync(await tools.FfmpegAsync(ct), ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", Math.Max(0, offset).ToString("0.###", CultureInfo.InvariantCulture), "-i", file, "-frames:v", "1", "-pix_fmt", "rgb24", "-c:v", "png", png], ct);
            return r.ExitCode == 0 && File.Exists(png) ? InProcessMedia.DecodeImage(await File.ReadAllBytesAsync(png, ct)) : null;
        }
        finally { try { File.Delete(png); } catch { } }
    }

    /// <summary>The upload's pictures: its stored frames when it has them, otherwise its sample.</summary>
    public static async Task<ISource?> OpenSourceAsync(IStore store, Paths paths, string id, Tools tools, CancellationToken ct, bool preferSample = false)
    {
        if (preferSample && await SampleSource.OpenAsync(store, paths, id, tools, ct) is { } s0) return s0;
        if (await FramesSource.OpenAsync(store, paths, id, tools, ct) is { } f) return f;
        if (!preferSample && await SampleSource.OpenAsync(store, paths, id, tools, ct) is { } s1) return s1;
        return preferSample ? null : await VideoSource.OpenAsync(store, paths, id, tools, ct);
    }

    // ---- the duplicate's record

    /// <summary>The upload a duplicate was matched with (the queue's entry) and, when the processing queue's log still has the decision, the matched time of each sample segment.</summary>
    public static async Task<(string? Match, double[]? Times, string? Reason)> DuplicateOfAsync(IStore store, Paths paths, string id, CancellationToken ct)
    {
        string? match = null, reason = null;
        try { var q = await JobQueue.OpenForVideoAsync(store, paths, "cli", id, ct); match = q.Read(st => st.Get(id)?.Entries.Values.Select(e => e.Job?.DuplicateOf).FirstOrDefault(d => !string.IsNullOrEmpty(d))); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"sheet: the queue could not be read for {id} ({e.Message})"); }
        foreach (var b in (await StageZipStore.ListRawAsync(store, paths.ProcessQueue.QueueDir + "queue", ct)).Where(b => b.Name.EndsWith(".log", StringComparison.Ordinal)))
        {
            if (await store.GetAsync(b.Name, ct) is not { } log) continue;
            foreach (var line in System.Text.Encoding.UTF8.GetString(log.Data).Split('\n'))
            {
                if (!line.Contains(id, StringComparison.Ordinal) || !line.Contains("duplicate", StringComparison.Ordinal)) continue;
                JsonObject? o; try { o = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
                if ((string?)o?["s"] != id) continue;
                match ??= (string?)o?["job"]?["duplicateOf"];
                var r = (string?)(o?["pre"]?["reasons"] as JsonArray)?.FirstOrDefault() ?? (string?)o?["why"];
                if (MatchTimes(r) != null) reason = r;
            }
        }
        return (match, MatchTimes(reason), reason);
    }

    // ---- the command

    public static async Task<int> RunAsync(Args a)
    {
        var pos = a.Positional.ToList(); bool pair = pos.FirstOrDefault() == "pair"; if (pair) pos.RemoveAt(0);
        if (a.Get("ids") is { } idsFile) pos.AddRange(File.ReadAllLines(idsFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        if (a.Get("video") is { } one) pos.Add(one);
        if (pos.Count == 0) { Console.Error.WriteLine(Usage); return 2; }
        int cols = a.Get("cols") is { } c ? int.Parse(c) : 4, width = a.Get("width") is { } w ? int.Parse(w) : 240, maxKb = a.Get("max-kb") is { } m ? int.Parse(m) : 100;
        var outDir = a.Get("output-local") ?? a.Get("out") ?? "sheets"; Directory.CreateDirectory(outDir);
        var (cfg, store, paths, http) = Commands.Open(a); using var _h = http; var tools = new Tools(http, cfg); using var cts = new CancellationTokenSource(); var ct = cts.Token;
        int failed = 0;
        foreach (var id in pos)
        {
            try { if (pair) await PairAsync(a, store, paths, tools, id, cols, width, maxKb, outDir, ct); else await OneAsync(a, store, paths, tools, id, cols, width, maxKb, outDir, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { failed++; Console.WriteLine($"{id}: no sheet: {e.Message}"); }
        }
        return failed == 0 ? 0 : 1;
    }

    static List<double>? Times(string? s) => s?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();

    static async Task OneAsync(Args a, IStore store, Paths paths, Tools tools, string id, int cols, int width, int maxKb, string outDir, CancellationToken ct)
    {
        await using var src = await OpenSourceAsync(store, paths, id, tools, ct, a.Has("sample")) ?? throw new InvalidOperationException("the store has neither stored frames nor a sample for it");
        int count = a.Get("count") is { } n ? int.Parse(n) : 12;
        var times = Times(a.Get("at")) ?? (src is SampleSource ss ? ss.Segments.SelectMany(s => Spread(s.Start, s.End, Math.Max(1, count / ss.Segments.Count))).ToList()
            : Spread((src is FramesSource f1 ? f1.Duration : ((VideoSource)src).Duration) * 0.03, (src is FramesSource f2 ? f2.Duration : ((VideoSource)src).Duration) * 0.97, count));
        var shots = new List<Shot?>(); foreach (var t in times) shots.Add(await src.AtAsync(t, ct));
        var rows = shots.Chunk(cols).Select(r => (IReadOnlyList<Shot?>)r).ToList();
        var (jpg, q, cw) = Fit(rows, width, maxKb * 1024);
        var file = Path.Combine(outDir, id + ".jpg"); await File.WriteAllBytesAsync(file, jpg, ct);
        Console.WriteLine($"{id}: {file}  {jpg.Length / 1024.0:F0} KB (quality {q}, cells {cw} px) from its {src.Kind}; frames at {string.Join(" ", shots.Select(s => s == null ? "-" : Clock(s.T)))}");
    }

    static async Task PairAsync(Args a, IStore store, Paths paths, Tools tools, string id, int cols, int width, int maxKb, string outDir, CancellationToken ct)
    {
        var (found, foundTimes, reason) = await DuplicateOfAsync(store, paths, id, ct);
        var match = a.Get("match") ?? found ?? throw new InvalidOperationException("the queue does not name the upload it duplicates; give --match <id>");
        await using var dup = await SampleSource.OpenAsync(store, paths, id, tools, ct) is { } s ? s : await OpenSourceAsync(store, paths, id, tools, ct) ?? throw new InvalidOperationException("the store has no sample, no stored frames and no video for it");
        await using var other = await OpenSourceAsync(store, paths, match, tools, ct) ?? throw new InvalidOperationException($"the store has neither stored frames nor a sample for the matched upload {match}");
        int per = a.Get("per-segment") is { } p ? int.Parse(p) : Math.Max(1, cols / 2);
        double align = a.Get("align") is { } al ? double.Parse(al, CultureInfo.InvariantCulture) : 20;   // seconds around the recorded time in which the matched upload's most similar frame is taken; 0: the recorded time as it is
        static double Dur(ISource x) => x switch { SampleSource a1 => a1.Duration, FramesSource a2 => a2.Duration, VideoSource a3 => a3.Duration, _ => 0 };
        // without a sample: the two 5-minute windows the pre-check itself looks at (30% and 70% of the video)
        var segs = dup is SampleSource sd ? sd.Segments.Select(x => (x.Start, x.End)).ToList() : new[] { 0.3, 0.7 }.Select(f => (Start: Math.Max(0, Dur(dup) * f - 150), End: Math.Min(Dur(dup), Dur(dup) * f + 150))).ToList();
        var mt = Times(a.Get("match-times"))?.ToArray() ?? foundTimes; string how = mt != null ? (a.Has("match-times") ? "given" : "from the check's record") : "same position in the video (no record of the matched times was found)";
        double dupDur = Dur(dup), otherDur = Dur(other);
        var top = new List<Shot?>(); var bottom = new List<Shot?>();
        for (int i = 0; i < segs.Count; i++)
        {
            double mid = (segs[i].Start + segs[i].End) / 2;
            foreach (var t in Spread(segs[i].Start, segs[i].End, per))
            {
                var up = await dup.AtAsync(t, ct); top.Add(up);
                double want = mt != null && i < mt.Length ? t - mid + mt[i] : dupDur > 0 ? t * otherDur / dupDur : t;
                // the record's time is the middle of the segment's matches: the look-alike frame is searched for a few seconds around it, so the two rows show the same shot when the uploads have it
                if (align > 0 && up != null && other is FramesSource fs && await fs.NearAsync(want, align, ct) is { Count: > 0 } near)
                    bottom.Add(near.Select(n => (N: n, S: Similarity(up.Im, n.Im))).OrderByDescending(x => x.S).ThenBy(x => Math.Abs(x.N.T - want)).First().N);
                else bottom.Add(await other.AtAsync(want, ct));
            }
        }
        var (jpg, q, cw) = Fit([top, bottom], width, maxKb * 1024);
        var file = Path.Combine(outDir, id + ".jpg"); await File.WriteAllBytesAsync(file, jpg, ct);
        double off = bottom.Where(x => x != null).Select(x => Math.Abs(x!.T - x.Wanted)).DefaultIfEmpty(0).Max();
        Console.WriteLine($"{id} (top, its {dup.Kind}) against {match} (bottom, its {other.Kind}): {file}  {jpg.Length / 1024.0:F0} KB (quality {q}, cells {cw} px)");
        Console.WriteLine($"  top at {string.Join(" ", top.Select(x => x == null ? "-" : Clock(x.T)))}; bottom at {string.Join(" ", bottom.Select(x => x == null ? "-" : Clock(x.T)))} (matched times {how}; the bottom frames are at most {off:F0} s from the wanted moments)");
        if (reason != null) Console.WriteLine($"  the check: {reason}");
    }
}
