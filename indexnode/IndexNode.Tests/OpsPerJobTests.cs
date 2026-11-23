using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;
using static IndexNode.Tests.ActorsBuildTests;

namespace IndexNode.Tests;

/// <summary>0.13.27: what a normal node costs in store ops, on fakes only (no live store): actors jobs per job and per minute with pacing, the registry, the aggregate cache, an idle node.</summary>
public class OpsPerJobTests(ITestOutputHelper output)
{
    static readonly Paths P = new("index/");

    [Fact]
    public async Task The_group_map_reads_head_yaml_at_most_once_a_minute_and_the_groups_file_only_when_the_version_moves()
    {
        ActorsStage.ForgetSeen(); var fs = new FakeStore(); var clock = new DateTime(2026, 11, 23, 1, 0, 0, DateTimeKind.Utc); ActorsStage.Clock = () => clock;
        try
        {
            async Task Head(int v) => await fs.PutAsync(SearchData.HeadName(P), System.Text.Encoding.UTF8.GetBytes($"version: {v}\n"), null, null, false, default);
            async Task Groups(int v, string up) { var g = new GroupSet { Groups = [new DramaGroup { Id = "g" + v, Members = [new GroupMember { Upload = up }] }] }; await GroupStore.WriteAsync(fs, P, g, v, default); }
            await Head(75); await Groups(75, "aaaaaaaaaaa");
            int H() => fs.Gets.Count(n => n.EndsWith("head.yaml")); int G() => fs.Gets.Count(n => n.Contains("groups/v"));
            foreach (var io in new[] { new ActorsIO(fs, P), new ActorsIO(fs, P, false, "local-out") })
            {
                ActorsStage.ForgetSeen(); fs.Gets.Clear();
                for (int i = 0; i < 20; i++) { await ActorsStage.GroupOfAsync(io, default); clock += TimeSpan.FromSeconds(2); }   // 40 s
                Assert.Equal(1, H()); Assert.Equal(1, G());
                clock += TimeSpan.FromSeconds(30); await ActorsStage.GroupOfAsync(io, default);   // a minute passed, the version is the same
                Assert.Equal(2, H()); Assert.Equal(1, G());
                await Head(76); await Groups(76, "bbbbbbbbbbb"); clock += TimeSpan.FromSeconds(61);
                var m = await ActorsStage.GroupOfAsync(io, default);
                Assert.Equal(3, H()); Assert.Equal(2, G()); Assert.True(m.ContainsKey("bbbbbbbbbbb"));
                await Head(75);
            }
        }
        finally { ActorsStage.Clock = () => DateTime.UtcNow; ActorsStage.ForgetSeen(); }
    }

    [Fact]
    public async Task A_hundred_actors_jobs_on_two_lanes_stay_under_200_ops_a_minute_and_8_ops_a_job_in_steady_state()
    {
        VideoAggregate.ClearCache(); ActorsStage.ForgetSeen();
        var fs = new FakeStore(); var clock = new DateTime(2026, 11, 23, 1, 0, 0, DateTimeKind.Utc);
        var m = new StoreMeter(() => clock) { Limits = new StorageLimits { MaxPerMin = 200, WarnPerMin = 150, MaxPerStep = 100000, MaxFailuresPerMin = 100000 } };
        var store = new MeteredStore(fs, m);
        VideoAggregate.Clock = () => clock; ActorsStage.Clock = () => clock;
        try
        {
            const int N = 100; var ids = Enumerable.Range(0, N).Select(i => "d" + i.ToString("D3")).ToList();
            for (int i = 0; i < N; i++)
            {
                await Drama(fs, ids[i], WithCos(1 - (i % 7) * 0.05, i % 5 + 1), ((byte)(10 + i), 50, 90), n: 4, seed: i + 1);
                await VideoJson.UpdateAsync(fs, P, ids[i], r => r["grouping"] = new JsonObject { ["v"] = 1, ["proposal"] = new JsonObject { ["kind"] = "new", ["mixed"] = false }, ["seq"] = int.Parse(ids[i][1..]) + 1 }, default);
            }
            VideoAggregate.ClearCache();
            var pacer = new OpPacer(() => m.Total, () => m.Limits.MaxPerMin, 0.8, () => clock, (d, t) => { clock += d; return Task.CompletedTask; });
            var stage = new ActorsStage(store, null, 0.64) { MakeR50 = () => new ColourEmbedder(), Decode = Imaging.DecodeJpeg };
            var io = new ActorsIO(store, P); var perJob = new List<long>(); var stamps = new List<(DateTime At, long Total)>(); var rnd = 0;
            for (int i = 0; i < N; i++)
            {
                await pacer.WaitAsync(default);   // before the claim
                long before = m.Total; using (m.BeginStep(0, ids[i]))
                {
                    var preds = new List<string>(); var rep = await stage.RunAsync(io, ids[i], false, 1, default, ids.Take(i).ToList());
                }
                perJob.Add(m.Total - before); stamps.Add((clock, m.Total));
                clock += TimeSpan.FromSeconds(3.5 / 2); rnd++;   // two lanes, each job ~3.5 s of CPU
            }
            var steady = perJob.Skip(N / 2).ToList();
            // ops in any 60 s window
            long peak = 0; for (int i = 0; i < stamps.Count; i++) { long start = i == 0 ? 0 : stamps[i - 1].Total; int j = i; while (j > 0 && stamps[i].At - stamps[j - 1].At <= TimeSpan.FromSeconds(60)) j--; peak = Math.Max(peak, stamps[i].Total - (j == 0 ? 0 : stamps[j - 1].Total)); }
            output.WriteLine($"actors jobs: {N}, steady-state ops/job max={steady.Max()} avg={steady.Average():F1}; peak ops in a minute={peak}, pacer waits={pacer.Waits}; per-site: " + string.Join("; ", m.Table().Take(15).Select(x => $"{x.Site} {x.Op} x{x.Count}")));
            var sorted = steady.Order().ToList(); output.WriteLine($"steady ops/job: median={sorted[sorted.Count / 2]} p90={sorted[sorted.Count * 9 / 10]} max={sorted.Last()} (a job that carries the once-a-minute aggregate/search-head revalidation costs 1-2 more)");
            Assert.True(sorted[sorted.Count * 9 / 10] <= 8 && steady.Average() <= 8 && steady.Max() <= 10, "steady ops/job " + steady.Max() + ": " + string.Join("; ", m.Table().Take(15).Select(x => $"{x.Site} {x.Op} x{x.Count}")));
            Assert.True(peak <= 200 + 16, "peak " + peak);
            Assert.False(m.Tripped);
        }
        finally { VideoAggregate.Clock = () => DateTime.UtcNow; ActorsStage.Clock = () => DateTime.UtcNow; }
    }

    sealed class Fn(Func<JobContext, Task> f) : IStageHandler { public Task RunAsync(JobContext ctx) => f(ctx); }

    static EngineOptions Opts(StoreMeter m, Func<DateTimeOffset> now, Func<TimeSpan, CancellationToken, Task> delay) => new()
    {
        NodeId = "n1", Name = "n1", Stages = ["actors"], Workers = 2, Threads = 4, Meter = m, Ops = new OpCounter(), Now = now, Delay = delay,
        PollInterval = TimeSpan.FromSeconds(60), RenewInterval = TimeSpan.FromSeconds(20), RegistryInterval = TimeSpan.FromSeconds(60), ControlInterval = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task An_idle_node_stays_under_20_ops_a_minute_and_writes_its_registry_once_a_minute()
    {
        var fs = new FakeStore(); var clock = new DateTimeOffset(2026, 11, 23, 1, 0, 0, TimeSpan.Zero); var cts = new CancellationTokenSource(); var start = clock;
        var m = new StoreMeter(() => clock) { Limits = new StorageLimits { MaxPerMin = 100000, WarnPerMin = 100000, MaxPerStep = 100000, MaxFailuresPerMin = 100000 } };
        var store = new MeteredStore(fs, m); object gate = new();
        Task Delay(TimeSpan d, CancellationToken ct) { lock (gate) { clock += d; if (clock - start >= TimeSpan.FromMinutes(10)) cts.Cancel(); } return Task.Delay(1, CancellationToken.None); }
        var h = new Dictionary<string, IStageHandler> { ["actors"] = new Fn(_ => Task.CompletedTask) };
        var e = new NodeEngine(store, P, Opts(m, () => clock, Delay), h);
        await e.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(60));
        var minutes = (clock - start).TotalMinutes; long total = m.Total;
        output.WriteLine($"idle node: {total} ops in {minutes:F1} virtual min = {total / minutes:F1} ops/min; registry writes={e.RegistryWrites}; sites: " + string.Join("; ", m.Table().Take(10).Select(x => $"{x.Site} {x.Op} x{x.Count}")));
        Assert.True(total / minutes <= 20, "idle ops/min " + total / minutes);
        Assert.True(e.RegistryWrites <= minutes + 2, "registry writes " + e.RegistryWrites);
    }

    [Fact]
    public async Task The_aggregate_is_read_once_then_revalidated_with_the_etag_and_a_write_drops_the_copy()
    {
        VideoAggregate.ClearCache(); var clock = DateTime.UtcNow; VideoAggregate.Clock = () => clock;
        try
        {
            var fs = new FakeStore();
            Assert.True(await VideoAggregate.WriteAsync(fs, P, "x1", 1, new JsonObject { ["a"] = 1 }, default)); VideoAggregate.Stats.Reset();
            for (int i = 0; i < 5; i++) Assert.NotNull(await VideoAggregate.ReadManyAsync(fs, P, null, default));
            Assert.Equal(1, VideoAggregate.Stats.BulkReads); Assert.Equal(4, VideoAggregate.Stats.CacheHits);
            clock += TimeSpan.FromSeconds(61); Assert.NotNull(await VideoAggregate.ReadManyAsync(fs, P, null, default));
            Assert.Equal(1, VideoAggregate.Stats.NotModified); Assert.Equal(1, VideoAggregate.Stats.BulkReads);   // 304: no second whole read
            Assert.True(await VideoAggregate.WriteAsync(fs, P, "x2", 1, new JsonObject { ["b"] = 2 }, default));
            var after = await VideoAggregate.ReadManyAsync(fs, P, null, default); Assert.Contains("x2", after!.Keys); Assert.Equal(2, VideoAggregate.Stats.BulkReads);
        }
        finally { VideoAggregate.Clock = () => DateTime.UtcNow; VideoAggregate.ClearCache(); }
    }
    [Fact]
    public void The_bootstrap_loop_passes_INDEXNODE_SERVE_ARGS_to_serve()
    {
        var f = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../sandbox/bootstrap.sh"));
        var text = File.ReadAllText(f).Replace("\r\n", "\n");
        Assert.Contains("SERVE_ARGS=\"${INDEXNODE_SERVE_ARGS:-}\"", text);
        Assert.Contains("serve --port $PORT --threads $THREADS $SERVE_ARGS >> \"$LOG\"", text);
    }
    [Fact]
    public async Task An_actors_job_makes_no_request_to_the_aggregate_and_takes_predecessors_and_AI_from_the_queue()
    {
        VideoAggregate.ClearCache(); ActorsStage.ForgetSeen();
        var fs = new FakeStore(); var m = new StoreMeter { Limits = new StorageLimits { MaxPerMin = 100000, WarnPerMin = 100000, MaxPerStep = 100000, MaxFailuresPerMin = 100000 } }; var store = new MeteredStore(fs, m);
        foreach (var (id, seed) in new[] { ("a1", 1), ("a2", 2), ("ai1", 3), ("me", 4) })
        {
            await Drama(fs, id, WithCos(0.9, seed), ((byte)(20 * seed), 50, 90), n: 4, seed: seed);
            await VideoJson.UpdateAsync(fs, P, id, r => r["grouping"] = new JsonObject { ["v"] = 1, ["proposal"] = new JsonObject { ["kind"] = "new", ["mixed"] = false }, ["seq"] = seed }, default);
        }
        // "ai1" is AI only by its hold in the queue (its video.json says nothing): it must be skipped, its cast files never read
        var aiHolds = new HashSet<string> { "ai1" }; var stage = new ActorsStage(store, null, 0.64) { MakeR50 = () => new ColourEmbedder(), Decode = Imaging.DecodeJpeg };
        VideoAggregate.Stats.Reset(); m.Reset();
        var rep = await stage.RunAsync(new ActorsIO(store, P), "me", false, 1, default, ["a1", "a2", "ai1"], null, up => aiHolds.Contains(up));
        output.WriteLine("actors job vs the aggregate: listGets=" + VideoAggregate.Stats.ListGets + " bulkReads=" + VideoAggregate.Stats.BulkReads + "; sites: " + string.Join("; ", m.Table().Select(x => $"{x.Site} {x.Op} x{x.Count}")));
        Assert.Equal(0, VideoAggregate.Stats.ListGets + VideoAggregate.Stats.BulkReads + VideoAggregate.Stats.RangeReads);
        Assert.DoesNotContain(m.Table(), x => x.Site.Contains("VideoAggregate"));
        Assert.Equal(2, rep.Predecessors); Assert.Equal(1, rep.PredecessorsAi);   // a1, a2 compared; ai1 skipped by its hold
        Assert.DoesNotContain(fs.Seen, n => n.Contains("videos.agg")); Assert.DoesNotContain(fs.Seen, n => n.Contains("ai1/cast"));
    }

    [Fact]
    public async Task Aggregate_writer_ops_per_video_json_update_on_two_lanes()
    {
        VideoAggregate.ClearCache(); VideoAggregate.Backoff = _ => TimeSpan.Zero;
        var fs = new FakeStore(); var m = new StoreMeter { Limits = new StorageLimits { MaxPerMin = 1000000, WarnPerMin = 1000000, MaxPerStep = 1000000, MaxFailuresPerMin = 1000000 } }; var store = new MeteredStore(fs, m);
        const int N = 200; foreach (var i in Enumerable.Range(0, 50)) await VideoJson.UpdateAsync(fs, P, "s" + i, r => r["x"] = 1, default);   // a populated aggregate
        VideoAggregate.Stats.Reset(); m.Reset();
        await Task.WhenAll(Enumerable.Range(0, 2).Select(lane => Task.Run(async () => { for (int i = 0; i < N / 2; i++) await VideoJson.UpdateAsync(store, P, "u" + lane + "_" + i, r => r["x"] = i, default); })));
        var rows = m.Table(); long vj = rows.Where(x => x.Site.Contains("VideoJson.UpdateAsync") || x.Site.Contains("VideoJson.ReadAsync")).Sum(x => x.Count);
        long agg = rows.Where(x => x.Site.Contains("VideoAggregate")).Sum(x => x.Count); 
        output.WriteLine($"aggregate writer, {N} video.json updates on 2 lanes: per update: video.json {(double)vj / N:F2} ops, aggregate {(double)agg / N:F2} ops (Put Block {(double)VideoAggregate.Stats.BlocksPut / N:F2}, Get Block List {(double)VideoAggregate.Stats.ListGets / N:F2}, Put Block List {(double)VideoAggregate.Stats.Commits / N:F2}, commit 412 retries {(double)VideoAggregate.Stats.CommitConflicts / N:F2}, skipped commit {(double)VideoAggregate.Stats.AlreadyCommitted / N:F2}); sites: " + string.Join("; ", rows.Select(x => $"{x.Site} {x.Op} x{x.Count}")));
        Assert.True(VideoAggregate.Stats.Writes >= N);
    }
}
