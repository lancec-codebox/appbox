namespace IndexNode;

/// <summary>
/// 0.13.25: keeps a bulk job under the store breaker: <see cref="WaitAsync"/> returns once the ops made in the last 60 seconds (read from the meter's running total) are below <c>fraction</c> of the breaker's max-per-min,
/// so the job's threads can never trip it. The overshoot is at most what the threads in flight add before they next ask (a few ops each).
/// </summary>
public sealed class OpPacer(Func<long> total, Func<int> maxPerMin, double fraction = 0.8, Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public OpPacer(Func<long> total, int maxPerMin, double fraction = 0.8, Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null) : this(total, () => maxPerMin, fraction, now, delay) { }
    readonly Func<DateTime> clock = now ?? (() => DateTime.UtcNow); readonly Func<TimeSpan, CancellationToken, Task> sleep = delay ?? ((d, t) => Task.Delay(d, t));
    readonly Queue<(DateTime At, long Total)> samples = new(); readonly object gate = new(); public long Waits;
    public int Budget => Math.Max(1, (int)(maxPerMin() * fraction));

    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            long inWindow; lock (gate)
            {
                var t = clock(); var cur = total(); samples.Enqueue((t, cur));
                while (samples.Count > 1 && t - samples.Peek().At > TimeSpan.FromSeconds(60)) samples.Dequeue();
                inWindow = cur - samples.Peek().Total;   // ops since the oldest sample inside the minute
                while (samples.Count > 1000) samples.Dequeue();
            }
            if (inWindow < Budget) return;
            Interlocked.Increment(ref Waits); await sleep(TimeSpan.FromMilliseconds(250), ct);
        }
    }
}
