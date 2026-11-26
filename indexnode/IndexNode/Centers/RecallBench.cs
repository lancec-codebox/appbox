using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>
/// 0.13.68 `indexnode search recall --shots &lt;manifest.json&gt; [--pace 150] [--cache-gb 40] [--top-centers 300,1239,5000] [--codes-k 1000,10000,100000] [--trim] [--threads N] [--out report.json] [--uploads N]`:
/// a measurement, not a feature (owner, 2026-11-26: "So let's measure it"). For real screenshots with known answers, how well does today's search (bit codes of the CENTERS, then the members of the
/// best centers) find the right drama, against a flat scan of a 64-byte sign code of EVERY stored frame followed by an exact re-rank of the closest K?
/// It downloads every frames file of the published search index once (whole files, into the local frames cache, paced at --pace per minute), writes the codes of all frames to a memory-mapped
/// file (work/frame-codes.bin), and then scans it per screenshot. Reads the store only. The manifest: [{"file": "...png", "uploads": ["id", ...], "time": 476}] (time optional, seconds).
/// A hit counts when its upload is one of the listed uploads or in the same drama group.
/// </summary>
public static class RecallBench
{
    sealed record Shot(string File, HashSet<string> Uploads, double? Time);

    public static async Task<int> RunAsync(Args a)
    {
        var (cfg, store, paths, http) = CentersCommands.OpenStore(a);
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); }; var ct = cts.Token;
        int threads = a.Get("threads") is { } t ? int.Parse(t) : Environment.ProcessorCount;
        double pace = a.Get("pace") is { } p ? double.Parse(p, CultureInfo.InvariantCulture) : 150;
        int[] topCs = (a.Get("top-centers") ?? "300,1239,5000").Split(',').Select(int.Parse).ToArray();
        int[] ks = (a.Get("codes-k") ?? "1000,10000,100000").Split(',').Select(int.Parse).OrderBy(x => x).ToArray();
        bool trim = a.Has("trim");
        var work = Path.GetFullPath(a.Get("work") ?? "recall-work"); Directory.CreateDirectory(work);
        var manifest = a.Get("shots") ?? throw new ArgumentException("search recall --shots <manifest.json> [--pace 150] [--cache-gb 40] [--top-centers 300,1239,5000] [--codes-k 1000,10000,100000] [--trim] [--out report.json]");
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

        // 2) a sign code of every frame (bit d set when v[d] > mean[d], as the index's own codes), into a memory-mapped file; per frame its upload, time and the vector's place in its file
        var codesPath = Path.Combine(work, "frame-codes.bin"); const int D = CentersFile.Dim, CB = 64;
        var up = new List<int>(16_000_000); var at = new List<float>(16_000_000); var off = new List<long>(16_000_000);
        var mean = idx.Mean;
        using (var cf = new FileStream(codesPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var code = new byte[CB];
            for (int i = 0; i < idx.Uploads.Count; i++)
            {
                if (local[i] is not { } lp) continue;
                using var fs = new FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                var hb = new byte[CentersFile.HeaderSize]; fs.ReadExactly(hb); var h = CentersFile.ParseHeader(hb);
                var tb = new byte[h.K * CentersFile.TableEntry]; fs.Position = h.TableOff; fs.ReadExactly(tb);
                foreach (var b in CentersFile.ParseTable(tb, h.K))
                {
                    if (b.Count == 0) continue;
                    var blk = new byte[b.Length]; fs.Position = b.Offset; fs.ReadExactly(blk);
                    var mem = CentersFile.ParseBlock(blk, b.Count);
                    for (int r = 0; r < b.Count; r++)
                    {
                        Array.Clear(code);
                        for (int d = 0; d < D; d++) if ((float)BitConverter.ToHalf(mem.Vectors, (r * D + d) * 2) - mean[d] > 0) code[d >> 3] |= (byte)(1 << (d & 7));
                        cf.Write(code); up.Add(i); at.Add(mem.Times[r]); off.Add(b.Offset + (long)r * D * 2);
                    }
                }
            }
        }
        int n = up.Count; Console.WriteLine($"codes of {n:N0} frames written ({new FileInfo(codesPath).Length / 1e6:F0} MB), {sw.Elapsed.TotalMinutes:F1} min");
        using var mmf = MemoryMappedFile.CreateFromFile(codesPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mmf.CreateViewAccessor(0, (long)n * CB, MemoryMappedFileAccess.Read);

        // the exact vector of a frame, from its local frames file
        var handles = new Dictionary<int, FileStream>();
        float[] Vec(int fi)
        {
            int u = up[fi]; if (!handles.TryGetValue(u, out var fs)) handles[u] = fs = new FileStream(local[u]!, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            var b = new byte[D * 2]; fs.Position = off[fi]; fs.ReadExactly(b); var v = new float[D]; for (int d = 0; d < D; d++) v[d] = (float)BitConverter.ToHalf(b, d * 2); return v;
        }
        static double Dot(float[] x, float[] y) { double s = 0; for (int i = 0; i < x.Length; i++) s += x[i] * y[i]; return s; }

        var report = new JsonArray(); var tools = new Tools(http, cfg);
        var summary = new Dictionary<string, (int Top1, int Top5, int Miss)>();
        void Count(string method, int? rank) { var c = summary.GetValueOrDefault(method); summary[method] = rank == 1 ? (c.Top1 + 1, c.Top5 + 1, c.Miss) : rank is <= 5 ? (c.Top1, c.Top5 + 1, c.Miss) : (c.Top1, c.Top5, c.Miss + 1); }
        foreach (var s in shots)
        {
            var q = await CentersCommands.EmbedImageAsync(tools, cfg, http, s.File, ct, trim);
            var row = new JsonObject { ["file"] = Path.GetFileName(s.File), ["truth"] = string.Join(" ", s.Uploads.Take(4)) };
            // a) today's route: bit codes of the centers, the members of the top C centers, exact
            foreach (var c in topCs)
            {
                var t0 = Stopwatch.GetTimestamp();
                var (hits, _) = await CenterSearch.QueryAsync(store, paths, idx, q, c, 50, threads, ct);
                var dr = hits.Select(h => h.Upload).Distinct(StringComparer.Ordinal).ToList(); int pos = dr.FindIndex(s.Uploads.Contains);
                int? rank = pos < 0 ? null : pos + 1; Count($"centers {c}", rank);
                row[$"centers{c}"] = new JsonObject { ["rank"] = rank, ["top"] = hits.Count > 0 ? Math.Round(hits[0].Score, 3) : null, ["ms"] = Math.Round(Stopwatch.GetElapsedTime(t0).TotalMilliseconds) };
            }
            // b) every frame's code: hamming distance to all, the K closest re-ranked exactly
            var qc = new byte[CB]; for (int d = 0; d < D; d++) if (q[d] - mean[d] > 0) qc[d >> 3] |= (byte)(1 << (d & 7));
            var qw = new ulong[8]; for (int w = 0; w < 8; w++) qw[w] = BitConverter.ToUInt64(qc, w * 8);
            var dist = new ushort[n]; var ts = Stopwatch.GetTimestamp();
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, part =>
            {
                long from = (long)n * part / threads, to = (long)n * (part + 1) / threads; var buf = new ulong[8];
                for (long fi = from; fi < to; fi++) { view.ReadArray(fi * CB, buf, 0, 8); int dd = 0; for (int w = 0; w < 8; w++) dd += BitOperations.PopCount(buf[w] ^ qw[w]); dist[fi] = (ushort)dd; }
            });
            double scanMs = Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
            var hist = new int[D + 1]; foreach (var dd in dist) hist[dd]++;
            int kmax = ks[^1], cut = 0, acc = 0; while (cut <= D && acc + hist[cut] < kmax) { acc += hist[cut]; cut++; }
            var cand = new List<int>(kmax + 1024); for (int fi = 0; fi < n && cand.Count < kmax; fi++) if (dist[fi] < cut) cand.Add(fi);
            for (int fi = 0; fi < n && cand.Count < kmax; fi++) if (dist[fi] == cut) cand.Add(fi);
            cand.Sort((x, y) => dist[x].CompareTo(dist[y]));
            // where the truth's best frame sits by code distance (the K it needs)
            int truthBest = -1; double truthCos = -1; for (int fi = 0; fi < n; fi++) if (s.Uploads.Contains(idx.Uploads[up[fi]].Id) && dist[fi] <= (truthBest < 0 ? 512 : dist[truthBest])) { var cs = Dot(q, Vec(fi)); if (cs > truthCos) { truthCos = cs; truthBest = fi; } }
            int truthCodeRank = truthBest < 0 ? -1 : 1 + dist.Count(x => x < dist[truthBest]);
            var exact = cand.Select(fi => (Fi: fi, Cos: Dot(q, Vec(fi)))).ToList();
            var codesRow = new JsonObject { ["scanMs"] = Math.Round(scanMs), ["truthBestCos"] = Math.Round(truthCos, 3), ["truthCodeRank"] = truthCodeRank };
            foreach (var k in ks)
            {
                var top = exact.Take(k).OrderByDescending(x => x.Cos).Select(x => idx.Uploads[up[x.Fi]].Id).Distinct(StringComparer.Ordinal).ToList(); int pos = top.FindIndex(s.Uploads.Contains);
                int? rank = pos < 0 ? null : pos + 1; Count($"codes {k}", rank); codesRow[$"k{k}"] = rank;
            }
            row["codes"] = codesRow; report.Add(row);
            Console.WriteLine($"{Path.GetFileName(s.File)}: " + string.Join("  ", topCs.Select(c => $"centers{c} {(row[$"centers{c}"]!["rank"] is { } r ? "#" + r : "-")}")) + "  |  " + string.Join("  ", ks.Select(k => $"codes{k} {(codesRow[$"k{k}"] is { } r ? "#" + r : "-")}")) + $"  | truth best cos {truthCos:F3}, its code rank {truthCodeRank:N0}, scan {scanMs:F0} ms");
        }
        foreach (var fs in handles.Values) fs.Dispose();
        Console.WriteLine($"\n{shots.Count} screenshots (rank of the right drama among distinct uploads; top 1 / top 5 / not in the first 50):");
        foreach (var (m, c) in summary) Console.WriteLine($"  {m,-16} {c.Top1,3} / {c.Top5,3} / {c.Miss,3}");
        Console.WriteLine($"store ops {StoreMeter.Current.Total}, {sw.Elapsed.TotalMinutes:F1} min");
        if (a.Get("out") is { } o) File.WriteAllText(o, new JsonObject { ["index"] = idx.Version, ["frames"] = n, ["shots"] = report }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
