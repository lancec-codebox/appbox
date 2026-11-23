using System.Net;

namespace IndexNode.Tests;

/// <summary>0.13.26: the low normal storage budget (200 ops/min, warn 150, 1,000 per step, 100 failures/min), a file upload's Put Blocks as one op, the pacer following the live limit, and what the stages measure on fakes.</summary>
public class StorageBudgetTests
{
    const string Sas = "https://acct.blob.core.windows.net/media?sv=2021-12-02&sp=racwdlfti&sig=SIG";
    sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { lock (Seen) Seen.Add(r.Method + " " + r.RequestUri!.Query.Split('&')[0]); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)); }
    }

    [Fact]
    public void The_defaults_are_the_low_normal_budget()
    {
        var l = new StorageLimits();
        Assert.Equal((150, 200, 1000, 100, TimeSpan.FromMinutes(5)), (l.WarnPerMin, l.MaxPerMin, l.MaxPerStep, l.MaxFailuresPerMin, l.Cooldown));
    }


    [Fact]
    public async Task Storage_max_and_a_step_over_1000_throw()
    {
        var m = new StoreMeter { CliMax = 5, Limits = new StorageLimits { MaxFailuresPerMin = 100000 } }; var fs = new MeteredStore(new FakeStore(), m);
        for (int i = 0; i < 5; i++) await fs.HeadAsync("b" + i, default);
        await Assert.ThrowsAsync<StorageBudgetExceeded>(() => fs.HeadAsync("c", default));   // the 6th op passes --storage-max 5
        var m2 = new StoreMeter { Limits = new StorageLimits { MaxPerMin = 100000, WarnPerMin = 100000, MaxFailuresPerMin = 100000 } }; var f2 = new MeteredStore(new FakeStore(), m2);
        using (m2.BeginStep())
        {
            for (int i = 0; i < 1000; i++) await f2.HeadAsync("s" + i, default);
            await Assert.ThrowsAsync<StorageBudgetExceeded>(() => f2.HeadAsync("one-too-many", default));
        }
    }

    [Fact]
    public async Task The_pacer_follows_the_live_limit()
    {
        int max = 200; long ops = 0; var t = DateTime.UtcNow; TimeSpan slept = TimeSpan.Zero;
        var p = new OpPacer(() => ops, () => max, 0.8, () => t, (d, _) => { slept += d; t += d; if (slept > TimeSpan.FromSeconds(5)) ops = 0; return Task.CompletedTask; });
        Assert.Equal(160, p.Budget); max = 1000; Assert.Equal(800, p.Budget);   // --storage-max raised the limit after the pacer was made
        await p.WaitAsync(default); ops = 700; await p.WaitAsync(default); Assert.Equal(0, p.Waits);   // 700 ops in the window, under the raised budget 800: no wait
        max = 200; ops = 710; await p.WaitAsync(default); Assert.True(p.Waits > 0);                    // back to the low default (160): it waits
    }
    sealed class StreamHandler : HttpMessageHandler
    {
        public int Blocks, Lists, Blobs; public long Bytes, ListBytes, MaxConcurrent; long cur;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var q = r.RequestUri!.Query;
            if (r.Content != null)
            {
                var n = Interlocked.Increment(ref cur); long seen; do { seen = Interlocked.Read(ref MaxConcurrent); } while (n > seen && Interlocked.CompareExchange(ref MaxConcurrent, n, seen) != seen);
                using var st = await r.Content.ReadAsStreamAsync(ct); var buf = new byte[64 * 1024]; int k;
                var isList = r.RequestUri!.Query.Contains("comp=blocklist");
                while ((k = await st.ReadAsync(buf, ct)) > 0) Interlocked.Add(ref isList ? ref ListBytes : ref Bytes, k);   // consume in small pieces, like the wire
                Interlocked.Decrement(ref cur);
            }
            if (q.Contains("comp=blocklist")) Interlocked.Increment(ref Lists);
            else if (q.Contains("comp=block")) Interlocked.Increment(ref Blocks);
            else if (r.Method == HttpMethod.Put) Interlocked.Increment(ref Blobs);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    static string Sparse(long len) { var p = Path.Combine(Path.GetTempPath(), "bud-" + Guid.NewGuid().ToString("N")[..8] + ".bin"); using var fs = new FileStream(p, FileMode.Create); fs.SetLength(len); return p; }

    [Fact]
    public async Task A_big_upload_is_ceil_size_over_64MiB_put_blocks_plus_one_list_all_counted_and_streamed()
    {
        Assert.Equal(64 * 1024 * 1024, RestStore.BlockSize); Assert.Equal(2, RestStore.BlockParallelism);
        long size = 2L * RestStore.BlockSize + 5;   // a sparse 128 MiB + 5 file: 3 blocks
        var path = Sparse(size);
        try
        {
            var m = new StoreMeter(); var h = new StreamHandler();
            var s = new RestStore(new HttpClient(h), Sas) { RetryDelay = _ => TimeSpan.Zero, Meter = m };
            var before = GC.GetTotalAllocatedBytes(true);
            await s.PutFileAsync("index/videos/x/video.mp4", path, "video/mp4", null, default);
            var allocated = GC.GetTotalAllocatedBytes(true) - before;
            Assert.Equal(3, h.Blocks); Assert.Equal(1, h.Lists); Assert.Equal(0, h.Blobs);
            Assert.Equal(size, h.Bytes);
            Assert.Equal(4, m.Total); Assert.Equal(4, m.OpsLastMinute);   // every op counts
            Assert.InRange(h.MaxConcurrent, 1, 2);
            Assert.True(allocated < 64L * 1024 * 1024, $"allocated {allocated} bytes: the blocks were buffered, not streamed");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_file_under_one_block_is_one_block_and_the_list()
    {
        var path = Sparse(1024 * 1024);
        try
        {
            var h = new StreamHandler();
            var s = new RestStore(new HttpClient(h), Sas) { RetryDelay = _ => TimeSpan.Zero, Meter = new StoreMeter() };
            await s.PutFileAsync("index/videos/x/small.mp4", path, "video/mp4", null, default);
            Assert.Equal((1, 1, 0), (h.Blocks, h.Lists, h.Blobs));   // as before: the code has no single-Put-Blob path for a non-empty file
        }
        finally { File.Delete(path); }
    }
}
