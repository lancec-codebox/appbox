using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// `indexnode cast recluster &lt;id&gt;|--ids &lt;file&gt; [--merge-at D] [--split] [--split-sim S] [--split-min N] [--threshold T] [--min-faces N] [--local &lt;root&gt;] [--out-local &lt;dir&gt;]`:
/// re-runs the cast clustering of a drama from its stored faces (cast/faces.json, cast/faces.f16, cast/clusters.json of cast.zip): the same core + attach clustering as CastFrames (average linkage at the model's threshold),
/// then the centroid merge at --merge-at (default: the model's, 0.45 for buffalo_s). With --split every main group also gets a 2-means split report. Read-only: no detection, no video, no store write; only local files (--out-local).
/// </summary>
public static class CastRecluster
{
    public sealed record Options(double? Threshold = null, double? MergeAt = null, int MinFaces = CastFrames.MainMinFaces, bool Split = false, double SplitSim = 0.60, int SplitMin = 20);
    public sealed record Group(int Id, int Faces, double Seconds, bool Main);
    public sealed record SplitReport(int Group, int FacesA, int FacesB, double CentreSim, double FitOwn, double FitOther, bool Split);
    public sealed record Result(string Id, string Model, double Threshold, double MergeAt, int Faces, List<Group> Old, List<Group> New, List<CastLogic.Merge> Merges, List<SplitReport> Splits, int Changed, int RawClusters);

    public static CastModel ModelOf(JsonObject faces) => (faces["model"]?.GetValue<string>()) is { } m ? CastLogic.ModelFor(m) : CastLogic.BuffaloS;

    /// <summary>The stored faces, re-clustered. <paramref name="faces"/> carry their stored cluster labels (the old groups) and embeddings.</summary>
    public static Result Analyze(string id, CastModel model, double storedThreshold, IReadOnlyList<FaceRec> faces, Options o)
    {
        double thr = o.Threshold ?? storedThreshold, mergeAt = o.MergeAt ?? model.Merge;
        var usable = faces.Where(f => f.Emb.Length > 0).ToList(); var oldLabel = usable.ToDictionary(f => f, f => f.Cluster);
        var times = usable.Select(f => f.T).Distinct().OrderBy(t => t).ToList();   // faces.json keeps no keyframe list: each face counts the time to the next face time (at most 10 s)
        List<Group> Groups(Func<FaceRec, int> label) { var main = usable.Where(f => label(f) >= 0).GroupBy(label).ToList(); return main.OrderBy(g => g.Key).Select(g => new Group(g.Key, g.Count(), CastLogic.ScreenSeconds(g, times), g.Count() >= o.MinFaces)).ToList(); }
        var old = Groups(f => oldLabel[f]);
        // the same as CastFrames: the best MaxCluster faces exactly, the rest attached to the nearest centroid within the threshold, then the centroid merge
        ClusterRaw(usable, thr);
        int raw = usable.Select(f => f.Cluster).Where(c => c >= 0).Distinct().Count();
        var merges = CastLogic.MergeClusters(usable, mergeAt);
        var nw = Groups(f => f.Cluster);
        // faces that changed group: those outside the new group their old group mostly went to
        int changed = 0;
        foreach (var g in usable.GroupBy(f => oldLabel[f])) { var dom = g.GroupBy(f => f.Cluster).OrderByDescending(x => x.Count()).First().Key; changed += g.Count(f => f.Cluster != dom); }
        var splits = new List<SplitReport>();
        if (o.Split) foreach (var g in usable.Where(f => f.Cluster >= 0).GroupBy(f => f.Cluster).Where(g => g.Count() >= o.MinFaces).OrderBy(g => g.Key)) if (TwoMeans(g.Key, g.ToList(), o) is { } sr) splits.Add(sr);
        return new Result(id, model.Id, thr, mergeAt, usable.Count, old, nw, merges, splits, changed, raw);
    }

    /// <summary>The same as CastFrames: the best MaxCluster faces by quality exactly (average linkage at <paramref name="thr"/>), the rest attached to the nearest centroid within it; sets Cluster on every face (-1: none).</summary>
    public static void ClusterRaw(List<FaceRec> usable, double thr)
    {
        foreach (var f in usable) f.Cluster = -1;
        var core = usable.OrderByDescending(f => f.Quality).Take(CastStage.MaxCluster).ToList();
        var ids = CastLogic.Cluster(core.Select(f => f.Emb).ToList(), thr);
        for (int i = 0; i < core.Count; i++) core[i].Cluster = ids[i];
        var cents = core.GroupBy(f => f.Cluster).ToDictionary(g => g.Key, g => CastLogic.Centroid(g.Select(f => f.Emb)));
        foreach (var f in usable.Where(f => f.Cluster < 0))
        {
            int bc = -1; double bd = thr; foreach (var (c, v) in cents) { var dd = 1 - CastLogic.Cosine(f.Emb, v); if (dd <= bd) { bd = dd; bc = c; } }
            f.Cluster = bc;
        }
    }

    /// <summary>2-means on the unit embeddings of one group (cosine): seeds are the face farthest from the centre and the face farthest from that one; the report is null when a half comes out empty.</summary>
    /// <summary>The 2-means partition of <paramref name="fs"/> (cosine; seeds: the face farthest from the centre and the face farthest from that one): side per face and the two centres; null when a half comes out empty.</summary>
    public static (bool[] Side, float[] Ca, float[] Cb)? Bisect(IReadOnlyList<FaceRec> fs)
    {
        if (fs.Count < 2) return null;
        var c0 = CastLogic.Centroid(fs.Select(f => f.Emb)); var s1 = fs.OrderBy(f => CastLogic.Cosine(f.Emb, c0)).First(); var s2 = fs.OrderBy(f => CastLogic.Cosine(f.Emb, s1.Emb)).First();
        float[] ca = s1.Emb, cb = s2.Emb; var side = new bool[fs.Count];
        for (int it = 0; it < 40; it++)
        {
            bool moved = false;
            for (int i = 0; i < fs.Count; i++) { bool b = CastLogic.Cosine(fs[i].Emb, cb) > CastLogic.Cosine(fs[i].Emb, ca); if (b != side[i]) { side[i] = b; moved = true; } }
            var a = Enumerable.Range(0, fs.Count).Where(i => !side[i]).Select(i => fs[i].Emb).ToList(); var b2 = Enumerable.Range(0, fs.Count).Where(i => side[i]).Select(i => fs[i].Emb).ToList();
            if (a.Count == 0 || b2.Count == 0) return null;
            ca = CastLogic.Centroid(a); cb = CastLogic.Centroid(b2); if (!moved && it > 0) break;
        }
        if (side.All(x => x) || side.All(x => !x)) return null;
        return (side, ca, cb);
    }

    public static SplitReport? TwoMeans(int group, IReadOnlyList<FaceRec> fs, Options o)
    {
        if (Bisect(fs) is not { } bs) return null; var (side, ca, cb) = bs;
        int na = side.Count(s => !s), nb = side.Count(s => s);
        double sim = CastLogic.Cosine(ca, cb);
        var own = new List<double>(); var other = new List<double>();
        for (int i = 0; i < fs.Count; i++) { own.Add(CastLogic.Cosine(fs[i].Emb, side[i] ? cb : ca)); other.Add(CastLogic.Cosine(fs[i].Emb, side[i] ? ca : cb)); }
        return new SplitReport(group, na, nb, Math.Round(sim, 3), Math.Round(Median(own), 3), Math.Round(Median(other), 3), sim < o.SplitSim && Math.Min(na, nb) >= o.SplitMin);
    }

    static double Median(List<double> v) { v.Sort(); return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2; }

    /// <summary>Reads cast/faces.json, faces.f16 and clusters.json of a drama (the store's cast.zip, through the stage-zip wrapper) and analyses them.</summary>
    public static async Task<Result> RunDramaAsync(IJobSource src, Options o, CancellationToken ct)
    {
        var id = src.JobId;
        var fj = await src.ReadAsync("cast/faces.json", ct) ?? throw new InvalidOperationException($"{id}: no cast/faces.json (cast has not run)");
        var f16 = await src.ReadAsync("cast/faces.f16", ct); var cj = await src.ReadAsync("cast/clusters.json", ct) ?? throw new InvalidOperationException($"{id}: no cast/clusters.json");
        var doc = StoredJson.Object(fj, $"{id}/cast/faces.json"); StoredJson.Object(cj, $"{id}/cast/clusters.json");
        var faces = CastCandidates.LoadFaces(doc, f16, null, 360, $"{id}/cast/faces.f16");
        return Analyze(id, ModelOf(doc), doc["threshold"]?.GetValue<double>() ?? CastLogic.BuffaloS.Threshold, faces, o);
    }

    public static string Text(Result r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.Id}: {r.Faces} faces, model {r.Model}, threshold {r.Threshold}, merge-at {r.MergeAt}: {r.Old.Count} stored groups ({r.Old.Count(g => g.Main)} main) -> {r.RawClusters} raw clusters -> {r.New.Count} groups ({r.New.Count(g => g.Main)} main), {r.Merges.Count} merges, {r.Changed} faces changed group");
        sb.AppendLine("  old groups: " + string.Join("  ", r.Old.Where(g => g.Main).Select(g => $"#{g.Id}:{g.Faces}f/{g.Seconds}s")));
        sb.AppendLine("  new groups: " + string.Join("  ", r.New.Where(g => g.Main).Select(g => $"#{g.Id}:{g.Faces}f/{g.Seconds}s")));
        foreach (var m in r.Merges) sb.AppendLine($"  merge: into {m.Into} from {m.From} dist {m.Dist.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
        foreach (var s in r.Splits) sb.AppendLine($"  {(s.Split ? "SPLIT" : "keep ")} group {s.Group}: halves {s.FacesA}/{s.FacesB}, centre sim {s.CentreSim.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}, median fit own {s.FitOwn.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} vs other {s.FitOther.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
        return sb.ToString();
    }

    public static JsonNode Json(Result r) => JsonSerializer.SerializeToNode(new
    {
        id = r.Id, model = r.Model, threshold = r.Threshold, mergeAt = r.MergeAt, faces = r.Faces, rawClusters = r.RawClusters, changedFaces = r.Changed,
        oldGroups = r.Old.Select(g => new { id = g.Id, faces = g.Faces, screenSeconds = g.Seconds, main = g.Main }), newGroups = r.New.Select(g => new { id = g.Id, faces = g.Faces, screenSeconds = g.Seconds, main = g.Main }),
        merges = r.Merges.Select(m => new { into = m.Into, from = m.From, dist = m.Dist }),
        splits = r.Splits.Select(s => new { group = s.Group, facesA = s.FacesA, facesB = s.FacesB, centreSim = s.CentreSim, fitOwn = s.FitOwn, fitOther = s.FitOther, split = s.Split }),
    })!;

    public static string SummaryTable(IReadOnlyList<Result> rs, bool split)
    {
        var sb = new StringBuilder(); sb.AppendLine("id\tfaces\told main\tnew main\tmerges\tchanged" + (split ? "\tsplit/groups checked" : ""));
        foreach (var r in rs) sb.AppendLine($"{r.Id}\t{r.Faces}\t{r.Old.Count(g => g.Main)}\t{r.New.Count(g => g.Main)}\t{r.Merges.Count}\t{r.Changed}" + (split ? $"\t{r.Splits.Count(s => s.Split)}/{r.Splits.Count}" : ""));
        return sb.ToString();
    }

    public static async Task<int> RunAsync(Args a)
    {
        const string use = "cast recluster <id>|--ids <file> [--merge-at D] [--split [--split-sim S] [--split-min N]] [--threshold T] [--min-faces N] [--local <root>] [--out-local <dir>] [--prefix index/]";
        var ids = new List<string>(); if (a.Get("ids") is { } f) ids.AddRange(File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        ids.AddRange(a.Positional.Skip(1)); if (ids.Count == 0) throw new ArgumentException(use);
        double? D(string k) => a.Get(k) is { } v ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        var def = new Options(); var o = new Options(D("threshold"), D("merge-at"), a.Get("min-faces") is { } mf ? int.Parse(mf) : def.MinFaces, a.Has("split"), D("split-sim") ?? def.SplitSim, a.Get("split-min") is { } sm ? int.Parse(sm) : def.SplitMin);
        var outDir = a.Get("out-local"); if (outDir != null) Directory.CreateDirectory(outDir);
        IStore? store = null; Paths? paths = null;
        if (a.Get("local") == null)
        {
            var cfg = NodeConfig.Load(); if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or set INDEXNODE_SAS_URL (or --local <root>)");
            store = StageZipStore.Wrap(new RestStore(Commands.NewHttp(), cfg.Sas)); paths = new Paths(a.Get("prefix") ?? "index/");
        }
        var results = new List<Result>();
        foreach (var id in ids)
        {
            try
            {
                IJobSource src = a.Get("local") is { } root ? new LocalJob(Path.Combine(root, id), id) : new StoreJob(store!, paths!, id);
                var r = await RunDramaAsync(src, o, CancellationToken.None); results.Add(r); Console.Write(Text(r));
                if (outDir != null) File.WriteAllText(Path.Combine(outDir, id + ".recluster.json"), Json(r).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or JsonException or IOException) { Console.Error.WriteLine($"{id}: {e.Message}"); }
        }
        if (ids.Count > 1 || a.Has("ids")) { var t = SummaryTable(results, o.Split); Console.Write(t); if (outDir != null) File.WriteAllText(Path.Combine(outDir, "summary.tsv"), t); }
        return results.Count == ids.Count ? 0 : 1;
    }
}
