using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// `indexnode cast-candidates --job &lt;id&gt; [--out &lt;dir&gt;] [--write] [--dry]`: rebuilds a job's 12 candidates per main character (cast/cand_*.json, cand_*_k.jpg, cand_hs_*_k.jpg, sheet_*.jpg) from the stored frames and
/// faces without re-running cast. The stored clusters, embeddings and boxes (faces.json, faces.f16) are kept; only the small face detector runs again, on the frames that hold a main character's face, to get the
/// landmarks (pitch, eyes) the stored data lacks; detections are matched to stored faces by overlap. It touches only cast's candidate files (one writer: cast's own outputs) and never the clusters, describe or publish.
/// Without --write it writes to --out (a folder) and the store is only read. After a re-pick, describe must re-score the candidates (its expression scores and picks are by candidate index).
/// </summary>
public static class CastCandidates
{
    public sealed record Plan(int Cluster, List<FaceRec> Old, List<FaceRec> New, int Pool, int Unmatched, int Dropped);

    /// <summary>The candidate faces of one cluster, as the stored cand_&lt;id&gt;.json lists them: (t, box) per k.</summary>
    public static List<(double T, double[] Box)> StoredCandidates(JsonArray? cand) =>
        cand?.OfType<JsonObject>().Select(c => (c["t"]!.GetValue<double>(), (c["box"] as JsonArray)!.Select(v => v!.GetValue<double>()).ToArray())).ToList() ?? [];

    public static double Iou(double[] a, double[] b)
    {
        double x0 = Math.Max(a[0], b[0]), y0 = Math.Max(a[1], b[1]), x1 = Math.Min(a[0] + a[2], b[0] + b[2]), y1 = Math.Min(a[1] + a[3], b[1] + b[3]);
        double i = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0), u = a[2] * a[3] + b[2] * b[3] - i;
        return u <= 0 ? 0 : i / u;
    }

    /// <summary>The stored faces of faces.json (+ faces.f16 embeddings), in file order.</summary>
    public static List<FaceRec> LoadFaces(JsonObject doc, byte[]? f16, SubtitleBand? subs, double frameW)
    {
        int dim = doc["dim"]?.GetValue<int>() ?? 512; var list = new List<FaceRec>(); int i = 0;
        foreach (var f in (doc["faces"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var b = (f["box"] as JsonArray)!.Select(v => v!.GetValue<float>()).ToArray(); double t = f["t"]!.GetValue<double>();
            var r = new FaceRec { T = t, X = b[0], Y = b[1], W = b[2], H = b[3], Score = f["score"]!.GetValue<float>(), Blur = f["blur"]!.GetValue<double>(), Quality = f["quality"]!.GetValue<double>(), Cluster = f["cluster"]!.GetValue<int>(), Other = f["other"]?.GetValue<double>() ?? 0, Sub = subs?.At(t, frameW) };
            if (f16 != null && f16.Length >= (i + 1) * dim * 2) { r.Emb = new float[dim]; for (int d = 0; d < dim; d++) r.Emb[d] = (float)BitConverter.ToHalf(f16, (i * dim + d) * 2); }
            list.Add(r); i++;
        }
        return list;
    }

    /// <summary>Landmarks for the given faces: <paramref name="detect"/> runs the detector on the frame at a time; detections are matched to the faces of that frame by box overlap (at least 0.5). Faces without a match keep Quality 0 (left out); faces that fail the filter now (pitch) too.</summary>
    public static (int Matched, int Unmatched, int Dropped) Annotate(IReadOnlyList<FaceRec> faces, Func<double, (List<FaceDet> Dets, Rgb Im)> detect, int frameH)
    {
        int matched = 0, unmatched = 0, dropped = 0;
        foreach (var g in faces.GroupBy(f => f.T))
        {
            var (dets, im) = detect(g.Key);
            foreach (var f in g)
            {
                var best = dets.Select(d => (D: d, I: Iou([f.X, f.Y, f.W, f.H], [d.X, d.Y, d.W, d.H]))).OrderByDescending(x => x.I).FirstOrDefault();
                if (best.D == null || best.I < 0.5) { f.Quality = 0; unmatched++; continue; }
                var (yaw, roll) = FaceMath.Pose(best.D.Kps);
                f.Kps = best.D.Kps; f.Yaw = Math.Round(yaw, 3); f.Roll = Math.Round(roll, 1); f.Pitch = Math.Round(FaceMath.Pitch(best.D.Kps), 3); f.Eyes = FaceMath.EyeEnergy(im, best.D.Kps);
                if (!CastLogic.Passes(f)) { f.Quality = 0; dropped++; continue; }
                f.Quality = CastLogic.QualityOf(f, frameH); matched++;
            }
        }
        return (matched, unmatched, dropped);
    }

    public static async Task<int> RunAsync(Args a)
    {
        var id = a.Get("job") ?? throw new ArgumentException("cast-candidates --job <id> [--out <dir>] [--write] [--dry] [--clusters 1,2]");
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var tools = new Tools(http, cfg); var ct = CancellationToken.None;
        IStore store; Paths paths;
        if (a.Get("local") is { } local) { store = new LocalStore(Path.GetFullPath(local)); paths = new Paths(""); }
        else { store = new RestStore(http, cfg.Sas); paths = new Paths(a.Get("prefix") ?? "index/"); }
        bool write = a.Has("write"); var outDir = a.Get("out") ?? Path.Combine(Path.GetTempPath(), "cast-candidates", id);
        async Task<byte[]?> Read(string f) => (await store.GetAsync(paths.Video(id, f), ct))?.Data;
        var facesDoc = JsonNode.Parse(await Read("cast/faces.json") ?? throw new InvalidOperationException($"{id}: no cast/faces.json (run cast)")) as JsonObject ?? throw new InvalidOperationException("faces.json");
        var clustersDoc = JsonNode.Parse(await Read("cast/clusters.json") ?? throw new InvalidOperationException($"{id}: no cast/clusters.json"))!.AsObject();
        int fw = facesDoc["source"]?["width"]?.GetValue<int>() ?? 360, fh = facesDoc["source"]?["height"]?.GetValue<int>() ?? 640;
        var subs = SubtitleBand.Parse(await Read("ocr/lines.json"));
        var faces = LoadFaces(facesDoc, await FrameChunks.ReadAsync(store, paths.Video(id, "cast/faces.f16"), ct), subs, fw); // a ghost blob: hydrated first
        var only = a.Get("clusters")?.Split(',').Select(int.Parse).ToHashSet();
        var main = (clustersDoc["clusters"] as JsonArray)!.OfType<JsonObject>().Where(c => c["main"]?.GetValue<bool>() == true).Select(c => c["id"]!.GetValue<int>()).Where(c => only == null || only.Contains(c)).ToList();
        var pool = faces.Where(f => main.Contains(f.Cluster) && f.Emb.Length > 0).ToList();
        Console.Error.WriteLine($"{id}: {faces.Count} stored faces, {main.Count} main clusters, {pool.Count} faces to re-score on {pool.Select(f => f.T).Distinct().Count()} frames");

        // the stored frames (one per shot) and the small detector
        var work = Path.Combine(Path.GetTempPath(), "cast-candidates-frames", id);
        var fs = await FrameChunks.DownloadAsync(store, paths, id, "frames", work, ct) ?? throw new InvalidOperationException($"{id}: no frames/");
        var (detPath, _) = await Models.EnsureBuffaloSAsync(http, ct);
        using var det = new Scrfd(detPath, Math.Max(1, a.Get("threads") is { } th ? int.Parse(th) : Environment.ProcessorCount));
        var rowOf = new Dictionary<double, (ChunkInfo C, ChunkFrame F)>();
        foreach (var c in fs.Chunks) foreach (var f in c.Frames) rowOf[Math.Round(f.T, 3)] = (c, f);
        var handles = new Dictionary<string, byte[]>();
        byte[] FrameJpeg(double t) { var (c, f) = rowOf[Math.Round(t, 3)]; var file = fs.ChunkFile(c); using var h = File.OpenHandle(file); var b = new byte[f.Len]; RandomAccess.Read(h, b, f.Off); return b; }
        Rgb? last = null; double lastT = double.NaN;
        var (matched, unmatched, dropped) = Annotate(pool, t => { var im = Imaging.DecodeJpeg(FrameJpeg(t)); return (det.Detect(im), im); }, fh);
        Console.Error.WriteLine($"landmarks: {matched} matched, {unmatched} without a detection, {dropped} now fail the filter (pitch)");

        Directory.CreateDirectory(Path.Combine(outDir, "cast")); int changed = 0; var report = new JsonArray();
        foreach (var c in main)
        {
            var cl = pool.Where(f => f.Cluster == c).ToList();
            var sheet = CastLogic.Diverse(cl, CastStage.Candidates);
            var oldCand = await Read($"cast/cand_{c}.json") is { } ob ? JsonNode.Parse(ob) as JsonArray : null; var oldList = StoredCandidates(oldCand);
            bool same = oldList.Count == sheet.Count && oldList.Zip(sheet, (o, n) => Math.Abs(o.T - n.T) < 1e-3 && Iou(o.Box, [n.X, n.Y, n.W, n.H]) > 0.9).All(x => x);
            if (!same) changed++;
            report.Add(new JsonObject { ["cluster"] = c, ["changed"] = !same, ["old"] = new JsonArray(oldList.Select((o, k) => { var m = cl.Where(f => Math.Abs(f.T - o.T) < 1e-3).OrderByDescending(f => Iou(o.Box, [f.X, f.Y, f.W, f.H])).FirstOrDefault(); return (JsonNode)new JsonObject { ["k"] = k, ["t"] = Math.Round(o.T, 1), ["oldQuality"] = oldCand![k]!["quality"]!.DeepClone(), ["quality"] = m?.Quality, ["pitch"] = m?.Pitch, ["eyes"] = m?.Eyes, ["yaw"] = m?.Yaw, ["passes"] = m != null && m.Quality > 0 }; }).ToArray()), ["new"] = new JsonArray(sheet.Select(f => (JsonNode)new JsonArray(Math.Round(f.T, 1), f.X, f.Y, Math.Round(f.Quality, 3), f.Pitch)).ToArray()) });
            if (a.Has("dry") || sheet.Count == 0) continue;
            var cdir = Path.Combine(outDir, "cast"); var hs = new List<Rgb>();
            for (int k = 0; k < sheet.Count; k++)
            {
                var f = sheet[k]; var jpg = FrameJpeg(f.T); var im = Imaging.DecodeJpeg(jpg); var head = CastStage.HeadShoulders(im, new FaceRec { X = f.X, Y = f.Y, W = f.W, H = f.H }); hs.Add(head);
                await File.WriteAllBytesAsync(Path.Combine(cdir, $"cand_{c}_{k}.jpg"), jpg, ct);
                await File.WriteAllBytesAsync(Path.Combine(cdir, $"cand_hs_{c}_{k}.jpg"), Imaging.EncodeJpeg(head, 90), ct);
            }
            await Imaging.WriteJpegAsync(tools, Imaging.Grid(hs.Select(h => Imaging.Resize(h, CastStage.SheetTile, CastStage.SheetTile * 5 / 4)).ToList(), 4), Path.Combine(cdir, $"sheet_{c}.jpg"), ct, 4);
            await File.WriteAllTextAsync(Path.Combine(cdir, $"cand_{c}.json"), JsonSerializer.Serialize(sheet.Select((f, k) => new { k, t = f.T, quality = f.Quality, masked = false, solo = f.Solo, sub = f.Sub?.Select(v => Math.Round(v, 1)), pitch = f.Pitch, eyes = f.Eyes, box = new[] { f.X, f.Y, f.W, f.H } })), ct);
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"{id}: candidate sets changed for {changed} of {main.Count} main characters ({outDir})");
        if (write && !a.Has("dry"))
        {
            // cast's own candidate files only: the old ones of these clusters go (their indices are re-numbered), then the new ones
            foreach (var c in main)
            {
                foreach (var b in await store.ListAsync(paths.Video(id, "cast/"), false, false, ct)) { var n = Path.GetFileName(b.Name); if (n.StartsWith($"cand_{c}_") || n.StartsWith($"cand_hs_{c}_")) await store.DeleteAsync(b.Name, ct); }
                foreach (var f in Directory.GetFiles(Path.Combine(outDir, "cast"))) { var n = Path.GetFileName(f); if (n == $"cand_{c}.json" || n == $"sheet_{c}.jpg" || n.StartsWith($"cand_{c}_") || n.StartsWith($"cand_hs_{c}_")) await store.PutAsync(paths.Video(id, "cast/" + n), await File.ReadAllBytesAsync(f, ct), null, null, false, ct, n.EndsWith(".json") ? "application/json" : "image/jpeg"); }
            }
            Console.WriteLine("written to the store; describe must re-score these candidates (expression scores and picks are by candidate index)");
        }
        return 0;
    }
}
