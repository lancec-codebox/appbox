using System.Diagnostics;

namespace IndexNode;

/// <summary>
/// 0.13.1: the "search-index" queue stage: builds index/search/vN+1/ from every centers.zip (reusing unchanged uploads of the head, the same builder as `search-index update`) and moves head.yaml with If-Match
/// (a lost race leaves the winner's head and fails this run, which then retries). Versions older than head - search.keepVersions (default 3) are deleted after head.yaml moves (derived data). When more centers finished while it ran, the entry goes pending again, due in 15 minutes.
/// </summary>
public sealed class SearchIndexStage(IStore store, int keepVersions = 3, PreCheckSettings? settings = null) : IStageHandler
{
    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.Token; var paths = ctx.Paths; var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        ctx.Report("search-index", "listing");
        var head = await SearchData.LoadAsync(store, paths, null, ct);
        var ups = await SearchIndexBuild.ListCentersAsync(store, paths, ct);
        int threads = Math.Max(1, ctx.ThreadShare());
        SearchData next;
        if (head == null) next = await SearchIndexBuild.BuildAsync(store, paths, ups, null, null, 1, 1, threads, m => Log.Info("search-index: " + m), ct);
        else next = await SearchIndexBuild.BuildAsync(store, paths, ups, head.Mean, head, head.Version + 1, head.MeanVersion, threads, m => Log.Info("search-index: " + m), ct);
        next.BuiltAt = started;   // centers that finish after this are not in it: the pre-check looks at them by brute force
        ctx.Report("search-index", "groups");
        var groups = await GroupBuild.ApplyAsync(store, paths, head, next, ct, m => Log.Info("search-index: " + m), threads, settings, requireGroups: true, foldOnly: true);
        ctx.Report("search-index", "catalog");
        next.Catalog = await CatalogBuild.BuildAsync(store, paths, next.Version, next.Uploads, head?.Catalog, new PreCheckSettings().AiFlag, m => Log.Info("search-index: " + m), ct);
        next.Catalog.ApplyGroups(groups);
        var missing = next.Catalog.Missing;
        if (missing.Count > 0) Log.Info($"search-index: WARNING {missing.Count} upload(s) have no readable video.json (catalog data \"missing\"; run: indexnode videos backfill, the next build fills them in): {string.Join(", ", missing.Take(20))}{(missing.Count > 20 ? ", ..." : "")}");
        ctx.Report("search-index", $"publishing v{next.Version}");
        await next.PublishAsync(store, paths, ct);
        try { var gone = await SearchData.PruneAsync(store, paths, keepVersions, ct); if (gone.Count > 0) Log.Info($"search-index: removed old versions {string.Join(", ", gone.Select(g => "v" + g))} (keeping {keepVersions} back)"); }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"search-index: pruning old versions failed ({e.Message})"); }
        Log.Info($"search-index: published v{next.Version}: {next.Uploads.Count} uploads, {next.Count} centers, {sw.Elapsed.TotalSeconds:F1}s (head {(head == null ? "none" : "v" + head.Version)}, {ups.Count} centers.zip{(missing.Count > 0 ? $", {missing.Count} with video.json missing" : "")})");
        ctx.Job.ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        // centers finished while this ran are not in the new version: the next build is due in 15 minutes
        var cut = Protocol.Utc(started);
        if (ctx.Queue != null && ctx.Queue.Read(st => st.Subjects.Values.Any(s => s.CentersAt != null && string.CompareOrdinal(s.CentersAt, cut) > 0))) ctx.NextDue = Protocol.Utc(DateTimeOffset.UtcNow + SearchIndexJobs.Delay);
    }
}
