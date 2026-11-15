using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace IndexNode;

/// <summary>
/// The cast stage on the frames stage's output, with no video: one stored 1 fps frame per shot (frames/shots.json), each JPEG decoded once.
/// While a frame is open, every kept face is detected, embedded, and its head-and-shoulders crop JPEG-encoded, so nothing is decoded twice
/// (contact sheets and mask checks decode those small crops, never a frame). Full-frame candidate images are the stored frame JPEGs, copied as they are.
/// Outputs as the video-based cast (cast/*) minus credits (the text stage reads all text) and the two-shot posters (dropped, owner 2026-11-14).
/// </summary>
public static class CastFrames
{
    /// <param name="Det">face detector (SCRFD family, keypoints)</param>
    /// <param name="Rec">face embedder (ArcFace family, 112x112, 512-d)</param>
    /// <param name="AlsoRec">calibration only: a second embedder run on the same aligned faces, written as faces_alt.f16</param>
    public sealed record Settings(string Det, string Rec, string ModelId, double Threshold, double Merge, int Threads, int MinFaces = MainMinFaces, int MaskPerCluster = 12, string? AlsoRec = null);

    public const int HsW = CastStage.PortraitW, HsH = CastStage.PortraitH;
    /// <summary>Faces for a main group: 14 on one frame per shot at 360x640 (the video cast used 20 on ~3,300 keyframes at 854p, with ~25% more faces); 14 gave production's 16 main characters of 01FsMcrfjWI and no extra.</summary>
    public const int MainMinFaces = 14;

    sealed record Kept(FaceRec F, int Row, int Crop, float[]? Alt);

    /// <summary>The rows (kept-frame indexes) to read: one per shot; every frame when there is no shot list.</summary>
    public static List<int> Rows(byte[]? shotsJson, int count)
    {
        if (shotsJson == null) return Enumerable.Range(0, count).ToList();
        var rows = JsonDocument.Parse(shotsJson).RootElement.GetProperty("shots").EnumerateArray().Select(s => s.GetProperty("frame").GetInt32()).Where(r => r >= 0 && r < count);
        return rows.Distinct().Order().ToList();
    }

    public static async Task<string> RunAsync(Tools tools, ChunkDir frames, byte[]? shotsJson, string outDir, Settings s, Action<string> progress, CancellationToken ct, string? modelsDir = null, byte[]? ocrLines = null)
    {
        var subs = SubtitleBand.Parse(ocrLines); // burned-in subtitles (ocr/lines.json), so head shots can avoid them
        var stats = new StepStats("cast"); var wall = Stopwatch.StartNew();
        var all = frames.Chunks.SelectMany(c => c.Frames.Select(f => (C: c, F: f))).ToList();
        var rows = Rows(shotsJson, all.Count);
        int w = frames.W, h = frames.H, minH = (int)Math.Round(CastLogic.MinHeight * h / 854.0); // the filter was tuned on 854-high frames
        progress($"{rows.Count} frames (one per shot) of {all.Count}, {w}x{h}; {s.Threads} parts; detector {Path.GetFileName(s.Det)}, embedder {Path.GetFileName(s.Rec)}");
        var ffmpeg = await tools.FfmpegAsync(ct);
        int parts = Math.Max(1, Math.Min(s.Threads, rows.Count)), done = 0;
        var plan = Enumerable.Range(0, parts).Select(p => rows.Skip(rows.Count * p / parts).Take(rows.Count * (p + 1) / parts - rows.Count * p / parts).ToList()).ToList();
        var results = new (List<Kept> Faces, List<byte[]> Crops, long Det, long Emb, long Crop, long Decode)[parts];

        using (stats.Phase("faces-wall"))
            await Task.WhenAll(plan.Select((part, pi) => Task.Run(async () =>
            {
                using var det = new Scrfd(s.Det, 1); using var emb = new ArcFace(s.Rec, 1); using var alt = s.AlsoRec != null ? new ArcFace(s.AlsoRec, 1) : null;
                var kept = new List<Kept>(); long tDet = 0, tEmb = 0, tCrop = 0, tDec = 0; int crops = 0;
                // decoder: the part's JPEGs piped in, raw frames out
                using var dec = Start(ffmpeg, ["-f", "mjpeg", "-i", "-", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], ct);
                var feed = Task.Run(async () =>
                {
                    var handles = new Dictionary<string, SafeFileHandle>();
                    try
                    {
                        foreach (var r in part)
                        {
                            var (c, f) = all[r]; var path = frames.ChunkFile(c);
                            if (!handles.TryGetValue(path, out var fh)) handles[path] = fh = File.OpenHandle(path);
                            var b = new byte[f.Len]; RandomAccess.Read(fh, b, f.Off);
                            await dec.StandardInput.BaseStream.WriteAsync(b, ct);
                        }
                    }
                    finally { dec.StandardInput.Close(); foreach (var fh in handles.Values) fh.Dispose(); }
                }, ct);
                // crop encoder: raw 320x400 crops in, concatenated JPEGs out
                using var enc = Start(ffmpeg, ["-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{HsW}x{HsH}", "-i", "-", "-c:v", "mjpeg", "-q:v", "3", "-f", "mjpeg", "-"], ct);
                var encOut = new MemoryStream(); var drain = enc.StandardOutput.BaseStream.CopyToAsync(encOut, ct);
                int fb = w * h * 3; var st = dec.StandardOutput.BaseStream;
                foreach (var r in part)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    var buf = new byte[fb]; int off = 0; while (off < fb) { int k = await st.ReadAsync(buf.AsMemory(off, fb - off), ct); if (k == 0) break; off += k; }
                    if (off < fb) throw new InvalidOperationException($"cast: frame {r} did not decode ({(await dec.StandardError.ReadToEndAsync(ct)).Trim()})");
                    var im = new Rgb(buf, w, h); double t = all[r].F.T;
                    var t1 = Stopwatch.GetTimestamp(); tDec += t1 - t0;
                    var dets = det.Detect(im);
                    var t2 = Stopwatch.GetTimestamp(); tDet += t2 - t1;
                    var pass = new List<(FaceRec F, Rgb A)>();
                    foreach (var d in dets)
                    {
                        if (d.H < minH || d.Score < CastLogic.MinScore) continue;
                        var (yaw, roll) = FaceMath.Pose(d.Kps);
                        var f = new FaceRec { T = Math.Round(t, 3), X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score, Yaw = Math.Round(yaw, 3), Roll = Math.Round(roll, 1), Kps = d.Kps, Blur = Math.Round(CastStage.FaceBlur(im, d), 1), Pitch = Math.Round(FaceMath.Pitch(d.Kps), 3), Eyes = FaceMath.EyeEnergy(im, d.Kps) };
                        if (f.Blur < CastLogic.MinBlur || f.Yaw > CastLogic.MaxYaw || f.Roll > CastLogic.MaxRoll || Math.Abs(f.Pitch) > CastLogic.MaxPitch) continue;
                        double fa = Math.Max(1.0, (double)d.W * d.H);
                        f.Other = Math.Round(dets.Where(o => !ReferenceEquals(o, d) && o.H >= minH).Select(o => o.W * o.H / fa).DefaultIfEmpty(0).Max(), 3); // every detection of the frame counts, not only kept faces
                        f.Sub = subs?.At(t, w);
                        pass.Add((f, Imaging.AlignFace(im, d.Kps)));
                    }
                    var t3 = Stopwatch.GetTimestamp();
                    if (pass.Count > 0)
                    {
                        var e = emb.EmbedBatch(pass.Select(x => x.A).ToList()); var e2 = alt?.EmbedBatch(pass.Select(x => x.A).ToList());
                        var t4 = Stopwatch.GetTimestamp(); tEmb += t4 - t3;
                        for (int i = 0; i < pass.Count; i++)
                        {
                            var f = pass[i].F; f.Emb = e[i]; f.Quality = CastLogic.QualityOf(f, h);
                            await enc.StandardInput.BaseStream.WriteAsync(CastStage.HeadShoulders(im, f).Data, ct);
                            kept.Add(new Kept(f, r, crops++, e2?[i]));
                        }
                        tCrop += Stopwatch.GetTimestamp() - t4;
                    }
                    int n = Interlocked.Increment(ref done);
                    if (n % 250 == 0 || n == rows.Count) progress($"faces: {n}/{rows.Count} frames");
                }
                enc.StandardInput.Close(); await feed; await drain; await enc.WaitForExitAsync(ct); await dec.WaitForExitAsync(ct);
                var data = encOut.ToArray(); var jpgs = FrameChunks.SplitJpegs(data).Select(x => data.AsSpan(x.Off, x.Len).ToArray()).ToList();
                if (jpgs.Count != crops) throw new InvalidOperationException($"cast: {crops} crops encoded into {jpgs.Count} JPEGs");
                results[pi] = (kept, jpgs, tDet, tEmb, tCrop, tDec);
            }, ct)));
        double Sec(long ticks) => ticks / (double)Stopwatch.Frequency;
        var faceList = new List<FaceRec>(); var hsJpeg = new Dictionary<FaceRec, byte[]>(); var rowOf = new Dictionary<FaceRec, int>(); var altOf = new Dictionary<FaceRec, float[]>();
        foreach (var r in results) foreach (var k in r.Faces) { faceList.Add(k.F); hsJpeg[k.F] = r.Crops[k.Crop]; rowOf[k.F] = k.Row; if (k.Alt != null) altOf[k.F] = k.Alt; }
        var faces = faceList.OrderBy(f => f.T).ThenBy(f => f.X).ToList();
        Log.Info($"cast: {rows.Count} frames, {faces.Count} faces kept in {wall.Elapsed.TotalSeconds:F1}s; model time (all parts): decode {Sec(results.Sum(r => r.Decode)):F1}s, detect {Sec(results.Sum(r => r.Det)):F1}s, embed {Sec(results.Sum(r => r.Emb)):F1}s, crops {Sec(results.Sum(r => r.Crop)):F1}s");

        // cluster the best MaxCluster faces exactly; attach the rest to the nearest centroid within the threshold
        List<CastLogic.Merge> merges; int rawClusters;
        using (stats.Phase("cluster"))
        {
            var core = faces.OrderByDescending(f => f.Quality).Take(CastStage.MaxCluster).ToList();
            var ids = CastLogic.Cluster(core.Select(f => f.Emb).ToList(), s.Threshold);
            for (int i = 0; i < core.Count; i++) core[i].Cluster = ids[i];
            var cents = core.GroupBy(f => f.Cluster).ToDictionary(g => g.Key, g => CastLogic.Centroid(g.Select(f => f.Emb)));
            foreach (var f in faces.Where(f => f.Cluster < 0))
            {
                int bc = -1; double bd = s.Threshold; foreach (var (c, v) in cents) { var dd = 1 - CastLogic.Cosine(f.Emb, v); if (dd <= bd) { bd = dd; bc = c; } }
                f.Cluster = bc;
            }
            rawClusters = faces.Select(f => f.Cluster).Where(c => c >= 0).Distinct().Count();
        }
        using (stats.Phase("merge")) merges = CastLogic.MergeClusters(faces, s.Merge);
        var main = CastLogic.MainClusters(faces, s.MinFaces);
        Log.Info($"cast: {rawClusters} clusters, {merges.Count} merged -> {faces.Select(f => f.Cluster).Where(c => c >= 0).Distinct().Count()}, {main.Count} main");

        // per main cluster: the 12 diverse contact-sheet faces and the best MaskPerCluster faces (the portrait is the best unmasked of these)
        // masks are no longer scored here (owner 2026-11-14): SigLIP flagged 235 of 1,211 production main characters, mostly wrongly; describe marks covered faces on the sheets
        var sheets = new Dictionary<int, List<FaceRec>>(); var need = new List<FaceRec>();
        foreach (var c in main) { sheets[c] = CastLogic.Diverse(faces.Where(f => f.Cluster == c).ToList(), CastStage.Candidates); need.AddRange(sheets[c]); }
        need = need.Distinct().ToList();
        Dictionary<FaceRec, Rgb> hs;
        using (stats.Phase("crop-decode")) hs = await DecodeCropsAsync(ffmpeg, need, hsJpeg, ct);

        var outputs = stats.Phase("outputs");
        var castDir = Path.Combine(outDir, "cast"); Directory.CreateDirectory(castDir);
        var kfTimes = rows.Select(r => all[r].F.T).ToList(); var clusters = new List<object>();
        byte[] FrameJpeg(int row) { var (c, f) = all[row]; using var fh = File.OpenHandle(frames.ChunkFile(c)); var b = new byte[f.Len]; RandomAccess.Read(fh, b, f.Off); return b; }
        foreach (var g in faces.Where(f => f.Cluster >= 0).GroupBy(f => f.Cluster).OrderBy(g => g.Key))
        {
            var list = g.ToList(); bool isMain = main.Contains(g.Key);
            clusters.Add(new { id = g.Key, faces = list.Count, screenSeconds = CastLogic.ScreenSeconds(list, kfTimes), first = list.Min(f => f.T), last = list.Max(f => f.T), main = isMain,
                masked = false, centroid = CastLogic.Centroid(list.Select(f => f.Emb)).Select(x => MathF.Round(x, 5)) }); // masked: decided by describe
            if (!isMain) continue;
            // the portrait: the best-quality sheet face; describe's pick replaces it at publish
            if (sheets[g.Key].OrderByDescending(f => f.Quality).FirstOrDefault() is { } pf) await File.WriteAllBytesAsync(Path.Combine(castDir, $"portrait_{g.Key}.jpg"), hsJpeg[pf], ct);
            var cand = sheets[g.Key];
            if (cand.Count == 0) continue;
            await Imaging.WriteJpegAsync(tools, Imaging.Grid(cand.Select(f => Imaging.Resize(hs[f], CastStage.SheetTile, CastStage.SheetTile * 5 / 4)).ToList(), 4), Path.Combine(castDir, $"sheet_{g.Key}.jpg"), ct, 4);
            for (int k = 0; k < cand.Count; k++)
            {
                await File.WriteAllBytesAsync(Path.Combine(castDir, $"cand_{g.Key}_{k}.jpg"), FrameJpeg(rowOf[cand[k]]), ct);
                await File.WriteAllBytesAsync(Path.Combine(castDir, $"cand_hs_{g.Key}_{k}.jpg"), hsJpeg[cand[k]], ct);
            }
            await File.WriteAllTextAsync(Path.Combine(castDir, $"cand_{g.Key}.json"), JsonSerializer.Serialize(cand.Select((f, k) => new { k, t = f.T, quality = f.Quality, masked = false, solo = f.Solo, sub = f.Sub?.Select(v => Math.Round(v, 1)), pitch = f.Pitch, eyes = f.Eyes, box = new[] { f.X, f.Y, f.W, f.H } })), ct);
        }
        int dim = faces.FirstOrDefault()?.Emb.Length ?? 512;
        await File.WriteAllBytesAsync(Path.Combine(castDir, "faces.f16"), CastStage.FacesF16(faces, dim), ct);
        if (altOf.Count == faces.Count && faces.Count > 0)
            await File.WriteAllBytesAsync(Path.Combine(castDir, "faces_alt.f16"), CastStage.FacesF16(faces.Select(f => new FaceRec { Emb = altOf[f] }).ToList(), altOf[faces[0]].Length), ct);
        var source = new { frames = "frames/ (1 fps JPEG)", width = w, height = h, rows = rows.Count, detector = Path.GetFileName(s.Det), embedder = Path.GetFileName(s.Rec) };
        await File.WriteAllTextAsync(Path.Combine(castDir, "faces.json"), JsonSerializer.Serialize(new { v = 1, model = s.ModelId, dim, threshold = s.Threshold, keyframes = rows.Count, source,
            faces = faces.Select((f, i) => new { i, t = f.T, box = new[] { MathF.Round(f.X, 1), MathF.Round(f.Y, 1), MathF.Round(f.W, 1), MathF.Round(f.H, 1) }, score = MathF.Round(f.Score, 3), blur = f.Blur, quality = f.Quality, other = f.Other, sub = f.Sub != null, pitch = f.Pitch, eyes = f.Eyes, kps = f.Kps.Select(v => MathF.Round(v, 1)), cluster = f.Cluster, masked = (bool?)null }) }), ct);
        await File.WriteAllTextAsync(Path.Combine(castDir, "clusters.json"), JsonSerializer.Serialize(new { v = 1, model = s.ModelId, threshold = s.Threshold, mergeAt = s.Merge, borderAt = 0, rawClusters, source,
            merges = merges.Select(m => new { into = m.Into, from = m.From, dist = m.Dist, vis = m.Vis }), clusters }), ct);
        outputs.Dispose();
        stats.Log(rows.Count, "frames", s.Threads);
        return $"{s.ModelId}: {rows.Count} frames, {faces.Count} faces, {faces.Select(f => f.Cluster).Where(c => c >= 0).Distinct().Count()} clusters, {main.Count} main";
    }

    /// <summary>CastStage.ScoreMasks, but one single-thread SigLIP session per part (one session over all threads scaled poorly: 37 faces took 5 s on 12 threads).</summary>
    static void ScoreMasksParallel(IReadOnlyList<FaceRec> faces, Func<FaceRec, Rgb> crop, string visionModel, string textModel, string tokenizer, int threads, Action<string> progress, CancellationToken ct)
    {
        if (faces.Count == 0) return;
        var tok = Clip.LoadTokenizer(tokenizer);
        var tm = Clip.EmbedText(textModel, tok, CastStage.MaskPrompt, threads); var tb = Clip.EmbedText(textModel, tok, CastStage.BarePrompt, threads);
        int parts = Math.Min(threads, faces.Count), done = 0, S = Clip.Size;
        Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = parts, CancellationToken = ct }, p =>
        {
            using var s = OrtOptions.Session(visionModel, 1, true); var name = s.InputMetadata.Keys.First();
            for (int i = p; i < faces.Count; i += parts)
            {
                ct.ThrowIfCancellationRequested(); var f = faces[i];
                var im = Imaging.Resize(crop(f), S, S); var t = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>([1, 3, S, S]);
                for (int y = 0; y < S; y++) for (int x = 0; x < S; x++) for (int c = 0; c < 3; c++) t[0, c, y, x] = im.Data[(y * S + x) * 3 + c] / 127.5f - 1f;
                using var r = s.Run([Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(name, t)]);
                var o = r.First(q => q.Name == "pooler_output").AsTensor<float>(); var v = new float[Clip.Dim]; for (int d = 0; d < Clip.Dim; d++) v[d] = o[0, d]; Sscd.L2Normalize(v);
                f.Vis = v; f.MaskScore = Math.Round(Clip.Dot(v, tm) - Clip.Dot(v, tb), 4); f.Masked = f.MaskScore > 0;
                int n = Interlocked.Increment(ref done); if (n % 50 == 0 || n == faces.Count) progress($"masks {n}/{faces.Count}");
            }
        });
    }

    /// <summary>
    /// Credits lines (主演, 饰, 领衔) read from the stored frames of the first and last 3 minutes (the whole video when shorter than 6 minutes),
    /// each JPEG decoded once, <paramref name="threads"/> parts at once. Replaces the video-based credits OCR of the old cast.
    /// </summary>
    public static async Task<List<(double Start, double End, string Text)>> CreditsAsync(Tools tools, HttpClient http, ChunkDir frames, double duration, int threads, Action<string> progress, CancellationToken ct)
    {
        var det = await Models.EnsureAsync(Models.OcrDet, http, ct); var rec = await Models.EnsureAsync(Models.OcrRec, http, ct); var dict = await Models.EnsureAsync(Models.OcrDict, http, ct);
        var all = frames.Chunks.SelectMany(c => c.Frames.Select(f => (C: c, F: f))).ToList();
        if (duration <= 0) duration = all.Count > 0 ? all[^1].F.T + 1 : 0;
        const double edge = 180;
        var rows = Enumerable.Range(0, all.Count).Where(i => duration <= 2 * edge || all[i].F.T < edge || all[i].F.T >= duration - edge).ToList();
        if (rows.Count == 0) return [];
        var ffmpeg = await tools.FfmpegAsync(ct); int w = frames.W, h = frames.H, fb = w * h * 3, parts = Math.Max(1, Math.Min(threads, rows.Count));
        var hits = new List<(double S, double E, string Key)>[parts];
        await Task.WhenAll(Enumerable.Range(0, parts).Select(p => Task.Run(async () =>
        {
            var mine = rows.Where((_, k) => k % parts == p).ToList(); var list = hits[p] = [];
            using var eng = new OcrEngine(det, rec, dict, 1);
            using var dec = Start(ffmpeg, ["-f", "mjpeg", "-i", "-", "-f", "rawvideo", "-pix_fmt", "bgr24", "-"], ct);
            var feed = Task.Run(async () =>
            {
                try { foreach (var r in mine) { var (c, f) = all[r]; using var fh = File.OpenHandle(frames.ChunkFile(c)); var b = new byte[f.Len]; RandomAccess.Read(fh, b, f.Off); await dec.StandardInput.BaseStream.WriteAsync(b, ct); } }
                finally { dec.StandardInput.Close(); }
            }, ct);
            var st = dec.StandardOutput.BaseStream;
            foreach (var r in mine)
            {
                var buf = new byte[fb]; int off = 0; while (off < fb) { int k = await st.ReadAsync(buf.AsMemory(off, fb - off), ct); if (k == 0) break; off += k; }
                if (off < fb) break;
                var f = all[r].F; double end = f.Spans.Count > 0 ? f.Spans.Max(x => x[1]) : f.T + 1;
                foreach (var l in eng.Recognize(buf, w, h))
                    if (CastLogic.CreditWords.Any(c => l.Text.Contains(c))) list.Add((f.T, end, OpenCc.T2S.Convert(l.Text)));
            }
            await feed; await dec.WaitForExitAsync(ct);
        }, ct)));
        var seen = new Dictionary<string, (double S, double E)>();
        foreach (var (s, e, key) in hits.SelectMany(x => x).OrderBy(x => x.S)) seen[key] = seen.TryGetValue(key, out var v) ? (Math.Min(v.S, s), Math.Max(v.E, e)) : (s, e);
        progress($"credits: {rows.Count} frames read, {seen.Count} lines");
        return seen.Select(kv => (kv.Value.S, kv.Value.E, kv.Key)).OrderBy(x => x.Item1).ToList();
    }

    static Process Start(string ffmpeg, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-threads", "1" }.Concat(args)) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!; ct.Register(() => Proc.Kill(p));
        return p;
    }

    /// <summary>The head-and-shoulders crops of <paramref name="faces"/>, decoded from their JPEGs in one ffmpeg run (the crops, never the frames).</summary>
    static async Task<Dictionary<FaceRec, Rgb>> DecodeCropsAsync(string ffmpeg, IReadOnlyList<FaceRec> faces, Dictionary<FaceRec, byte[]> jpeg, CancellationToken ct)
    {
        var res = new Dictionary<FaceRec, Rgb>(); if (faces.Count == 0) return res;
        using var p = Start(ffmpeg, ["-f", "mjpeg", "-i", "-", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], ct);
        var feed = Task.Run(async () => { try { foreach (var f in faces) await p.StandardInput.BaseStream.WriteAsync(jpeg[f], ct); } finally { p.StandardInput.Close(); } }, ct);
        int fb = HsW * HsH * 3; var st = p.StandardOutput.BaseStream;
        foreach (var f in faces)
        {
            var buf = new byte[fb]; int off = 0; while (off < fb) { int k = await st.ReadAsync(buf.AsMemory(off, fb - off), ct); if (k == 0) break; off += k; }
            if (off < fb) throw new InvalidOperationException("cast: a face crop did not decode");
            res[f] = new Rgb(buf, HsW, HsH);
        }
        await feed; await p.WaitForExitAsync(ct);
        return res;
    }
}
