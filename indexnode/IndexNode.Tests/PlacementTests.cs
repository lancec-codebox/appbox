using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IndexNode.Tests;

public class PlacementTests
{
    private sealed class H(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Seen = r; return f(r); }
    }

    private const string Imds = "{\"compute\":{\"location\":\"westus2\",\"vmSize\":\"Standard_D4s_v5\",\"zone\":\"1\"}}";
    private static Task Ok(string h, CancellationToken t) => Task.Delay(1, t);

    [Fact]
    public async Task Imds_gives_azure_region_size_zone_and_header()
    {
        var h = new H(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Imds, Encoding.UTF8, "application/json") }));
        var p = await Placement.DetectAsync("acct.blob.core.windows.net", "westus2", h, null, Ok, default);
        Assert.Equal("azure", p.Provider); Assert.Equal("westus2", p.Region); Assert.Equal("Standard_D4s_v5", p.VmSize); Assert.Equal("1", p.Zone); Assert.Equal("imds", p.Source);
        Assert.True(p.SameRegionAsStorage); Assert.NotNull(p.StorageRttMs);
        Assert.Equal("true", h.Seen!.Headers.GetValues("Metadata").Single());
        Assert.Equal(Placement.ImdsUrl, h.Seen.RequestUri!.ToString());
    }

    [Fact]
    public async Task Other_region_is_not_same_region()
    {
        var h = new H(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Imds.Replace("westus2", "eastus")) }));
        var p = await Placement.DetectAsync("x", "West US 2", h, null, Ok, default);
        Assert.False(p.SameRegionAsStorage);
        p = await Placement.DetectAsync("x", "West US 2", new H(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Imds) })), null, Ok, default);
        Assert.True(p.SameRegionAsStorage);
    }

    [Fact]
    public async Task Not_found_timeout_and_refused_are_unknown_with_a_reason()
    {
        var p = await Placement.DetectAsync(null, "westus2", new H(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))), null, Ok, default);
        Assert.Equal("unknown", p.Provider); Assert.Equal("404", p.Reason); Assert.Null(p.SameRegionAsStorage); Assert.Null(p.Region);
        p = await Placement.DetectAsync(null, "westus2", new H(_ => throw new TaskCanceledException()), null, Ok, default);
        Assert.Equal("timeout", p.Reason);
        p = await Placement.DetectAsync(null, "westus2", new H(_ => throw new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused))), null, Ok, default);
        Assert.Equal("refused", p.Reason);
    }

    [Fact]
    public async Task Region_env_override_when_imds_is_absent()
    {
        var p = await Placement.DetectAsync(null, "westus2", new H(_ => throw new TaskCanceledException()), "westus2", Ok, default);
        Assert.Equal("unknown", p.Provider); Assert.Equal("westus2", p.Region); Assert.Equal("env", p.Source); Assert.True(p.SameRegionAsStorage); Assert.Null(p.Reason);
    }

    [Fact]
    public async Task Storage_rtt_is_the_median_of_five_connects()
    {
        var delays = new Queue<int>([50, 10, 200, 30, 20]);
        var rtt = await Placement.StorageRttAsync("h", 5, (_, t) => Task.Delay(delays.Dequeue(), t), default);
        Assert.InRange(rtt!.Value, 25, 60); // median of 10,20,30,50,200 = 30 (plus scheduling)
        Assert.Null(await Placement.StorageRttAsync("h", 3, (_, _) => throw new SocketException(), default));
    }
}

public class TrafficCarryOverTests
{
    [Fact]
    public void Previous_record_is_loaded_and_added_to()
    {
        var old = new Traffic(); old.Add(Traffic.StorageRead, 500);
        var rec = old.Snapshot();
        var t = new Traffic(); t.Load(rec); t.Add(Traffic.StorageRead, 100);
        var s = t.Snapshot();
        Assert.Equal(600, s.Totals.StorageRead);
        Assert.Equal(600, s.Hours.Single().StorageRead); // same hour: the bucket accumulates
    }

    [Fact]
    public void Old_buckets_beyond_48_hours_are_dropped_on_load_but_totals_stay()
    {
        var rec = new TrafficReport { Totals = new TrafficCounts { OtherIn = 9 }, Hours = [new TrafficHour { Hour = "2020-01-01T00:00Z", OtherIn = 9 }] };
        var t = new Traffic(); t.Load(rec);
        var s = t.Snapshot();
        Assert.Equal(9, s.Totals.OtherIn); Assert.Empty(s.Hours);
    }
}
