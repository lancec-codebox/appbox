using System.Diagnostics;

namespace IndexNode;

/// <summary>
/// 0.13.92 (owner, 2026-11-28: "if we did have a lock ... around the GPU ... maybe it would be fine to do the two-lane thing"): on a CUDA node, the steps that run on the card
/// take one process-wide lock, so with two lanes one lane's CPU steps (frames, centers, thumbs, a decode) run while the other lane's GPU step has the card, and two GPU steps
/// never share it (the T4's 16 GB filled with two lanes at once on 2026-11-28: CUBLAS_STATUS_ALLOC_FAILED in OCR). Env INDEXNODE_GPU_LOCK=0 turns it off.
/// </summary>
public static class GpuGate
{
    static readonly SemaphoreSlim gate = new(1, 1);
    /// <summary>The steps that run on the card with --device cuda.</summary>
    public static readonly IReadOnlySet<string> Steps = new HashSet<string>(StringComparer.Ordinal) { "sscd", "clip", "dino", "transcribe", "ocrsubs", "cast" };
    public static bool On => Environment.GetEnvironmentVariable("INDEXNODE_GPU_LOCK") != "0";
    /// <summary>Total time steps waited for the card (ms), for the run's summary.</summary>
    public static long WaitedMs;

    /// <summary>The lock for <paramref name="step"/> on <paramref name="device"/>, or null when the step does not take it (not CUDA, not a GPU step, or the lock is off).</summary>
    public static async Task<IDisposable?> EnterAsync(string? device, string step, CancellationToken ct)
    {
        if (device != "cuda" || !On || !Steps.Contains(step)) return null;
        var sw = Stopwatch.StartNew();
        await gate.WaitAsync(ct);
        Interlocked.Add(ref WaitedMs, sw.ElapsedMilliseconds);
        if (sw.Elapsed.TotalSeconds >= 1) Log.Info($"gpu: {step} waited {sw.Elapsed.TotalSeconds:0.0}s for the card");
        return new Held();
    }

    sealed class Held : IDisposable { int released; public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate.Release(); } }
}
