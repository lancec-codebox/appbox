using Xunit;
namespace IndexNode.Tests;

/// <summary>0.13.34: a claim's downloaded zips go when it ends, a dead process's zips dir is removed at start, and a local disk error fails only that claim (or stops the node cleanly), never crashes it.</summary>
public class DiskHygieneTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly Paths P = new("index-test/");
    sealed class Fn(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    static long Bytes(string dir) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
    static string Tmp() => Path.Combine(Path.GetTempPath(), "dh-" + Guid.NewGuid().ToString("N")[..8]);

    static async Task<FakeStore> Store(string dir, params string[] ids)
    {
        var fs = new FakeStore(); var put = new StageZipStore(fs, Path.Combine(dir, "put"));
        foreach (var id in ids)
        {
            await put.PutAsync(P.Video(id, "frames/0001.mjpeg"), new byte[50_000], null, null, false, default); await put.PutAsync(P.Video(id, "cast/clusters.json"), new byte[20_000], null, null, false, default);
            await put.CommitStageAsync(id, "frames", "n", false, false, default); await put.CommitStageAsync(id, "cast", "n", false, false, default);
        }
        foreach (var id in ids) { await fs.PutAsync(P.Video(id, "download.zip"), [1, 2, 3], null, null, false, default); await VideoJsonSeed.PutAsync(fs, P, id, 600); }
        return fs;
    }
    static async Task Enter(FakeStore fs, string id)
    {
        var q = new JobQueue(fs, P, "setup"); await q.LoadAsync(default); const string at = "2026-01-01T00:00:00Z";
        await q.AppendLinesAsync([new QLine { T = "enter", S = id, Data = "full", At = at }, QLine.Add(id, "process", JobDescriptor.NewProcess(id), at, null, null, "pending")], default);
    }
    static EngineOptions Opts(Func<string, long?>? free = null) => new()
    {
        NodeId = "n-1", Name = "n", Stages = ["process"], Workers = 1, Threads = 2, Once = true, PollInterval = TimeSpan.FromMilliseconds(30), RenewInterval = TimeSpan.FromMilliseconds(40), RegistryInterval = TimeSpan.FromMilliseconds(100), ControlInterval = TimeSpan.FromMilliseconds(40),
        Shadow = true, TransientBackoff = _ => TimeSpan.Zero, Ops = new OpCounter(), FreeSpace = free ?? (_ => 100L << 30),
    };

    /// <summary>A process handler that reads both zips whole through the store, as a repair does.</summary>
    static Dictionary<string, IStageHandler> Handlers(IStore store, Func<JobContext, Task>? extra = null)
    {
        var h = new Dictionary<string, IStageHandler>();
        foreach (var st in new[] { "download", "refresh", "classify", "availability" }) h[st] = new Fn(_ => Task.CompletedTask);
        foreach (var st in Protocol.StepStages) h[st] = new Fn(async ctx =>
        {
            if (st == "frames")
            {   // the first step reads both zips whole through the store, as a repair does
                using (StageZipStore.OnlyZips("cast.zip", "frames.zip")) { Assert.NotNull(await store.GetAsync(P.Video(ctx.Job.JobId, "frames/0001.mjpeg"), default)); Assert.NotNull(await store.GetAsync(P.Video(ctx.Job.JobId, "cast/clusters.json"), default)); }
                if (extra != null) await extra(ctx);
            }
            if (st is not ("describe" or "translate" or "sync") && await ctx.Store.HeadAsync(P.Video(ctx.Job.JobId, st + ".zip"), default) == null) await ctx.Store.PutAsync(P.Video(ctx.Job.JobId, st + ".zip"), [1], null, null, false, default);
        });
        h["process"] = h["frames"];
        return h;
    }

    [Fact]
    public async Task Three_claims_through_the_real_StageZipStore_leave_zero_bytes_under_the_zips_dir()
    {
        var dir = Tmp();
        try
        {
            var fs = await Store(dir, "a", "b", "c"); foreach (var id in new[] { "a", "b", "c" }) await Enter(fs, id);
            var m = new StoreMeter(); var rest = FakeBlobHttp.Rest(fs, m, out _); var zs = new StageZipStore(rest, Path.Combine(dir, "zips")) { WholeZipLimit = 1 }; var sh = new ShadowStore(zs, Path.Combine(dir, "shadow"));
            long peak = 0; var h = Handlers(sh, async _ => { peak = Math.Max(peak, Bytes(Path.Combine(dir, "zips"))); await Task.CompletedTask; });
            await new NodeEngine(sh, P, Opts(), h).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            output.WriteLine($"peak during a claim {peak} bytes; after: {Bytes(Path.Combine(dir, "zips"))}");
            Assert.True(peak > 0, "the zips were downloaded (so the test would see a leak)");
            Assert.Equal(0, Bytes(Path.Combine(dir, "zips")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task A_failed_claim_leaves_nothing_either_a_Transient_throw_after_the_zips_were_read()
    {
        var dir = Tmp();
        try
        {
            var fs = await Store(dir, "a"); await Enter(fs, "a");
            var m = new StoreMeter(); var rest = FakeBlobHttp.Rest(fs, m, out _); var zs = new StageZipStore(rest, Path.Combine(dir, "zips")) { WholeZipLimit = 1 }; var sh = new ShadowStore(zs, Path.Combine(dir, "shadow"));
            await new NodeEngine(sh, P, Opts(), Handlers(sh, _ => throw new InvalidOperationException("boom"))).RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, Bytes(Path.Combine(dir, "zips")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_stale_pid_dir_is_removed_at_start_and_a_live_one_is_kept()
    {
        var root = Tmp(); Directory.CreateDirectory(root);
        try
        {
            var dead = Path.Combine(root, "zips-2147000"); var live = Path.Combine(root, "zips-" + Environment.ProcessId); var other = Path.Combine(root, "other");
            foreach (var d in new[] { dead, live, other }) { Directory.CreateDirectory(Path.Combine(d, "zips")); File.WriteAllBytes(Path.Combine(d, "zips", "x.zip"), new byte[1000]); }
            Assert.Equal(1, StageZipStore.SweepStaleDirs(root, pid => pid == Environment.ProcessId));
            Assert.False(Directory.Exists(dead)); Assert.True(Directory.Exists(live)); Assert.True(Directory.Exists(other));   // another process that is alive, and anything not named zips-<pid>, stay
            Assert.Equal(0, StageZipStore.SweepStaleDirs(Path.Combine(root, "none")));
            Directory.CreateDirectory(dead); Assert.Equal(1, StageZipStore.SweepStaleDirs(root));   // with the real liveness check, a pid that cannot exist is gone too
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>The real RestStore over a handler whose body stream throws ENOSPC while the download is copied to the file (what the stack in the log shows).</summary>
    sealed class DiskFullHttp(HttpMessageHandler inner, Func<bool> full) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage q, CancellationToken ct)
        {
            var r = await base.SendAsync(q, ct);
            if (q.Method == HttpMethod.Get && r.IsSuccessStatusCode && r.Content.Headers.ContentLength > 1000 && q.RequestUri!.AbsolutePath.EndsWith(".zip") && full()) { r.Content = new StreamContent(new FailingStream()); r.Content.Headers.ContentLength = 50_000; }
            return r;
        }
    }
    sealed class FailingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set { } }
        public override void Flush() { } public override int Read(byte[] b, int o, int c) => throw new HttpRequestException("Error while copying content to a stream.", new IOException("No space left on device"));
        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => throw new HttpRequestException("Error while copying content to a stream.", new IOException("No space left on device"));
        public override ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken ct = default) => throw new HttpRequestException("Error while copying content to a stream.", new IOException("No space left on device"));
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) { } public override void Write(byte[] b, int o, int c) { }
    }

    static (StageZipStore Zs, FakeStore Fs) Build(string dir, FakeStore fs, Func<bool> full)
    {
        var rest = new RestStore(new HttpClient(new DiskFullHttp(new FakeBlobHttp(fs), full)), "https://acct.blob.core.windows.net/c?sv=1&sig=x") { Meter = new StoreMeter(), Counters = new Traffic(), Ops = new OpCounter(), RetryDelay = _ => TimeSpan.Zero, Negative = new NegativeCache() };
        rest.GhostBlobFor = name => new Plain(fs, name);
        return (new StageZipStore(rest, Path.Combine(dir, "zips")) { WholeZipLimit = 1 }, fs);
    }
    sealed class Plain(FakeStore fs, string name) : IGhostBlob
    {
        public string Name => name;
        public async Task<GhostState?> ReadAsync(CancellationToken ct) { var p = await fs.HeadAsync(name, ct); return p == null ? null : new GhostState(p.Size, p.ETag ?? "\"0\"", new Dictionary<string, string>(), p.Metadata); }
        public Task<long?> SnapshotLengthAsync(string snapshot, CancellationToken ct) => Task.FromResult<long?>(null);
        public Task CommitGhostAsync(GhostState state, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task ENOSPC_on_a_download_fails_only_that_claim_as_Transient_leaves_no_part_file_and_the_node_goes_on()
    {
        var dir = Tmp();
        try
        {
            var fs = await Store(dir, "bad", "good"); await Enter(fs, "bad"); await Enter(fs, "good");
            int fails = 0; var (zs, _) = Build(dir, fs, () => Interlocked.Increment(ref fails) <= 1);   // only the first zip download hits the full disk
            var sh = new ShadowStore(zs, Path.Combine(dir, "shadow"));
            var seen = new List<string>(); var h = Handlers(sh, ctx => { lock (seen) seen.Add(ctx.Job.JobId); return Task.CompletedTask; });
            // the first subject read fails on the disk; once it has, the disk "has room again" (the second claim downloads fine)
            var o = Opts(free: _ => 50L << 30); var eng = new NodeEngine(sh, P, o, h);
            await eng.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Null(eng.DiskStop);   // plenty of room: the node did not stop
            lock (seen) Assert.Equal(["good"], seen);   // the claim that hit the disk never got past its zip read; the next claim ran its steps: the node went on
            Assert.Equal(0, Directory.Exists(Path.Combine(dir, "zips", "zips")) ? Directory.EnumerateFiles(Path.Combine(dir, "zips", "zips"), "*.part").Count() : 0);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ENOSPC_with_the_disk_still_too_full_stops_the_node_cleanly_with_a_log_line_and_does_not_throw()
    {
        var dir = Tmp();
        try
        {
            var fs = await Store(dir, "a", "b"); await Enter(fs, "a"); await Enter(fs, "b");
            var (zs, _) = Build(dir, fs, () => true); var sh = new ShadowStore(zs, Path.Combine(dir, "shadow"));
            var log = new StringWriter(); var was = Log.Out; Log.Out = log;
            NodeEngine eng; try { eng = new NodeEngine(sh, P, Opts(free: _ => 300L << 20), Handlers(sh)); await eng.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); output.WriteLine(log.ToString()); Assert.StartsWith("local disk full (", eng.DiskStop); Assert.Contains("node stopped: local disk full (", log.ToString()); Assert.Contains("300 MB free", log.ToString()); } finally { Log.Out = was; }
            var q = fs.Replay(P.Prefix); Assert.NotEqual("done", q.Entry("a", "process")!.State);   // the claim that hit the disk is not marked done
            Assert.Null(q.Get("a")!.ClaimBy);   // and its claim was released
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void DiskGuard_recognises_a_full_disk_through_the_http_wrapper_and_not_a_missing_file_or_a_cancel()
    {
        Assert.True(DiskGuard.IsDiskError(new HttpRequestException("x", new IOException("No space left on device"))));
        Assert.True(DiskGuard.IsDiskError(new IOException("disk")));
        Assert.False(DiskGuard.IsDiskError(new FileNotFoundException("gone")));
        Assert.False(DiskGuard.IsDiskError(new OperationCanceledException()));
        Assert.False(DiskGuard.IsDiskError(new InvalidOperationException("boom")));
    }
}
