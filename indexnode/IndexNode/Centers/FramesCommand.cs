using System.Diagnostics;

namespace IndexNode;

/// <summary>`indexnode frames convert [--dry-run] [--threads N] [--limit N]` (0.13.9): writes index/search/frames/&lt;id&gt;.bin for every drama that has a centers.zip and no frames file yet, from one read of the centers.bin span.
/// Create-only (a frames file that exists is never touched), and a drama under an active claim is skipped (its claim writes the frames file itself). Run once, from a node near the store.</summary>
public static class FramesCommand
{
    public sealed record Report(int Zips, int Have, int Converted, int Claimed, int Raced, List<string> ClaimedIds, List<string> Failed);

    public static async Task<int> RunAsync(Args a, IStore? remote = null)
    {
        if (a.Positional.FirstOrDefault() != "convert") throw new ArgumentException("frames convert [--dry-run] [--threads N] [--limit N]");
        var (cfg, store, paths, _) = CentersCommands.OpenStore(a, remote);
        using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        bool dry = a.Has("dry-run") || a.Has("dry");
        int threads = a.Get("threads") is { } t ? int.Parse(t) : Math.Min(8, cfg.EffectiveThreads);
        int limit = a.Get("limit") is { } lm ? int.Parse(lm) : int.MaxValue;
        var sw = Stopwatch.StartNew();
        var rep = await ConvertAsync(store, paths, dry, threads, limit, m => Console.WriteLine("  " + m), cts.Token);
        Console.WriteLine($"frames convert: {rep.Zips} centers.zip, {rep.Have} already have a frames file, {(dry ? "would convert" : "converted")} {rep.Converted}, {rep.Claimed} skipped (under an active claim){(rep.Raced > 0 ? $", {rep.Raced} raced (created meanwhile)" : "")}{(rep.Failed.Count > 0 ? $", {rep.Failed.Count} FAILED" : "")}{(dry ? " (dry run)" : $", {sw.Elapsed.TotalMinutes:F1} min")}");
        foreach (var id in rep.ClaimedIds.Take(20)) Console.WriteLine("  claimed: " + id);
        foreach (var f in rep.Failed.Take(20)) Console.WriteLine("  FAILED: " + f);
        return rep.Failed.Count == 0 ? 0 : 1;
    }

    public static async Task<Report> ConvertAsync(IStore store, Paths paths, bool dry, int threads, int limit, Action<string>? say, CancellationToken ct)
    {
        var pre = paths.Prefix + "videos/"; var zips = new List<string>();
        foreach (var b in await store.ListAsync(pre, false, false, ct))
        {
            if (!b.Name.EndsWith("/centers.zip", StringComparison.Ordinal)) continue;
            var id = b.Name[pre.Length..^"/centers.zip".Length]; if (!id.Contains('/')) zips.Add(id);
        }
        zips.Sort(StringComparer.Ordinal);
        var have = (await store.ListAsync(paths.FramesDir, false, false, ct)).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal); var now = DateTimeOffset.UtcNow;
        foreach (var b in await store.ListAsync(paths.ActiveDir, false, false, ct))
            if (b.LeaseState == "leased" || b.Created is { } c && now - c < JobQueue.NewActiveGrace) claimed.Add(Path.GetFileNameWithoutExtension(b.Name));
        int nHave = 0, converted = 0, nClaimed = 0, raced = 0; var claimedIds = new List<string>(); var failed = new List<string>(); var todo = new List<string>();
        foreach (var id in zips)
        {
            if (have.Contains(paths.FramesFile(id))) { nHave++; continue; }
            if (claimed.Contains(id)) { nClaimed++; claimedIds.Add(id); continue; }
            todo.Add(id);
        }
        todo = todo.Take(limit).ToList();
        if (dry) return new Report(zips.Count, nHave, todo.Count, nClaimed, 0, claimedIds, failed);
        int done = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, async (id, t) =>
        {
            try
            {
                var z = await CentersZip.OpenAsync(store, paths.Video(id, "centers.zip"), t) ?? throw new FileNotFoundException(id);
                var bin = await z.ReadAsync(0, (int)z.DataLength, t);   // one read of the centers.bin span
                CentersFile.ParseHeader(bin);
                if (await FramesFile.WriteAsync(store, paths, id, bin, 0, true, t)) Interlocked.Increment(ref converted); else Interlocked.Increment(ref raced);
                int d = Interlocked.Increment(ref done); if (d % 100 == 0) say?.Invoke($"{d} of {todo.Count}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { lock (failed) failed.Add($"{id}: {e.Message}"); }
        });
        return new Report(zips.Count, nHave, converted, nClaimed, raced, claimedIds, failed);
    }
}
