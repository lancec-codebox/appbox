namespace IndexNode;

/// <summary>0.13.17 `indexnode region [--stages a,b]`: a read-only diagnostic. Prints what this machine detects (env override, IMDS with its timing, the node's placement detection with its timing), the final region and its source, control.json's region, and whether a node here could run the region-only stages. Writes nothing.</summary>
public static class RegionCommand
{
    public static async Task<int> RunAsync(Args a, IStore? storeOverride = null, Paths? pathsOverride = null, HttpMessageHandler? imds = null, Func<CancellationToken, Task<Placement>>? placement = null, string? envRegion = null, TextWriter? output = null)
    {
        var o = output ?? Console.Out;
        var cfg = NodeConfig.Load();
        var d = await RegionGate.DetectAsync(imds, envRegion ?? Environment.GetEnvironmentVariable("INDEXNODE_REGION"), placement, cfg.StorageRegion ?? "westus2", default);
        // the detection stops at the first source that answers; the diagnostic shows every source, so ask the ones it skipped as well
        var im = d.Source == "env" ? await RegionGate.ImdsAsync(imds, default) : (d.Imds, d.ImdsMs, d.ImdsNote ?? "ok");
        var pl = d.Source is "env" or "imds" ? await RegionGate.PlacementAsync(placement, cfg.StorageRegion ?? "westus2", RegionGate.PlacementTimeout, default) : (d.PlacementRegion, d.PlacementMs, d.PlacementNote ?? "");
        o.WriteLine($"env INDEXNODE_REGION: {(string.IsNullOrWhiteSpace(envRegion ?? Environment.GetEnvironmentVariable("INDEXNODE_REGION")) ? "(not set)" : (envRegion ?? Environment.GetEnvironmentVariable("INDEXNODE_REGION")))}");
        o.WriteLine($"imds:      {(im.Item1 ?? "no answer")} ({im.Item3}, {im.Item2:0} ms)");
        o.WriteLine($"placement: {(pl.Item1 ?? "no region")} ({pl.Item3}, {pl.Item2:0} ms)");
        o.WriteLine($"this machine's region: {d.Region ?? "none"} (source: {d.Source})");
        string? ctl = null; string ctlNote;
        try
        {
            IStore store; Paths paths;
            if (storeOverride != null) { store = storeOverride; paths = pathsOverride ?? new Paths("index/"); }
            else { var (_, st, pa, _) = Commands.Open(a); store = st; paths = pa; }
            ctl = ControlFile.Parse((await store.GetAsync(paths.Control, default))?.Data).Region; ctlNote = ctl ?? "(none: the region rule is off)";
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException) { ctlNote = "(not read: " + e.Message + ")"; }
        o.WriteLine($"control.json region: {ctlNote}");
        var stages = a.Get("stages") is { } sg ? Protocol.ExpandStages(sg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : RegionGate.OnlyStages;
        var v = RegionGate.Evaluate(stages.Where(s => RegionGate.OnlyStages.Contains(s)), ctl, d, false);
        var only = string.Join(",", RegionGate.OnlyStages.Where(s => !Protocol.StepStages.Contains(s)));
        o.WriteLine(!v.Run ? $"a node here could NOT run the region-only stages ({only}, and the process steps): {v.Error!.Replace("indexnode: ", "")}"
            : v.Warning != null ? $"a node here could run them, but: {v.Warning.Replace("region: ", "")}"
            : $"a node here COULD run the region-only stages ({only}, and the process steps).");
        return v.Run ? 0 : 4;
    }
}
