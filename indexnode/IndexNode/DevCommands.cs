using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace IndexNode;

/// <summary>`indexnode dev ...`: runs a process step on a local video file, with no blob storage (used by scripts/check-models.sh).</summary>
public static class DevCommands
{
    public static async Task<int> RunAsync(Args a)
    {
        var sub = a.Positional.FirstOrDefault() ?? throw new ArgumentException("dev clip|ocr|asr|embed-text --video <file> --out <dir> [--offset <sec>] [--threads N]");
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var tools = new Tools(http, cfg);
        var video = a.Get("video"); var outDir = a.Get("out") ?? Path.Combine(Path.GetTempPath(), "indexnode-dev");
        Directory.CreateDirectory(outDir);
        int threads = a.Get("threads") is { } t ? int.Parse(t) : Math.Min(Environment.ProcessorCount, 4);
        double offset = a.Get("offset") is { } o ? double.Parse(o, CultureInfo.InvariantCulture) : 0;
        var ct = CancellationToken.None;
        var sw = Stopwatch.StartNew();
        switch (sub)
        {
            case "frames":
            {
                var duration = await Frames.DurationAsync(tools, video!, ct);
                var dir = Path.Combine(outDir, "chunks");
                var r = await Frames.ProduceAsync(tools, video!, outDir, FrameSettings.From(cfg), threads, duration, p => Console.Error.WriteLine(p), (n, b, _) => { var f = Path.Combine(dir, n.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, b); return Task.CompletedTask; }, ct);
                Console.WriteLine($"frames: {r.Kept}/{r.Sampled} frames {r.FrameBytes / 1048576.0:F1} MB, {r.Keyframes} keyframes {r.KeyframeBytes / 1048576.0:F1} MB, {duration:F0}s of video, {sw.Elapsed.TotalSeconds:F0}s");
                break;
            }
            case "prune-stats":
            {
                // prune rate of the colour-grid gate over the whole video at 1 fps (36x64 area-scaled), for a range of thresholds
                var raw = Path.Combine(outDir, "grid.rgb");
                var rr = await Proc.RunAsync(await tools.FfmpegAsync(ct), ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", video!, "-an", "-vf", "fps=1,scale=36:64:flags=area", "-pix_fmt", "rgb24", "-f", "rawvideo", raw], ct);
                var bytes = File.ReadAllBytes(raw); int fbs = 36 * 64 * 3; var feats = new List<float[]>();
                for (int off = 0; off + fbs <= bytes.Length; off += fbs) feats.Add(ColorGrid.Compute(new Rgb(bytes[off..(off + fbs)], 36, 64)));
                foreach (var thr in new[] { 0.01, 0.02, 0.03, 0.04, 0.045, 0.05, 0.06, 0.08, 0.1, 0.15 })
                {
                    var pr = new FramePruner(new FrameSettings(GridThreshold: thr)); int pruned = 0, k = 0;
                    for (int i = 0; i < feats.Count; i++) { if (pr.Offer(i, feats[i]) >= 0) pruned++; else pr.Keep(k++, i, feats[i]); }
                    Console.WriteLine($"grid {thr:F3}: pruned {pruned} of {feats.Count} = {100.0 * pruned / feats.Count:F1}%");
                }
                break;
            }
            case "embed":
            {
                // dev embed --step sscd|clip --out <dir with chunks/frames>: vectors from the stored chunks (no video decode), written as <out>/<step>.f16 plus timing
                var set = ChunkDir.Open(Path.Combine(outDir, "chunks", "frames")); var dir = Path.Combine(outDir, "jpg");
                var t0 = sw.Elapsed.TotalSeconds; await set.ExtractAsync(tools, dir, ct); var t1 = sw.Elapsed.TotalSeconds;
                byte[] rows;
                if ((a.Get("step") ?? "sscd") == "sscd") rows = await Sscd.DescribeAsync(tools, a.Get("model") ?? await Sscd.EnsureModelAsync(cfg, http, ct), dir, set.Count, threads, _ => { }, ct);
                else rows = await Clip.EmbedFramesAsync(tools, await Models.EnsureAsync(Models.ClipVision, http, ct), dir, set.Count, threads, _ => { }, ct);
                File.WriteAllBytes(Path.Combine(outDir, (a.Get("step") ?? "sscd") + ".f16"), rows);
                Console.WriteLine($"{a.Get("step") ?? "sscd"}: {set.Count} frames, decode {t1 - t0:F0}s, embed {sw.Elapsed.TotalSeconds - t1:F0}s");
                break;
            }
            case "sscd-cmp":
            {
                // dev sscd-cmp --video <orig> --out <dir with chunks/frames> --n 300: SSCD of the stored frame vs the original frame at the same second, int8 and fp32
                int n = int.Parse(a.Get("n") ?? "300");
                var set = ChunkDir.Open(Path.Combine(outDir, "chunks", "frames")); var all = set.Chunks.SelectMany(c => c.Frames).ToList();
                var dS = Path.Combine(outDir, "cmp-stored"); var dO = Path.Combine(outDir, "cmp-orig"); Directory.CreateDirectory(dS); Directory.CreateDirectory(dO);
                var jpgs = Path.Combine(outDir, "jpg"); if (!Directory.Exists(jpgs)) await set.ExtractAsync(tools, jpgs, ct);
                // the original through the same sampler (fps=1, same scale), at the sampled seconds: one ffmpeg over the whole video
                var ff = await tools.FfmpegAsync(ct); var allOrig = Path.Combine(outDir, "cmp-all"); Directory.CreateDirectory(allOrig);
                if (Directory.GetFiles(allOrig).Length == 0) { var r2 = await Proc.RunAsync(ff, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", video!, "-an", "-vf", "fps=1,scale=" + set.W + ":" + set.H + ":flags=bicubic", "-q:v", "1", Path.Combine(allOrig, "%06d.jpg")], ct); if (r2.ExitCode != 0) throw new InvalidOperationException(r2.Stderr); }
                for (int k = 0; k < n; k++)
                {
                    int i = (int)((long)k * all.Count / n);
                    File.Copy(Path.Combine(jpgs, $"{i:D6}.jpg"), Path.Combine(dS, $"{k:D6}.jpg"), true);
                    File.Copy(Path.Combine(allOrig, $"{(int)all[i].T + 1:D6}.jpg"), Path.Combine(dO, $"{k:D6}.jpg"), true);
                }
                foreach (var model in new[] { a.Get("model"), a.Get("model2") }.Where(m => m != null))
                {
                    float[][] Rows(byte[] b) => Enumerable.Range(0, b.Length / (Sscd.Dim * 2)).Select(i => Enumerable.Range(0, Sscd.Dim).Select(d => (float)BitConverter.ToHalf(b, (i * Sscd.Dim + d) * 2)).ToArray()).ToArray();
                    var vs = Rows(await Sscd.DescribeAsync(tools, model!, dS, n, threads, _ => { }, ct)); var vo = Rows(await Sscd.DescribeAsync(tools, model!, dO, n, threads, _ => { }, ct));
                    var cos = Enumerable.Range(0, n).Select(i => (double)Clip.Dot(vs[i], vo[i])).OrderBy(x => x).ToArray();
                    Console.WriteLine($"{Path.GetFileName(model!)} (512-dim rows): stored vs original, {n} frames: median {cos[n / 2]:F3}, p1 {cos[(int)(n * 0.01)]:F3}, min {cos[0]:F3}");
                }
                break;
            }
            case "crf-sweep":
            {
                // dev crf-sweep --out <dir with cmp-all/%06d.jpg> --n 600 --crfs 26,30,34 --model <sscd onnx>: encode the first n 1 fps frames as the frames chunk (SVT-AV1 preset 8, -g 10) at each CRF,
                // decode them back, and compare SSCD and SigLIP of the decoded frame with the same frame from the original; prints bytes per chunk and cosine median / p1 / min
                int n = int.Parse(a.Get("n") ?? "600"); var src = Path.Combine(outDir, "cmp-all"); var ff = await tools.FfmpegAsync(ct);
                var first = Path.Combine(src, "000001.jpg"); var (sw0, sh0) = await OcrSubs.SizeAsync(tools, first, ct);
                float[][] Rows(byte[] b, int dim) => Enumerable.Range(0, b.Length / (dim * 2)).Select(i => Enumerable.Range(0, dim).Select(d => (float)BitConverter.ToHalf(b, (i * dim + d) * 2)).ToArray()).ToArray();
                var orig = Path.Combine(outDir, "sweep-orig"); Directory.CreateDirectory(orig);
                for (int k = 0; k < n; k++) File.Copy(Path.Combine(src, $"{k + 1:D6}.jpg"), Path.Combine(orig, $"{k:D6}.jpg"), true);
                var model = a.Get("model") ?? await Sscd.EnsureModelAsync(cfg, http, ct); var vision = await Models.EnsureAsync(Models.ClipVision, http, ct);
                var so = Rows(await Sscd.DescribeAsync(tools, model, orig, n, threads, _ => { }, ct), Sscd.Dim); var co = Clip.FromF16Bytes(await Clip.EmbedFramesAsync(tools, vision, orig, n, threads, _ => { }, ct));
                string Stat(double[] v) { var o = v.OrderBy(x => x).ToArray(); return $"median {o[o.Length / 2]:F3}, p1 {o[(int)(o.Length * 0.01)]:F3}, min {o[0]:F3}"; }
                foreach (var crf in (a.Get("crfs") ?? "26,30,34").Split(',').Select(int.Parse))
                {
                    var mp4 = Path.Combine(outDir, $"sweep{crf}.mp4"); var dec = Path.Combine(outDir, $"sweep-dec{crf}"); if (Directory.Exists(dec)) Directory.Delete(dec, true); Directory.CreateDirectory(dec);
                    var e1 = await Proc.RunAsync(ff, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-framerate", "1", "-i", Path.Combine(orig, "%06d.jpg"), "-c:v", "libsvtav1", "-preset", "8", "-crf", crf.ToString(), "-g", "10", "-pix_fmt", "yuv420p", mp4], ct);
                    if (e1.ExitCode != 0) throw new InvalidOperationException(e1.Stderr);
                    var e2 = await Proc.RunAsync(ff, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", mp4, "-q:v", "1", Path.Combine(dec, "%06d.jpg")], ct);
                    if (e2.ExitCode != 0) throw new InvalidOperationException(e2.Stderr);
                    for (int k = 0; k < n; k++) File.Move(Path.Combine(dec, $"{k + 1:D6}.jpg"), Path.Combine(dec, $"{k:D6}.jpg"), true);
                    var sd = Rows(await Sscd.DescribeAsync(tools, model, dec, n, threads, _ => { }, ct), Sscd.Dim); var cd = Clip.FromF16Bytes(await Clip.EmbedFramesAsync(tools, vision, dec, n, threads, _ => { }, ct));
                    var cs = Enumerable.Range(0, n).Select(i => (double)Clip.Dot(sd[i], so[i])).ToArray(); var cc = Enumerable.Range(0, n).Select(i => (double)Clip.Dot(cd[i], co[i])).ToArray();
                    Console.WriteLine($"crf {crf}: {new FileInfo(mp4).Length / 1048576.0:F1} MB for {n} frames; SSCD {Stat(cs)}; SigLIP {Stat(cc)}");
                }
                break;
            }
            case "cast":
            {
                var duration = await Frames.DurationAsync(tools, video!, ct);
                var thr = a.Get("threshold") is { } th ? double.Parse(th, CultureInfo.InvariantCulture) : (double?)null;
                var msg = await CastStage.RunAsync(tools, http, video!, duration, outDir, new CastStage.Options(a.Get("model") ?? cfg.CastModel ?? "buffalo_l", threads, thr), s => Console.Error.WriteLine(s), ct);
                Console.WriteLine($"cast: {msg} in {sw.Elapsed.TotalSeconds:F0}s");
                break;
            }
            case "clip":
            {
                var duration = await Frames.DurationAsync(tools, video!, ct);
                var frames = await Frames.ExtractAsync(tools, video!, outDir, Path.Combine(outDir, "frames"), threads, duration, _ => { }, ct);
                File.WriteAllBytes(Path.Combine(outDir, "frames.json"), Frames.ToJson(frames));
                var vision = await Models.EnsureAsync(Models.ClipVision, http, ct);
                var t0 = sw.Elapsed.TotalSeconds;
                var bytes = await Clip.EmbedFramesAsync(tools, vision, Path.Combine(outDir, "frames"), frames.Count, threads, _ => { }, ct);
                File.WriteAllBytes(Path.Combine(outDir, "clip.f16"), bytes);
                File.WriteAllBytes(Path.Combine(outDir, "clip.json"), Clip.InfoJson());
                Console.WriteLine($"clip: {frames.Count} frames, embed {sw.Elapsed.TotalSeconds - t0:F1}s ({frames.Count / (sw.Elapsed.TotalSeconds - t0):F1} frames/s)");
                break;
            }
            case "embed-text":
            {
                var q = string.Join(' ', a.Positional.Skip(1));
                var textModel = await Models.EnsureAsync(Models.ClipText, http, ct);
                var tok = Clip.LoadTokenizer(await Models.EnsureAsync(Models.ClipTokenizer, http, ct));
                Console.WriteLine(JsonSerializer.Serialize(new { ids = Clip.Tokenize(tok, q), vec = Clip.EmbedText(textModel, tok, q) }));
                break;
            }
            case "search":
            {
                var rows = Clip.FromF16Bytes(File.ReadAllBytes(Path.Combine(outDir, "clip.f16")));
                var times = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(outDir, "frames.json"))).RootElement.EnumerateArray().Select(e => e.GetProperty("t").GetDouble()).ToList();
                var textModel = await Models.EnsureAsync(Models.ClipText, http, ct);
                var tok = Clip.LoadTokenizer(await Models.EnsureAsync(Models.ClipTokenizer, http, ct));
                var q = Clip.EmbedText(textModel, tok, string.Join(' ', a.Positional.Skip(1)));
                foreach (var h in Clip.Search(rows, times, q, a.Get("top") is { } tp ? int.Parse(tp) : 8))
                    Console.WriteLine($"{Clip.Stamp(h.T + offset)}  {h.Score:F3}  shot of {h.ShotFrames}");
                break;
            }
            case "ocr":
            {
                var det = await Models.EnsureAsync(Models.OcrDet, http, ct); var rec = await Models.EnsureAsync(Models.OcrRec, http, ct); var dict = await Models.EnsureAsync(Models.OcrDict, http, ct);
                var duration = await Frames.DurationAsync(tools, video!, ct);
                var (w, h) = await OcrSubs.SizeAsync(tools, video!, ct);
                var band = await OcrSubs.FindBandAsync(tools, det, rec, dict, video!, w, h, 0, duration, threads, _ => { }, ct);
                Console.WriteLine($"band: {(band == null ? "none" : $"y={band.Value.Y} h={band.Value.H}")}  ({sw.Elapsed.TotalSeconds:F1}s)");
                if (band == null) return 1;
                if (a.Get("band") is { } bs) { var p = bs.Split(','); band = (int.Parse(p[0]), int.Parse(p[1])); }
                var r = await OcrSubs.ReadAsync(tools, video!, det, rec, dict, w, h, band.Value.Y, band.Value.H, 0, duration, threads, _ => { }, ct);
                // shift to the position in the full video
                foreach (var c in r.Zh.Concat(r.En)) { c.Start = Math.Round(c.Start + offset, 2); c.End = Math.Round(c.End + offset, 2); }
                File.WriteAllText(Path.Combine(outDir, "subs.zh.vtt"), OcrCues.ToVtt(r.Zh, false));
                File.WriteAllText(Path.Combine(outDir, "subs.zh-Hans.vtt"), OcrCues.ToVtt(r.Zh, true));
                File.WriteAllBytes(Path.Combine(outDir, "lines.json"), OcrCues.ToJson(r.Zh, r.En, r.BandY, r.BandH, r.Width));
                Console.WriteLine($"ocr: {r.FramesRead} frames in {r.Seconds:F1}s with {threads} worker(s) = {r.FramesRead / r.Seconds:F1} frames/s total, {r.FramesRead / r.Seconds / threads:F1} per core; {r.Zh.Count} zh cues, {r.En.Count} en cues");
                break;
            }
            case "ocrprof":
            {
                // --band y,h : one worker, first --frames (200) frames of the band, ms per frame by phase
                var det = await Models.EnsureAsync(Models.OcrDet, http, ct); var rec = await Models.EnsureAsync(Models.OcrRec, http, ct); var dict = await Models.EnsureAsync(Models.OcrDict, http, ct);
                var (w, h) = await OcrSubs.SizeAsync(tools, video!, ct);
                var p0 = a.Get("band")!.Split(','); int by = int.Parse(p0[0]), bh = int.Parse(p0[1]);
                int nf = a.Get("frames") is { } fs ? int.Parse(fs) : 200;
                var ffmpeg = await tools.FfmpegAsync(ct);
                using var eng = new OcrEngine(det, rec, dict, 1);
                var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var x in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-i", video!, "-an", "-vf", $"fps=4,crop={w}:{bh}:0:{by}", "-frames:v", nf.ToString(), "-f", "rawvideo", "-pix_fmt", "bgr24", "-" }) psi.ArgumentList.Add(x);
                using var pr = Process.Start(psi)!; _ = pr.StandardError.ReadToEndAsync();
                var fb = w * bh * 3; var buf = new byte[fb]; long tRead = 0; int n = 0; var lines = 0;
                var wall = Stopwatch.StartNew();
                while (n < nf)
                {
                    long r0 = Stopwatch.GetTimestamp(); int off = 0;
                    while (off < fb) { int k = await pr.StandardOutput.BaseStream.ReadAsync(buf.AsMemory(off, fb - off), ct); if (k == 0) break; off += k; }
                    tRead += Stopwatch.GetTimestamp() - r0;
                    if (off < fb) break;
                    lines += eng.Recognize(buf, w, bh).Count; n++;
                }
                double ms(long t) => t * 1000.0 / Stopwatch.Frequency / Math.Max(1, n);
                Console.WriteLine($"ocrprof: {n} frames {w}x{bh}, {lines} lines, wall {wall.Elapsed.TotalSeconds:F1}s = {n / wall.Elapsed.TotalSeconds:F1} frames/s; ms/frame: read(decode wait) {ms(tRead):F1}, det-pre {ms(eng.TPre):F1}, det {ms(eng.TDet):F1}, det-post {ms(eng.TPost):F1}, crop {ms(eng.TCrop):F1}, rec-pre {ms(eng.TRecPre):F1}, rec {ms(eng.TRec):F1}, ctc {ms(eng.TCtc):F1}");
                break;
            }
            case "shape":
            {
                // --asr <transcript json, words as [t, ..., text]>: shape it and report the counts before and after
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(a.Get("asr")!));
                var segs = doc.RootElement.GetProperty("segments").EnumerateArray().Select(s => new AsrSegment(
                    s.GetProperty("start").GetDouble(), s.GetProperty("end").GetDouble(), s.GetProperty("text").GetString() ?? "", "zh",
                    s.TryGetProperty("words", out var w) ? w.EnumerateArray().Select(x => new AsrChar(x[0].GetDouble(), x[x.GetArrayLength() - 1].GetString() ?? "")).ToList() : [])).ToList();
                var before = CueShaper.Stats(segs.Select(s => new ShapeCue(s.Start, s.End, s.Text.Trim())).ToList());
                int b6 = segs.Count(s => s.End - s.Start > 6); double longest = segs.Max(s => s.End - s.Start); int longChars = segs.Max(s => s.Text.Trim().Length);
                var cues = Transcribe.Shape(segs); var after = CueShaper.Stats(cues);
                Console.WriteLine($"before: {System.Text.Json.JsonSerializer.Serialize(before.ToJson())} longest {longest:F1}s {longChars} chars");
                Console.WriteLine($"after:  {System.Text.Json.JsonSerializer.Serialize(after.ToJson())} longest {cues.Max(c => c.End - c.Start):F1}s {cues.Max(c => c.Text.Split('\n').Max(l => l.Length))} chars/line");
                // breaks (between cues, and between the two lines of a cue) that fall inside a word of the original text
                int breaks = 0, inWord = 0, ci = 0;
                foreach (var s in segs.OrderBy(s => s.Start))
                {
                    var orig = s.Text.Trim(); var bd = WordSeg.Boundaries(orig); int pos = 0; bool first = true;
                    while (ci < cues.Count && cues[ci].Start < s.Start - 0.01) ci++;
                    for (int k = ci; k < cues.Count && cues[k].Start < s.End; k++)
                        foreach (var line in cues[k].Text.Split('\n'))
                        {
                            int at = orig.IndexOf(line, pos, StringComparison.Ordinal); if (at < 0) continue;
                            if (!first && !char.IsPunctuation(orig[at - 1]) && !char.IsWhiteSpace(orig[at - 1])) { breaks++; if (!bd[at]) inWord++; }
                            first = false; pos = at + line.Length;
                        }
                }
                Console.WriteLine($"breaks without punctuation: {breaks}, inside a word: {inWord}");
                foreach (var s in segs.Where(s => s.End - s.Start > 8).OrderByDescending(s => s.End - s.Start).Take(3))
                {
                    Console.WriteLine($"-- {s.Start:F1}-{s.End:F1}s ({s.End - s.Start:F1}s, {s.Text.Trim().Length} chars): {s.Text.Trim()}");
                    foreach (var c in cues.Where(c => c.Start >= s.Start - 0.01 && c.Start < s.End)) Console.WriteLine($"   {c.Start:F2}-{c.End:F2} {c.Text.Replace("\n", " / ")}");
                }
                break;
            }
            case "asr":
            {
                var m = await Models.EnsureAsync(Models.AsrModel, http, ct); var tk = await Models.EnsureAsync(Models.AsrTokens, http, ct); var vad = await Models.EnsureAsync(Models.Vad, http, ct);
                var audio = await Transcribe.ReadAudioAsync(tools, video!, 0, 0, ct);
                var t0 = sw.Elapsed.TotalSeconds;
                var segs = Transcribe.Run(m, tk, vad, audio, threads, _ => { }, ct, offset);
                var (lang, dubbed) = Transcribe.Dubbing(segs);
                var wall = sw.Elapsed.TotalSeconds - t0; var secs = audio.Length / 16000.0;
                File.WriteAllText(Path.Combine(outDir, "sensevoice.zh.vtt"), Transcribe.Vtt(segs));
                File.WriteAllBytes(Path.Combine(outDir, "sensevoice.zh.json"), Transcribe.ToJson(segs, lang, dubbed, secs, wall));
                Console.WriteLine($"asr: {segs.Count} segments, audio {secs:F1}s, wall {wall:F1}s, RTF {wall / secs:F3} with {threads} thread(s); speechLanguage={lang} dubbed={dubbed}");
                break;
            }
            case "sync":
            {
                // --lines <lines.json> --asr <asr json> [--from S --to S]
                var r = AvSync.FromFiles(File.ReadAllBytes(a.Get("lines")!), File.ReadAllBytes(a.Get("asr")!),
                    a.Get("from") is { } fr ? double.Parse(fr, CultureInfo.InvariantCulture) : 0, a.Get("to") is { } to ? double.Parse(to, CultureInfo.InvariantCulture) : double.MaxValue);
                if (r == null) { Console.WriteLine("sync: nothing to measure"); return 1; }
                Console.WriteLine($"sync: matches {r.Matches}, offset {r.Offset:+0.000;-0.000} s, drift {r.Drift:0.000} s, suspect {r.Suspect}");
                foreach (var w in r.Windows) Console.WriteLine($"  {w.Start,7:F0}s n={w.N,4} median {w.Median:+0.000;-0.000} iqr {w.Iqr:0.000}");
                break;
            }
            default: throw new ArgumentException("unknown dev command " + sub);
        }
        return 0;
    }
}
