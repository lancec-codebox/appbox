using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IndexNode;

/// <summary>The `cast` process step (no AI): faces on the keyframes, clusters, masks, portraits, contact sheets, credits OCR.</summary>
public static class CastStage
{
    public const int PortraitW = 320, PortraitH = 400, SheetTile = 160, MaxCluster = 3000, Candidates = 12, PosterShortlist = 24, PosterCandidates = 60, PerPair = 3;
    public const string MaskPrompt = "a person wearing a metal mask over the eyes", BarePrompt = "a bare face";
    public sealed record Options(string Model, int Threads, double? Threshold = null, int MinFaces = 20, double? MergeAt = null, double? Border = null);

    public static ModelFile[] ModelFiles(string model) => CastLogic.ModelFor(model).Id == "buffalo_l" ? Models.FaceDefault : Models.FaceYuNet;
    public static ModelFile[] AllModelFiles(string model) => [.. ModelFiles(model), Models.ClipVision, Models.ClipText, Models.ClipTokenizer, Models.OcrDet, Models.OcrRec, Models.OcrDict];

    /// <summary>Keyframe packets only (the AV1 decoder ignores -skip_frame nokey), as a small mkv.</summary>
    public static async Task<string> KeyframeFileAsync(Tools tools, string video, string work, CancellationToken ct)
    {
        var kf = Path.Combine(work, "keyframes.mkv");
        var r = await Proc.RunAsync(await tools.FfmpegAsync(ct), ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", video, "-map", "0:v:0", "-c", "copy", "-bsf:v", "noise=drop='not(key)'", "-an", "-f", "matroska", kf], ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("keyframes: " + r.Stderr.Trim());
        return kf;
    }

    /// <summary>Streams the decoded keyframes as (time, rgb) to <paramref name="each"/>; times come from showinfo and are matched in order.</summary>
    public static async Task DecodeAsync(Tools tools, string kf, int w, int h, Func<double, Rgb, bool, Task> each, Func<int, bool>? want, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(await tools.FfmpegAsync(ct)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "info", "-nostdin", "-i", kf, "-fps_mode", "passthrough", "-vf", "showinfo", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!; using var reg = ct.Register(() => Proc.Kill(p));
        var times = new List<double>(); var lk = new object();
        var errTask = Task.Run(async () =>
        {
            string? line; while ((line = await p.StandardError.ReadLineAsync()) != null)
            {
                int i = line.IndexOf("pts_time:", StringComparison.Ordinal);
                if (i < 0 || !line.Contains("Parsed_showinfo")) continue;
                var s = line[(i + 9)..].Split(' ', 2)[0];
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) lock (lk) times.Add(t);
            }
        });
        int fb = w * h * 3; var buf = new byte[fb]; var st = p.StandardOutput.BaseStream;
        for (int n = 0; ; n++)
        {
            int off = 0; while (off < fb) { int k = await st.ReadAsync(buf.AsMemory(off, fb - off), ct); if (k == 0) break; off += k; }
            if (off < fb) break;
            if (want != null && !want(n)) continue;
            double t = double.NaN; for (int tries = 0; tries < 200; tries++) { lock (lk) if (n < times.Count) { t = times[n]; break; } await Task.Delay(5, ct); }
            await each(t, new Rgb((byte[])buf.Clone(), w, h), true);
        }
        await p.WaitForExitAsync(CancellationToken.None); await errTask;
    }

    public static (IFaceDetector, IFaceEmbedder) Open(string model, string dir, int threads)
    {
        if (CastLogic.ModelFor(model).Id == "buffalo_l") return (new Scrfd(Environment.GetEnvironmentVariable("INDEXNODE_FACE_DET") ?? Models.PathOf(Models.ScrfdDet, dir), threads), new ArcFace(Environment.GetEnvironmentVariable("INDEXNODE_FACE_REC") ?? Models.PathOf(Models.ArcFaceRec, dir), threads));
        return (new YuNet(Models.PathOf(Models.YuNetDet, dir), threads), new SFace(Models.PathOf(Models.SFaceRec, dir), threads));
    }

    public static double FaceBlur(Rgb im, FaceDet d)
    {
        var c = Imaging.CropResize(im, d.X, d.Y, d.W, d.H, 96, 96);
        return Imaging.LaplacianVariance(c);
    }

    /// <summary>The head-and-shoulders crop of a face: box widened 2.5x, 4:5, 320x400.</summary>
    public static Rgb HeadShoulders(Rgb im, FaceRec f, int w = PortraitW, int h = PortraitH)
    {
        double cw = Math.Max(f.W, f.H * 0.8) * 2.5, ch = cw * h / w, cx = f.X + f.W / 2, cy = f.Y + f.H / 2 + f.H * 0.45;
        return Imaging.CropResize(im, cx - cw / 2, cy - ch / 2, cw, ch, w, h);
    }

    public static byte[] FacesF16(IReadOnlyList<FaceRec> faces, int dim)
    {
        var b = new byte[faces.Count * dim * 2]; int o = 0;
        foreach (var f in faces) for (int i = 0; i < dim; i++) { BitConverter.TryWriteBytes(b.AsSpan(o, 2), (Half)f.Emb[i]); o += 2; }
        return b;
    }

    /// <summary>Mask score per face (cosine to the mask prompt minus cosine to the bare-face prompt, SigLIP): positive = masked.</summary>
    public static void ScoreMasks(IReadOnlyList<FaceRec> faces, Func<FaceRec, Rgb> crop, string visionModel, string textModel, string tokenizer, int threads, Action<string> progress, CancellationToken ct)
    {
        var tok = Clip.LoadTokenizer(tokenizer);
        var tm = Clip.EmbedText(textModel, tok, MaskPrompt, threads); var tb = Clip.EmbedText(textModel, tok, BarePrompt, threads);
        using var s = OrtOptions.Session(visionModel, threads, true); var name = s.InputMetadata.Keys.First();
        int S = Clip.Size, i = 0;
        foreach (var f in faces)
        {
            ct.ThrowIfCancellationRequested();
            var im = Imaging.Resize(crop(f), S, S); var t = new DenseTensor<float>([1, 3, S, S]);
            for (int y = 0; y < S; y++) for (int x = 0; x < S; x++) for (int c = 0; c < 3; c++) t[0, c, y, x] = im.Data[(y * S + x) * 3 + c] / 127.5f - 1f;
            using var r = s.Run([NamedOnnxValue.CreateFromTensor(name, t)]);
            var o = r.First(q => q.Name == "pooler_output").AsTensor<float>(); var v = new float[Clip.Dim]; for (int d = 0; d < Clip.Dim; d++) v[d] = o[0, d]; Sscd.L2Normalize(v);
            f.Vis = v; f.MaskScore = Math.Round(Clip.Dot(v, tm) - Clip.Dot(v, tb), 4); f.Masked = f.MaskScore > 0;
            if (++i % 50 == 0) progress($"masks {i}/{faces.Count}");
        }
    }

    public static async Task<string> RunAsync(Tools tools, HttpClient http, string video, double duration, string outDir, Options opt, Action<string> progress, CancellationToken ct, string? modelsDir = null, ChunkDir? keyframes = null)
    {
        var cm = CastLogic.ModelFor(opt.Model); double thr = opt.Threshold ?? cm.Threshold;
        Directory.CreateDirectory(outDir); var work = Path.Combine(Path.GetTempPath(), "indexnode", "cast-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(work);
        try
        {
            var stats = new StepStats("cast");
            using (stats.Phase("models")) await Models.EnsureAllAsync(AllModelFiles(opt.Model), http, ct, modelsDir);
            var (w, h) = keyframes != null ? (keyframes.W, keyframes.H) : await OcrSubs.SizeAsync(tools, video, ct);
            progress("keyframes"); string kf; using (stats.Phase("keyframe-extract")) kf = keyframes != null ? "" : await KeyframeFileAsync(tools, video, work, ct);
            // stored keyframes (keyframes/NNNN.jpgs) when given, else decoded from the video
            Task Each(Func<double, Rgb, bool, Task> each) => keyframes != null ? keyframes.DecodeAsync(tools, (_, t, im) => each(t, im, true), ct) : DecodeAsync(tools, kf, w, h, each, null, ct);
            var (det, emb) = Open(opt.Model, modelsDir ?? NodeConfig.ToolsDir(), opt.Threads);
            var faces = new List<FaceRec>(); var kfTimes = new List<double>(); int nkf = 0, rawDet = 0;
            using (det) using (emb) using (stats.Phase("pass1-wall"))
                await Each((t, im, _) =>
                {
                    kfTimes.Add(t); nkf++;
                    foreach (var d in stats.Time("detect", () => det.Detect(im)))
                    {
                        rawDet++;
                        if (d.H < CastLogic.MinHeight || d.Score < CastLogic.MinScore) continue;
                        var (yaw, roll) = FaceMath.Pose(d.Kps);
                        FaceRec f; using (stats.Phase("blur+pose")) f = new FaceRec { T = Math.Round(t, 3), X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score, Yaw = Math.Round(yaw, 3), Roll = Math.Round(roll, 1), Kps = d.Kps, Blur = Math.Round(FaceBlur(im, d), 1) };
                        if (!CastLogic.Passes(f)) continue;
                        f.Emb = stats.Time("embed", () => emb.Embed(Imaging.AlignFace(im, d.Kps))); f.Quality = CastLogic.QualityOf(f, h); faces.Add(f);
                    }
                    if (nkf % 100 == 0) progress($"faces {nkf} keyframes, {faces.Count} kept");
                    return Task.CompletedTask;
                });
            Log.Info($"cast: {nkf} keyframes, {rawDet} detections, {faces.Count} faces kept");

            // cluster the best MaxCluster faces exactly; attach the rest to the nearest centroid within the threshold
            var core = faces.OrderByDescending(f => f.Quality).Take(MaxCluster).ToList();
            var ids = CastLogic.Cluster(core.Select(f => f.Emb).ToList(), thr);
            for (int i = 0; i < core.Count; i++) core[i].Cluster = ids[i];
            var cents = core.GroupBy(f => f.Cluster).ToDictionary(g => g.Key, g => CastLogic.Centroid(g.Select(f => f.Emb)));
            foreach (var f in faces.Where(f => f.Cluster < 0))
            {
                int bc = -1; double bd = thr; foreach (var (c, v) in cents) { var dd = 1 - CastLogic.Cosine(f.Emb, v); if (dd <= bd) { bd = dd; bc = c; } }
                f.Cluster = bc;
            }
            faces = faces.OrderBy(f => f.T).ThenBy(f => f.X).ToList();
            int rawClusters = faces.Select(f => f.Cluster).Where(c => c >= 0).Distinct().Count();
            // second pass over the clusters: same-person centroid merge, then the borderline pairs whose SigLIP face-crop centroids agree
            double mergeAt = opt.MergeAt ?? cm.Merge, borderAt = opt.Border ?? cm.Border;
            var merges = CastLogic.MergeClusters(faces, mergeAt);
            var probe = faces.Where(f => f.Cluster >= 0).GroupBy(f => f.Cluster).Where(g => g.Count() >= opt.MinFaces).SelectMany(g => g.OrderByDescending(f => f.Quality).Take(8)).ToList();
            if (borderAt > mergeAt && probe.Count > 0)
            {
                progress("merge"); using var mergePhase = stats.Phase("merge-probe");
                var pt = probe.GroupBy(f => f.T).ToDictionary(g => g.Key, g => g.ToList()); var pcrop = new Dictionary<FaceRec, Rgb>();
                await Each((t, im, _) => { if (pt.TryGetValue(Math.Round(t, 3), out var fs)) foreach (var f in fs) pcrop[f] = HeadShoulders(im, f); return Task.CompletedTask; });
                ScoreMasks(probe.Where(pcrop.ContainsKey).ToList(), f => Imaging.CropResize(pcrop[f], pcrop[f].W * 0.2, pcrop[f].H * 0.12, pcrop[f].W * 0.6, pcrop[f].H * 0.5, 224, 224), Models.PathOf(Models.ClipVision, modelsDir), Models.PathOf(Models.ClipText, modelsDir), Models.PathOf(Models.ClipTokenizer, modelsDir), opt.Threads, progress, ct);
                foreach (var bp in CastLogic.BorderPairs(faces, mergeAt, borderAt)) Log.Info($"cast: borderline pair {bp.A}/{bp.B}: face distance {bp.Dist}, SigLIP cosine {bp.Vis}{(bp.Dist <= borderAt && bp.Vis >= CastLogic.VisAgree ? " -> merge" : "")}");
                merges.AddRange(CastLogic.MergeClusters(faces, mergeAt, borderAt));
            }
            Log.Info($"cast: {rawClusters} clusters, {merges.Count} merged -> {faces.Select(f => f.Cluster).Where(c => c >= 0).Distinct().Count()}");
            var main = CastLogic.MainClusters(faces, opt.MinFaces);

            // second pass over the keyframes: crops of the faces we need (masks, portraits, sheets, stills)
            var need = new List<FaceRec>(); var sheets = new Dictionary<int, List<FaceRec>>();
            foreach (var c in main) { var cl = faces.Where(f => f.Cluster == c).ToList(); sheets[c] = CastLogic.Diverse(cl, Candidates); need.AddRange(cl.OrderByDescending(f => f.Quality).Take(40)); }
            // poster candidates: keyframes where any two main clusters appear together, both faces large and sharp; the best PerPair per cluster pair
            // (the leads are only known after describe, so publish filters these by the two lead clusters)
            var mainSet = main.ToHashSet(); var pairs = new List<(double T, FaceRec A, FaceRec B, double Score)>();
            foreach (var g in faces.Where(f => mainSet.Contains(f.Cluster)).GroupBy(f => f.T))
            {
                var best = g.GroupBy(f => f.Cluster).Select(x => x.OrderByDescending(f => f.Quality).First()).OrderByDescending(f => f.H).ToList();
                for (int i = 0; i < best.Count; i++) for (int j = i + 1; j < best.Count; j++)
                    pairs.Add((g.Key, best[i], best[j], Math.Min(best[i].Quality, best[j].Quality) * Math.Min(1.0, Math.Min(best[i].H, best[j].H) / (0.2 * h))));
            }
            var pairCand = pairs.GroupBy(x => (Math.Min(x.A.Cluster, x.B.Cluster), Math.Max(x.A.Cluster, x.B.Cluster))).SelectMany(g => g.OrderByDescending(x => x.Score).Take(PerPair)).OrderByDescending(x => x.Score).Take(PosterCandidates * 2).ToList();
            foreach (var pc in pairCand) { need.Add(pc.A); need.Add(pc.B); }
            need = need.Distinct().ToList(); var byTime = need.GroupBy(f => f.T).ToDictionary(g => g.Key, g => g.ToList());
            var pairFrames = new Dictionary<double, Rgb>(); var hs = new Dictionary<FaceRec, Rgb>(); var full = new Dictionary<FaceRec, Rgb>(); var candSet = sheets.Values.SelectMany(x => x).ToHashSet();
            using var crops = stats.Phase("crop-pass");
            await Each((t, im, _) =>
            {
                if (byTime.TryGetValue(Math.Round(t, 3), out var fs)) foreach (var f in fs) { hs[f] = HeadShoulders(im, f); if (candSet.Contains(f)) full[f] = im; }
                if (pairCand.Any(x => Math.Abs(x.T - t) < 1e-3)) pairFrames[Math.Round(t, 3)] = im;
                return Task.CompletedTask;
            });
            crops.Dispose(); progress("masks"); using var masks = stats.Phase("masks");
            var vision = Models.PathOf(Models.ClipVision, modelsDir); var text = Models.PathOf(Models.ClipText, modelsDir); var tokp = Models.PathOf(Models.ClipTokenizer, modelsDir);
            ScoreMasks(need.Where(hs.ContainsKey).ToList(), f => Imaging.CropResize(hs[f], hs[f].W * 0.2, hs[f].H * 0.12, hs[f].W * 0.6, hs[f].H * 0.5, 224, 224), vision, text, tokp, opt.Threads, progress, ct);

            masks.Dispose(); var clusters = new List<object>(); var castDir = Path.Combine(outDir, "cast"); Directory.CreateDirectory(castDir);
            foreach (var g in faces.Where(f => f.Cluster >= 0).GroupBy(f => f.Cluster).OrderBy(g => g.Key))
            {
                var list = g.ToList(); bool isMain = main.Contains(g.Key);
                clusters.Add(new { id = g.Key, faces = list.Count, screenSeconds = CastLogic.ScreenSeconds(list, kfTimes), first = list.Min(f => f.T), last = list.Max(f => f.T), main = isMain, masked = isMain && list.Where(hs.ContainsKey).Any() && list.Where(hs.ContainsKey).Count(f => f.Masked) > list.Count(hs.ContainsKey) / 2, centroid = CastLogic.Centroid(list.Select(f => f.Emb)).Select(x => MathF.Round(x, 5)) });
                if (!isMain) continue;
                var pf = CastLogic.PortraitFace(list.Where(hs.ContainsKey));
                if (pf != null) await Imaging.WriteJpegAsync(tools, hs[pf], Path.Combine(castDir, $"portrait_{g.Key}.jpg"), ct);
                var cand = sheets[g.Key].Where(hs.ContainsKey).ToList();
                if (cand.Count > 0)
                {
                    await Imaging.WriteJpegAsync(tools, Imaging.Grid(cand.Select(f => Imaging.Resize(hs[f], SheetTile, SheetTile * 5 / 4)).ToList(), 4), Path.Combine(castDir, $"sheet_{g.Key}.jpg"), ct, 4);
                    for (int k = 0; k < cand.Count; k++) { if (full.TryGetValue(cand[k], out var fi)) await Imaging.WriteJpegAsync(tools, fi, Path.Combine(castDir, $"cand_{g.Key}_{k}.jpg"), ct, 5); if (hs.TryGetValue(cand[k], out var hh)) await Imaging.WriteJpegAsync(tools, hh, Path.Combine(castDir, $"cand_hs_{g.Key}_{k}.jpg"), ct, 4); }
                    await File.WriteAllTextAsync(Path.Combine(castDir, $"cand_{g.Key}.json"), JsonSerializer.Serialize(cand.Select((f, k) => new { k, t = f.T, quality = f.Quality, masked = f.Masked, box = new[] { f.X, f.Y, f.W, f.H } })), ct);
                }
            }
            // poster candidates: both faces unmasked (after the mask scores); all kept as cast/poster_<k>.jpg, ordered by score; poster_sheet.jpg shows the best PosterShortlist for describe
            var pcs = pairCand.Where(x => !x.A.Masked && !x.B.Masked && pairFrames.ContainsKey(Math.Round(x.T, 3))).Take(PosterCandidates).ToList();
            if (pcs.Count > 0)
            {
                var tiles = new List<Rgb>();
                for (int k = 0; k < pcs.Count; k++) { var fr = pairFrames[Math.Round(pcs[k].T, 3)]; await Imaging.WriteJpegAsync(tools, fr, Path.Combine(castDir, $"poster_{k}.jpg"), ct, 3); if (k < PosterShortlist) tiles.Add(Imaging.Resize(fr, 160, 284)); }
                await Imaging.WriteJpegAsync(tools, Imaging.Grid(tiles, 6), Path.Combine(castDir, "poster_sheet.jpg"), ct, 4);
                await File.WriteAllTextAsync(Path.Combine(castDir, "poster_cands.json"), JsonSerializer.Serialize(pcs.Select((x, k) => new { k, t = x.T, clusters = new[] { x.A.Cluster, x.B.Cluster }, boxes = new[] { new[] { x.A.X, x.A.Y, x.A.W, x.A.H }, new[] { x.B.X, x.B.Y, x.B.W, x.B.H } }, score = Math.Round(x.Score, 3), shortlist = k < PosterShortlist })), ct);
            }
            await File.WriteAllBytesAsync(Path.Combine(castDir, "faces.f16"), FacesF16(faces, cm.Dim), ct);
            await File.WriteAllTextAsync(Path.Combine(castDir, "faces.json"), JsonSerializer.Serialize(new { v = 1, model = cm.Id, dim = cm.Dim, threshold = thr, keyframes = nkf, faces = faces.Select((f, i) => new { i, t = f.T, box = new[] { MathF.Round(f.X, 1), MathF.Round(f.Y, 1), MathF.Round(f.W, 1), MathF.Round(f.H, 1) }, score = MathF.Round(f.Score, 3), blur = f.Blur, quality = f.Quality, cluster = f.Cluster, masked = hs.ContainsKey(f) ? (bool?)f.Masked : null }) }), ct);
            await File.WriteAllTextAsync(Path.Combine(castDir, "clusters.json"), JsonSerializer.Serialize(new { v = 1, model = cm.Id, threshold = thr, mergeAt, borderAt, rawClusters, merges = merges.Select(m => new { into = m.Into, from = m.From, dist = m.Dist, vis = m.Vis }), clusters }), ct);
            progress("credits");
            List<(double Start, double End, string Text)> credits; using (stats.Phase("credits-ocr")) credits = await CreditsAsync(tools, video, w, h, duration, opt.Threads, modelsDir, ct);
            stats.Log(nkf, "keyframes", opt.Threads);
            await File.WriteAllTextAsync(Path.Combine(castDir, "credits.json"), JsonSerializer.Serialize(new { v = 1, lines = credits.Select(c => new { start = c.Start, end = c.End, text = c.Text }) }), ct);
            return $"{cm.Id}: {nkf} keyframes, {faces.Count} faces, {faces.Select(f => f.Cluster).Where(c => c >= 0).Distinct().Count()} clusters, {main.Count} main";
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>Credits OCR: full frames at 1 fps over the first and last 3 minutes; lines with 主演, 饰 or 领衔, merged over time.</summary>
    public static async Task<List<(double Start, double End, string Text)>> CreditsAsync(Tools tools, string video, int w, int h, double duration, int threads, string? modelsDir, CancellationToken ct)
    {
        using var eng = new OcrEngine(Models.PathOf(Models.OcrDet, modelsDir), Models.PathOf(Models.OcrRec, modelsDir), Models.PathOf(Models.OcrDict, modelsDir), Math.Max(1, threads));
        var spans = duration > 360 ? new[] { (0.0, 180.0), (duration - 180, 180.0) } : new[] { (0.0, duration) };
        var seen = new Dictionary<string, (double S, double E)>(); int fb = w * h * 3;
        foreach (var (start, len) in spans)
        {
            var psi = new ProcessStartInfo(await tools.FfmpegAsync(ct)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", start.ToString("F3", CultureInfo.InvariantCulture), "-t", len.ToString("F3", CultureInfo.InvariantCulture), "-i", video, "-an", "-vf", "fps=1", "-f", "rawvideo", "-pix_fmt", "bgr24", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!; using var reg = ct.Register(() => Proc.Kill(p)); _ = p.StandardError.ReadToEndAsync();
            var buf = new byte[fb];
            for (int i = 0; ; i++)
            {
                int off = 0; while (off < fb) { int k = await p.StandardOutput.BaseStream.ReadAsync(buf.AsMemory(off, fb - off), ct); if (k == 0) break; off += k; }
                if (off < fb) break;
                foreach (var l in eng.Recognize(buf, w, h))
                {
                    if (!CastLogic.CreditWords.Any(c => l.Text.Contains(c))) continue;
                    double t = start + i; var key = OpenCc.T2S.Convert(l.Text);
                    seen[key] = seen.TryGetValue(key, out var e) ? (e.S, t + 1) : (t, t + 1);
                }
            }
            await p.WaitForExitAsync(CancellationToken.None);
        }
        return seen.Select(kv => (kv.Value.S, kv.Value.E, kv.Key)).OrderBy(x => x.S).ToList();
    }
}
