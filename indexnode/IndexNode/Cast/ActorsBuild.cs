using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// `indexnode actors build --ids &lt;file&gt; --out-local &lt;dir&gt; [--local &lt;root&gt;] [--groups &lt;file&gt;] [--threads N] [--tiebreak-cut 0.64] [--child-probe &lt;file&gt;]`: the cast grouping and actor linking, run outside the queue.
/// Reads cast/faces.json, faces.f16, clusters.json (and video.json, the display shots) and writes only local files. Per drama: raw clusters as CastFrames does (CastRecluster.ClusterRaw), split recursively while a 2-means split gives halves
/// under 0.60 alike with at least 20 faces each, keep units of 14+ faces, drop child units (a --child-probe logistic regression; mean score at least 0.7726). Between units of different productions (uploads of one group count as one): centres at least 0.75 alike link;
/// 0.70 to under 0.75 go to the ResNet-50 tiebreaker (up to 30 faces per unit re-embedded from the stored landmarks; link when the r50 centre cosine reaches --tiebreak-cut); under 0.70 never. Actors are the union-find components.
/// </summary>
public static class ActorsBuild
{
    public const double DirectCut = 0.75, BandLow = 0.70, SplitSim = 0.60, ChildCut = 0.7726; public const int MinUnit = 14, SplitHalf = 20, TiebreakFaces = 30, SheetFaces = 6;

    public sealed class Unit
    {
        public string Drama = "", Prod = "", Key = ""; public int N; public List<int> Faces = []; public float[] Centre = []; public List<int> RepFaces = []; public double First, Last, Child;
    }
    public sealed record Link(Unit A, Unit B, double Cos, string Method, double? R50, bool Linked);
    public sealed class Drama { public string Id = ""; public string Prod = ""; public List<FaceRec> Faces = []; public int[] StoredCluster = []; public bool Ai; public string? Skip; public List<Unit> Units = []; public int ChildUnits; }
    /// <summary>child-probe.json: p = 1/(1+exp(-(w . ((x - mu)/sd) + b))) per face (w600k_mbf vectors); a unit is a child when its mean p >= Cut. Mu and Sd may be absent (0 and 1).</summary>
    public sealed record Probe(float[] W, double B, float[]? Mu = null, float[]? Sd = null, double Cut = ChildCut)
    {
        public double Score(float[] x) { double s = B; for (int i = 0; i < W.Length && i < x.Length; i++) { double v = x[i]; if (Mu != null) v -= Mu[i]; if (Sd != null && Sd[i] != 0) v /= Sd[i]; s += W[i] * v; } return 1 / (1 + Math.Exp(-s)); }
    }

    public const string StoreProbeName = "models/child-probe.json";
    public static string StoreProbe(Paths p) => p.Prefix + StoreProbeName;
    public static Probe? LoadProbe(string? path) => path == null ? null : ParseProbe(File.ReadAllText(path));
    /// <summary>The child probe: the --child-probe file when given, else index/models/child-probe.json in the store; null when there is none (the actors stage must then not be claimed: without it no child is ever excluded).</summary>
    public static async Task<Probe?> ResolveProbeAsync(IStore store, Paths paths, string? flag, CancellationToken ct)
    {
        if (flag != null) return LoadProbe(flag);
        var c = await store.GetAsync(StoreProbe(paths), ct); return c == null ? null : ParseProbe(System.Text.Encoding.UTF8.GetString(c.Data));
    }
    public static Probe ParseProbe(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("child probe: not a JSON object");
        var w = (o["w"] ?? o["weights"] ?? o["coef"]) as JsonArray ?? throw new InvalidDataException("child probe: needs w (the weights)"); 
        float[]? V(string k) => o[k] is JsonArray a ? a.Select(v => v!.GetValue<float>()).ToArray() : null;
        var p = new Probe(w.Select(v => v!.GetValue<float>()).ToArray(), (o["b"] ?? o["bias"] ?? o["intercept"])?.GetValue<double>() ?? 0, V("mu"), V("sd"), o["cut"]?.GetValue<double>() ?? ChildCut);
        if ((p.Mu != null && p.Mu.Length != p.W.Length) || (p.Sd != null && p.Sd.Length != p.W.Length)) throw new InvalidDataException("child probe: mu, sd and w must have the same length");
        return p;
    }

    /// <summary>Units of one drama: the faces' raw clusters, split while a 2-means split gives halves under 0.60 alike with 20+ faces each; units under 14 faces dropped.</summary>
    public static List<List<FaceRec>> MakeUnits(List<FaceRec> usable, double thr)
    {
        ClusterRaw(usable, thr); var res = new List<List<FaceRec>>();
        foreach (var g in usable.Where(f => f.Cluster >= 0).GroupBy(f => f.Cluster).OrderBy(g => g.Key)) foreach (var u in Split(g.ToList())) if (u.Count >= MinUnit) res.Add(u);
        return res;
    }
    static void ClusterRaw(List<FaceRec> usable, double thr) => CastRecluster.ClusterRaw(usable, thr);

    public static IEnumerable<List<FaceRec>> Split(List<FaceRec> l)
    {
        if (l.Count >= 2 * SplitHalf && CastRecluster.Bisect(l) is { } bs && CastLogic.Cosine(bs.Ca, bs.Cb) < SplitSim)
        {
            var a = l.Where((f, i) => !bs.Side[i]).ToList(); var b = l.Where((f, i) => bs.Side[i]).ToList();
            if (a.Count >= SplitHalf && b.Count >= SplitHalf) { foreach (var x in Split(a)) yield return x; foreach (var x in Split(b)) yield return x; yield break; }
        }
        yield return l;
    }

    /// <summary>3-4 representative faces of a unit: sub-centres (2-means, then again on the larger half) when it is big enough (40+ faces; a 4th at 80+), otherwise the faces nearest the centre; each is the face nearest its sub-centre.</summary>
    public static List<FaceRec> Representatives(List<FaceRec> u, float[] centre)
    {
        var picks = new List<FaceRec>();
        if (u.Count >= 40)
        {
            var parts = new List<List<FaceRec>> { u }; int want = u.Count >= 80 ? 4 : 3;
            while (parts.Count < want && parts.OrderByDescending(p => p.Count).First() is { Count: >= 20 } big && CastRecluster.Bisect(big) is { } bs)
            { parts.Remove(big); parts.Add(big.Where((f, i) => !bs.Side[i]).ToList()); parts.Add(big.Where((f, i) => bs.Side[i]).ToList()); }
            foreach (var p in parts) { var c = CastLogic.Centroid(p.Select(f => f.Emb)); var f0 = p.OrderByDescending(f => CastLogic.Cosine(f.Emb, c)).First(); if (!picks.Contains(f0)) picks.Add(f0); }
        }
        foreach (var f in u.OrderByDescending(f => CastLogic.Cosine(f.Emb, centre))) { if (picks.Count >= 3) break; if (!picks.Contains(f)) picks.Add(f); }
        return picks;
    }

    public static async Task<Drama> LoadDramaAsync(IStore store, Paths paths, string id, Dictionary<string, string> groupOf, Probe? probe, CancellationToken ct, Func<FaceRec, bool>? use = null, bool manual = false)
    {
        var d = new Drama { Id = id, Prod = groupOf.TryGetValue(id, out var g) ? g : id };
        if (!manual && await VideoJson.ReadAsync(store, paths, id, ct) is { } vj && (vj.Root["ai"] as JsonObject)?["flag"]?.GetValue<bool>() == true) { d.Ai = true; d.Skip = "AI"; return d; }   // AI dramas are skipped completely
        var fj = StoredJson.Object(await FrameChunks.ReadAsync(store, paths.Video(id, "cast/faces.json"), ct, false) ?? throw new InvalidOperationException($"{id}: no cast/faces.json (cast has not run)"), $"{id}/cast/faces.json");
        var f16 = await FrameChunks.ReadAsync(store, paths.Video(id, "cast/faces.f16"), ct, true);
        _ = await FrameChunks.ReadAsync(store, paths.Video(id, "cast/clusters.json"), ct, false) ?? throw new InvalidOperationException($"{id}: no cast/clusters.json");
        d.Faces = CastCandidates.LoadFaces(fj, f16, null, 360, $"{id}/cast/faces.f16"); d.StoredCluster = d.Faces.Select(f => f.Cluster).ToArray();
        var index = d.Faces.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i);
        double thr = fj["threshold"]?.GetValue<double>() ?? CastLogic.BuffaloS.Threshold; int n = 0;
        foreach (var u in MakeUnits(d.Faces.Where(f => f.Emb.Length > 0 && (use == null || use(f))).ToList(), thr))
        {
            var centre = CastLogic.Centroid(u.Select(f => f.Emb)); double child = probe == null ? 0 : u.Average(f => probe.Score(f.Emb));
            if (probe != null && child >= probe.Cut) { d.ChildUnits++; continue; }   // a child unit: left out, with its faces
            var un = new Unit { Drama = id, Prod = d.Prod, Key = $"{id}#{n++}", N = u.Count, Faces = u.Select(f => index[f]).OrderBy(i => i).ToList(), Centre = centre, First = u.Min(f => f.T), Last = u.Max(f => f.T), Child = child };
            un.RepFaces = Representatives(u, centre).Select(f => index[f]).ToList(); d.Units.Add(un);
        }
        return d;
    }

    public sealed class NullEmbedder : IFaceEmbedder { public int Dim => 1; public float[] Embed(Rgb a) => [0]; public float[][] EmbedBatch(IReadOnlyList<Rgb> f) => f.Select(_ => new float[1]).ToArray(); public void Dispose() { } }

    public sealed record Options(double TiebreakCut = 0.64, int Threads = 0, Probe? Probe = null);
    public sealed class Outcome { public List<Drama> Dramas = []; public List<Link> Links = []; public List<List<Unit>> Actors = []; public Dictionary<string, double> Seconds = []; public List<string> Failed = []; public int TiebreakEmbedded; }

    public static async Task<Outcome> BuildAsync(IStore store, Paths paths, IReadOnlyList<string> ids, GroupSet? groups, Func<IFaceEmbedder> makeR50, Func<byte[], Rgb> decode, Options o, CancellationToken ct)
    {
        var res = new Outcome(); var sw = Stopwatch.StartNew(); int threads = o.Threads > 0 ? o.Threads : Environment.ProcessorCount; var groupOf = groups?.GroupOf() ?? [];
        foreach (var id in ids)
        {
            try { var d = await LoadDramaAsync(store, paths, id, groupOf, o.Probe, ct); res.Dramas.Add(d); }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or JsonException or IOException) { res.Failed.Add($"{id}: {e.Message}"); }
        }
        res.Seconds["load+cluster"] = sw.Elapsed.TotalSeconds; sw.Restart();
        var units = res.Dramas.SelectMany(d => d.Units).ToList(); var bag = new ConcurrentBag<(int I, int J, double C)>();
        Parallel.For(0, units.Count, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, i =>
        { for (int j = i + 1; j < units.Count; j++) { if (units[i].Prod == units[j].Prod) continue; double c = CastLogic.Cosine(units[i].Centre, units[j].Centre); if (c >= BandLow) bag.Add((i, j, c)); } });
        var pairs = bag.OrderBy(p => p.I).ThenBy(p => p.J).ToList(); res.Seconds["pairs"] = sw.Elapsed.TotalSeconds; sw.Restart();
        // the tiebreaker: up to 30 faces of each unit in the 0.70-0.75 band, re-embedded with r50 from the stored landmarks, one pass per drama
        var need = pairs.Where(p => p.C < DirectCut).SelectMany(p => new[] { p.I, p.J }).Distinct().OrderBy(x => x).ToList(); var r50 = new Dictionary<int, float[]>();
        foreach (var dg in need.GroupBy(i => units[i].Drama))
        {
            var pick = dg.ToDictionary(i => i, i => CastReembed.PickPerGroup(units[i].Faces.Select(f => (I: f, G: 0, T: res.Dramas.First(d => d.Id == dg.Key).Faces[f].T, Q: res.Dramas.First(d => d.Id == dg.Key).Faces[f].Quality)), TiebreakFaces));
            var all = pick.Values.SelectMany(x => x).Distinct().OrderBy(x => x).ToArray(); if (all.Length == 0) continue;
            var r = (await CastReembed.RunManyAsync(store, paths, dg.Key, [new("w600k_r50", makeR50)], decode, threads, ct, onlyFaces: all))[0]; res.TiebreakEmbedded += r.Faces;
            var row = new Dictionary<int, int>(); for (int k = 0; k < r.Idx!.Length; k++) row[r.Idx[k]] = k;
            foreach (var (u, fs) in pick) { var vs = fs.Where(row.ContainsKey).Select(f => { var v = new float[r.Dim]; for (int d = 0; d < r.Dim; d++) v[d] = BitConverter.ToSingle(r.F32, (row[f] * r.Dim + d) * 4); return v; }).ToList(); if (vs.Count > 0) r50[u] = CastLogic.Centroid(vs); }
        }
        res.Seconds["tiebreak embed"] = sw.Elapsed.TotalSeconds;
        var parent = Enumerable.Range(0, units.Count).ToArray(); int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var (i, j, c) in pairs)
        {
            if (c >= DirectCut) { res.Links.Add(new Link(units[i], units[j], c, "direct", null, true)); parent[Find(i)] = Find(j); continue; }
            double? rv = r50.TryGetValue(i, out var a) && r50.TryGetValue(j, out var b) ? CastLogic.Cosine(a, b) : null; bool ok = rv >= o.TiebreakCut;
            res.Links.Add(new Link(units[i], units[j], c, "tiebreak", rv, ok)); if (ok) parent[Find(i)] = Find(j);
        }
        res.Actors = units.Select((u, i) => (u, r: Find(i))).GroupBy(x => x.r).Select(g => g.Select(x => x.u).ToList()).OrderByDescending(a => a.Select(u => u.Drama).Distinct().Count()).ThenByDescending(a => a.Sum(u => u.N)).ThenBy(a => a[0].Key, StringComparer.Ordinal).ToList();
        return res;
    }

    static string F(double v, string f = "0.000") => v.ToString(f, CultureInfo.InvariantCulture);
    public static string LinksTsv(Outcome r) => "unitA\tunitB\tcentreCosine\tmethod\tr50\tlinked\n" + string.Concat(r.Links.Select(l => $"{l.A.Key}\t{l.B.Key}\t{F(l.Cos, "0.0000")}\t{l.Method}\t{(l.R50 is { } v ? F(v, "0.0000") : "")}\t{(l.Linked ? "yes" : "no")}\n"));

    public static JsonNode ActorsJson(Outcome r, Func<Drama, int, double>? time = null) => new JsonObject
    {
        ["actors"] = new JsonArray(r.Actors.Select((a, k) => (JsonNode)new JsonObject
        {
            ["actor"] = k + 1, ["dramas"] = a.Select(u => u.Drama).Distinct().Count(), ["faces"] = a.Sum(u => u.N),
            ["units"] = new JsonArray(a.Select(u => { var d = r.Dramas.First(x => x.Id == u.Drama); return (JsonNode)new JsonObject
            {
                ["drama"] = u.Drama, ["unit"] = u.Key, ["production"] = u.Prod, ["faces"] = u.N, ["first"] = Math.Round(u.First, 1), ["last"] = Math.Round(u.Last, 1), ["childScore"] = Math.Round(u.Child, 4),
                ["faceIndexes"] = new JsonArray(u.Faces.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()), ["centre"] = new JsonArray(u.Centre.Select(x => (JsonNode)JsonValue.Create(MathF.Round(x, 4))).ToArray()),
                ["representatives"] = new JsonArray(u.RepFaces.Select(i => (JsonNode)new JsonObject { ["face"] = i, ["t"] = d.Faces[i].T }).ToArray()),
            }; }).ToArray()),
        }).ToArray()),
    };

    public static string Summary(Outcome r, IReadOnlyList<string> ids, bool probe, long storeOps, string? groupsNote, double cut)
    {
        var sb = new StringBuilder(); var multi = r.Actors.Where(a => a.Select(u => u.Drama).Distinct().Count() >= 2).ToList(); var units = r.Dramas.SelectMany(d => d.Units).ToList();
        sb.AppendLine($"dramas asked {ids.Count}: {r.Dramas.Count(d => !d.Ai)} read, {r.Dramas.Count(d => d.Ai)} skipped as AI, {r.Failed.Count} failed");
        foreach (var f in r.Failed) sb.AppendLine("  failed " + f);
        sb.AppendLine($"units {units.Count} ({units.Sum(u => u.N)} faces; min {MinUnit} faces; recursive split under {SplitSim} with {SplitHalf}+ faces per half)");
        sb.AppendLine(probe ? $"children: {r.Dramas.Sum(d => d.ChildUnits)} units left out (mean probe score >= the probe's cut)" : "children: NO --child-probe given, nothing was excluded as a child");
        if (groupsNote != null) sb.AppendLine(groupsNote);
        int direct = r.Links.Count(l => l.Method == "direct"), band = r.Links.Count(l => l.Method == "tiebreak"), bandOk = r.Links.Count(l => l.Method == "tiebreak" && l.Linked);
        sb.AppendLine($"links: {direct} direct (centres >= {DirectCut}); {band} in the 0.70-0.75 band, {bandOk} linked by the r50 tiebreak (cut {cut}), {band - bandOk} not; {r.TiebreakEmbedded} faces re-embedded");
        sb.AppendLine($"actors {r.Actors.Count} ({r.Actors.Count(a => a.Count > 1)} with 2+ units, {multi.Count} spanning 2+ dramas)");
        sb.AppendLine("largest actors (by dramas, then faces):");
        foreach (var (a, k) in r.Actors.Select((a, k) => (a, k)).Take(10)) sb.AppendLine($"  actor {k + 1}: {a.Select(u => u.Drama).Distinct().Count()} dramas, {a.Count} units, {a.Sum(u => u.N)} faces");
        sb.AppendLine("seconds: " + string.Join(", ", r.Seconds.Select(kv => $"{kv.Key} {F(kv.Value, "0.0")}")));
        sb.AppendLine($"store ops: {storeOps} (reads only; nothing is written to the store)");
        return sb.ToString();
    }

    static string Mmss(double t) { int s = (int)t; return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:D2}:{s % 60:D2}" : $"{s / 60}:{s % 60:D2}"; }

    /// <summary>The review sheet: one row per actor spanning 2+ dramas, up to 6 faces per drama as plain links (youtube watch ?v=ID&amp;t=Ss around an img), a time link under each, tap-to-mark one person / mixed and a Save button. Pictures: the cast.zip display shots (cand_hs) when the face has one, else the aligned crop from the stored frame.</summary>
    public static async Task<string> SheetAsync(Outcome r, IStore store, Paths paths, Func<byte[], Rgb> decode, int threads, CancellationToken ct)
    {
        var rows = r.Actors.Where(a => a.Select(u => u.Drama).Distinct().Count() >= 2).ToList(); var shots = new Dictionary<(string, int, double), string>(); var crops = new Dictionary<(string, int), string>();
        var picks = new Dictionary<(int, string), List<int>>();
        foreach (var (a, k) in rows.Select((a, k) => (a, k)))
            foreach (var dg in a.GroupBy(u => u.Drama))
            {
                var d = r.Dramas.First(x => x.Id == dg.Key); var cand = new Dictionary<int, List<(int K, double T)>>();
                foreach (var c in dg.SelectMany(u => u.Faces).Select(i => d.StoredCluster[i]).Distinct())
                    if (await FrameChunks.ReadAsync(store, paths.Video(dg.Key, $"cast/cand_{c}.json"), ct) is { } cb && JsonNode.Parse(cb) is JsonArray ca) cand[c] = ca.OfType<JsonObject>().Select(x => (x["k"]!.GetValue<int>(), x["t"]!.GetValue<double>())).ToList();
                var pool = dg.SelectMany(u => u.Faces).Distinct().ToList(); int? Shot(int i, out int c, out int kk) { c = d.StoredCluster[i]; kk = -1; if (cand.TryGetValue(c, out var l)) foreach (var x in l) if (Math.Abs(x.T - d.Faces[i].T) < 1e-3) { kk = x.K; return kk; } return null; }
                var with = pool.Where(i => Shot(i, out _, out _) != null).ToList(); var chosen = CastReembed.PickPerGroup(with.Select(i => (I: i, G: 0, T: d.Faces[i].T, Q: d.Faces[i].Quality)), SheetFaces).ToList();
                if (chosen.Count < SheetFaces) chosen.AddRange(CastReembed.PickPerGroup(pool.Except(chosen).Select(i => (I: i, G: 0, T: d.Faces[i].T, Q: d.Faces[i].Quality)), SheetFaces - chosen.Count));
                chosen = chosen.OrderBy(i => d.Faces[i].T).ToList(); picks[(k, dg.Key)] = chosen;
                foreach (var i in chosen) if (Shot(i, out var c, out var kk) is not null && await FrameChunks.ReadAsync(store, paths.Video(dg.Key, $"cast/cand_hs_{c}_{kk}.jpg"), ct) is { Length: > 0 } jb) shots[(dg.Key, i, 0)] = Convert.ToBase64String(jb);
            }
        foreach (var dg in picks.GroupBy(p => p.Key.Item2))
        {
            var miss = dg.SelectMany(p => p.Value).Where(i => !shots.ContainsKey((dg.Key, i, 0))).Distinct().ToArray(); if (miss.Length == 0) continue;
            var lk = new object(); var ms = miss.ToHashSet();
            await CastReembed.RunManyAsync(store, paths, dg.Key, [new("none", () => new NullEmbedder())], decode, threads, ct, ms, (i, im) => { var b = Convert.ToBase64String(Imaging.EncodeJpeg(im, 85)); lock (lk) crops[(dg.Key, i)] = b; }, onlyFaces: miss);
        }
        var sb = new StringBuilder("<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>actors</title><style>body{font:14px sans-serif;margin:8px}.row{border:1px solid #bbb;margin:10px 0;padding:6px}.d{display:inline-block;vertical-align:top;margin:4px 10px 4px 0}.f{display:inline-block;text-align:center;margin:1px}.f img{height:96px}.t{display:block;font-size:12px}button{font-size:15px;padding:6px 12px;margin-right:6px}button.on{background:#2a7;color:#fff}</style>\n");
        sb.Append($"<h3>{rows.Count} actors spanning 2+ dramas</h3><button onclick=\"save()\">Save</button> <span id=n></span>\n");
        foreach (var (a, k) in rows.Select((a, k) => (a, k)))
        {
            sb.Append($"<div class=row id=a{k + 1}><b>actor {k + 1}</b>: {a.Select(u => u.Drama).Distinct().Count()} dramas, {a.Count} units, {a.Sum(u => u.N)} faces <button onclick=\"mark({k + 1},'one',this)\">one person</button><button onclick=\"mark({k + 1},'mixed',this)\">mixed</button><br>");
            foreach (var dg in a.GroupBy(u => u.Drama))
            {
                var d = r.Dramas.First(x => x.Id == dg.Key); sb.Append($"<div class=d><small>{WebUtility.HtmlEncode(dg.Key)} ({string.Join(", ", dg.Select(u => u.Key + ":" + u.N))})</small><br>");
                foreach (var i in picks[(k, dg.Key)])
                {
                    var src = shots.TryGetValue((dg.Key, i, 0), out var s1) ? s1 : crops.GetValueOrDefault((dg.Key, i)); if (src == null) continue;
                    var url = $"https://www.youtube.com/watch?v={WebUtility.UrlEncode(dg.Key)}&amp;t={(int)d.Faces[i].T}s";
                    sb.Append($"<span class=f><a href=\"{url}\"><img src=\"data:image/jpeg;base64,{src}\"></a><a class=t href=\"{url}\">{Mmss(d.Faces[i].T)}</a></span>");
                }
                sb.Append("</div>");
            }
            sb.Append("</div>\n");
        }
        sb.Append(@"<script>var M={};function mark(a,v,b){var on=b.classList.contains('on');for(var x of b.parentNode.querySelectorAll('button'))x.classList.remove('on');if(on){delete M[a]}else{M[a]=v;b.classList.add('on')}document.getElementById('n').textContent=Object.keys(M).length+' marked'}
function save(){var a=document.createElement('a');a.href=URL.createObjectURL(new Blob([JSON.stringify(M,null,1)],{type:'application/json'}));a.download='actors-marks.json';a.click()}</script>");
        return sb.ToString();
    }

    public static async Task<int> Main2(Args a)
    {
        const string use = "actors build --ids <file> --out-local <dir> [--local <cast cache root>] [--groups <groups json>] [--threads N] [--tiebreak-cut 0.64] [--child-probe <file>] [--prefix index/]";
        if (a.Positional.FirstOrDefault() != "build") throw new ArgumentException(use);
        var ids = File.ReadAllLines(a.Get("ids") ?? throw new ArgumentException(use)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Distinct().ToList(); if (ids.Count == 0) throw new ArgumentException(use);
        var outDir = a.Get("out-local") ?? throw new ArgumentException(use); Directory.CreateDirectory(outDir);
        var cfg = NodeConfig.Load(); var http = Commands.NewHttp(); var ct = CancellationToken.None; int threads = a.Get("threads") is { } th ? int.Parse(th) : Environment.ProcessorCount;
        StoreMeter.Current = new StoreMeter(); IStore store; Paths paths;
        if (a.Get("local") is { } local) { store = new MeteredStore(new LocalStore(Path.GetFullPath(local))); paths = new Paths(""); }
        else { if (string.IsNullOrEmpty(cfg.Sas)) throw new InvalidOperationException("no SAS: run 'indexnode config set-sas <url>' or set INDEXNODE_SAS_URL (or --local <root>)"); store = StageZipStore.Wrap(new RestStore(http, cfg.Sas)); paths = new Paths(a.Get("prefix") ?? "index/"); }
        GroupSet? groups = null; string? gnote = null;
        if (a.Get("groups") is { } gf) groups = GroupSet.FromJson(JsonNode.Parse(File.ReadAllText(gf)) as JsonObject ?? throw new InvalidDataException("groups file: not an object"));
        else if (a.Get("local") == null && await store.GetAsync(SearchData.HeadName(paths), ct) is { } head) groups = await GroupStore.LoadAsync(store, paths, SearchData.ParseHead(head.Data), ct);
        if (groups == null) gnote = "groups: none read, every drama counts as its own production";
        double cut = a.Get("tiebreak-cut") is { } tc ? double.Parse(tc, CultureInfo.InvariantCulture) : 0.64; var probe = LoadProbe(a.Get("child-probe"));
        var r50 = await CastReembed.ModelPathAsync("w600k_r50", http, ct);
        var o = new Options(cut, threads, probe); var res = await BuildAsync(store, paths, ids, groups, () => new ArcFace(r50, 1), CastReembed.DecodeYuv, o, ct);
        var sw = Stopwatch.StartNew(); var sheet = await SheetAsync(res, store, paths, CastReembed.DecodeYuv, threads, ct); res.Seconds["sheet"] = sw.Elapsed.TotalSeconds;
        await File.WriteAllTextAsync(Path.Combine(outDir, "actors.json"), ActorsJson(res).ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        await File.WriteAllTextAsync(Path.Combine(outDir, "links.tsv"), LinksTsv(res), ct); await File.WriteAllTextAsync(Path.Combine(outDir, "actors-sheet.html"), sheet, ct);
        var sum = Summary(res, ids, probe != null, StoreMeter.Current.Total, gnote, cut); await File.WriteAllTextAsync(Path.Combine(outDir, "summary.txt"), sum, ct); Console.Write(sum);
        return res.Failed.Count == 0 ? 0 : 1;
    }
}
