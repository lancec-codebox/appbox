namespace IndexNode;

/// <summary>
/// 0.13.25: keeps a bulk job under the store breaker: <see cref="WaitAsync"/> returns once the ops made in the last 60 seconds (read from the meter's running total) are below <c>fraction</c> of the breaker's max-per-min,
/// so the job's threads can never trip it. The overshoot is at most what the threads in flight add before they next ask (a few ops each).
/// </summary>
public sealed class OpPacer(Func<long> total, Func<int> maxPerMin, double fraction = 0.8, Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public OpPacer(Func<long> total, int maxPerMin, double fraction = 0.8, Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null) : this(total, () => maxPerMin, fraction, now, delay) { }
    readonly Func<DateTime> clock = now ?? (() => DateTime.UtcNow); readonly Func<TimeSpan, CancellationToken, Task> sleep = delay ?? ((d, t) => Task.Delay(d, t));
    readonly Queue<(DateTime At, long Total)> samples = new(); readonly Queue<DateTime> admitted = new(); readonly object gate = new(); public long Waits;
    public int Budget => Math.Max(1, (int)(maxPerMin() * fraction));

    /// <summary>0.13.29: a slot is reserved atomically: every caller that passes is written to the admitted log under the lock, and the window count is the larger of that log and the meter's total, so N concurrent callers (threads, lanes, an index build) cannot all pass on the same reading.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (gate)
            {
                var t = clock(); var cur = total(); samples.Enqueue((t, cur));
                while (samples.Count > 1 && t - samples.Peek().At > TimeSpan.FromSeconds(60)) samples.Dequeue();
                while (samples.Count > 1000) samples.Dequeue();
                while (admitted.Count > 0 && t - admitted.Peek() > TimeSpan.FromSeconds(60)) admitted.Dequeue();
                long inWindow = Math.Max(admitted.Count, cur - samples.Peek().Total);
                if (inWindow < Budget) { admitted.Enqueue(t); return; }
            }
            Interlocked.Increment(ref Waits); await sleep(TimeSpan.FromMilliseconds(250), ct);
        }
    }
}
