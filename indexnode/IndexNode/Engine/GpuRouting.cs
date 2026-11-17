using System.Text.Json;

namespace IndexNode;

/// <summary>Which jobs go to the GPU node: the stage sets, the marker blobs that wake it, and the CPU fallback after a wait.</summary>
public static class GpuRouting
{
    /// <summary>The steps a GPU node claims (SSCD, CLIP, cast's face detect and embed, OCR det/rec).</summary>
    public static readonly string[] GpuStages = ["sscd", "clip", "cast", "ocrsubs"];

    /// <summary>The steps that get a marker blob and are held for the GPU node for a while. ocrsubs stays with the CPU nodes (its cost is the video decode), a GPU node may still list it.</summary>
    public static readonly string[] MarkerStages = ["sscd", "clip", "cast"];

    /// <summary>The usual hold to turn GPU routing on with ("indexnode control gpu-hold 30m"); there is no hold unless something asks for one.</summary>
    public static readonly TimeSpan CpuFallbackAfter = TimeSpan.FromMinutes(30);

    /// <summary>The hold in force: the node flag (--gpu-fallback) wins, else control.json gpu.hold; null = GPU routing off. A zero or negative value counts as off.</summary>
    public static TimeSpan? EffectiveHold(TimeSpan? flag, ControlFile? control)
    {
        TimeSpan? h = flag;
        if (h == null && control?.Gpu?.Hold is { Length: > 0 } s && !s.Equals("off", StringComparison.OrdinalIgnoreCase)) { try { h = WatchStage.ParseEvery(s); } catch (Exception) { h = null; } }
        return h is { } v && v > TimeSpan.Zero ? v : null;
    }

    /// <summary>The --gpu-fallback value: a duration, or "off" for no hold (which also beats the control file).</summary>
    public static TimeSpan ParseFlag(string v) => v.Equals("off", StringComparison.OrdinalIgnoreCase) ? TimeSpan.Zero : WatchStage.ParseEvery(v);

    public const string MarkerDir = "gpu-ready/";

    public static string MarkerName(Paths paths, string step, string jobId) => $"{paths.Prefix}{MarkerDir}{step}.{jobId}";

    public static bool IsMarkerStage(string stage) => MarkerStages.Contains(stage);

    /// <summary>A GPU node's stages are cut to the GPU ones; none left means the request was wrong.</summary>
    public static string[] GpuNodeStages(IEnumerable<string> requested) => requested.Where(s => GpuStages.Contains(s)).Distinct().ToArray();

    /// <summary>May this node claim a pending job of the stage, ready since the given time? A GPU node: always (its stage list is already the GPU stages). A CPU node: a stage without markers at once; a marker stage at once unless a hold is on, then only once the job has been ready that long.</summary>
    public static bool MayClaim(bool gpuNode, string stage, DateTimeOffset? readySince, DateTimeOffset now, TimeSpan? hold = null)
    {
        if (gpuNode || !IsMarkerStage(stage) || hold == null) return true; // no hold: nothing waits for the GPU node
        return readySince != null && now - readySince.Value >= hold.Value;
    }

    public static byte[] MarkerBody(string step, string jobId, DateTimeOffset readyAt) =>
        JsonSerializer.SerializeToUtf8Bytes(new { v = 1, step, jobId, readyAt = Protocol.Utc(readyAt) });

    public static DateTimeOffset? ReadyAtOf(byte[] body)
    {
        try { return JsonDocument.Parse(body).RootElement.TryGetProperty("readyAt", out var r) && DateTimeOffset.TryParse(r.GetString(), null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Writes index/gpu-ready/&lt;step&gt;.&lt;jobId&gt; (the first writer wins: its readyAt stays). True when this call made it.</summary>
    public static Task<bool> WriteMarkerAsync(IStore store, Paths paths, string step, string jobId, CancellationToken ct, DateTimeOffset? now = null) =>
        store.PutAsync(MarkerName(paths, step, jobId), MarkerBody(step, jobId, now ?? DateTimeOffset.UtcNow), null, null, true, ct);

    /// <summary>Called when a video's frames are done: one marker per pending GPU-stage job of the video (a step that is already done or has no job gets none). Returns the steps marked.</summary>
    public static async Task<string[]> WriteReadyMarkersAsync(IStore store, Paths paths, string jobId, CancellationToken ct, TimeSpan? flag = null)
    {
        // markers only while GPU routing is on: otherwise a finished frames step would wake (and bill) an on-demand GPU job
        var control = ControlFile.Parse((await store.GetAsync(paths.Control, ct))?.Data);
        if (EffectiveHold(flag, control) == null) return [];
        var made = new List<string>();
        foreach (var step in MarkerStages)
            if (await store.GetTagsAsync(paths.Job(step, jobId), ct) is { } tags && tags.GetValueOrDefault(Tags.State) == "pending")
            { await WriteMarkerAsync(store, paths, step, jobId, ct); made.Add(step); }
        return made.ToArray();
    }

    public static async Task DeleteMarkerAsync(IStore store, Paths paths, string step, string jobId)
    {
        if (!IsMarkerStage(step)) return;
        try { await store.DeleteAsync(MarkerName(paths, step, jobId), CancellationToken.None); } catch (Exception e) { Log.Info($"gpu marker {step}.{jobId}: delete failed ({e.Message})"); }
    }
}
