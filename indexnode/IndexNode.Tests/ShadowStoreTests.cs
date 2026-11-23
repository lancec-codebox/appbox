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
}
