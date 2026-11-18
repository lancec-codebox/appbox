using System.Diagnostics;

namespace IndexNode;

/// <summary>Time split of one step by phase (download, decode, model, upload, ai wait ...), logged as one throughput line at the end.</summary>
public sealed class StepStats(string step)
{
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly Dictionary<string, long> ticks = new();
    private readonly object gate = new();

    /// <summary>Times a block under a phase name (phases may overlap across threads: the sums are thread-seconds).</summary>
    public IDisposable Phase(string name) => new Scope(this, name);

    public void Add(string name, TimeSpan t) { lock (gate) ticks[name] = ticks.GetValueOrDefault(name) + t.Ticks; }

    public T Time<T>(string name, Func<T> f) { var sw = Stopwatch.StartNew(); try { return f(); } finally { Add(name, sw.Elapsed); } }
    public async Task<T> TimeAsync<T>(string name, Func<Task<T>> f) { var sw = Stopwatch.StartNew(); try { return await f(); } finally { Add(name, sw.Elapsed); } }

    public string Line(long items, string unit, int threads)
    {
        double s = Math.Max(0.001, total.Elapsed.TotalSeconds);
        string split; lock (gate) split = string.Join(", ", ticks.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {TimeSpan.FromTicks(k.Value).TotalSeconds:F0}s"));
        return $"{step}: {items} {unit} in {s:F0}s = {items / s:F1} {unit}/s on {threads} threads; time by phase: {(split.Length == 0 ? "-" : split)}";
    }

    public void Log(long items, string unit, int threads) => IndexNode.Log.Info(Line(items, unit, threads));

    private sealed class Scope(StepStats st, string name) : IDisposable
    {
        private readonly Stopwatch sw = Stopwatch.StartNew();
        public void Dispose() => st.Add(name, sw.Elapsed);
    }
}
