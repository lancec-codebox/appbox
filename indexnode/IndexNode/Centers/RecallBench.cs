using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.68 `indexnode search recall --shots &lt;manifest.json&gt; [--pace 150] [--cache-gb 40] [--top-centers 300,1239,5000|none] [--codes-k 100,1000] [--collapse 95,92.5,90,85] [--window 2]
/// [--trim] [--threads N] [--out report.json] [--uploads N]`: a measurement, not a feature (owner, 2026-11-26: "So let's measure it").
/// For real screenshots with known answers, how well does today's search (bit codes of the CENTERS, then the members of the best centers) find the right drama, against a flat scan of a 64-byte
/// sign code of EVERY stored frame followed by an exact re-rank of the closest K?
/// Second pass (owner, 2026-11-26 ~10:50 AM PT): in time order, a frame whose code shares at least P% of its bits with the last KEPT frame of its upload is dropped (collapse); how many frames
/// are left, and does a scan of only the kept codes, top 100, then an exact re-rank of each kept frame's run widened by --window frames each side, still find the right drama?
/// It downloads every frames file of the published search index once (whole files, into the local frames cache, paced at --pace per minute), keeps the codes of all frames in memory, and scans
/// them per screenshot. Reads the store only. The manifest: [{"file": "...png", "uploads": ["id", ...], "time": 476}] (time optional, seconds).
/// A hit counts when its upload is one of the listed uploads or in the same drama group.
/// </summary>
public static class RecallBench
{
    sealed record Shot(string File, HashSet<string> Uploads, double? Time);
    const int D = CentersFile.Dim, W8 = 8;

    static int Ham(ulong[] c, long i, ReadOnlySpan<ulong> q) { int d = 0; for (int w = 0; w < W8; w++) d += BitOperations.PopCount(c[i * W8 + w] ^ q[w]); return d; }
    static int Ham2(ulong[] c, long i, long j) { int d = 0; for (int w = 0; w < W8; w++) d += BitOperations.PopCount(c[i * W8 + w] ^ c[j * W8 + w]); return d; }

    // hamming distance of the query to every code: a tight loop over the array, split in `threads` parts
    static double Scan(ulong[] codes, int count, ulong[] q, ushort[] dist, int threads)
    {
        var ts = Stopwatch.GetTimestamp();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, part =>
        {
            int from = (int)((long)count * part / threads), to = (int)((long)count * (part + 1) / threads);
            ulong q0 = q[0], q1 = q[1], q2 = q[2], q3 = q[3], q4 = q[4], q5 = q[5], q6 = q[6], q7 = q[7];
            var span = codes.AsSpan();
            for (int i = from; i < to; i++)
            {
                var c = span.Slice(i * W8, W8);
                dist[i] = (ushort)(BitOperations.PopCount(c[0] ^ q0) + BitOperations.PopCount(c[1] ^ q1) + BitOperations.PopCount(c[2] ^ q2) + BitOperations.PopCount(c[3] ^ q3)
                                 + BitOperations.PopCount(c[4] ^ q4) + BitOperations.PopCount(c[5] ^ q5) + BitOperations.PopCount(c[6] ^ q6) + BitOperations.PopCount(c[7] ^ q7));
            }
        });
        return Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
    }

    // the k smallest distances, sorted (ties at the cut in index order)
    static List<int> TopK(ushort[] dist, int count, int k)
    {
        var hist = new int[D + 1]; for (int i = 0; i < count; i++) hist[dist[i]]++;
        int cut = 0, acc = 0; while (cut < D && acc + hist[cut] < k) { acc += hist[cut]; cut++; }
        var cand = new List<int>(k + 1024); for (int i = 0; i < count; i++) if (dist[i] < cut) cand.Add(i);
        for (int i = 0; i < count && cand.Count < k; i++) if (dist[i] == cut) cand.Add(i);
        cand.Sort((x, y) => dist[x] != dist[y] ? dist[x].CompareTo(dist[y]) : x.CompareTo(y)); return cand;
    }

    public static async Task<int> RunAsync(Args a)
    {
        var (cfg, store, paths, http) = CentersCommands.OpenStore(a);
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); }; var ct = cts.Token;
        int threads = a.Get("threads") is { } t ? int.Parse(t) : Environment.ProcessorCount;
        double pace = a.Get("pace") is { } p ? double.Parse(p, CultureInfo.InvariantCulture) : 150;
        int[] topCs = a.Get("top-centers") is "none" ? [] : (a.Get("top-centers") ?? "300,1239,5000").Split(',').Select(int.Parse).ToArray();
        int[] ks = (a.Get("codes-k") ?? "100,1000").Split(',').Select(int.Parse).OrderBy(x => x).ToArray();
        double[] collapse = (a.Get("collapse") ?? "95,92.5,90,85").Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        int window = a.Get("window") is { } wn ? int.Parse(wn) : 2;
        const int CollapsedK = 100;   // the owner's number: 100 kept frames, then their windows exactly
        bool trim = a.Has("trim");
        var manifest = a.Get("shots") ?? throw new ArgumentException("search recall --shots <manifest.json> [--pace 150] [--cache-gb 40] [--top-centers 300,1239,5000|none] [--codes-k 100,1000] [--collapse 95,92.5,90,85] [--window 2] [--trim] [--out report.json]");
        var mdir = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        var shots = (JsonNode.Parse(File.ReadAllText(manifest)) as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => new Shot(Path.Combine(mdir, (string)o["file"]!), (o["uploads"] as JsonArray ?? []).Select(x => (string)x!).ToHashSet(StringComparer.Ordinal), (double?)o["time"])).ToList();

        var sw = Stopwatch.StartNew();
        var idx = await SearchData.LoadAsync(store, paths, null, ct) ?? throw new InvalidOperationException("no search index");
        Console.WriteLine($"index v{idx.Version}: {idx.Uploads.Count} uploads, {idx.Count} centers");
        // the same drama: every upload in the group of a listed upload counts
        var groupOf = idx.Uploads.Where(u => u.Group != null).ToDictionary(u => u.Id, u => u.Group!, StringComparer.Ordinal);
        foreach (var s in shots) foreach (var u in s.Uploads.ToList()) if (groupOf.TryGetValue(u, out var g)) foreach (var m in idx.Uploads.Where(x => x.Group == g)) s.Uploads.Add(m.Id);

        // --uploads N (a local check): only the screenshots' own dramas and the first N others; the centers route still searches the whole index (its files come down as it needs them)
        var only = a.Get("uploads") is { } un ? new HashSet<string>(shots.SelectMany(x => x.Uploads).Concat(idx.Uploads.Select(x => x.Id).Take(int.Parse(un))), StringComparer.Ordinal) : null;
        // 1) every frames file, whole, paced
        double gb = a.Get("cache-gb") is { } cg ? double.Parse(cg, CultureInfo.InvariantCulture) : 40;
        FramesCache.Current = new FramesCache(Path.Combine(NodeConfig.ConfigDir(), "frames-cache"), (long)(gb * (1L << 30)));
        var fc = FramesCache.Current; var gap = TimeSpan.FromSeconds(60.0 / Math.Max(1, pace));
        var local = new string?[idx.Uploads.Count]; int fetched = 0, had = 0, missing = 0;
        for (int i = 0; i < idx.Uploads.Count; i++)
        {
            var u = idx.Uploads[i]; if (u.File == null) { missing++; continue; }
            if (only != null && !only.Contains(u.Id)) continue;
            var t0 = Stopwatch.GetTimestamp();
            var (path, got) = await fc.LocalOrFetchAsync(store, u.File, u.Sha, u.ZipSize, ct);
            local[i] = path; if (path == null) missing++; else if (got) fetched++; else had++;
            if (got) { var left = gap - Stopwatch.GetElapsedTime(t0); if (left > TimeSpan.Zero) await Task.Delay(left, ct); }
            if ((i + 1) % 200 == 0) Console.WriteLine($"  frames files: {i + 1}/{idx.Uploads.Count} ({fetched} fetched, {had} already here, {missing} missing), {sw.Elapsed.TotalMinutes:F1} min");
        }
        Console.WriteLine($"frames files: {fetched} fetched, {had} already here, {missing} missing; store ops so far {StoreMeter.Current.Total}");

        // 2) a sign code of every frame (bit d set when v[d] > mean[d], as the index's own codes), in memory, IN TIME ORDER per upload (a frames file keeps its frames by center, not by time);
        //    per frame its upload, time and the vector's place in its file
        var rows = new List<(int Up, float At, long Off, ulong[] Code)>();
        var mean = idx.Mean;
        var upStart = new List<int>();
        for (int i = 0; i < idx.Uploads.Count; i++)
        {
            if (local[i] is not { } lp) continue;
            using var fs = new FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var hb = new byte[CentersFile.HeaderSize]; fs.ReadExactly(hb); var h = CentersFile.ParseHeader(hb);
            var tb = new byte[h.K * CentersFile.TableEntry]; fs.Position = h.TableOff; fs.ReadExactly(tb);
            var mine = new List<(int Up, float At, long Off, ulong[] Code)>();
            foreach (var b in CentersFile.ParseTable(tb, h.K))
            {
                if (b.Count == 0) continue;
                var blk = new byte[b.Length]; fs.Position = b.Offset; fs.ReadExactly(blk);
                var mem = CentersFile.ParseBlock(blk, b.Count);
                for (int r = 0; r < b.Count; r++)
                {
                    var code = new ulong[W8];
                    for (int d = 0; d < D; d++) if ((float)BitConverter.ToHalf(mem.Vectors, (r * D + d) * 2) - mean[d] > 0) code[d >> 6] |= 1UL << (d & 63);
                    mine.Add((i, mem.Times[r], b.Offset + (long)r * D * 2, code));
                }
            }
            mine.Sort((x, y) => x.At.CompareTo(y.At)); upStart.Add(rows.Count); rows.AddRange(mine);
        }
        int n = rows.Count;
        var codes = new ulong[(long)n * W8]; var up = new int[n]; var at = new float[n]; var off = new long[n];
        for (int i = 0; i < n; i++) { rows[i].Code.CopyTo(codes, (long)i * W8); up[i] = rows[i].Up; at[i] = rows[i].At; off[i] = rows[i].Off; }
        rows = null!; GC.Collect();
        Console.WriteLine($"codes of {n:N0} frames in memory ({codes.LongLength * 8 / 1e6:F0} MB), time order per upload, {sw.Elapsed.TotalMinutes:F1} min");

        // 3) neighbours in time: the distance between each frame and the next of the same upload, and the time between them
        var adj = new long[D + 1]; var gaps = new List<float>(n);
        for (int i = 1; i < n; i++) if (up[i] == up[i - 1]) { adj[Ham2(codes, i - 1, i)]++; gaps.Add(at[i] - at[i - 1]); }
        long adjN = adj.Sum(); gaps.Sort();
        string Pct(long[] hist, long total, double q) { long acc = 0, want = (long)(total * q); for (int d = 0; d <= D; d++) { acc += hist[d]; if (acc > want) return d.ToString(); } return "512"; }
        Console.WriteLine($"neighbours in time: {adjN:N0} pairs; time between frames median {gaps[gaps.Count / 2]:F2} s; bits differing p10 {Pct(adj, adjN, .1)}, p25 {Pct(adj, adjN, .25)}, median {Pct(adj, adjN, .5)}, p75 {Pct(adj, adjN, .75)}, p90 {Pct(adj, adjN, .9)}");
        var neigh = new JsonObject { ["pairs"] = adjN, ["gapMedianS"] = Math.Round(gaps[gaps.Count / 2], 3) };
        foreach (var pc in new[] { 99.0, 97.5, 95, 92.5, 90, 85, 80 }) { int mx = (int)Math.Floor(D * (1 - pc / 100)); long c = 0; for (int d = 0; d <= mx; d++) c += adj[d]; neigh[$"same{pc.ToString(CultureInfo.InvariantCulture)}"] = Math.Round(100.0 * c / adjN, 1); Console.WriteLine($"  next frame shares >= {pc}% of bits (<= {mx} differ): {100.0 * c / adjN:F1}% of pairs"); }
        gaps = null!;
        {   // the exact cosine of neighbours in time, on a sample (as stored, and with the index mean removed as the codes do)
            var rnd = new Random(7); var raw = new List<double>(); var cen = new List<double>();
            for (int tries = 0; raw.Count < 20_000 && tries < 200_000; tries++)
            {
                int i = 1 + rnd.Next(n - 1); if (up[i] != up[i - 1]) continue;
                var x = VecAt(i - 1); var y = VecAt(i); raw.Add(Cos(x, y));
                for (int d = 0; d < D; d++) { x[d] -= mean[d]; y[d] -= mean[d]; } cen.Add(Cos(x, y));
            }
            raw.Sort(); cen.Sort(); string P(List<double> v) => $"p10 {v[v.Count / 10]:F3}, median {v[v.Count / 2]:F3}, p90 {v[v.Count * 9 / 10]:F3}";
            Console.WriteLine($"  exact cosine of neighbours ({raw.Count:N0} pairs): as stored {P(raw)}; mean removed {P(cen)}");
            neigh["cosStoredMedian"] = Math.Round(raw[raw.Count / 2], 3); neigh["cosCenteredMedian"] = Math.Round(cen[cen.Count / 2], 3);
        }
        float[] VecAt(int fi) { using var fs = new FileStream(local[up[fi]]!, FileMode.Open, FileAccess.Read, FileShare.Read, 4096); var b = new byte[D * 2]; fs.Position = off[fi]; fs.ReadExactly(b); var v = new float[D]; for (int d = 0; d < D; d++) v[d] = (float)BitConverter.ToHalf(b, d * 2); return v; }
        static double Cos(float[] x, float[] y) { double s = 0, a2 = 0, b2 = 0; for (int i = 0; i < x.Length; i++) { s += x[i] * y[i]; a2 += x[i] * x[i]; b2 += y[i] * y[i]; } return s / Math.Sqrt(a2 * b2); }

        // 4) collapse: in time order per upload, a frame within maxHam of the last KEPT frame is dropped; its run is the frames from it to the next kept frame
        var levels = new List<(double Pct, int MaxHam, int[] Kept, int[] RunEnd, ulong[] Codes)>();
        foreach (var pc in collapse)
        {
            int mx = (int)Math.Floor(D * (1 - pc / 100)); var kept = new List<int>(n / 2);
            int last = -1; for (int i = 0; i < n; i++) { if (last < 0 || up[i] != up[last] || Ham2(codes, last, i) > mx) { kept.Add(i); last = i; } }
            var ka = kept.ToArray(); var runEnd = new int[ka.Length];
            for (int k = 0; k < ka.Length; k++) { int e = k + 1 < ka.Length && up[ka[k + 1]] == up[ka[k]] ? ka[k + 1] - 1 : ka[k]; if (!(k + 1 < ka.Length && up[ka[k + 1]] == up[ka[k]])) { e = ka[k]; while (e + 1 < n && up[e + 1] == up[ka[k]]) e++; } runEnd[k] = e; }
            var kc = new ulong[(long)ka.Length * W8]; for (int k = 0; k < ka.Length; k++) Array.Copy(codes, (long)ka[k] * W8, kc, (long)k * W8, W8);
            levels.Add((pc, mx, ka, runEnd, kc));
            Console.WriteLine($"collapse at {pc}% same bits (<= {mx} differ from the last kept): {ka.Length:N0} of {n:N0} frames kept ({100.0 * ka.Length / n:F1}%), {kc.LongLength * 8 / 1e6:F0} MB of codes");
        }

        // the exact vector of a frame, from its local frames file
        var handles = new Dictionary<int, FileStream>(); long vecReads = 0;
        float[] Vec(int fi)
        {
            int u = up[fi]; if (!handles.TryGetValue(u, out var fs)) handles[u] = fs = new FileStream(local[u]!, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            var b = new byte[D * 2]; fs.Position = off[fi]; fs.ReadExactly(b); var v = new float[D]; for (int d = 0; d < D; d++) v[d] = (float)BitConverter.ToHalf(b, d * 2); vecReads++; return v;
        }
        static double Dot(float[] x, float[] y) { double s = 0; for (int i = 0; i < x.Length; i++) s += x[i] * y[i]; return s; }
        int? RankOf(IEnumerable<int> framesBestFirst, Shot s)
        {
            var seen = new HashSet<int>(); foreach (var fi in framesBestFirst) { if (!seen.Add(up[fi])) continue; if (s.Uploads.Contains(idx.Uploads[up[fi]].Id)) return seen.Count <= 50 ? seen.Count : null; if (seen.Count >= 50) return null; }
            return null;
        }

        var report = new JsonArray(); var tools = new Tools(http, cfg);
        var summary = new Dictionary<string, (int Top1, int Top5, int Miss)>(); var vecsPer = new Dictionary<string, List<long>>(); var upsPer = new Dictionary<string, List<int>>();
        var scanMsAll1 = new List<double>(); var scanMsAllN = new List<double>(); var scanMsLvl = levels.ToDictionary(l => l.Pct, _ => new List<double>());
        void Count(string method, int? rank) { var c = summary.GetValueOrDefault(method); summary[method] = rank == 1 ? (c.Top1 + 1, c.Top5 + 1, c.Miss) : rank is <= 5 ? (c.Top1, c.Top5 + 1, c.Miss) : (c.Top1, c.Top5, c.Miss + 1); }
        var dist = new ushort[n];
        foreach (var s in shots)
        {
            var q = await CentersCommands.EmbedImageAsync(tools, cfg, http, s.File, ct, trim);
            var row = new JsonObject { ["file"] = Path.GetFileName(s.File), ["truth"] = string.Join(" ", s.Uploads.Take(4)) };
            var line = new List<string>();
            // a) today's route: bit codes of the centers, the members of the top C centers, exact
            foreach (var c in topCs)
            {
                var (hits, _) = await CenterSearch.QueryAsync(store, paths, idx, q, c, 50, threads, ct);
                var dr = hits.Select(h => h.Upload).Distinct(StringComparer.Ordinal).ToList(); int pos = dr.FindIndex(s.Uploads.Contains);
                int? rank = pos < 0 ? null : pos + 1; Count($"centers {c}", rank); row[$"centers{c}"] = rank; line.Add($"centers{c} {(rank is { } r ? "#" + r : "-")}");
            }
            // b) every frame's code
            var qw = new ulong[W8]; for (int d = 0; d < D; d++) if (q[d] - mean[d] > 0) qw[d >> 6] |= 1UL << (d & 63);
            scanMsAll1.Add(Scan(codes, n, qw, dist, 1)); scanMsAllN.Add(Scan(codes, n, qw, dist, threads));
            var top = TopK(dist, n, Math.Max(20_000, ks[^1]));
            // where the truth's best frame (by exact cosine among its frames) sits by code
            int truthBest = -1; double truthCos = -1; for (int fi = 0; fi < n; fi++) if (s.Uploads.Contains(idx.Uploads[up[fi]].Id)) { var cs = Dot(q, Vec(fi)); if (cs > truthCos) { truthCos = cs; truthBest = fi; } }
            int truthCodeRank = truthBest < 0 ? -1 : 1 + dist.Count(x => x < dist[truthBest]);
            row["truthBestCos"] = Math.Round(truthCos, 3); row["truthBestBits"] = truthBest < 0 ? null : dist[truthBest]; row["truthCodeRank"] = truthCodeRank;
            int? r0 = RankOf(top, s); Count("all frames, code only", r0); row["codeOnly"] = r0; line.Add($"code-only {(r0 is { } x0 ? "#" + x0 : "-")}");
            long v0 = vecReads; var exact = top.Take(ks[^1]).Select(fi => (Fi: fi, Cos: Dot(q, Vec(fi)))).ToList();
            foreach (var k in ks)
            {
                var m = $"all frames, top {k} exact"; int? r = RankOf(exact.Take(k).OrderByDescending(x => x.Cos).Select(x => x.Fi), s); Count(m, r); row[$"top{k}"] = r; line.Add($"top{k} {(r is { } xr ? "#" + xr : "-")}");
                (vecsPer.TryGetValue(m, out var vl) ? vl : vecsPer[m] = []).Add(k); (upsPer.TryGetValue(m, out var ul) ? ul : upsPer[m] = []).Add(exact.Take(k).Select(x => up[x.Fi]).Distinct().Count());
            }
            // c) only the kept codes: top 100 kept frames, then each one's run widened by `window` frames each side, exact
            foreach (var l in levels)
            {
                var kd = new ushort[l.Kept.Length]; scanMsLvl[l.Pct].Add(Scan(l.Codes, l.Kept.Length, qw, kd, 1));
                var ktop = TopK(kd, l.Kept.Length, 20_000);
                int? rc = RankOf(ktop.Select(k => l.Kept[k]), s); Count($"collapse {l.Pct}%, code only", rc);
                var win = new HashSet<int>();
                foreach (var k in ktop.Take(CollapsedK)) { int f0 = l.Kept[k], u = up[f0]; for (int fi = Math.Max(0, f0 - window); fi <= Math.Min(n - 1, l.RunEnd[k] + window); fi++) if (up[fi] == u) win.Add(fi); }
                var ex = win.Select(fi => (Fi: fi, Cos: Dot(q, Vec(fi)))).OrderByDescending(x => x.Cos).Select(x => x.Fi).ToList();
                var m = $"collapse {l.Pct}%, top {CollapsedK} + window {window} exact"; int? rw = RankOf(ex, s); Count(m, rw);
                (vecsPer.TryGetValue(m, out var vl) ? vl : vecsPer[m] = []).Add(win.Count); (upsPer.TryGetValue(m, out var ul) ? ul : upsPer[m] = []).Add(win.Select(fi => up[fi]).Distinct().Count());
                int truthKeptRank = truthBest < 0 ? -1 : 1 + kd.Count(x => x < Ham(codes, truthBest, qw));
                row[$"c{l.Pct.ToString(CultureInfo.InvariantCulture)}"] = new JsonObject { ["codeOnly"] = rc, ["window"] = rw, ["vectors"] = win.Count, ["truthKeptRank"] = truthKeptRank };
                line.Add($"c{l.Pct} {(rc is { } y ? "#" + y : "-")}/{(rw is { } z ? "#" + z : "-")} ({win.Count} vec)");
            }
            report.Add(row);
            Console.WriteLine($"{Path.GetFileName(s.File)}: {string.Join("  ", line)} | truth cos {truthCos:F3}, {(truthBest < 0 ? -1 : dist[truthBest])} bits, code rank {truthCodeRank:N0}");
        }
        foreach (var fs in handles.Values) fs.Dispose();
        Console.WriteLine($"\n{shots.Count} screenshots (rank of the right drama among distinct uploads; top 1 / top 5 / not in the top 5; exact vectors read and uploads they come from, mean per screenshot):");
        foreach (var (m, c) in summary) Console.WriteLine($"  {m,-46} {c.Top1,3} / {c.Top5,3} / {c.Miss,3}" + (vecsPer.TryGetValue(m, out var vl) ? $"   {vl.Average():F0} vectors from {upsPer[m].Average():F0} uploads" : ""));
        Console.WriteLine($"scan of all {n:N0} codes: 1 thread median {Median(scanMsAll1):F0} ms, {threads} threads median {Median(scanMsAllN):F0} ms");
        foreach (var l in levels) Console.WriteLine($"scan of the {l.Kept.Length:N0} kept at {l.Pct}%: 1 thread median {Median(scanMsLvl[l.Pct]):F0} ms");
        Console.WriteLine($"store ops {StoreMeter.Current.Total}, {sw.Elapsed.TotalMinutes:F1} min");
        if (a.Get("out") is { } o) File.WriteAllText(o, new JsonObject
        {
            ["index"] = idx.Version, ["frames"] = n, ["neighbours"] = neigh,
            ["levels"] = new JsonArray(levels.Select(l => (JsonNode)new JsonObject { ["pct"] = l.Pct, ["maxBits"] = l.MaxHam, ["kept"] = l.Kept.Length, ["scanMs1"] = Median(scanMsLvl[l.Pct]) }).ToArray()),
            ["scanMs1"] = Median(scanMsAll1), ["scanMsN"] = Median(scanMsAllN), ["shots"] = report,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    static double Median(List<double> x) { if (x.Count == 0) return 0; var s = x.OrderBy(v => v).ToList(); return Math.Round(s[s.Count / 2]); }
}
