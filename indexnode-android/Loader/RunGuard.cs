namespace IndexNode.Hosting;

/// <summary>One run at a time per process. A second start (Run now during a run, a WorkManager start) is not an error: it is told so and does nothing.</summary>
public sealed class RunGuard
{
    private readonly object gate = new();
    private bool running;
    public bool Running { get { lock (gate) return running; } }
    public const string Busy = "a run is already in progress";
    /// <summary>true: the caller owns the run and must call Exit. false: a run is going, do nothing.</summary>
    public bool TryEnter() { lock (gate) { if (running) return false; running = true; return true; } }
    public void Exit() { lock (gate) running = false; }
}
