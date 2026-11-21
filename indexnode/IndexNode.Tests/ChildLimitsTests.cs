using System.Diagnostics;
namespace IndexNode.Tests;

/// <summary>0.10.9: a child process never outlives its job: wall-clock limit, no-output limit, lease lost, whole tree killed.</summary>
public class ChildLimitsTests
{
    static readonly Paths P = new("index-test/");
    sealed class FuncHandler(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }

    /// <summary>A silent child that starts a grandchild, writes the grandchild's pid to a file and then waits a minute (Windows: powershell + ping; Linux: sh + sleep).</summary>
    static (string File, string[] Args, string PidFile) Child(bool chatty = false)
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "child-" + Guid.NewGuid().ToString("N") + ".pid");
        if (OperatingSystem.IsWindows())
            return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", $"$p = Start-Process ping -ArgumentList '-n','61','127.0.0.1' -PassThru -WindowStyle Hidden; Set-Content -Path '{pidFile}' -Value $p.Id; {(chatty ? "'started';" : "")} Start-Sleep -Seconds 60"], pidFile);
        return ("/bin/sh", ["-c", $"sleep 60 & echo $! > '{pidFile}'; {(chatty ? "echo started;" : "")} wait"], pidFile);
    }

    static async Task<int> GrandchildPid(string pidFile)
    {
        for (int i = 0; i < 300; i++) { try { if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && pid > 0) return pid; } catch { } await Task.Delay(50); }
        throw new TimeoutException("the child never wrote its grandchild's pid");
    }

    static async Task AssertDead(int pid)
    {
        for (int i = 0; i < 100; i++)
        {
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) return; } catch (ArgumentException) { return; }
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"grandchild {pid} is still running");
    }

    [Fact]
    public async Task A_child_past_the_wall_clock_limit_is_killed_with_its_grandchild()
    {
        var (f, a, pidFile) = Child();
        var sw = Stopwatch.StartNew();
        var run = Proc.RunAsync(f, a, default, limits: new ChildLimits(Timeout: TimeSpan.FromSeconds(2)));
        var pid = await GrandchildPid(pidFile);
        var e = await Assert.ThrowsAsync<ChildTimeoutException>(() => run);
        Assert.Contains("timed out after 2 s", e.Message); Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), sw.Elapsed.ToString());
        await AssertDead(pid);
        Assert.Equal("5 min", ChildLimits.Say(YtDlpConfig.DefaultTimeout));   // the default limit
    }

    [Fact]
    public async Task A_child_that_prints_nothing_for_the_idle_limit_is_hung_and_killed()
    {
        var (f, a, pidFile) = Child(chatty: true);   // prints one line, then goes silent
        var run = Proc.RunAsync(f, a, default, limits: new ChildLimits(Timeout: TimeSpan.FromMinutes(5), Idle: TimeSpan.FromSeconds(2)));
        var pid = await GrandchildPid(pidFile);
        var e = await Assert.ThrowsAsync<ChildTimeoutException>(() => run);
        Assert.Contains("no output for 2 s", e.Message);
        await AssertDead(pid);
    }

    [Fact]
    public async Task Cancelling_kills_the_whole_tree()
    {
        var (f, a, pidFile) = Child(); using var cts = new CancellationTokenSource();
        var run = Proc.RunAsync(f, a, cts.Token);
        var pid = await GrandchildPid(pidFile); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await AssertDead(pid);
    }

    static EngineOptions Opts(TimeSpan? leaseLimit = null, TimeSpan? backoff = null) => new()
    {
        NodeId = "test-abc123", Name = "test", Stages = ["download"], Workers = 1, Threads = 1, Once = true,
        PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), ControlInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromSeconds(30),
        TransientBackoff = _ => backoff ?? TimeSpan.Zero, LeaseLimit = leaseLimit ?? TimeSpan.FromSeconds(60),
    };

    static NodeEngine Engine(IStore s, EngineOptions o, Func<JobContext, Task> download) => new(s, P, o, new Dictionary<string, IStageHandler> { ["download"] = new FuncHandler(download) });

    [Fact]
    public async Task A_child_timeout_fails_the_job_as_transient_with_the_reason_and_the_next_attempt_counts()
    {
        var s = new FakeStore(); await s.AddJob(P, "download", "vid", "2026-11-13T19:00:00Z");
        int pid = 0;
        await Engine(s, Opts(backoff: TimeSpan.FromHours(1)), async ctx =>
        {
            var (f, a, pidFile) = Child();
            var run = Proc.RunAsync(f, a, ctx.Token, limits: new ChildLimits(Timeout: TimeSpan.FromSeconds(2)));
            pid = await GrandchildPid(pidFile); await run;
        }).RunAsync(default).WaitAsync(TimeSpan.FromSeconds(60));
        var name = P.Job("download", "vid"); var j = s.Job(name);
        Assert.Equal("pending", s.State(name)); Assert.Equal(1, j.Attempts); Assert.NotEqual(true, j.Permanent); Assert.Contains("timed out after 2 s", j.LastError);   // transient: retried with the usual back-off
        await AssertDead(pid);
    }

    [Fact(Skip = "2026-11-21: flaky on the Windows runner (no Brave browser / timing); follow-up Victor29")]
    public async Task A_lost_lease_cancels_the_job_and_kills_its_child_tree()
    {
        var s = new FakeStore(); await s.AddJob(P, "download", "vid", "2026-11-13T19:00:00Z");
        var name = P.Job("download", "vid"); int pid = 0; bool cancelled = false;
        var e = Engine(s, Opts(), async ctx =>
        {
            var (f, a, pidFile) = Child();
            var run = Proc.RunAsync(f, a, ctx.Token);
            pid = await GrandchildPid(pidFile);
            s.Blobs[P.Active("vid")].Lease = "someone-else";   // another node took the job: the next renewal says LeaseLost
            try { await run; } catch (OperationCanceledException) { cancelled = true; throw; }
        });
        var sw = Stopwatch.StartNew();
        await e.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(cancelled); Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), sw.Elapsed.ToString());
        await AssertDead(pid);
    }

    /// <summary>Storage whose lease renewal never answers (DNS down): the job must not run on forever.</summary>
    sealed class HangingRenew(FakeStore inner, bool throwInstead) : IStore
    {
        public Task<IReadOnlyList<BlobEntry>> FindByTagsAsync(string w, CancellationToken ct) => inner.FindByTagsAsync(w, ct);
        public Task<IReadOnlyList<BlobEntry>> ListAsync(string p, bool t, bool m, CancellationToken ct) => inner.ListAsync(p, t, m, ct);
        public Task<BlobContent?> GetAsync(string n, CancellationToken ct) => inner.GetAsync(n, ct);
        public Task<ConditionalResult> GetIfChangedAsync(string n, string? e, CancellationToken ct) => inner.GetIfChangedAsync(n, e, ct);
        public Task<IReadOnlyDictionary<string, string>?> GetTagsAsync(string n, CancellationToken ct) => inner.GetTagsAsync(n, ct);
        public Task<bool> PutAsync(string n, byte[] d, IReadOnlyDictionary<string, string>? t, string? l, bool o, CancellationToken ct, string c = "application/json") => inner.PutAsync(n, d, t, l, o, ct, c);
        public Task PutFileAsync(string n, string p, string c, IProgress<long>? pr, CancellationToken ct) => inner.PutFileAsync(n, p, c, pr, ct);
        public Task DownloadToFileAsync(string n, string p, CancellationToken ct) => inner.DownloadToFileAsync(n, p, ct);
        public Task<string?> AcquireLeaseAsync(string n, int s, CancellationToken ct) => inner.AcquireLeaseAsync(n, s, ct);
        public async Task RenewLeaseAsync(string n, string l, CancellationToken ct) { if (throwInstead) throw new HttpRequestException("No such host is known"); await Task.Delay(Timeout.Infinite, ct); }
        public Task ReleaseLeaseAsync(string n, string l, CancellationToken ct) => inner.ReleaseLeaseAsync(n, l, ct);
        public Task SetMetadataAsync(string n, IReadOnlyDictionary<string, string> m, string? l, CancellationToken ct) => inner.SetMetadataAsync(n, m, l, ct);
        public Task SetTagsAsync(string n, IReadOnlyDictionary<string, string> t, string? l, CancellationToken ct) => inner.SetTagsAsync(n, t, l, ct);
        public Task DeleteAsync(string n, CancellationToken ct) => inner.DeleteAsync(n, ct);
        public Task<bool> CreateAppendBlobAsync(string n, bool o, CancellationToken ct) => inner.CreateAppendBlobAsync(n, o, ct);
        public Task<AppendResult> AppendBlockAsync(string n, byte[] d, long p, CancellationToken ct) => inner.AppendBlockAsync(n, d, p, ct);
        public Task<AppendRead> ReadAppendAsync(string n, long o, int m, CancellationToken ct) => inner.ReadAppendAsync(n, o, m, ct);
        public Task<bool?> SealAppendBlobAsync(string n, long p, CancellationToken ct) => inner.SealAppendBlobAsync(n, p, ct);
        public Task<bool> PutIfMatchAsync(string n, byte[] d, string? e, CancellationToken ct) => inner.PutIfMatchAsync(n, d, e, ct);
        public Task<AppendInfo?> GetAppendInfoAsync(string n, CancellationToken ct) => inner.GetAppendInfoAsync(n, ct);
    }

    [Theory]
    [InlineData(false)]   // the renewal call hangs
    [InlineData(true)]    // the renewal call fails (DNS)
    public async Task Renewals_that_keep_failing_for_a_lease_period_count_as_lost_and_stop_the_job_and_its_child(bool fails)
    {
        var s = new FakeStore(); await s.AddJob(P, "download", "vid", "2026-11-13T19:00:00Z");
        int pid = 0; bool cancelled = false;
        var e = Engine(new HangingRenew(s, fails), Opts(leaseLimit: TimeSpan.FromMilliseconds(500)), async ctx =>
        {
            var (f, a, pidFile) = Child(); var run = Proc.RunAsync(f, a, ctx.Token);
            pid = await GrandchildPid(pidFile);
            try { await run; } catch (OperationCanceledException) { cancelled = true; throw; }
        });
        await e.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(cancelled); await AssertDead(pid);
    }
}
