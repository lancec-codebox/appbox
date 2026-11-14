using System.Net;
using System.Text;
using System.Text.Json;

namespace IndexNode.Tests;

public class TrafficTests
{
    private const string Sas = "https://acct.blob.core.windows.net/media?sv=2021-12-02&sp=racwdlfti&sig=x";

    private sealed class H(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(f(r));
    }

    private static HttpResponseMessage Ok(HttpMethod m, byte[] body) => new(m == HttpMethod.Put ? HttpStatusCode.Created : HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task Storage_get_put_and_download_are_counted()
    {
        var t = new Traffic();
        var store = new RestStore(new HttpClient(new H(r => Ok(r.Method, r.Method == HttpMethod.Get ? new byte[1000] : []))), Sas) { Counters = t };
        await store.GetAsync("a", default);
        await store.PutAsync("b", new byte[300], null, null, false, default);
        var f = Path.GetTempFileName();
        try
        {
            await store.DownloadToFileAsync("c", f, default);
            File.WriteAllBytes(f, new byte[20_000]);
            await store.PutFileAsync("d", f, "video/mp4", null, default); // one block + the block list
        }
        finally { File.Delete(f); }
        var s = t.Snapshot().Totals;
        Assert.Equal(2000, s.StorageRead);
        Assert.True(s.StorageWrite >= 20_300, s.StorageWrite.ToString());
        Assert.Equal(0, s.YoutubeIn);
    }

    [Fact]
    public void Hours_roll_over_and_keep_48()
    {
        var now = new DateTimeOffset(2026, 11, 14, 6, 10, 0, TimeSpan.Zero);
        var t = new Traffic(() => now);
        t.Add(Traffic.YoutubeIn, 100);
        now = now.AddMinutes(30); t.Add(Traffic.YoutubeIn, 50);   // same hour
        now = now.AddHours(1); t.Add(Traffic.AiOut, 7);           // next hour
        var r = t.Snapshot();
        Assert.Equal(["2026-11-14T06:00Z", "2026-11-14T07:00Z"], r.Hours.Select(h => h.Hour));
        Assert.Equal(150, r.Hours[0].YoutubeIn);
        Assert.Equal(7, r.Hours[1].AiOut);
        Assert.Equal(150, r.Totals.YoutubeIn);

        for (int i = 0; i < 60; i++) { now = now.AddHours(1); t.Add(Traffic.OtherIn, 1); }
        r = t.Snapshot();
        Assert.Equal(48, r.Hours.Count);
        Assert.Equal(60, r.Totals.OtherIn);
        Assert.Equal(150, r.Totals.YoutubeIn); // totals never roll off
    }

    [Fact]
    public void Last_hour_and_24h_sums()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 20, 0, TimeSpan.Zero);
        var t = new Traffic(() => now);
        foreach (var back in new[] { 30, 5, 3, 1, 0 }) { now = new DateTimeOffset(2026, 11, 14, 12, 20, 0, TimeSpan.Zero).AddHours(-back); t.Add(Traffic.StorageRead, 10); }
        now = new DateTimeOffset(2026, 11, 14, 12, 20, 0, TimeSpan.Zero);
        var r = t.Snapshot();
        Assert.Equal(10, r.Sum(now, TimeSpan.FromHours(1)).StorageRead);
        Assert.Equal(40, r.Sum(now, TimeSpan.FromHours(24)).StorageRead);
        var j = StatusSnapshot.TrafficJson(r, now);
        Assert.Equal(10, j["lastHour"]!["storageRead"]!.GetValue<long>());
        Assert.Equal(40, j["last24h"]!["storageRead"]!.GetValue<long>());
        Assert.Equal(50, j["totals"]!["storageRead"]!.GetValue<long>());
    }

    [Fact]
    public void Node_record_round_trips_traffic()
    {
        var t = new Traffic(); t.Add(Traffic.AiIn, 9);
        var n = new NodeInfo { NodeId = "n", Traffic = t.Snapshot() };
        var back = NodeInfo.Parse(n.ToBytes())!;
        Assert.Equal(9, back.Traffic!.Totals.AiIn);
        Assert.Single(back.Traffic.Hours);
    }

    [Fact]
    public async Task Ai_request_and_response_bytes_are_counted()
    {
        var t = new Traffic();
        var cfg = new AiConfig { BaseUrl = "https://ai.example/v1", TextModel = "m", Api = "chat" };
        var client = new AiClient(new HttpClient(new H(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"hi\"}}]}", Encoding.UTF8, "application/json") })), cfg) { Counters = t };
        try { await client.ChatAsync("m", System.Text.Json.Nodes.JsonValue.Create("hello"), default); } catch { }
        var s = t.Snapshot().Totals;
        Assert.True(s.AiOut > 0); Assert.True(s.AiIn > 0);
    }
}
