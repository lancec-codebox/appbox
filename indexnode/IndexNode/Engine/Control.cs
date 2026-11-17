namespace IndexNode;

/// <summary>Re-reads index/control.json with a conditional GET (If-None-Match on the ETag).</summary>
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

    public Level Effective(string stage) => Current.Effective(stage);

    /// <summary>indexnode control gpu-hold &lt;duration&gt;|off: turns GPU routing on for every CPU node (and gpu-ready markers on), or off (no "gpu" entry).</summary>
    public static async Task<ControlFile> SetGpuHoldAsync(IStore store, Paths paths, string value, string changedBy, CancellationToken ct)
    {
        var cur = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) cur.Gpu = null;
        else { var d = WatchStage.ParseEvery(value); if (d <= TimeSpan.Zero) throw new ArgumentException("gpu-hold must be a positive duration or off"); cur.Gpu = new GpuControl { Hold = value }; }
        cur.V = 1; cur.ChangedAt = Protocol.NowUtc(); cur.ChangedBy = changedBy;
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct);
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
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct);
        return cur;
    }

    /// <summary>indexnode control run|drain|stop [--stage S] [--message M]</summary>
    public static async Task<ControlFile> WriteAsync(IStore store, Paths paths, string level, string? stage, string? message, string changedBy, CancellationToken ct)
    {
        var cur = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        cur.V = 1;
        if (stage == null)
        {
            cur.Mode = level;
            cur.Stages = new() { ["download"] = "run", ["process"] = "run", ["watch"] = "run" }; // a global setting replaces per-stage overrides
        }
        else
        {
            cur.Stages ??= new() { ["download"] = "run", ["process"] = "run", ["watch"] = "run" };
            cur.Stages[stage] = level;
        }
        if (message != null) cur.Message = message;
        cur.ChangedAt = Protocol.NowUtc();
        cur.ChangedBy = changedBy;
        await store.PutAsync(paths.Control, cur.ToBytes(), null, null, false, ct);
        return cur;
    }
}

