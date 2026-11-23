using System.Text.Json.Nodes;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.31: serve's search reads whole files only, never per query once warm; the breaker closes after its cooldown; serve --warm fills the cache.</summary>
[Collection("console")]
public class ServeSearchStoreTests
{
    static readonly Paths P = new("index/");
    const int D = CentersFile.Dim;
    sealed class Clock { public DateTimeOffset T = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero); public DateTimeOffset Now() => T; }

    static async Task<(FakeStore Fs, SearchData Idx, List<float[]> Xs)> Dramas(int n, int seed)
    {
        var cache = FramesFileTests.Tmp(); FakeStore fs = new(); StageZipStore? wr; var xs = new List<float[]>();
        for (int i = 0; i < n; i++) { var x = FramesFileTests.Make(300, 12, seed + i); xs.Add(x); (fs, wr) = await FramesFileTests.Drama("s" + i, FramesFileTests.F16(x), 300, cache + i, fs); await FramesFileTests.Centers(wr, "s" + i); }
        var ups = await SearchIndexBuild.ListCentersAsync(fs, P, default);
        var idx = await SearchIndexBuild.BuildAsync(fs, P, ups, null, null, 1, 1, 2, _ => { }, default);
        return (fs, idx, xs);
    }
    static byte[] Vec32(float[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4, 4), v[i]); return b; }

    [Fact]
    public async Task A_cold_upload_is_one_whole_GET_and_no_range_read_and_the_same_search_warm_is_zero_store_ops()
    {
        var dir = FramesFileTests.Tmp();
        try
        {
            var (fs, idx, xs) = await Dramas(1, 500);
            var meter = new StoreMeter { Limits = new StorageLimits { MaxPerStep = 25 } }; var ms = new MeteredStore(fs, meter);
            FramesCache.Current = new FramesCache(dir, 1L << 30);
            var q = xs[0].AsSpan(9 * D, D).ToArray();
            fs.WholeGets = 0; var cold = await CenterSearch.QueryAsync(ms, P, idx, q, 8, 5, 2, default);
            Assert.Equal(1, fs.WholeGets); Assert.Equal(1, meter.Total); Assert.DoesNotContain(meter.Table(), r => r.Op.StartsWith("range")); Assert.Equal(1, cold.Timings.Requests);
            var warm = await CenterSearch.QueryAsync(ms, P, idx, q, 8, 5, 2, default);
            Assert.Equal(1, meter.Total); Assert.Equal(1, fs.WholeGets); Assert.Equal(0, warm.Timings.Requests);   // warm: ZERO store ops
            Assert.Equal(cold.Hits.Select(h => (h.Upload, h.Row, h.Score)), warm.Hits.Select(h => (h.Upload, h.Row, h.Score)));
        }
        finally { FramesCache.Current = null; try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Serve_search_is_one_step_cold_costs_the_one_file_and_warm_costs_zero_with_no_head_yaml_read()
    {
        var dir = FramesFileTests.Tmp(); var old = StoreMeter.Current;
        try
        {
            var (fs, idx, xs) = await Dramas(1, 600); await idx.PublishAsync(fs, P, default);
            var meter = new StoreMeter(); StoreMeter.Current = meter; var ms = new MeteredStore(fs, meter);
            FramesCache.Current = new FramesCache(Path.Combine(dir, "fc"), 1L << 30);
            var cache = new SearchIndexCache(Path.Combine(dir, "ix"), TimeSpan.FromMinutes(5)); var http = new HttpClient(); var cfg = new NodeConfig(); var tools = new Tools(http, cfg);
            var server = new PreCheckServer(new PreCheckAnalyzer(tools, cfg, http, ms, P, cache), cache, ms, P, new BlockCache(1 << 20), new PreCheckSettings(), 2, "t");
            Assert.NotNull(await server.IndexAsync(default));   // start-up: the one index load
            long afterLoad = meter.Total; var vec = Vec32(xs[0].AsSpan(3 * D, D).ToArray());
            var r1 = await server.SearchAsync(null, "", vec, false, 8, 5, default);
            Assert.InRange((long)r1["timings"]!["storeOps"]!, 1, 3); Assert.Equal(afterLoad + (long)r1["timings"]!["storeOps"]!, meter.Total);
            var r2 = await server.SearchAsync(null, "", vec, false, 8, 5, default);
            Assert.Equal(0L, (long)r2["timings"]!["storeOps"]!); Assert.Equal(afterLoad + (long)r1["timings"]!["storeOps"]!, meter.Total);   // warm: nothing, not even head.yaml
            Assert.Equal(r1["hits"]!.ToJsonString(), r2["hits"]!.ToJsonString());
        }
        finally { StoreMeter.Current = old; FramesCache.Current = null; try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task The_breaker_closes_after_the_cooldown_and_calls_refused_while_open_are_not_ops_in_the_window()
    {
        var c = new Clock(); var m = new StoreMeter(c.Now) { Limits = new StorageLimits { MaxPerMin = 20, WarnPerMin = 15, Cooldown = TimeSpan.FromMinutes(5) } }; var s = new MeteredStore(new FakeStore(), m);
        await Assert.ThrowsAsync<StorageBudgetExceeded>(async () => { for (int i = 0; i < 100; i++) await s.HeadAsync("x" + i, default); });
        Assert.True(m.Tripped); long total = m.Total;
        for (int min = 1; min <= 4; min++) { c.T = c.T.AddSeconds(60); for (int i = 0; i < 30; i++) await Assert.ThrowsAsync<StorageBudgetExceeded>(() => s.HeadAsync("again", default)); }   // a query every few seconds while open
        Assert.Equal(total, m.Total); Assert.Equal(0, m.OpsLastMinute);   // refused calls count nowhere
        c.T = c.T.AddSeconds(61);   // 5 min 1 s since the trip
        await s.HeadAsync("fine", default); Assert.False(m.Tripped); Assert.Equal(1, m.OpsLastMinute);
        for (int i = 0; i < 10; i++) await s.HeadAsync("more", default);   // a normal load goes on
    }

    [Fact]
    public async Task Serve_warm_fetches_every_frames_file_once_into_the_cache_and_a_second_warm_costs_nothing()
    {
        var dir = FramesFileTests.Tmp();
        try
        {
            var (fs, idx, _) = await Dramas(3, 700); FramesCache.Current = new FramesCache(dir, 1L << 30);
            fs.WholeGets = 0; var lines = new List<string>();
            var r = await PreCheckServer.WarmFramesAsync(fs, idx, null, lines.Add, default);
            Assert.Equal((3, 0, 0), r); Assert.Equal(3, fs.WholeGets); Assert.Equal(3, Directory.GetFiles(dir, "*.bin").Length);
            var again = await PreCheckServer.WarmFramesAsync(fs, idx, null, null, default);
            Assert.Equal((0, 3, 0), again); Assert.Equal(3, fs.WholeGets);
        }
        finally { FramesCache.Current = null; try { Directory.Delete(dir, true); } catch { } }
    }
}
