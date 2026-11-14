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
