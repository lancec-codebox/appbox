using System.Collections.Concurrent;
using IndexNode;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.9.0 stage batches: one stage at a time across a batch of dramas, --lanes side by side, per-stage progress in the store, one leased hold blob per drama.</summary>
[Collection("ort")]
public class BatchTests
{
    private static readonly Paths P = new("index-test/");
    private sealed class FuncHandler(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }
    private static readonly string[] Steps = StageOrder.PassOrder;

    private static async Task Drama(FakeStore s, string id, string added, int priority = 0, string[]? steps = null, double seconds = 600)
    {
        await s.PutAsync(P.Video(id, "downloaded.json"), System.Text.Encoding.UTF8.GetBytes("{\"durationSeconds\":" + seconds + "}"), null, null, false, default);
        foreach (var st in steps ?? Steps)
        {
            var job = JobDescriptor.NewStep(st, id); job.JobId = id; job.AddedAt = added; job.Priority = priority;
            var tags = Tags.Job(st, "pending", added, null, priority);
            await s.PutAsync(P.Job(st, id), job.ToBytes(), tags, null, true, default);
        }
    }

    private static EngineOptions Opts(string node = "batch-abc123", int lanes = 2, int batch = 6, double hours = 2, Action? unload = null) => new()
    {
        NodeId = node, Name = "batch", Stages = Steps, Workers = 1, Lanes = lanes, Batch = batch, BatchHours = hours, Threads = 8, Once = true,
        PollInterval = TimeSpan.FromMilliseconds(20), RenewInterval = TimeSpan.FromMilliseconds(30), RegistryInterval = TimeSpan.FromMilliseconds(100),
        TransientBackoff = _ => TimeSpan.Zero, ControlInterval = TimeSpan.FromMilliseconds(40), HoldRenew = TimeSpan.FromMilliseconds(30), UnloadModels = unload ?? (() => { }),
    };

    private static IStageHandler Writes(Func<JobContext, Task>? also = null) => new FuncHandler(async ctx =>
    {
        if (also != null) await also(ctx);
        var file = ProcessStage.Outputs.First(x => x.Step == ctx.Stage).File;
        await ctx.Store.PutAsync(P.Video(ctx.Job.JobId, file), "{\"v\":1}"u8.ToArray(), null, null, false, ctx.Token);
    });

    private static NodeEngine Engine(FakeStore s, EngineOptions o, Func<string, IStageHandler> h) => new(s, P, o, Steps.ToDictionary(x => x, h));
    private static Task Run(NodeEngine e, CancellationToken ct = default) => e.RunAsync(ct).WaitAsync(TimeSpan.FromSeconds(30));
    private static string HoldName(string id) => P.Hold(id);
    private static IEnumerable<string> HoldBlobs(FakeStore s) { lock (s.Blobs) return s.Blobs.Keys.Where(k => k.StartsWith(P.HoldsDir)).ToList(); }

    [Fact]
    public async Task A_stage_runs_for_the_whole_batch_before_the_next_stage_starts()
    {
        var s = new FakeStore(); foreach (var d in new[] { "d1", "d2", "d3" }) await Drama(s, d, "2026-11-17T10:00:0" + d[1] + "Z");
        var seen = new List<string>();
        await Run(Engine(s, Opts(), _ => Writes(ctx => { lock (seen) seen.Add(ctx.Stage + ":" + ctx.Job.JobId); return Task.CompletedTask; })));
        Assert.Equal(21, seen.Count);
        var stages = seen.Select(x => Array.IndexOf(Steps, x.Split(':')[0])).ToList();
        Assert.Equal(stages.OrderBy(x => x).ToList(), stages);                          // frames x3, sscd x3, thumbs x3, ...
        Assert.Equal(Steps, seen.Select(x => x.Split(':')[0]).Distinct().ToArray());      // in the order frames, sscd, thumbs, transcribe, ocrsubs, cast, sync
    }

    [Fact]
    public async Task Lanes_run_two_dramas_side_by_side_and_share_the_stage_model_which_is_unloaded_after_each_stage()
    {
        var s = new FakeStore(); foreach (var d in new[] { "d1", "d2", "d3", "d4" }) await Drama(s, d, "2026-11-17T10:00:0" + d[1] + "Z", steps: ["frames", "sscd"]);
        var model = SharedModelTests.Model(); int now = 0, max = 0, unloads = 0;
        var perStage = new ConcurrentDictionary<string, ConcurrentBag<object>>();
        var loadedAtStart = new ConcurrentDictionary<string, int>();
        try
        {
            var o = Opts(unload: () => { Interlocked.Increment(ref unloads); OrtOptions.UnloadShared(); });
            var e = new NodeEngine(s, P, o, new[] { "frames", "sscd" }.ToDictionary(x => x, _ => Writes(async ctx =>
            {
                loadedAtStart.TryAdd(ctx.Stage, OrtOptions.SharedCount);
                perStage.GetOrAdd(ctx.Stage, _ => new()).Add(OrtOptions.Shared(model, 1, true));
                var n = Interlocked.Increment(ref now); lock (perStage) max = Math.Max(max, n);
                await Task.Delay(60); Interlocked.Decrement(ref now);
            })) as IReadOnlyDictionary<string, IStageHandler>);
            await Run(e);
        }
        finally { OrtOptions.UnloadShared(); File.Delete(model); }
        Assert.Equal(2, max);                                                           // two lanes, never more
        foreach (var bag in perStage.Values) { Assert.Equal(4, bag.Count); Assert.All(bag, x => Assert.Same(bag.First(), x)); } // one instance for all four dramas of a stage
        Assert.Equal(0, loadedAtStart["frames"]); Assert.Equal(0, loadedAtStart["sscd"]); // the previous stage's model was gone when the next stage began
        Assert.True(unloads >= 2);
        Assert.Equal(0, OrtOptions.SharedCount);
    }

    [Fact]
    public async Task Models_are_unloaded_after_every_stage_that_ran()
    {
        var s = new FakeStore(); await Drama(s, "d1", "2026-11-17T10:00:00Z", steps: ["frames", "sscd", "cast"]);
        int unloads = 0; var log = new List<string>();
        await Run(Engine(s, Opts(unload: () => { lock (log) { unloads++; log.Add("unload"); } }), _ => Writes(ctx => { lock (log) log.Add(ctx.Stage); return Task.CompletedTask; })));
        Assert.Equal(["frames", "unload", "sscd", "unload", "cast", "unload", "unload"], log);  // one per stage, one more when the batch ends
    }

    [Fact]
    public async Task Each_drama_is_uploaded_and_marked_done_the_moment_its_stage_finishes()
    {
        var s = new FakeStore(); await Drama(s, "d1", "2026-11-17T10:00:00Z", steps: ["frames", "sscd"]); await Drama(s, "d2", "2026-11-17T10:00:01Z", steps: ["frames", "sscd"]);
        string? d1AtD2 = null; var gate = new TaskCompletionSource();
        await Run(Engine(s, Opts(lanes: 1), _ => Writes(async ctx =>
        {
            if (ctx.Stage == "frames" && ctx.Job.JobId == "d2") d1AtD2 = s.State(P.Job("frames", "d1")) + "/" + s.Exists(P.Video("d1", "frames/manifest.json")); // d1 finished frames before d2's frames began
            if (ctx.Stage == "sscd") Assert.Equal("done", s.State(P.Job("frames", ctx.Job.JobId)));   // the next stage sees every earlier stage done in the store
            await Task.CompletedTask;
        })));
        Assert.Equal("done/True", d1AtD2);
        foreach (var d in new[] { "d1", "d2" }) foreach (var st in new[] { "frames", "sscd" }) Assert.Equal("done", s.State(P.Job(st, d)));
        Assert.True(s.Exists(P.Video("d2", "sscd/manifest.json")));
    }


    [Fact]
    public async Task A_drama_is_held_by_a_leased_blob_with_metadata_and_tags_that_a_renewal_keeps_and_that_is_deleted_when_its_last_stage_is_done()
    {
        var s = new FakeStore(); await Drama(s, "d1", "2026-11-17T10:00:00Z", steps: ["frames", "sscd", "thumbs"]);
        Dictionary<string, string>? meta0 = null, tags0 = null, meta1 = null, tags1 = null; string? lease = null, stepLease = null, laterLease = null; int renews0 = 0, renews1 = 0, blobsDuring = 0;
        await Run(Engine(s, Opts(), _ => Writes(async ctx =>
        {
            if (ctx.Stage != "frames") return;
            var h = s[HoldName("d1")];
            lease = h.Lease; meta0 = new(h.Meta); tags0 = new(h.Tags); renews0 = s.RenewCount(HoldName("d1")); blobsDuring = HoldBlobs(s).Count();
            stepLease = s.LeaseOf(P.Job("frames", "d1")); laterLease = s.LeaseOf(P.Job("sscd", "d1"));
            await Task.Delay(300);
            meta1 = new(s[HoldName("d1")].Meta); tags1 = new(s[HoldName("d1")].Tags); renews1 = s.RenewCount(HoldName("d1"));
        })));
        Assert.NotNull(lease); Assert.Equal(1, blobsDuring);                              // the lease is the hold; one blob for the drama, not one per step
        Assert.NotNull(stepLease); Assert.Null(laterLease);                               // the current step has its own 60 s lease, later steps only the drama's hold
        Assert.Equal("batch-abc123", meta0!["idx_node"]); Assert.StartsWith("batch-", meta0["idx_batch"]); Assert.True(meta0.ContainsKey("idx_since")); Assert.True(meta0.ContainsKey("idx_until"));
        Assert.Equal("batch-abc123", tags0!["idx_hold_node"]); Assert.True(tags0.ContainsKey("idx_hold_until"));
        Assert.True(renews1 - renews0 >= 3);                                              // renewed while the stage ran
        Assert.Equal(meta0["idx_node"], meta1!["idx_node"]); Assert.Equal(meta0["idx_since"], meta1["idx_since"]);   // a renewal keeps metadata ...
        Assert.Equal(tags0["idx_hold_node"], tags1!["idx_hold_node"]); Assert.True(tags1.ContainsKey("idx_hold_until"));  // ... and tags
        Assert.True(string.CompareOrdinal(meta1["idx_until"], meta0["idx_until"]) >= 0);
        Assert.Empty(HoldBlobs(s));                                                       // deleted with the last batch stage
        Assert.All(s.LeaseLog, l => Assert.Equal(Protocol.LeaseSeconds, l.Seconds));
    }

    [Fact]
    public async Task A_hold_is_deleted_as_soon_as_the_dramas_last_stage_is_done_not_when_the_whole_batch_ends()
    {
        var s = new FakeStore(); await Drama(s, "d1", "2026-11-17T10:00:00Z", steps: ["frames"]); await Drama(s, "d2", "2026-11-17T10:00:01Z", steps: ["frames", "sscd"]);
        bool d1Gone = false, d2Held = false;
        await Run(Engine(s, Opts(lanes: 1), _ => Writes(ctx =>
        {
            if (ctx.Stage == "sscd") { d1Gone = !s.Exists(HoldName("d1")); d2Held = s.Exists(HoldName("d2")); }
            return Task.CompletedTask;
        })));
        Assert.True(d1Gone); Assert.True(d2Held); Assert.Empty(HoldBlobs(s));
    }

    [Fact]
    public async Task Another_node_skips_a_drama_whose_hold_is_leased()
    {
        var s = new FakeStore(); await Drama(s, "d1", "2026-11-17T10:00:00Z", steps: ["frames", "sscd"]);
        var other = new List<string>();
        await Run(Engine(s, Opts(), _ => Writes(async ctx =>
        {
            if (ctx.Stage != "frames") return;
            var b = new NodeEngine(s, P, Opts("other-def456"), Steps.ToDictionary(x => x, _ => Writes(c => { lock (other) other.Add(c.Stage); return Task.CompletedTask; })));
            await b.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));   // frames is leased, sscd's drama is held
        })));
        Assert.Empty(other);
        Assert.Equal("done", s.State(P.Job("sscd", "d1")));
    }

    [Fact]
    public async Task Two_nodes_racing_for_the_same_dramas_run_every_step_exactly_once()
    {
        for (int i = 0; i < 20; i++)
        {
            var s = new FakeStore(); foreach (var d in new[] { "d1", "d2", "d3" }) await Drama(s, d, "2026-11-17T10:00:0" + d[1] + "Z", steps: ["frames", "sscd"]);
            var runs = new ConcurrentDictionary<string, int>();
            var trace = new ConcurrentQueue<string>(); var t0 = DateTime.UtcNow;
            IStageHandler H(string node) => Writes(async ctx => { trace.Enqueue($"{(DateTime.UtcNow - t0).TotalMilliseconds:F0}ms {node} {ctx.Stage}:{ctx.Job.JobId} tags={s.State(P.Job(ctx.Stage, ctx.Job.JobId))} fin={s.Job(P.Job(ctx.Stage, ctx.Job.JobId)).FinishedAt}"); runs.AddOrUpdate(ctx.Stage + ":" + ctx.Job.JobId, 1, (_, v) => v + 1); await Task.Delay(Random.Shared.Next(0, 10)); });
            await Task.WhenAll(Run(Engine(s, Opts("node-a1", lanes: 2), _ => H("A"))), Run(Engine(s, Opts("node-b2", lanes: 2), _ => H("B"))));
            Assert.Equal(6, runs.Count); Assert.All(runs, kv => Assert.True(kv.Value == 1, kv.Key + " ran " + kv.Value + " times\n" + string.Join("\n", trace) + "\n" + string.Join("\n", s.Trace.Where(x => x.Contains(kv.Key.Split(':')[1])))));
            Assert.Empty(HoldBlobs(s));
        }
    }

    [Fact]
    public async Task Concurrent_creates_and_lease_acquires_have_exactly_one_winner()
    {
        var s = new FakeStore(); var name = P.Hold("d1");
        var created = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => s.PutAsync(name, "{}"u8.ToArray(), null, null, true, default))));
        Assert.Equal(1, created.Count(x => x));
        var leases = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => s.AcquireLeaseAsync(name, 60, default))));
        Assert.Equal(1, leases.Count(x => x != null));
        await Assert.ThrowsAsync<StorageException>(() => s.DeleteAsync(name, "not-the-lease", default));   // nobody else can delete a held blob
        await s.DeleteAsync(name, leases.First(x => x != null), default); Assert.False(s.Exists(name));
    }

    [Fact]
    public async Task A_clean_stop_deletes_every_hold_and_releases_every_lease()
    {
        var s = new FakeStore(); foreach (var d in new[] { "d1", "d2" }) await Drama(s, d, "2026-11-17T10:00:0" + d[1] + "Z");
        using var stop = new CancellationTokenSource(); var ranLog = new List<string>();
        await Run(Engine(s, Opts(), _ => Writes(async ctx =>
        {
            lock (ranLog) ranLog.Add(ctx.Stage + ":" + ctx.Job.JobId + "@" + Environment.CurrentManagedThreadId + (stop.IsCancellationRequested ? "!" : ""));
            if (ctx.Stage == "sscd" && ctx.Job.JobId == "d1") { lock (ranLog) ranLog.Add("holds=" + string.Join("|", HoldBlobs(s))); stop.Cancel(); await Task.Delay(Timeout.Infinite, ctx.Token); }
        })), stop.Token);
        Assert.True(ranLog.Where(x => !x.StartsWith("holds=")).Select(x => x.Split('@')[0]).Distinct().Count() == ranLog.Count(x => !x.StartsWith("holds=")), "a step ran twice: " + string.Join(",", ranLog));
        Assert.Empty(HoldBlobs(s));
        foreach (var d in new[] { "d1", "d2" }) foreach (var st in Steps) Assert.Null(s.LeaseOf(P.Job(st, d)));
        Assert.Equal("done", s.State(P.Job("frames", "d1"))); Assert.Equal("done", s.State(P.Job("frames", "d2")));
        Assert.True(s.State(P.Job("thumbs", "d1")) == "pending", "thumbs d1 was " + s.State(P.Job("thumbs", "d1")) + "; log: " + string.Join(",", ranLog));
    }

    [Fact]
    public async Task After_a_crash_the_hold_lease_keeps_others_out_and_once_it_expires_another_node_takes_over_and_resumes()
    {
        var s = new FakeStore(); foreach (var d in new[] { "d1", "d2" }) await Drama(s, d, "2026-11-17T10:00:0" + d[1] + "Z", steps: ["frames", "sscd", "thumbs"]);
        using var crash = new CancellationTokenSource();
        await Run(Engine(s, Opts(), _ => Writes(async ctx =>
        {
            if (ctx.Stage == "sscd") { crash.Cancel(); await Task.Delay(Timeout.Infinite, ctx.Token); }
        })), crash.Token);
        // what a real crash leaves instead of a clean stop: the dead node's hold leases (and the sscd leases) until they run out
        foreach (var d in new[] { "d1", "d2" })
        {
            await s.PutAsync(P.Hold(d), "{}"u8.ToArray(), new Dictionary<string, string> { ["idx_hold_node"] = "batch-abc123" }, null, true, default);
            await s.SetMetadataAsync(P.Hold(d), new Dictionary<string, string> { ["idx_node"] = "batch-abc123", ["idx_batch"] = "batch-1", ["idx_since"] = Protocol.NowUtc(), ["idx_until"] = Protocol.NowUtc() }, null, default);
            await s.AcquireLeaseAsync(P.Hold(d), 60, default); await s.AcquireLeaseAsync(P.Job("sscd", d), 60, default);
        }
        var ran = new List<string>();
        Func<string, IStageHandler> rec = _ => Writes(ctx => { lock (ran) ran.Add(ctx.Stage + ":" + ctx.Job.JobId); return Task.CompletedTask; });
        await Run(Engine(s, Opts("rescue-def456"), rec));
        Assert.Empty(ran);                                                              // hold leases still live: nobody else touches the dramas
        foreach (var d in new[] { "d1", "d2" }) { s[P.Hold(d)].Lease = null; s[P.Job("sscd", d)].Lease = null; }   // 60 s without a renewal: the leases expire
        await Run(Engine(s, Opts("rescue-def456"), rec));
        Assert.DoesNotContain(ran, x => x.StartsWith("frames:"));                       // finished stages are not redone
        Assert.Equal(["sscd:d1", "sscd:d2", "thumbs:d1", "thumbs:d2"], ran.OrderBy(x => Array.IndexOf(Steps, x.Split(':')[0])).ThenBy(x => x).ToArray());
        foreach (var d in new[] { "d1", "d2" }) foreach (var st in new[] { "frames", "sscd", "thumbs" }) Assert.Equal("done", s.State(P.Job(st, d)));
        Assert.Empty(HoldBlobs(s));
    }

    [Fact]
    public async Task A_stale_tag_query_that_still_lists_finished_steps_as_pending_never_runs_them_again()
    {
        var s = new FakeStore(); foreach (var d in new[] { "d1", "d2" }) await Drama(s, d, "2026-11-17T10:00:0" + d[1] + "Z", steps: ["frames", "sscd"]);
        var pendingTags = new Dictionary<string, Dictionary<string, string>>();
        foreach (var d in new[] { "d1", "d2" }) foreach (var st in new[] { "frames", "sscd" }) pendingTags[P.Job(st, d)] = new(s[P.Job(st, d)].Tags);
        await Run(Engine(s, Opts(), _ => Writes()));
        foreach (var d in new[] { "d1", "d2" }) foreach (var st in new[] { "frames", "sscd" }) Assert.Equal("done", s.State(P.Job(st, d)));
        // the tag index lags: the query still answers with the old pending entries, and even the tags of the blobs are stale; only the body knows the step finished
        s.StaleFindResult = pendingTags.Select(kv => new BlobEntry(kv.Key, kv.Value, new Dictionary<string, string>())).ToList();
        foreach (var kv in pendingTags) s[kv.Key].Tags = new(kv.Value);
        var ran = new List<string>();
        await Run(Engine(s, Opts("late-ghi789"), _ => Writes(ctx => { lock (ran) ran.Add(ctx.Stage + ":" + ctx.Job.JobId); return Task.CompletedTask; })));
        Assert.Empty(ran);
        Assert.Empty(HoldBlobs(s));
    }

    [Fact]
    public async Task One_drama_failing_a_stage_does_not_stop_the_batch()
    {
        var s = new FakeStore(); foreach (var d in new[] { "d1", "d2", "d3" }) await Drama(s, d, "2026-11-17T10:00:0" + d[1] + "Z", steps: ["frames", "sscd", "transcribe"]);
        await Run(Engine(s, Opts(), _ => Writes(ctx => ctx.Stage == "frames" && ctx.Job.JobId == "d2" ? throw new PermanentFailure("no video") : Task.CompletedTask)));
        Assert.Equal("failed", s.State(P.Job("frames", "d2")));
        Assert.Equal("pending", s.State(P.Job("sscd", "d2"))); Assert.Null(s.LeaseOf(P.Job("sscd", "d2")));   // dependent step released
        Assert.Equal("done", s.State(P.Job("transcribe", "d2")));                        // independent step still ran
        foreach (var d in new[] { "d1", "d3" }) foreach (var st in new[] { "frames", "sscd", "transcribe" }) Assert.Equal("done", s.State(P.Job(st, d)));
        Assert.Empty(HoldBlobs(s));
    }

    [Fact]
    public async Task A_batch_takes_the_best_dramas_first_by_priority_then_age_and_stops_at_the_size_limit()
    {
        var s = new FakeStore();
        await Drama(s, "old", "2026-11-17T09:00:00Z", 0, ["frames", "sscd"]); await Drama(s, "mid", "2026-11-17T09:30:00Z", 0, ["frames", "sscd"]);
        await Drama(s, "vip", "2026-11-17T11:00:00Z", 5, ["frames", "sscd"]); await Drama(s, "new", "2026-11-17T12:00:00Z", 0, ["frames", "sscd"]);
        var order = new List<string>();
        await Run(Engine(s, Opts(lanes: 1, batch: 2), _ => Writes(ctx => { lock (order) order.Add(ctx.Job.JobId + ":" + ctx.Stage); return Task.CompletedTask; })));
        Assert.Equal(["vip:frames", "old:frames", "vip:sscd", "old:sscd", "mid:frames", "new:frames", "mid:sscd", "new:sscd"], order);   // two batches of two
    }

    [Fact]
    public async Task A_batch_stops_at_about_two_hours_of_video()
    {
        var s = new FakeStore();
        foreach (var (d, i) in new[] { "a", "b", "c" }.Select((d, i) => (d, i))) await Drama(s, d, "2026-11-17T10:00:0" + i + "Z", 0, ["frames", "sscd"], seconds: 3700);
        var order = new List<string>();
        await Run(Engine(s, Opts(lanes: 1), _ => Writes(ctx => { lock (order) order.Add(ctx.Job.JobId + ":" + ctx.Stage); return Task.CompletedTask; })));
        Assert.Equal(["a:frames", "b:frames", "a:sscd", "b:sscd", "c:frames", "c:sscd"], order);   // a + b = 2.06 h reaches the limit
    }

    [Fact]
    public async Task Stress_no_step_is_ever_run_twice()
    {
        var rnd = new Random(12345); var bad = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            var s = new FakeStore();
            foreach (var (d, n) in new[] { "a", "b", "c", "d" }.Select((d, n) => (d, n))) await Drama(s, d, "2026-11-17T10:00:0" + n + "Z", n % 2, n < 3 ? ["frames", "sscd", "thumbs"] : ["frames", "sscd"], seconds: 3700);
            var runs = new ConcurrentDictionary<string, int>(); int seed = rnd.Next();
            var o = Opts(lanes: 1 + i % 3, batch: 2 + i % 3);
            await Run(Engine(s, o, _ => Writes(async ctx =>
            {
                runs.AddOrUpdate(ctx.Stage + ":" + ctx.Job.JobId, 1, (_, v) => v + 1);
                await Task.Delay(new Random(seed ^ ctx.Job.JobId.GetHashCode() ^ ctx.Stage.GetHashCode()).Next(0, 25));
            })));
            foreach (var kv in runs.Where(k => k.Value > 1)) bad.Add($"run {i}: {kv.Key} x{kv.Value}");
            Assert.Empty(HoldBlobs(s));
            foreach (var st in new[] { "frames", "sscd" }) foreach (var d in new[] { "a", "b", "c", "d" }) if (s.State(P.Job(st, d)) != "done") bad.Add($"run {i}: {st}:{d} not done");
        }
        Assert.Empty(bad);
    }
}
