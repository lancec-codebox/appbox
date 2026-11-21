using System.Diagnostics;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>`indexnode groups build [--dry-run] [--threads N] [--link 0.75] [--top 20]`: the one-time group table over the indexed uploads, from center-level shares.</summary>
public static class GroupsCommand
{
    public const float Cos = 0.80f, DupCos = 0.85f;

    /// <summary>Share of A's centers whose best center in B has cosine >= <paramref name="thr"/> (centers are unit vectors, rows of 512).</summary>
    public static double Share(float[] a, float[] b, float thr)
    {
        int na = a.Length / CentersFile.Dim, nb = b.Length / CentersFile.Dim; if (na == 0 || nb == 0) return 0; int hit = 0;
        for (int i = 0; i < na; i++)
        {
            var x = a.AsSpan(i * CentersFile.Dim, CentersFile.Dim);
            for (int j = 0; j < nb; j++) if (Vec.Dot(x, b.AsSpan(j * CentersFile.Dim, CentersFile.Dim)) >= thr) { hit++; break; }
        }
        return (double)hit / na;
    }

    /// <summary>The group set from pairs: a pair links when the larger of its two directional shares is >= <paramref name="link"/>. Members record the best linked pair's shares (match at 0.80, duplicate at 0.85).</summary>
    public static GroupSet FromPairs(IReadOnlyList<SearchUpload> ups, IReadOnlyDictionary<(string, string), (double Match, double Dup)> pairs, double link, Func<string, (double? Duration, string? At)> facts)
    {
        var links = pairs.Where(p => p.Value.Match >= link).Select(p => p.Key).ToList();
        var comps = GroupSet.Components(ups.Select(u => u.Id), links);
        var byId = ups.ToDictionary(u => u.Id, StringComparer.Ordinal); var set = new GroupSet();
        foreach (var c in comps)
        {
            var g = new DramaGroup { Id = "g-" + c[0] };
            foreach (var id in c)
            {
                var best = pairs.Where(p => p.Value.Match >= link && (p.Key.Item1 == id || p.Key.Item2 == id)).OrderByDescending(p => p.Value.Match).Select(p => p.Value).FirstOrDefault();
                var (dur, at) = facts(id);
                g.Members.Add(new GroupMember { Upload = id, MatchShare = best.Match, DupShare = best.Dup, SpeechLanguage = byId[id].SpeechLanguage, Dubbed = byId[id].Dubbed, Duration = dur, ProcessedAt = at });
            }
            set.Groups.Add(g);
        }
        set.ElectAll();
        return set;
    }

    public static async Task<int> RunAsync(Args a, IStore? remote = null)
    {
        if (a.Positional.FirstOrDefault() != "build") throw new ArgumentException("groups build [--dry-run] [--threads N] [--link 0.75] [--top 20]");
        var (cfg, store, paths, _) = CentersCommands.OpenStore(a, remote);
        int threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads; bool dry = a.Has("dry-run");
        double link = a.Get("link") is { } l ? double.Parse(l, System.Globalization.CultureInfo.InvariantCulture) : 0.75; int topRows = a.Get("top") is { } tp ? int.Parse(tp) : 20;
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var sw = Stopwatch.StartNew();
        var idx = await SearchData.LoadAsync(store, paths, null, cts.Token) ?? throw new InvalidOperationException("no search index yet: run search-index build first");
        if (!dry && await store.GetAsync(GroupStore.File(paths, idx.Version), cts.Token) != null) throw new InvalidOperationException($"{GroupStore.File(paths, idx.Version)} exists: the groups are written once (the search-index job maintains them from here)");
        int n = idx.Uploads.Count; Console.WriteLine($"groups build: {n} indexed uploads (index v{idx.Version}){(dry ? ", dry run" : "")}");
        var cen = new float[n][]; int done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, n), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = cts.Token }, async (i, ct) =>
        {
            try { var l2 = await SearchIndexBuild.LoadUploadAsync(store, paths, idx.Uploads[i].Id, ct); cen[i] = l2.Centers; for (int r = 0; r < l2.Up.K; r++) Vec.Normalize(cen[i].AsSpan(r * CentersFile.Dim, CentersFile.Dim)); }
            catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"groups: {idx.Uploads[i].Id}: {e.Message}"); cen[i] = []; }
            if (Interlocked.Increment(ref done) % 100 == 0) Console.WriteLine($"  read {done}/{n}");
        });
        // candidates per center from the index's sign codes, then exact cosines on the centers themselves
        var cand = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte>();
        await Parallel.ForEachAsync(Enumerable.Range(0, n), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = cts.Token }, (i, ct) =>
        {
            int k = cen[i].Length / CentersFile.Dim; var seen = new HashSet<int>();
            for (int c = 0; c < k; c++)
            {
                var q = CenterSearch.MinusMean(cen[i].AsSpan(c * CentersFile.Dim, CentersFile.Dim).ToArray(), idx.Mean);
                foreach (var row in idx.Scan(q, topRows, 1, i)) seen.Add(idx.RefUpload[row]);
            }
            foreach (var j in seen) cand[i < j ? (i, j) : (j, i)] = 1;
            return ValueTask.CompletedTask;
        });
        var pairs = new System.Collections.Concurrent.ConcurrentDictionary<(string, string), (double, double)>();
        Parallel.ForEach(cand.Keys, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = cts.Token }, p =>
        {
            var (i, j) = p; if (cen[i].Length == 0 || cen[j].Length == 0) return;
            double m = Math.Max(Share(cen[i], cen[j], Cos), Share(cen[j], cen[i], Cos)), d = Math.Max(Share(cen[i], cen[j], DupCos), Share(cen[j], cen[i], DupCos));
            if (m > 0) pairs[(idx.Uploads[i].Id, idx.Uploads[j].Id)] = (m, d);
        });
        var ce = (await SearchIndexBuild.ListCentersAsync(store, paths, cts.Token)).ToDictionary(x => x.Id, x => x.Zip.Created, StringComparer.Ordinal);
        var durs = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var u in idx.Uploads) { try { durs[u.Id] = store.GetAsync(paths.Video(u.Id, "downloaded.json"), cts.Token).GetAwaiter().GetResult() is { } d ? (double?)JsonNode.Parse(d.Data)?["durationSeconds"] : null; } catch (Exception e) when (e is not OperationCanceledException) { durs[u.Id] = null; } }
        var set = FromPairs(idx.Uploads, pairs, link, id => (durs.GetValueOrDefault(id), ce.GetValueOrDefault(id) is { } c ? Protocol.Utc(c) : null));
        set.BuiltAt = Protocol.NowUtc(); set.IndexVersion = idx.Version;
        int multi = set.Groups.Count(g => g.Members.Count > 1), single = set.Groups.Count - multi;
        Console.WriteLine($"groups: {set.Groups.Count} groups ({multi} with 2+ uploads, {single} single), {n} uploads, {pairs.Count} linked-or-near pairs, link >= {link:0.00} (center share at cosine {Cos:0.00}), {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var g in set.Groups.Take(10)) Console.WriteLine($"  {g.Id}: {g.Members.Count} uploads, canonical {g.Canonical ?? "none"} ({g.CanonicalWhy})");
        if (dry) { Console.WriteLine("dry run: nothing written"); return 0; }
        if (!await GroupStore.WriteAsync(store, paths, set, idx.Version, cts.Token)) throw new InvalidOperationException("groups were written meanwhile");
        Console.WriteLine($"wrote {GroupStore.File(paths, idx.Version)}");
        return 0;
    }
}
