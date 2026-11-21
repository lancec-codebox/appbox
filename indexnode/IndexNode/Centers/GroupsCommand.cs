using System.Diagnostics;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>`indexnode groups build`: the one-time group table over the indexed uploads (0.13.3: by the frame-level full check, not center shares).</summary>
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

    /// <summary>0.13.3: lines "idA idB" (blank lines and # comments skipped).</summary>
    public static List<(string A, string B)> ReadPairs(string path) => File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l => l.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 2).Select(p => (p[0], p[1])).ToList();

    /// <summary>0.13.3: how many listed pairs end up in the same group; the ids of the others with why (not indexed, or in different groups).</summary>
    public static (int Same, int Total, List<string> Apart) CheckPairs(GroupSet set, IReadOnlyList<(string A, string B)> pairs, IEnumerable<string> indexed)
    {
        var of = set.GroupOf(); var idx = indexed.ToHashSet(StringComparer.Ordinal); int same = 0; var apart = new List<string>();
        foreach (var (a, b) in pairs)
        {
            if (of.TryGetValue(a, out var ga) && of.TryGetValue(b, out var gb) && ga == gb) same++;
            else apart.Add($"{a} {b}: " + (!idx.Contains(a) || !idx.Contains(b) ? $"not indexed ({(idx.Contains(a) ? "" : a)}{(idx.Contains(a) || idx.Contains(b) ? "" : " ")}{(idx.Contains(b) ? "" : b)})" : $"different groups ({of.GetValueOrDefault(a)} / {of.GetValueOrDefault(b)})"));
        }
        return (same, pairs.Count, apart);
    }

    /// <summary>0.13.3: `groups build [--dry-run] [--threads N] [--check-pairs FILE]`: the one-time group table, by the SAME frame-level full check the index build runs for a new upload (each upload's own frames searched against the index, joined at &gt;= 5 distinct-frame hits, mixed and offset as in 0.13.2; the language is video.json's).</summary>
    public static async Task<int> RunAsync(Args a, IStore? remote = null)
    {
        if (a.Positional.FirstOrDefault() == "enqueue")
        {
            var (_, es, ep, _) = CentersCommands.OpenStore(a, remote);
            await JobQueue.RequireSnapshotAsync(es, ep, default);
            var eq = new JobQueue(es, ep, "cli"); await eq.LoadAsync(default);
            var er = await SeqCommand.EnqueueGroupsAsync(eq, es, ep, a.Has("dry-run"), default);
            Console.WriteLine($"groups enqueue{(a.Has("dry-run") ? " (dry run)" : "")}: {er.Candidates} dramas with frames, {er.Existing} already have a group entry, {er.NoSeq} have no sequence number (run: indexnode seq assign), {er.Added} {(a.Has("dry-run") ? "would be " : "")}added");
            return 0;
        }
        if (a.Positional.FirstOrDefault() == "backfill-joins")
        {
            var (_, bs, bp, _) = CentersCommands.OpenStore(a, remote);
            await JobQueue.RequireSnapshotAsync(bs, bp, default);
            var bq = new JobQueue(bs, bp, "cli"); await bq.LoadAsync(default);
            var br = await SeqCommand.BackfillJoinsAsync(bq, bs, bp, a.Has("dry-run"), default);
            Console.WriteLine($"groups backfill-joins{(a.Has("dry-run") ? " (dry run)" : "")}: {br.Grouped} grouped dramas, {br.Missing} without a join record, {br.Read} video.json read, {br.NoRecord} with no grouping record yet, {br.Added} join line(s) {(a.Has("dry-run") ? "would be " : "")}added");
            return 0;
        }
        if (a.Positional.FirstOrDefault() == "rerun")
        {
            var (_, rs, rp, _) = CentersCommands.OpenStore(a, remote);
            await JobQueue.RequireSnapshotAsync(rs, rp, default);
            var rq = new JobQueue(rs, rp, "cli"); await rq.LoadAsync(default);
            var rr = await SeqCommand.RerunGroupsAsync(rq, rs, rp, a.Get("kind") ?? throw new ArgumentException("groups rerun --kind join [--dry-run]"), a.Has("dry-run"), default);
            Console.WriteLine($"groups rerun --kind join{(a.Has("dry-run") ? " (dry run)" : "")}: {rr.Joins} drama(s) with a join record, {rr.Added} group entr{(rr.Added == 1 ? "y" : "ies")} {(a.Has("dry-run") ? "would be " : "")}added, {rr.AlreadyPending} already pending (sequence numbers kept; later members compare against earlier members' full frames, so nothing cascades)");
            return 0;
        }
        if (a.Positional.FirstOrDefault() != "build")
 throw new ArgumentException("groups build [--dry-run] [--threads N] [--check-pairs <file>] [--top 20]");
        var (cfg, store, paths, _) = CentersCommands.OpenStore(a, remote);
        int threads = a.Get("threads") is { } t ? int.Parse(t) : cfg.EffectiveThreads; bool dry = a.Has("dry-run");
        var pairs = a.Get("check-pairs") is { } cp ? ReadPairs(cp) : null; int topRows = a.Get("top") is { } tp ? int.Parse(tp) : 20;
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var sw = Stopwatch.StartNew();
        var idx = await SearchData.LoadAsync(store, paths, null, cts.Token) ?? throw new InvalidOperationException("no search index yet: run search-index build first");
        if (!dry && await store.GetAsync(GroupStore.File(paths, idx.Version), cts.Token) != null) throw new InvalidOperationException($"{GroupStore.File(paths, idx.Version)} exists: the groups are written once (the search-index job maintains them from here)");
        Console.WriteLine($"groups build: {idx.Uploads.Count} indexed uploads (index v{idx.Version}), frame-level full check per upload{(dry ? ", dry run" : "")}");
        // an empty group set with no head: every upload of the index is "new" and goes through the full check (bootstrap: join cycles are broken, the earliest processed owns the group)
        var set = await GroupBuild.ApplyAsync(store, paths, null, idx, cts.Token, m => Console.WriteLine("  " + m), threads, cfg.PreCheck, bootstrap: true, write: false);
        set.IndexVersion = idx.Version;
        int multi = set.Groups.Count(g => g.Members.Count > 1), single = set.Groups.Count - multi;
        Console.WriteLine($"groups: {set.Groups.Count} groups ({multi} with 2+ uploads, {single} single), {idx.Uploads.Count} uploads, {sw.Elapsed.TotalSeconds:F1}s (join at >= {new PreCheckSettings().GroupMinHits} distinct-frame hits at cosine {new PreCheckSettings().Tau:0.00}; mixed and offset as in the index build)");
        foreach (var g in set.Groups.Where(g => g.Members.Count > 1).OrderByDescending(g => g.Members.Count).Take(topRows)) Console.WriteLine($"  {g.Id}: {g.Members.Count} uploads, canonical {g.Canonical ?? "none"} ({g.CanonicalWhy})");
        if (pairs != null)
        {
            var (same, total, apart) = CheckPairs(set, pairs, idx.Uploads.Select(u => u.Id));
            Console.WriteLine($"check-pairs: {same} of {total} listed pair(s) share a group");
            foreach (var x in apart) Console.WriteLine("  apart: " + x);
        }
        if (dry) { Console.WriteLine("dry run: nothing written"); return 0; }
        if (!await GroupStore.WriteAsync(store, paths, set, idx.Version, cts.Token)) throw new InvalidOperationException("groups were written meanwhile");
        Console.WriteLine($"wrote {GroupStore.File(paths, idx.Version)}");
        return 0;
    }
}
