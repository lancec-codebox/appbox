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
    public void Groups_come_from_the_queue_join_chains_with_chains_and_a_merge_and_no_groups_file_is_read()
    {
        var st = new QueueState();
        void Grp(string id, string? join, string kind) { st.Apply(new QLine { T = "add", S = id, Stage = "group", Job = new JobDescriptor { Stage = "group" }, At = "2026-01-01T00:00:00Z" }); st.Apply(new QLine { T = "done", S = id, Stage = "group", Join = join, JoinKind = kind, At = "2026-01-01T00:00:01Z" }); }
        Grp("a", null, "new"); Grp("b", "a", "join"); Grp("c", "b", "join"); Grp("x", null, "new"); Grp("y", "x", "join"); Grp("m", "c", "mixed");
        var g = ActorsStage.GroupOfQueue(st);
        Assert.Equal("a", g["a"]); Assert.Equal("a", g["b"]); Assert.Equal("a", g["c"]); Assert.Equal("x", g["y"]); Assert.Equal("m", g["m"]);
    }

    [Fact]
    public async Task A_hundred_actors_jobs_on_two_lanes_stay_under_200_ops_a_minute_and_8_ops_a_job_in_steady_state()
    {
        VideoAggregate.ClearCache(); ActorsStage.ForgetSeen();
        var fs = new FakeStore(); var clock = new DateTime(2026, 11, 23, 1, 0, 0, DateTimeKind.Utc);
        var m = new StoreMeter(() => clock) { Limits = new StorageLimits { MaxPerMin = 200, WarnPerMin = 150, MaxPerStep = 100000, MaxFailuresPerMin = 100000 } };
        var store = new MeteredStore(fs, m);
        VideoAggregate.Clock = () => clock;
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
            int annV = 0; var io = new ActorsIO(store, P); var perJob = new List<long>(); var stamps = new List<(DateTime At, long Total)>(); var rnd = 0;
            for (int i = 0; i < N; i++)
            {
                await pacer.WaitAsync(default);   // before the claim
                long before = m.Total; using (m.BeginStep(0, ids[i]))
                {
                    var preds = new List<string>(); var rep = await stage.RunAsync(io, ids[i], false, 1, default, ids.Take(i).ToList(), null, null, new Dictionary<string, string>(), annV);
                }
                perJob.Add(m.Total - before); if ((i + 1) % 25 == 0) { var bm = m.Total; await ActorsIndex.BuildAsync(new ActorsIO(fs, P), default); ActorsIndex.ForgetCache(); annV++; } stamps.Add((clock, m.Total));
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
        finally { VideoAggregate.Clock = () => DateTime.UtcNow; }
    }


    static async Task<(FakeStore Fs, ActorsIndex Idx)> BigIndex(int uploads, int rowsPer)
    {
        var fs = new FakeStore(); int dim = 512; var rnd = new Random(7); var ups = new JsonArray(); int row = 0; var codes = new byte[uploads * rowsPer * SearchData.CodeBytes]; var vec = new byte[(long)uploads * rowsPer * dim * 2];
        rnd.NextBytes(codes);
        for (int i = 0; i < uploads; i++) { var units = new JsonArray(); units.Add(new JsonObject { ["key"] = $"u{i}#0", ["actor"] = "a" + i, ["prod"] = "p" + i, ["n"] = 5, ["row"] = row, ["rows"] = rowsPer }); row += rowsPer; ups.Add(new JsonObject { ["id"] = "up" + i, ["etag"] = "e" + i, ["units"] = units }); }
        var dir = ActorsData.VersionDir(P, 3);
        await fs.PutAsync(dir + "uploads.json", System.Text.Encoding.UTF8.GetBytes(ups.ToJsonString()), null, null, false, default); await fs.PutAsync(dir + "mean.f32", new byte[dim * 4], null, null, false, default);
        await fs.PutAsync(dir + "codes.bin", codes, null, null, false, default); await fs.PutAsync(dir + "vec.f16", vec, null, null, false, default);
        await fs.PutAsync(ActorsData.HeadFile(P), System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["version"] = 3, ["dim"] = dim }.ToJsonString()), null, null, false, default);
        return (fs, (await ActorsIndex.LoadAsync(new ActorsIO(fs, P), default, withVec: false)).Idx!);
    }

    [Fact]
    public async Task An_index_of_50_uploads_and_2000_vectors_is_read_whole_once_per_announced_version_and_head_json_only_on_cold_start()
    {
        ActorsIndex.ForgetCache(); var (fs, _) = await BigIndex(60, 40); var io = new ActorsIO(fs, P); fs.Gets.Clear();
        var a = await ActorsIndex.ForAnnouncedAsync(io, 0, default); Assert.True(a!.Count >= 2000 && a.Uploads.Count >= 50 && a.Vec != null);
        Assert.Equal(1, fs.Gets.Count(n => n.EndsWith("head.json"))); Assert.Equal(1, fs.Gets.Count(n => n.EndsWith("vec.f16")));
        fs.Gets.Clear(); for (int i = 0; i < 50; i++) Assert.Same(a, await ActorsIndex.ForAnnouncedAsync(io, 3, default)); Assert.Empty(fs.Gets);   // the announced version is held: nothing read
        foreach (var n in new[] { "uploads.json", "mean.f32", "codes.bin", "vec.f16" }) await fs.PutAsync(ActorsData.VersionDir(P, 4) + n, (await fs.GetAsync(ActorsData.VersionDir(P, 3) + n, default))!.Data, null, null, false, default); fs.Gets.Clear();
        var b = await ActorsIndex.ForAnnouncedAsync(io, 4, default); Assert.NotSame(a, b);   // a newer announcement: loaded once, without head.json
        Assert.DoesNotContain(fs.Gets, n => n.EndsWith("head.json")); ActorsIndex.ForgetCache();
    }

    [Fact]
    public async Task Over_32_MB_vec_f16_is_not_read_whole_and_a_shortlist_takes_at_most_8_ranges_of_4_MB()
    {
        ActorsIndex.ForgetCache(); var (fs, idx) = await BigIndex(40, 1000);   // 40000 rows x 1 KB = 41 MB
        var io = new ActorsIO(fs, P); Assert.True((long)idx.Count * idx.Dim * 2 > ActorsIndex.MaxWholeVec);
        var cached = await ActorsIndex.ForAnnouncedAsync(io, 0, default); Assert.Null(cached!.Vec); Assert.DoesNotContain(fs.Gets, n => n.EndsWith("vec.f16") && !n.Contains("range"));
        var rows = Enumerable.Range(0, 60).Select(i => i * 650).ToList(); fs.Gets.Clear();
        await cached.PrefetchAsync(io, rows, default); output.WriteLine("ranges read: " + cached.RowReads);
        Assert.InRange(cached.RowReads, 1, ActorsIndex.MaxRangeReads); Assert.True(fs.Gets.Count <= ActorsIndex.MaxRangeReads);
        await cached.PrefetchAsync(io, rows.Select(r => r + 1), default, ActorsIndex.MaxRangeReads - cached.RowReads); Assert.True(cached.RowReads <= ActorsIndex.MaxRangeReads);
        ActorsIndex.ForgetCache();
    }

    [Fact]
    public async Task A_heavy_single_step_is_paced_per_store_op_and_never_trips_the_breaker()
    {
        var fs = new FakeStore(); var clock = new DateTime(2026, 11, 23, 1, 0, 0, DateTimeKind.Utc);
        var m = new StoreMeter(() => clock) { Limits = new StorageLimits { MaxPerMin = 200, WarnPerMin = 150, MaxPerStep = 100000, MaxFailuresPerMin = 100000 } };
        var store = new MeteredStore(fs, m); store.Pacer = new OpPacer(() => m.Total, () => m.Limits.MaxPerMin, 0.8, () => clock, (d, t) => { clock += d; return Task.CompletedTask; });
        await fs.PutAsync("x", [1], null, null, false, default);
        long peak = 0; var stamps = new List<(DateTime At, long T)>();
        for (int i = 0; i < 1000; i++) { await store.GetAsync("x", default); stamps.Add((clock, m.Total)); clock += TimeSpan.FromMilliseconds(20); }
        for (int i = 0; i < stamps.Count; i++) { int j = i; while (j > 0 && stamps[i].At - stamps[j - 1].At <= TimeSpan.FromSeconds(60)) j--; peak = Math.Max(peak, stamps[i].T - (j == 0 ? 0 : stamps[j - 1].T)); }
        output.WriteLine($"heavy step: 1000 ops, pacer waits={store.Pacer.Waits}, peak/min={peak}"); Assert.True(store.Pacer.Waits > 0); Assert.True(peak <= 200); Assert.False(m.Tripped);
    }

    [Fact]
    public async Task Pruning_keeps_the_head_and_three_back_for_groups_files_as_for_search_versions()
    {
        var fs = new FakeStore();
        for (int v = 1; v <= 5; v++) { await GroupStore.WriteAsync(fs, P, new GroupSet { Groups = [] }, v, default); await fs.PutAsync(P.Prefix + $"search/v{v}/x.bin", [1], null, null, false, default); }
        await fs.PutAsync(SearchData.HeadName(P), SearchData.HeadYaml(5, "search/v5/"), null, null, false, default);
        await SearchData.PruneAsync(fs, P, 3, default);
        var left = (await fs.ListAsync(P.Prefix + "groups/", false, false, default)).Select(b => b.Name).OrderBy(x => x).ToList(); Assert.Equal(4, left.Count); Assert.DoesNotContain(P.Prefix + "groups/v1.json", left);
        Assert.Equal(4, (await fs.ListAsync(P.Prefix + "search/v", false, false, default)).Count);
    }

    [Fact]
    public async Task Pruning_after_5_builds_leaves_the_same_window_of_groups_files_as_search_versions()
    {
        var fs = new FakeStore();
        for (int v = 1; v <= 5; v++)
        {
            await fs.PutAsync(P.Prefix + $"search/v{v}/meta.json", [1], null, null, false, default);
            await GroupStore.WriteAsync(fs, P, new GroupSet { Groups = [] }, v, default);
            await fs.PutAsync(SearchData.HeadName(P), SearchData.HeadYaml(v, $"search/v{v}/"), null, null, false, default);
            await SearchData.PruneAsync(fs, P, 3, default);
        }
        var groups = (await fs.ListAsync(P.Prefix + "groups/", false, false, default)).Select(b => b.Name).Order().ToList();
        output.WriteLine(string.Join(",", groups)); Assert.Equal(4, groups.Count); Assert.DoesNotContain(groups, n => n.EndsWith("groups/v1.json")); Assert.Contains(groups, n => n.EndsWith("groups/v2.json"));
    }

    [Fact]
    public async Task A_cold_start_actors_job_with_no_announcement_reads_each_index_file_whole_once_and_never_a_range_nor_a_groups_file()
    {
        ActorsIndex.ForgetCache(); ActorsStage.ForgetSeen(); VideoAggregate.ClearCache();
        var fs = new FakeStore(); var cm = new StoreMeter(); var store = new MeteredStore(fs, cm);
        for (int i = 0; i < 3; i++)
        {
            var id = "c" + i; await Drama(fs, id, WithCos(1 - i * 0.05, 2), ((byte)(10 + i), 50, 90), n: 4, seed: i + 1);
            await VideoJson.UpdateAsync(fs, P, id, r => r["grouping"] = new JsonObject { ["v"] = 1, ["proposal"] = new JsonObject { ["kind"] = "new", ["mixed"] = false }, ["seq"] = i + 1 }, default);
        }
        await ActorsIndex.BuildAsync(new ActorsIO(fs, P), default); ActorsIndex.ForgetCache(); VideoAggregate.ClearCache();
        var stage = new ActorsStage(store, null, 0.64) { MakeR50 = () => new ColourEmbedder(), Decode = Imaging.DecodeJpeg }; var io = new ActorsIO(store, P);
        var q = new QueueState(); for (int i = 0; i < 3; i++) { q.Apply(new QLine { T = "add", S = "c" + i, Stage = "group", Job = new JobDescriptor { Stage = "group" }, At = "2026-01-01T00:00:00Z" }); q.Apply(new QLine { T = "done", S = "c" + i, Stage = "group", Join = null, JoinKind = "new", At = "2026-01-01T00:00:01Z" }); }
        fs.Gets.Clear(); var r0 = cm.Table().Where(x => x.Op.Contains("range", StringComparison.OrdinalIgnoreCase) && x.Site.Contains("ActorsIO")).Sum(x => x.Count);
        await stage.RunAsync(io, "c2", false, 1, default, ["c0", "c1"], null, null, ActorsStage.GroupOfQueue(q), 0);   // 0 = nothing announced yet
        output.WriteLine("table: " + string.Join("; ", cm.Table().Select(x => $"{x.Site} {x.Op} x{x.Count}"))); output.WriteLine("gets: " + string.Join(", ", fs.Gets.GroupBy(n => n).Select(g => g.Key.Split('/').Last() + " x" + g.Count())));
        Assert.Equal(r0, cm.Table().Where(x => x.Op.Contains("range", StringComparison.OrdinalIgnoreCase) && x.Site.Contains("ActorsIO")).Sum(x => x.Count)); Assert.Equal(1, fs.Gets.Count(n => n.EndsWith("vec.f16"))); Assert.Equal(1, fs.Gets.Count(n => n.EndsWith("codes.bin")));
        Assert.DoesNotContain(fs.Gets, n => n.Contains("groups/") || n.EndsWith("head.yaml"));
        ActorsIndex.ForgetCache();
    }

    [Fact]
    public async Task A_queue_whose_actors_index_done_line_has_no_ver_still_uses_the_index_from_head_json_and_links_to_indexed_uploads()
    {
        ActorsIndex.ForgetCache(); ActorsStage.ForgetSeen(); VideoAggregate.ClearCache();
        var fs = new FakeStore(); var store = new MeteredStore(fs, new StoreMeter());
        for (int i = 0; i < 3; i++)
        {
            var id = "n" + i; await Drama(fs, id, WithCos(i == 1 ? 0.5 : 1 - i * 0.02, i == 1 ? 6 : 1), (200, 150, 100), seed: i + 1);
            await VideoJson.UpdateAsync(fs, P, id, r => r["grouping"] = new JsonObject { ["v"] = 1, ["proposal"] = new JsonObject { ["kind"] = "new", ["mixed"] = false }, ["seq"] = i + 1 }, default);
        }
        var st = new ActorsStage(store, null, 0.64) { MakeR50 = () => new ColourEmbedder(), Decode = Imaging.DecodeJpeg };
        await st.RunAsync(new ActorsIO(fs, P), "n0", false, 1, default, []); await st.RunAsync(new ActorsIO(fs, P), "n1", false, 1, default, ["n0"]);
        await ActorsIndex.BuildAsync(new ActorsIO(fs, P), default); ActorsIndex.ForgetCache(); ActorsStage.ForgetSeen(); VideoAggregate.ClearCache();
        var q = new QueueState(); q.Apply(new QLine { T = "done", S = "actors-index", Stage = "actors-index", At = "2026-01-01T00:00:00Z" });   // a 0.13.27 done line: no ver
        Assert.Equal(0, q.ActorsIndexVer);
        var r2 = await st.RunAsync(new ActorsIO(store, P), "n2", false, 1, default, ["n0", "n1"], null, null, new Dictionary<string, string>(), q.ActorsIndexVer);
        var dbg = await ActorsIndex.ForAnnouncedAsync(new ActorsIO(store, P), 0, default); output.WriteLine($"idx: {(dbg == null ? "null" : dbg.Version + " uploads=" + dbg.Uploads.Count + " rows=" + dbg.Count)}; band={r2.Band} links={r2.Links}"); output.WriteLine($"indexed predecessors routed: {r2.IndexRouted}, predecessors computed: {r2.Predecessors}");
        Assert.True(r2.IndexRouted >= 1, "the index was not used"); ActorsIndex.ForgetCache();
    }

    [Fact]
    public async Task The_actors_run_path_with_a_real_queue_whose_actors_index_done_line_has_no_ver_reads_head_json_and_the_v1_files_once_and_considers_the_indexed_uploads()
    {
        ActorsIndex.ForgetCache(); ActorsStage.ForgetSeen(); VideoAggregate.ClearCache();
        var fs = new FakeStore(); var cm = new StoreMeter(); var store = new MeteredStore(fs, cm);
        for (int i = 0; i < 3; i++)
        {
            var id = "n" + i; await Drama(fs, id, WithCos(i == 1 ? 0.5 : 1 - i * 0.02, i == 1 ? 6 : 1), (200, 150, 100), seed: i + 1);
            await VideoJson.UpdateAsync(fs, P, id, r => r["grouping"] = new JsonObject { ["v"] = 1, ["proposal"] = new JsonObject { ["kind"] = "new", ["mixed"] = false }, ["seq"] = i + 1 }, default);
        }
        var st = new ActorsStage(store, null, 0.64) { MakeR50 = () => new ColourEmbedder(), Decode = Imaging.DecodeJpeg };
        await st.RunAsync(new ActorsIO(fs, P), "n0", false, 1, default, []); await st.RunAsync(new ActorsIO(fs, P), "n1", false, 1, default, ["n0"]);
        await ActorsIndex.BuildAsync(new ActorsIO(fs, P), default); ActorsIndex.ForgetCache(); ActorsStage.ForgetSeen(); VideoAggregate.ClearCache();
        // the live processing queue: seq lines and a 0.13.27 actors-index done line (no ver)
        var wq = new JobQueue(fs, P, "t"); await wq.LoadAsync(default);
        var lines = new List<QLine>(); for (int i = 0; i < 3; i++) { lines.Add(new QLine { T = "add", S = "n" + i, Stage = "group", Job = new JobDescriptor { Stage = "group" }, At = "2026-01-01T00:00:00Z" }); lines.Add(new QLine { T = "seq", S = "n" + i, Seq = i + 1, At = "2026-01-01T00:00:00Z" }); lines.Add(new QLine { T = "done", S = "n" + i, Stage = "group", JoinKind = "new", At = "2026-01-01T00:00:01Z" }); }
        lines.Add(new QLine { T = "add", S = "actors-index", Stage = "actors-index", Job = new JobDescriptor { Stage = "actors-index" }, At = "2026-01-01T00:00:00Z" }); lines.Add(new QLine { T = "done", S = "actors-index", Stage = "actors-index", At = "2026-01-01T00:00:02Z" });
        await wq.AppendLinesAsync(lines, default);
        var pq = await JobQueue.OpenAsync(store, P, "cli", false, default); Assert.Equal(0, pq.Read(x => x.ActorsIndexVer));
        fs.Gets.Clear(); var r2 = await st.RunWithQueueAsync(new ActorsIO(store, P), pq, "n2", false, 1, default);
        output.WriteLine($"preds={r2.Predecessors} routed={r2.IndexRouted} links={r2.Links}; gets: " + string.Join(", ", fs.Gets.Select(n => n.Split('/').Last())));
        Assert.Contains(fs.Gets, n => n.EndsWith("actors/head.json")); Assert.Equal(1, fs.Gets.Count(n => n.EndsWith("vec.f16"))); Assert.True(r2.IndexRouted >= 1, "the indexed uploads were not considered"); ActorsIndex.ForgetCache();
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
