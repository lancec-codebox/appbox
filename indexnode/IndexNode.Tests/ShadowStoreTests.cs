namespace IndexNode.Tests;

/// <summary>0.13.30: run --shadow --out-local: reads the real store, never writes to it.</summary>
public class ShadowStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "shadow-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public async Task Writes_go_to_the_overlay_and_the_real_store_is_never_changed()
    {
        var real = new FakeStore(); await real.PutAsync("index/a.json", [1, 2, 3], null, null, false, default);
        var s = new ShadowStore(real, dir); var before = real.Calls.Count;
        Assert.Equal([1, 2, 3], (await s.GetAsync("index/a.json", default))!.Data);                 // reads the real blob
        Assert.True(await s.PutAsync("index/b.json", [9], null, null, true, default));              // create-only into the overlay
        Assert.False(await s.PutAsync("index/b.json", [8], null, null, true, default));
        await s.PutAsync("index/a.json", [7], null, null, false, default);                          // shadows the real one
        Assert.Equal([7], (await s.GetAsync("index/a.json", default))!.Data); Assert.Equal([1, 2, 3], (await real.GetAsync("index/a.json", default))!.Data);
        Assert.False(real.Exists("index/b.json"));
        await s.DeleteAsync("index/a.json", default); Assert.Null(await s.GetAsync("index/a.json", default)); Assert.True(real.Exists("index/a.json"));
        Assert.Contains(await s.ListAsync("index/", false, false, default), b => b.Name == "index/b.json");
        Assert.DoesNotContain(await s.ListAsync("index/", false, false, default), b => b.Name == "index/a.json");
    }

    [Fact]
    public async Task The_queue_log_is_copy_on_write_and_the_real_log_is_not_appended()
    {
        var real = new FakeStore(); await real.CreateAppendBlobAsync("index/q/log-1", true, default);
        var r1 = await real.AppendBlockAsync("index/q/log-1", "{\"a\":1}\n"u8.ToArray(), 0, default); Assert.Equal(AppendOutcome.Ok, r1.Outcome);
        var s = new ShadowStore(real, dir);
        var info = await s.GetAppendInfoAsync("index/q/log-1", default); Assert.Equal(r1.Length, info!.Length);
        var ap = await s.AppendBlockAsync("index/q/log-1", "{\"b\":2}\n"u8.ToArray(), info.Length, default); Assert.Equal(AppendOutcome.Ok, ap.Outcome);
        Assert.Equal(AppendOutcome.PositionMismatch, (await s.AppendBlockAsync("index/q/log-1", [1], info.Length, default)).Outcome);
        var rd = await s.ReadAppendAsync("index/q/log-1", 0, 4096, default); Assert.Equal("{\"a\":1}\n{\"b\":2}\n", System.Text.Encoding.UTF8.GetString(rd.Data));
        Assert.Equal(r1.Length, (await real.GetAppendInfoAsync("index/q/log-1", default))!.Length);   // the real log is untouched
        s.Flush(); Assert.Single(Directory.GetFiles(Path.Combine(dir, "append")));
    }

    [Fact]
    public async Task Aggregate_block_writes_are_dropped()
    {
        var real = new FakeStore(); var s = new ShadowStore(real, dir);
        await s.PutBlockAsync("index/videos.agg", "AAAA", [1], default); Assert.True(await s.PutBlockListAsync("index/videos.agg", ["AAAA"], null, default));
        Assert.Equal(2, s.BlockedWrites); Assert.False(real.Exists("index/videos.agg"));
    }

    static byte[] ZipOf(params (string, string)[] m) { var ms = new MemoryStream(); using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true)) foreach (var (n, d) in m) { var e = z.CreateEntry(n); using var w = e.Open(); w.Write(System.Text.Encoding.UTF8.GetBytes(d)); } return ms.ToArray(); }

    [Fact]
    public async Task In_shadow_mode_five_opens_of_one_zip_cost_one_store_GET()
    {
        var fs = new FakeStore(); var P = new Paths("index/");
        await fs.PutAsync(P.Video("sz1", "cast.zip"), ZipOf(("cast/faces.json", "{}"), ("cast/clusters.json", "{}")), null, null, false, default);
        var m = new StoreMeter { Limits = new StorageLimits { MaxPerStep = 1000 } }; var rest = FakeBlobHttp.Rest(fs, m, out var http);
        var shadow = new ShadowStore(rest, Path.Combine(dir, "out")); var store = StageZipStore.Wrap(shadow, Path.Combine(dir, "zips"));   // exactly what Program.Open builds
        using (StageZipStore.WholeZips())
            for (int i = 0; i < 5; i++) { Assert.NotNull(await store.GetAsync(P.Video("sz1", "cast/faces.json"), default)); await store.GetTagsAsync(P.Video("sz1", "cast/clusters.json"), default); }
        var gets = http.Log.Count(l => l == "GET index/videos/sz1/cast.zip"); Assert.True(gets == 1, "shadow: cast.zip GETs " + gets + ": " + string.Join(" | ", http.Log));
        // the live node path: the same opens, over a fresh store with a per-job LocalCacheStore on top
        while (http.Log.TryDequeue(out _)) { }
        var live = new LocalCacheStore(StageZipStore.Wrap(rest, Path.Combine(dir, "zips2")), Path.Combine(dir, "lc"));
        using (StageZipStore.WholeZips())
            for (int i = 0; i < 5; i++) { Assert.NotNull(await live.GetAsync(P.Video("sz1", "cast/faces.json"), default)); await live.GetTagsAsync(P.Video("sz1", "cast/clusters.json"), default); }
        gets = http.Log.Count(l => l == "GET index/videos/sz1/cast.zip"); Assert.True(gets == 1, "live: cast.zip GETs " + gets + ": " + string.Join(" | ", http.Log));
    }

    [Fact]
    public async Task A_shadow_download_is_kept_so_five_downloads_of_one_blob_cost_one_GET()
    {
        var fs = new FakeStore(); await fs.PutAsync("index/videos/sz2/cast.zip", new byte[5000], null, null, false, default);
        var m = new StoreMeter { Limits = new StorageLimits { MaxPerStep = 1000 } }; var rest = FakeBlobHttp.Rest(fs, m, out var http); var s = new ShadowStore(rest, Path.Combine(dir, "out"));
        for (int i = 0; i < 5; i++) { var f = Path.Combine(dir, "d" + i); await s.DownloadToFileAsync("index/videos/sz2/cast.zip", f, default); Assert.Equal(5000, new FileInfo(f).Length); }
        var gets = http.Log.Count(l => l == "GET index/videos/sz2/cast.zip"); Assert.True(gets == 1, "GETs " + gets + ": " + string.Join(" | ", http.Log));
        await s.PutAsync("index/videos/sz2/cast.zip", new byte[10], null, null, false, default);   // a shadow write wins over the kept download
        var g = Path.Combine(dir, "dw"); await s.DownloadToFileAsync("index/videos/sz2/cast.zip", g, default); Assert.Equal(10, new FileInfo(g).Length);
    }

    [Fact]
    public async Task All_readers_of_one_job_share_one_open_of_each_zip_no_per_member_touch_or_head()
    {
        var fs = new FakeStore(); var P = new Paths("index/");
        await fs.PutAsync(P.Video("sz3", "cast.zip"), ZipOf(("cast/faces.json", "{}"), ("cast/clusters.json", "{}"), ("cast/faces.f16", "xx")), null, null, false, default);
        var m = new StoreMeter { Limits = new StorageLimits { MaxPerStep = 1000 } }; var rest = FakeBlobHttp.Rest(fs, m, out var http);
        var store = StageZipStore.Wrap(new ShadowStore(rest, Path.Combine(dir, "out")), Path.Combine(dir, "zips"));
        using (StageZipStore.WholeZips())
            for (int i = 0; i < 5; i++) foreach (var n in new[] { "cast/faces.json", "cast/clusters.json", "cast/faces.f16" }) Assert.NotNull(await FrameChunks.ReadAsync(store, P.Video("sz3", n), default));
        var z = http.Log.Where(l => l.Contains("sz3/cast.zip")).GroupBy(l => l.Split(' ')[0] + (l.Contains('?') ? "?" + l.Split('?')[1] : "")).ToDictionary(g => g.Key, g => g.Count());
        Assert.True(z.GetValueOrDefault("GET") == 1 && z.Values.Sum() <= 4 && http.Log.Count <= 6, "one open of cast.zip: " + string.Join(", ", z.Select(x => x.Key + " x" + x.Value)) + " | " + string.Join(" | ", http.Log));
    }
}
