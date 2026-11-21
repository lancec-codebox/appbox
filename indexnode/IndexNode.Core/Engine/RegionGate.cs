using System.Net.Sockets;

namespace IndexNode;

/// <summary>0.13.17: the region rule. Some stages read or write a lot against the storage account, so a node may run them only from the store's region (control.json "region", e.g. "westus2").
/// At start-up a node finds its own region (IMDS, 2 s hard, else the placement override INDEXNODE_REGION) and exits before claiming anything when it asks for a region-only stage from elsewhere.</summary>
public static class RegionGate
{
    /// <summary>THE list of region-only stages (one place): the check, the process stage and every one of its steps, the group step and the search-index build.</summary>
    public static readonly string[] OnlyStages = ["check", "process", .. Protocol.StepStages, "group", "search-index"];

    public const string ImdsLocationUrl = "http://169.254.169.254/metadata/instance/compute/location?api-version=2021-02-01&format=text";
    public static readonly TimeSpan ImdsTimeout = TimeSpan.FromSeconds(2);

    /// <summary>What was found. Source: env, imds, placement or none. The rest is for the diagnostic: each source's answer and timing.</summary>
    public sealed record Detected(string? Region, string Source)
    {
        public string? Env { get; init; }
        public string? Imds { get; init; } public double ImdsMs { get; init; } public string? ImdsNote { get; init; }
        public string? PlacementRegion { get; init; } public double PlacementMs { get; init; } public string? PlacementNote { get; init; }
    }
    public sealed record Verdict(bool Run, string? Error, string? Warning);

    /// <summary>The time limit of the placement detection at start-up (it tries IMDS itself first, 2 s).</summary>
    public static readonly TimeSpan PlacementTimeout = TimeSpan.FromSeconds(4);

    /// <summary>IMDS location with a HARD 2 s limit (connect included, no proxy): the region, or null with a note.</summary>
    public static async Task<(string? Region, double Ms, string Note)> ImdsAsync(HttpMessageHandler? imds, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var http = imds != null ? new HttpClient(imds, false) : new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = ImdsTimeout });
            http.Timeout = Timeout.InfiniteTimeSpan;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(ImdsTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, ImdsLocationUrl); req.Headers.TryAddWithoutValidation("Metadata", "true");
            var send = http.SendAsync(req, limit.Token);
            if (await Task.WhenAny(send, Task.Delay(ImdsTimeout, ct)) == send)
            {
                using var res = await send;
                if (!res.IsSuccessStatusCode) return (null, sw.Elapsed.TotalMilliseconds, "HTTP " + (int)res.StatusCode);
                var loc = (await res.Content.ReadAsStringAsync(limit.Token)).Trim();
                return loc.Length > 0 ? (loc, sw.Elapsed.TotalMilliseconds, "ok") : (null, sw.Elapsed.TotalMilliseconds, "empty answer");
            }
            limit.Cancel(); return (null, sw.Elapsed.TotalMilliseconds, "timeout (2 s)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or SocketException or IOException) { return (null, sw.Elapsed.TotalMilliseconds, e.InnerException?.Message ?? e.Message); }
    }

    /// <summary>The node's own placement detection (the one that logs "placement: azure westus2 ..."), with a time limit; storage connect times are skipped here (only the region matters).</summary>
    public static async Task<(string? Region, double Ms, string Note)> PlacementAsync(Func<CancellationToken, Task<Placement>>? probe, string storageRegion, TimeSpan limit, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var lim = CancellationTokenSource.CreateLinkedTokenSource(ct); lim.CancelAfter(limit);
            var t = (probe ?? (c => Placement.DetectAsync(null, storageRegion, null, null, null, c)))(lim.Token);
            if (await Task.WhenAny(t, Task.Delay(limit, ct)) != t) { lim.Cancel(); return (null, sw.Elapsed.TotalMilliseconds, $"timeout ({limit.TotalSeconds:0} s)"); }
            var p = await t;
            return (string.IsNullOrWhiteSpace(p.Region) ? null : p.Region.Trim(), sw.Elapsed.TotalMilliseconds, p.Region != null ? $"{p.Provider} {p.Region}" : $"{p.Provider}, region unknown{(p.Reason != null ? ": " + p.Reason : "")}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (null, sw.Elapsed.TotalMilliseconds, $"timeout ({limit.TotalSeconds:0.#} s)"); }
        catch (Exception e) { return (null, sw.Elapsed.TotalMilliseconds, "failed: " + e.Message); }
    }

    /// <summary>The node's region, first that answers: INDEXNODE_REGION (an explicit override), IMDS (2 s hard), the placement detection (a few seconds); else none.</summary>
    public static async Task<Detected> DetectAsync(HttpMessageHandler? imds, string? envRegion, Func<CancellationToken, Task<Placement>>? placement, string storageRegion, CancellationToken ct, TimeSpan? placementLimit = null)
    {
        if (!string.IsNullOrWhiteSpace(envRegion)) return new Detected(envRegion.Trim(), "env") { Env = envRegion.Trim() };
        var i = await ImdsAsync(imds, ct);
        if (i.Region != null) return new Detected(i.Region, "imds") { Imds = i.Region, ImdsMs = i.Ms, ImdsNote = i.Note };
        var p = await PlacementAsync(placement, storageRegion, placementLimit ?? PlacementTimeout, ct);
        return new Detected(p.Region, p.Region != null ? "placement" : "none") { ImdsMs = i.Ms, ImdsNote = i.Note, PlacementRegion = p.Region, PlacementMs = p.Ms, PlacementNote = p.Note };
    }

    /// <summary>Decides whether this node may start. <paramref name="controlRegion"/> null = control.json has none: a warning, everything runs as before.</summary>
    public static Verdict Evaluate(IEnumerable<string> stages, string? controlRegion, Detected own, bool allowOutOfRegion)
    {
        var only = stages.Where(s => OnlyStages.Contains(s)).ToList();
        if (only.Count == 0) return new(true, null, null);
        if (string.IsNullOrWhiteSpace(controlRegion)) return new(true, null, "region: control.json has no region, so the region rule is off (set it with: indexnode control region <name>)");
        bool inRegion = own.Region != null && Placement.NormRegion(own.Region) == Placement.NormRegion(controlRegion);
        if (inRegion) return new(true, null, null);
        var list = string.Join(",", only);
        var msg = $"region-only stage(s) {list} need region {controlRegion}, this node is in {(own.Region ?? "no detected Azure region")} (source: {own.Source})";
        return allowOutOfRegion ? new(true, null, "region: " + msg + " - running anyway (--allow-out-of-region)") : new(false, "indexnode: " + msg + ". Run them on a node in " + controlRegion + ", or pass --allow-out-of-region for a one-item test.", null);
    }
}
