namespace IndexNode;

/// <summary>
/// 0.13.26 `indexnode serve --download-service`: the one reader and writer of the full download queue (index/jobs/download/), under a singleton lease on index/jobs/download/service.json (a second serve waits and takes over when the lease lapses).
/// A loop runs <see cref="DownloadService.RunOnceAsync"/> every `download.service-seconds` (default 20 s). The work queue's size is `download.work-max` (default 1000). Needs a SAS that can write.
/// </summary>
public static class DownloadServiceHost
{
    public static async Task StartAsync(PreCheckServer server, IStore store, Paths paths, NodeConfig cfg, Args a, CancellationToken ct)
    {
        var node = cfg.NodeName ?? Environment.MachineName; int workMax = cfg.Download?.WorkMax ?? QueueSplit.DefaultWorkMax; int every = Math.Max(5, cfg.Download?.ServiceSeconds ?? 20);
        server.ContainerUrl = Uri.TryCreate(cfg.Sas, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) is var p && p.Length > 0 ? new UriBuilder(u) { Query = "", Fragment = "" }.Uri.ToString().TrimEnd('/') : null : null;
        var root = await JobQueue.ResolveAsync(store, paths, ct); if (root.IsLegacyLayout) throw new QueueNotMigratedException("indexnode: --download-service needs the split queue (run: indexnode queue split)");
        var full = new JobQueue(store, paths.DownloadQueue, "service-" + node); var work = new JobQueue(store, paths.DownloadWork, "service-" + node); var proc = new JobQueue(store, paths.ProcessQueue, "service-" + node);
        var svc = new DownloadService(full, work, proc, async (id, t) => (await server.CheckAsync(id, false, t))["record"] as System.Text.Json.Nodes.JsonObject, workMax, store);
        server.Downloads = svc;
        _ = Task.Run(async () =>
        {
            var name = paths.DownloadQueue.Prefix + "jobs/download/service.json"; string? lease = null; bool loaded = false;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (lease == null)
                    {
                        await store.PutAsync(name, System.Text.Encoding.UTF8.GetBytes("{\"service\":\"download\"}"), null, null, true, ct);
                        lease = await store.AcquireLeaseAsync(name, Protocol.LeaseSeconds, ct);
                        if (lease == null) { await Task.Delay(TimeSpan.FromSeconds(every), ct); continue; }   // another serve owns the queue
                        Log.Info("download service: holding the singleton lease; reading the download queue");
                    }
                    else await store.RenewLeaseAsync(name, lease, ct);
                    if (!loaded) { await full.LoadAsync(ct); await work.LoadAsync(ct); await proc.LoadAsync(ct); loaded = true; }
                    var pass = await svc.RunOnceAsync(ct);
                    if (pass.Cleared > 0 && work.Blocks >= 200) await svc.RollOverWorkAsync(ct);   // the snapshot then holds only the live items
                    if (pass.FoldedBack + pass.Checked + pass.Entered + pass.ToppedUp + pass.Cleared > 0) Log.Info($"download service: folded back {pass.FoldedBack}, checked {pass.Checked}, entered {pass.Entered}, topped up {pass.ToppedUp}, cleared {pass.Cleared}");
                }
                catch (OperationCanceledException) { break; }
                catch (LeaseLostException) { lease = null; loaded = false; }
                catch (Exception e) { Log.Info("download service: pass failed: " + Redact.Sas(e.Message)); if (e is StorageException { Status: 409 or 404 }) lease = null; }
                try { await Task.Delay(TimeSpan.FromSeconds(every), ct); } catch (OperationCanceledException) { break; }
            }
            if (lease != null) try { await store.ReleaseLeaseAsync(name, lease, CancellationToken.None); } catch { }
        }, ct);
        Log.Info($"download service: work queue max {workMax}, pass every {every} s");
    }
}
