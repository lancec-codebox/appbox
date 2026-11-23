namespace IndexNode;

/// <summary>Re-reads jobs/control.json with a conditional GET (If-None-Match on the ETag).</summary>
public sealed class ControlWatcher(IStore store, Paths paths)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? etag;
    private bool missing;
    private DateTimeOffset lastFetch = DateTimeOffset.MinValue;

    public ControlFile Current { get; private set; } = new();
    public event Action? Changed;

    /// <summary>Calls with <paramref name="force"/> always hit storage; others are throttled to 1 s.</summary>
    public async Task RefreshAsync(CancellationToken ct, bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - lastFetch < TimeSpan.FromSeconds(1)) return;
        await gate.WaitAsync(ct);
        try
        {
            lastFetch = DateTimeOffset.UtcNow;
            var r = await store.GetIfChangedAsync(paths.Control, etag, ct);
            if (!r.Changed) return;
            if (!r.Exists && missing) return; // still no control file: nothing to report
            missing = !r.Exists;
            etag = r.ETag;
            var before = Current;
            Current = r.Exists ? ControlFile.Parse(r.Data) : new ControlFile();
            Log.Info($"control: mode={Current.Mode} stages={string.Join(',', (Current.Stages ?? new()).Select(s => s.Key + "=" + s.Value))}");
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Log.Info($"control: read failed, keeping last setting ({e.Message})"); }
        finally { gate.Release(); }
    }

    private string? announced;
    /// <summary>0.13.28: control changes arrive as a "ctl" line on the processing queue (its etag names the new control.json): the file is read only when the queue says it changed, never on a timer.</summary>
    public async Task RefreshIfAnnouncedAsync(string? queueEtag, CancellationToken ct)
    {
        if (queueEtag == announced) return; announced = queueEtag; await RefreshAsync(ct, force: true);
    }

    /// <summary>After a control write: one "ctl" line on the processing queue (a node reading the queue learns of it at its next tail).</summary>
    public static async Task AnnounceAsync(IStore store, Paths paths, string by, CancellationToken ct)
    {
        try
        {
            var et = (await store.HeadAsync(paths.Control, ct))?.ETag ?? Guid.NewGuid().ToString("N");
            var q = await JobQueue.OpenAsync(store, paths, by, false, ct);
            await q.AppendLinesAsync([new QLine { T = "ctl", S = "control", Ets = et, By = by }], ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info("control: written, but the queue line failed (" + e.Message + "); nodes pick it up at their next start"); }
    }

    public Level Effective(string stage) => Current.Effective(stage);

    /// <summary>indexnode control gpu-hold &lt;duration&gt;|off: turns GPU routing on for every CPU node (and gpu-ready markers on), or off (no "gpu" entry).</summary>
    public static async Task<ControlFile> SetGpuHoldAsync(IStore store, Paths paths, string value, string changedBy, CancellationToken ct)
    {
        var cur = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) cur.Gpu = null;
        else { var d = RefreshStage.ParseEvery(value); if (d <= TimeSpan.Zero) throw new ArgumentException("gpu-hold must be a positive duration or off"); cur.Gpu = new GpuControl { Hold = value }; }
        cur.V = 1; cur.ChangedAt = Protocol.NowUtc(); cur.ChangedBy = changedBy;
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct); await AnnounceAsync(store, paths, changedBy, ct);
        return cur;
    }

    /// <summary>indexnode control storage stop|run: the kill switch (control.json "storage": "stop"). Everything else in the file stays.</summary>
    public static async Task<ControlFile> SetStorageAsync(IStore store, Paths paths, string value, string changedBy, CancellationToken ct)
    {
        var v = value.Trim().ToLowerInvariant(); if (v is not ("stop" or "run")) throw new ArgumentException("control storage stop|run");
        var cur = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        cur.Storage = v == "stop" ? "stop" : null; cur.V = 1; cur.ChangedAt = Protocol.NowUtc(); cur.ChangedBy = changedBy;
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct); await AnnounceAsync(store, paths, changedBy, ct);
        return cur;
    }

    /// <summary>indexnode control region &lt;name&gt;|off: the store's region for the region rule (off removes it). Everything else in the file stays.</summary>
    public static async Task<ControlFile> SetRegionAsync(IStore store, Paths paths, string value, string changedBy, CancellationToken ct)
    {
        var cur = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("control region <name like westus2>|off");
        cur.Region = value.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : Placement.NormRegion(value);
        cur.V = 1; cur.ChangedAt = Protocol.NowUtc(); cur.ChangedBy = changedBy;
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct); await AnnounceAsync(store, paths, changedBy, ct);
        return cur;
    }

    /// <summary>indexnode control ai-dramas on|off: whether nodes may claim a drama the pre-check held as AI.</summary>
    public static async Task<ControlFile> SetAiDramasAsync(IStore store, Paths paths, string value, string changedBy, CancellationToken ct)
    {
        var cur = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        if (!value.Equals("on", StringComparison.OrdinalIgnoreCase) && !value.Equals("off", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("ai-dramas must be on or off");
        cur.AiDramas = value.ToLowerInvariant(); cur.V = 1; cur.ChangedAt = Protocol.NowUtc(); cur.ChangedBy = changedBy;
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct); await AnnounceAsync(store, paths, changedBy, ct);
        return cur;
    }

    /// <summary>indexnode control classify on|off: whether classify is seeded and claimed (default off).</summary>
    public static async Task<ControlFile> SetClassifyAsync(IStore store, Paths paths, string value, string changedBy, CancellationToken ct)
    {
        var cur = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        if (!value.Equals("on", StringComparison.OrdinalIgnoreCase) && !value.Equals("off", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("classify must be on or off");
        cur.Classify = value.ToLowerInvariant(); cur.V = 1; cur.ChangedAt = Protocol.NowUtc(); cur.ChangedBy = changedBy;
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct); await AnnounceAsync(store, paths, changedBy, ct);
        return cur;
    }

    /// <summary>indexnode control dynamic-refresh on|off: whether the download service seeds refreshes of dynamic-schedule sources (default off).</summary>
    public static async Task<ControlFile> SetDynamicRefreshAsync(IStore store, Paths paths, string value, string changedBy, CancellationToken ct)
    {
        var cur = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        if (!value.Equals("on", StringComparison.OrdinalIgnoreCase) && !value.Equals("off", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("dynamic-refresh must be on or off");
        cur.DynamicRefresh = value.ToLowerInvariant(); cur.V = 1; cur.ChangedAt = Protocol.NowUtc(); cur.ChangedBy = changedBy;
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct); await AnnounceAsync(store, paths, changedBy, ct);
        return cur;
    }

    /// <summary>indexnode control --max-per-ip N|off: the per-IP download cap (downloads.maxPerIp); off removes it. Everything else in the file stays.</summary>
    public static async Task<ControlFile> SetMaxPerIpAsync(IStore store, Paths paths, string value, string changedBy, CancellationToken ct)
    {
        var cur = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value == "0") cur.Downloads = null;
        else if (int.TryParse(value, out var n) && n > 0) cur.Downloads = new DownloadsControl { MaxPerIp = n };
        else throw new ArgumentException("--max-per-ip must be a positive number or off");
        cur.V = 1; cur.ChangedAt = Protocol.NowUtc(); cur.ChangedBy = changedBy;
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct); await AnnounceAsync(store, paths, changedBy, ct);
        return cur;
    }

    /// <summary>indexnode control run|drain|stop [--stage S] [--message M]</summary>
    public static async Task<ControlFile> WriteAsync(IStore store, Paths paths, string level, string? stage, string? message, string changedBy, CancellationToken ct)
    {
        var cur = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        cur.V = 1;
        if (stage != null) stage = Protocol.CanonicalStage(stage); // the old "watch" is the refresh stage
        if (stage == null)
        {
            cur.Mode = level;
            cur.Stages = new() { ["download"] = "run", ["process"] = "run", ["refresh"] = "run" }; // a global setting replaces per-stage overrides
        }
        else
        {
            cur.Stages ??= new() { ["download"] = "run", ["process"] = "run", ["refresh"] = "run" };
            if (stage == "refresh") cur.Stages.Remove("watch"); // an old key would be shadowed by the new one
            cur.Stages[stage] = level;
        }
        if (message != null) cur.Message = message;
        cur.ChangedAt = Protocol.NowUtc();
        cur.ChangedBy = changedBy;
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct); await AnnounceAsync(store, paths, changedBy, ct);
        return cur;
    }
}

