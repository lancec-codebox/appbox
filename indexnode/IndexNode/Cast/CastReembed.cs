using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// `indexnode cast reembed &lt;id&gt;|--ids &lt;file&gt; --rec w600k_mbf|w600k_r50|w600k_r50_int8|glintr100 [--rec-path file.onnx] [--decoder libjpeg|ffmpeg] [--fp32] [--out-local dir] [--threads N] [--local root]`: a dev tool for a side-by-side face-model comparison.
/// For each face of the stored cast/faces.json it reads the stored frame the face came from (a byte range of frames/&lt;chunk&gt;.mjpeg), aligns the face from its stored landmarks (Imaging.AlignFace, exactly as CastFrames does
/// for its embedder, with no new detection) and embeds it with the chosen recognizer. Writes &lt;dir&gt;/&lt;id&gt;/faces_&lt;model&gt;.f16 (float16, same order as cast/faces.f16) and faces_&lt;model&gt;.json. Read-only on the store.
/// </summary>
public static class CastReembed
{
    public sealed record Timing(double Read, double Decode, double Align, double Embed, double Wall);
    public sealed record Check(int Faces, double MaxAbsDiff, double MaxAbsDiffHalf, int FacesOver, double MinCosine);
    /// <summary>Change in pairwise cosine over random face pairs: Storage = our float32 against our own float16 rounding (what float16 storage alone does); Stored = our float32 against the stored faces.f16 (storage plus any decoder drift). Flips = pairs on the other side of a bar (0.70, 0.80) in the two.</summary>
    public sealed record PairStats(int Pairs, double MaxStorage, double MedianStorage, int FlipsStorage, double MaxStored, double MedianStored, int FlipsStored);
    public sealed record Result(string Id, string Model, int Faces, int Dim, int Frames, byte[] F16, Timing Time, Check? Vs, byte[] F32, PairStats? Pairs, int[]? Idx = null);

    public static readonly string[] Names = ["w600k_mbf", "w600k_r50", "w600k_r50_int8", "glintr100"];

    public static async Task<string> ModelPathAsync(string rec, HttpClient http, CancellationToken ct) => rec switch
    {
        "w600k_mbf" => (await Models.EnsureBuffaloSAsync(http, ct)).Rec,
        "w600k_r50" => await Models.EnsureAsync(Models.ArcFaceRec, http, ct),
        "glintr100" => await Models.EnsureAsync(Models.GlintR100, http, ct),
        "w600k_r50_int8" => throw new ArgumentException("--rec w600k_r50_int8 needs --rec-path <the int8 file> (make it with scripts/quantize_face.py)"),
        _ => throw new ArgumentException("--rec: " + string.Join(", ", Names)),
    };

    /// <summary>The fast decoder: the repository's ffmpeg-exact YCbCr decoder plus swscale's yuv to rgb at the same size (about 18 ms for a 360x640 frame; managed libjpeg takes several times that); a JPEG it does not take falls back to libjpeg.</summary>
    public static Rgb DecodeYuv(byte[] jpg) { var y = JpegYuv.Decode(jpg); return y == null ? Imaging.DecodeJpeg(jpg) : Swscale.ScaleYuv(y, y.W, y.H); }

    /// <summary>One model of a pass: its name and a maker of its embedder (one per thread).</summary>
    public sealed record ModelRun(string Model, Func<IFaceEmbedder> Make);

    public static async Task<Result> RunAsync(IStore store, Paths paths, string id, string model, Func<IFaceEmbedder> makeEmbedder, Func<byte[], Rgb> decode, int threads, CancellationToken ct, HashSet<int>? cropIdx = null, Action<int, Rgb>? onCrop = null)
        => (await RunManyAsync(store, paths, id, [new ModelRun(model, makeEmbedder)], decode, threads, ct, cropIdx, onCrop))[0];

    /// <summary>One pass over a drama for all <paramref name="models"/>: each frames chunk is fetched whole once (one GET, or one read of its member in frames.zip), each needed JPEG is decoded once and each face aligned once, then every model embeds the aligned faces. One result per model, in order; read, decode and align are the shared cost, embed and the pair stats are the model's own.</summary>
    public static async Task<Result[]> RunManyAsync(IStore store, Paths paths, string id, IReadOnlyList<ModelRun> models, Func<byte[], Rgb> decode, int threads, CancellationToken ct, HashSet<int>? cropIdx = null, Action<int, Rgb>? onCrop = null, int maxPerGroup = 0, IReadOnlyCollection<int>? onlyFaces = null)
    {
        var wall = Stopwatch.StartNew();
        var fj = StoredJson.Object(await FrameChunks.ReadAsync(store, paths.Video(id, "cast/faces.json"), ct) ?? throw new InvalidOperationException($"{id}: no cast/faces.json (cast has not run)"), $"{id}/cast/faces.json");
        var faces = (fj["faces"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var man = JsonDocument.Parse(await FrameChunks.ReadAsync(store, paths.Video(id, "frames/manifest.json"), ct) ?? throw new InvalidOperationException($"{id}: no frames/ (the stored frames are what the faces came from)")).RootElement;
        if ((man.TryGetProperty("container", out var co) ? co.GetString() : "mp4") != "mjpeg") throw new InvalidOperationException($"{id}: frames are {co.GetString()}, only mjpeg chunks can be read");
        var rowOf = new Dictionary<double, (string Chunk, long Off, int Len)>();
        foreach (var c in man.GetProperty("chunks").EnumerateArray())
        {
            var n = c.GetProperty("chunk").GetString()!;
            var ci = FrameChunks.ParseChunk(n, await FrameChunks.ReadAsync(store, paths.Video(id, $"frames/{n}.json"), ct) ?? throw new InvalidOperationException($"{id}: no frames/{n}.json"), n + ".json");
            foreach (var f in ci.Frames) rowOf[Math.Round(f.T, 3)] = (n, f.Off, f.Len);
        }
        int[]? keep = null;
        if (onlyFaces != null) { keep = onlyFaces.Distinct().OrderBy(i => i).ToArray(); if (keep.Any(i => i < 0 || i >= faces.Count)) throw new ArgumentOutOfRangeException(nameof(onlyFaces)); }   // exactly these faces (actors build); rows come out ascending, Idx says which
        else if (maxPerGroup > 0)
        {
            var cj = StoredJson.Object(await FrameChunks.ReadAsync(store, paths.Video(id, "cast/clusters.json"), ct) ?? throw new InvalidOperationException($"{id}: no cast/clusters.json (--max-faces-per-group needs the groups)"), $"{id}/cast/clusters.json");
            var main = (cj["clusters"] as JsonArray)?.OfType<JsonObject>().Where(c => c["main"]?.GetValue<bool>() == true).Select(c => c["id"]!.GetValue<int>()).ToHashSet() ?? [];
            keep = PickPerGroup(faces.Select((f, i) => (I: i, G: f["cluster"]?.GetValue<int>() ?? -1, T: f["t"]!.GetValue<double>(), Q: f["quality"]?.GetValue<double>() ?? 0)).Where(x => main.Contains(x.G)), maxPerGroup);
        }
        var keepSet = keep?.ToHashSet();
        var work = faces.Select((f, i) => (I: i, T: Math.Round(f["t"]!.GetValue<double>(), 3), Kps: (f["kps"] as JsonArray)?.Select(v => v!.GetValue<float>()).ToArray() ?? [])).Where(w => keepSet == null || keepSet.Contains(w.I)).ToList();
        if (work.Any(w => w.Kps.Length < 10)) throw new InvalidOperationException($"{id}: a stored face has no landmarks (cast/faces.json kps)");
        var frames = work.GroupBy(w => w.T).OrderBy(g => g.Key).ToList();
        foreach (var g in frames) if (!rowOf.ContainsKey(g.Key)) throw new InvalidOperationException($"{id}: no stored frame at t={g.Key}");
        var chunks = frames.GroupBy(g => rowOf[g.Key].Chunk, StringComparer.Ordinal).OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        int parts = Math.Max(1, Math.Min(threads, frames.Count)), nm = models.Count; var dims = new int[nm];
        var emb = models.Select(_ => new float[faces.Count][]).ToArray(); var tEmb = new long[nm]; long tRead = 0, tDec = 0, tAlign = 0;
        var es = Enumerable.Range(0, parts).Select(_ => models.Select(m => m.Make()).ToArray()).ToArray();
        try
        {
            for (int m = 0; m < nm; m++) dims[m] = es[0][m].Dim;
            foreach (var ch in chunks)
            {
                var t0 = Stopwatch.GetTimestamp();
                var data = (await store.GetAsync(paths.Video(id, $"frames/{ch.Key}.mjpeg"), ct))?.Data ?? throw new FileNotFoundException($"frames/{ch.Key}.mjpeg");   // the whole chunk, once
                tRead += Stopwatch.GetTimestamp() - t0;
                var list = ch.ToList();
                await Task.WhenAll(Enumerable.Range(0, parts).Select(p => Task.Run(() =>
                {
                    var batch = new List<Rgb>(); var idx = new List<int>();
                    void Flush()
                    {
                        if (batch.Count == 0) return;
                        for (int m = 0; m < nm; m++) { var t = Stopwatch.GetTimestamp(); var r = es[p][m].EmbedBatch(batch); Interlocked.Add(ref tEmb[m], Stopwatch.GetTimestamp() - t); for (int k = 0; k < r.Length; k++) emb[m][idx[k]] = r[k]; }
                        batch.Clear(); idx.Clear();
                    }
                    int lo = list.Count * p / parts, hi = list.Count * (p + 1) / parts;
                    for (int q = lo; q < hi; q++)
                    {
                        var g = list[q]; var fr = rowOf[g.Key];
                        if (fr.Off < 0 || fr.Off + fr.Len > data.Length) throw new InvalidDataException($"{id}: frame at t={g.Key} lies outside frames/{ch.Key}.mjpeg");
                        var t1 = Stopwatch.GetTimestamp(); var im = decode(data.AsSpan((int)fr.Off, fr.Len).ToArray()); var t2 = Stopwatch.GetTimestamp();
                        foreach (var w in g) { var al = Imaging.AlignFace(im, w.Kps); if (cropIdx != null && cropIdx.Contains(w.I)) onCrop?.Invoke(w.I, al); batch.Add(al); idx.Add(w.I); }
                        Interlocked.Add(ref tDec, t2 - t1); Interlocked.Add(ref tAlign, Stopwatch.GetTimestamp() - t2);
                        if (batch.Count >= ArcFace.MaxBatch) Flush();
                    }
                    Flush();
                }, ct)));
            }
        }
        finally { foreach (var row in es) foreach (var e in row) e.Dispose(); }
        double S(long t) => Math.Round(t / (double)Stopwatch.Frequency, 3);
        var storedF16 = await FrameChunks.ReadAsync(store, paths.Video(id, "cast/faces.f16"), ct);
        var res = new Result[nm]; var rows = keep ?? Enumerable.Range(0, faces.Count).ToArray();   // a subset (--max-faces-per-group) is written in ascending face order, with its indexes
        for (int m = 0; m < nm; m++)
        {
            int dim = faces.Count == 0 ? 512 : dims[m]; var e = rows.Select(i => emb[m][i]).ToArray();
            var f16 = new byte[rows.Length * dim * 2];
            for (int i = 0; i < rows.Length; i++) for (int d = 0; d < dim; d++) BitConverter.TryWriteBytes(f16.AsSpan((i * dim + d) * 2, 2), (Half)e[i][d]);
            var f32 = new byte[rows.Length * dim * 4]; for (int i = 0; i < rows.Length; i++) for (int d = 0; d < dim; d++) BitConverter.TryWriteBytes(f32.AsSpan((i * dim + d) * 4, 4), e[i][d]);
            bool same = keep == null && storedF16 is { Length: > 0 } && storedF16.Length == f16.Length;
            Check? vs = same ? Compare(e, storedF16!, dim) : null;
            res[m] = new Result(id, models[m].Model, rows.Length, dim, frames.Count, f16, new Timing(S(tRead), S(tDec), S(tAlign), S(tEmb[m]), Math.Round(wall.Elapsed.TotalSeconds, 3)), vs, f32, PairChange(e, same ? storedF16 : null, dim, 10_000), keep);
        }
        return res;
    }

    /// <summary>Random face pairs (fixed seed): |cos(a,b) in float32 - cos(a,b) from float16|, max and median, and how many pairs straddle 0.70 or 0.80 between the two.</summary>
    public static PairStats? PairChange(float[][] ours, byte[]? stored, int dim, int pairs, int seed = 12345)
    {
        int n = ours.Length; if (n < 2) return null;
        double Cos(Func<int, int, double> g, int a, int b) { double dot = 0, na = 0, nb = 0; for (int d = 0; d < dim; d++) { double x = g(a, d), y = g(b, d); dot += x * y; na += x * x; nb += y * y; } return dot / Math.Sqrt(na * nb); }
        double F32(int i, int d) => ours[i][d]; double Own16(int i, int d) => (float)(Half)ours[i][d]; double St16(int i, int d) => (float)BitConverter.ToHalf(stored!, (i * dim + d) * 2);
        var rnd = new Random(seed); var ds = new List<double>(); var dt = new List<double>(); int fs = 0, ft = 0;
        bool Flip(double a, double b) => (a >= 0.70) != (b >= 0.70) || (a >= 0.80) != (b >= 0.80);
        for (int k = 0; k < pairs; k++)
        {
            int a = rnd.Next(n), b = rnd.Next(n - 1); if (b >= a) b++;
            double c = Cos(F32, a, b), c16 = Cos(Own16, a, b); ds.Add(Math.Abs(c - c16)); if (Flip(c, c16)) fs++;
            if (stored != null && stored.Length >= n * dim * 2) { double cs = Cos(St16, a, b); dt.Add(Math.Abs(c - cs)); if (Flip(c, cs)) ft++; }
        }
        double Med(List<double> v) { if (v.Count == 0) return double.NaN; v.Sort(); return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2; }
        return new PairStats(pairs, ds.Max(), Med(ds), fs, dt.Count == 0 ? double.NaN : dt.Max(), Med(dt), ft);
    }

    /// <summary>The new embeddings against the stored float16 ones: the largest absolute difference (of the float values, and after rounding ours to float16), and the lowest cosine.</summary>
    public static Check Compare(float[][] ours, byte[] stored, int dim)
    {
        double max = 0, maxHalf = 0, minCos = 1; int over = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            double dot = 0, na = 0, nb = 0;
            for (int d = 0; d < dim; d++)
            {
                double s = (float)BitConverter.ToHalf(stored, (i * dim + d) * 2), o = ours[i][d], oh = (float)(Half)ours[i][d];
                max = Math.Max(max, Math.Abs(o - s)); maxHalf = Math.Max(maxHalf, Math.Abs(oh - s)); dot += s * o; na += s * s; nb += o * o;
            }
            double cos = dot / Math.Sqrt(na * nb); minCos = Math.Min(minCos, cos); if (cos < 0.999) over++;
        }
        return new Check(ours.Length, Math.Round(max, 6), Math.Round(maxHalf, 6), over, Math.Round(minCos, 6));
    }

    /// <summary>At most <paramref name="n"/> faces per group, spread over the group's time span and favouring quality: the span is cut into n equal time bins and each bin gives its highest-quality face (ties: the lower index); empty bins give nothing. Deterministic; returns face indexes ascending.</summary>
    public static int[] PickPerGroup(IEnumerable<(int I, int G, double T, double Q)> faces, int n)
    {
        var all = new List<int>();
        foreach (var g in faces.GroupBy(x => x.G))
        {
            var l = g.ToList(); if (l.Count <= n) { all.AddRange(l.Select(x => x.I)); continue; }
            double t0 = l.Min(x => x.T), t1 = l.Max(x => x.T), span = Math.Max(t1 - t0, 1e-9);
            foreach (var bin in l.GroupBy(x => Math.Min(n - 1, (int)((x.T - t0) / span * n)))) all.Add(bin.OrderByDescending(x => x.Q).ThenBy(x => x.I).First().I);
        }
        all.Sort(); return all.ToArray();
    }

    /// <summary>N face indices spread evenly over <paramref name="faces"/> (all of them when N >= faces).</summary>
    public static HashSet<int> SpreadIndices(int faces, int n) => n <= 0 || faces <= 0 ? [] : n >= faces ? Enumerable.Range(0, faces).ToHashSet() : Enumerable.Range(0, n).Select(i => (int)((long)i * faces / n)).ToHashSet();

    /// <summary>A numpy .npy (v1.0) file of a uint8 array (H, W, 3), RGB: np.load reads it.</summary>
    public static byte[] Npy(Rgb im)
    {
        var hdr = $"{{'descr': '|u1', 'fortran_order': False, 'shape': ({im.H}, {im.W}, 3), }}"; int pad = 64 - (10 + hdr.Length + 1) % 64; hdr += new string(' ', pad % 64) + "\n";
        var o = new byte[10 + hdr.Length + im.Data.Length]; new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' }.CopyTo(o, 0); o[6] = 1; o[7] = 0; o[8] = (byte)(hdr.Length & 255); o[9] = (byte)(hdr.Length >> 8);
        System.Text.Encoding.ASCII.GetBytes(hdr).CopyTo(o, 10); im.Data.CopyTo(o, 10 + hdr.Length); return o;
    }

    public static JsonNode Json(Result r) => JsonSerializer.SerializeToNode(new
    {
        id = r.Id, model = r.Model, faces = r.Faces, dim = r.Dim, frames = r.Frames, file = $"faces_{r.Model}.f16", order = "same as cast/faces.f16",
        seconds = new { read = r.Time.Read, decode = r.Time.Decode, align = r.Time.Align, embed = r.Time.Embed, wall = r.Time.Wall },
        pairCosineChange = r.Pairs == null ? null : new { pairs = r.Pairs.Pairs, storageOnly = new { max = r.Pairs.MaxStorage, median = r.Pairs.MedianStorage, flipsAtBars070And080 = r.Pairs.FlipsStorage }, vsStoredF16 = double.IsNaN(r.Pairs.MaxStored) ? null : new { max = r.Pairs.MaxStored, median = r.Pairs.MedianStored, flipsAtBars070And080 = r.Pairs.FlipsStored } },
        vsStored = r.Vs == null ? null : new { faces = r.Vs.Faces, maxAbsDiff = r.Vs.MaxAbsDiff, maxAbsDiffAfterHalfRounding = r.Vs.MaxAbsDiffHalf, facesBelowCosine0999 = r.Vs.FacesOver, minCosine = r.Vs.MinCosine },
    })!;

    public static string Text(Result r) => $"{r.Id}: {r.Faces} faces on {r.Frames} frames, {r.Model} ({r.Dim}-d): read {r.Time.Read}s, decode {r.Time.Decode}s, align {r.Time.Align}s, embed {r.Time.Embed}s (cpu-seconds summed over threads), wall {r.Time.Wall}s"
        + (r.Pairs == null ? "" : $"\n  pairwise cosine change over {r.Pairs.Pairs} random pairs, f32 vs its own f16 rounding: max {r.Pairs.MaxStorage:G3}, median {r.Pairs.MedianStorage:G3}, {r.Pairs.FlipsStorage} pairs cross 0.70/0.80" + (double.IsNaN(r.Pairs.MaxStored) ? "" : $"; f32 vs the stored f16: max {r.Pairs.MaxStored:G3}, median {r.Pairs.MedianStored:G3}, {r.Pairs.FlipsStored} pairs cross 0.70/0.80"))
        + (r.Vs == null ? "" : $"\n  vs stored faces.f16: max |diff| {r.Vs.MaxAbsDiff} ({r.Vs.MaxAbsDiffHalf} after float16 rounding), min cosine {r.Vs.MinCosine}, {r.Vs.FacesOver} of {r.Vs.Faces} faces below cosine 0.999");

    public static async Task<int> Main2(Args a)
    {
        const string use = "cast reembed <id>|--ids <file> --rec w600k_mbf[,w600k_r50,w600k_r50_int8,glintr100] [--rec-path <file.onnx> (one model only; required for w600k_r50_int8)] [--rec-path-w600k_r50_int8 <file.onnx>] [--decoder fast|libjpeg|ffmpeg] [--fp32] [--dump-crops <N>] [--max-faces-per-group <N>] [--out-local <dir>] [--threads N] [--local <root>] [--prefix index/]";
        var recs = (a.Get("rec") ?? throw new ArgumentException(use)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (recs.Count == 0 || recs.Any(r => !Names.Contains(r))) throw new ArgumentException(use);
        if (a.Get("rec-path") != null && recs.Count > 1) throw new ArgumentException("--rec-path names one file: with several --rec give the int8 file as --rec-path-w600k_r50_int8");
        var ids = new List<string>(); if (a.Get("ids") is { } f) ids.AddRange(File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        ids.AddRange(a.Positional.Skip(1)); if (ids.Count == 0) throw new ArgumentException(use);
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var ct = CancellationToken.None; int threads = a.Get("threads") is { } th ? int.Parse(th) : Environment.ProcessorCount;
        var outRoot = a.Get("out-local") ?? Path.Combine(Path.GetTempPath(), "cast-reembed");
        IStore store; Paths paths;
        if (a.Get("local") is { } local) { store = new LocalStore(Path.GetFullPath(local)); paths = new Paths(""); }
        else { if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or set INDEXNODE_SAS_URL (or --local <root>)"); store = StageZipStore.Wrap(new RestStore(http, cfg.Sas)); paths = new Paths(a.Get("prefix") ?? "index/"); }
        var runs = new List<ModelRun>();
        foreach (var rec in recs)
        {
            var path = (recs.Count == 1 ? a.Get("rec-path") : null) ?? a.Get("rec-path-" + rec) ?? await ModelPathAsync(rec, http, ct);
            runs.Add(new ModelRun(rec, () => new ArcFace(path, 1)));
        }
        Func<byte[], Rgb> decode = (a.Get("decoder") ?? "fast") switch { "fast" => DecodeYuv, "libjpeg" => Imaging.DecodeJpeg, "ffmpeg" => null!, _ => throw new ArgumentException(use) };
        if (a.Get("decoder") == "ffmpeg") { var ff = await new Tools(http, cfg).FfmpegAsync(ct); decode = jpg => FfmpegDecode(ff, jpg); }
        int rc = 0;
        foreach (var id in ids)
        {
            try
            {
                var dir = Path.Combine(outRoot, id); Directory.CreateDirectory(dir);
                int nCrops = a.Get("dump-crops") is { } dc ? int.Parse(dc) : 0; HashSet<int>? want = null; Action<int, Rgb>? onCrop = null;
                if (nCrops > 0) { var nf = ((StoredJson.Object(await FrameChunks.ReadAsync(store, paths.Video(id, "cast/faces.json"), ct) ?? [], $"{id}/cast/faces.json")["faces"] as JsonArray)?.Count) ?? 0; want = SpreadIndices(nf, nCrops); var cd = Path.Combine(dir, "crops"); Directory.CreateDirectory(cd); onCrop = (i, im) => File.WriteAllBytes(Path.Combine(cd, $"{i:D6}.npy"), Npy(im)); }
                var results = await RunManyAsync(store, paths, id, runs, decode, threads, ct, want, onCrop, a.Get("max-faces-per-group") is { } mg ? int.Parse(mg) : 0);
                foreach (var r in results)
                {
                    await File.WriteAllBytesAsync(Path.Combine(dir, $"faces_{r.Model}.f16"), r.F16, ct);
                    if (r.Idx != null) { var ib = new byte[r.Idx.Length * 4]; for (int k = 0; k < r.Idx.Length; k++) BitConverter.TryWriteBytes(ib.AsSpan(k * 4, 4), r.Idx[k]); await File.WriteAllBytesAsync(Path.Combine(dir, $"faces_{r.Model}.idx"), ib, ct); }
                    if (a.Has("fp32")) await File.WriteAllBytesAsync(Path.Combine(dir, $"faces_{r.Model}.f32"), r.F32, ct);
                    await File.WriteAllTextAsync(Path.Combine(dir, $"faces_{r.Model}.json"), Json(r).ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
                    Console.WriteLine(Text(r));
                }
                Console.WriteLine($"  -> {dir}");
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or JsonException or IOException) { Console.Error.WriteLine($"{id}: {e.Message}"); rc = 1; }
        }
        return rc;
    }

    /// <summary>One JPEG through ffmpeg to rgb24 (what CastFrames' decoder produces; libjpeg's IDCT can differ from it by a few levels).</summary>
    static Rgb FfmpegDecode(string ffmpeg, byte[] jpg)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var x in new[] { "-v", "error", "-f", "mjpeg", "-i", "-", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!; var err = p.StandardError.ReadToEndAsync(); var outp = new MemoryStream(); var drain = p.StandardOutput.BaseStream.CopyToAsync(outp);
        p.StandardInput.BaseStream.Write(jpg); p.StandardInput.Close(); drain.GetAwaiter().GetResult(); p.WaitForExit();
        var probe = Imaging.DecodeJpeg(jpg); var data = outp.ToArray();   // the size comes from the JPEG itself
        if (data.Length != probe.W * probe.H * 3) throw new InvalidOperationException("ffmpeg decode: " + err.GetAwaiter().GetResult().Trim());
        return new Rgb(data, probe.W, probe.H);
    }
}
