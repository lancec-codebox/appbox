namespace IndexNode.Tests;

/// <summary>A fake timer for the engine's renewal and control loops: a delay completes only when the test ticks it. No real time, so nothing depends on scheduling speed.</summary>
public sealed class FakeTimer
{
    private readonly object gate = new();
    private readonly List<(TimeSpan Span, TaskCompletionSource Tcs)> waiting = [];

    public Task Delay(TimeSpan span, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) waiting.Add((span, tcs));
        ct.Register(() => { lock (gate) waiting.RemoveAll(w => w.Tcs == tcs); tcs.TrySetCanceled(ct); });
        return tcs.Task;
    }

    public int Pending(TimeSpan span) { lock (gate) return waiting.Count(w => w.Span == span); }

    /// <summary>Waits until <paramref name="waiters"/> delays of this length are pending (each loop has reached its sleep), then fires them all once.</summary>
    public async Task TickAsync(TimeSpan span, int waiters)
    {
        var until = DateTime.UtcNow.AddSeconds(30); // a failure bound, not a pacing
        while (Pending(span) < waiters) { if (DateTime.UtcNow > until) throw new TimeoutException($"only {Pending(span)} of {waiters} loops reached their {span.TotalMilliseconds} ms sleep"); await Task.Yield(); }
        List<TaskCompletionSource> fire;
        lock (gate) { fire = waiting.Where(w => w.Span == span).Select(w => w.Tcs).ToList(); waiting.RemoveAll(w => w.Span == span); }
        foreach (var t in fire) t.TrySetResult();
    }
}
