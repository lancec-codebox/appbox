using System.Net;

namespace IndexNode.Tests;

public class RegionGateTests
{
    sealed class H(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => f(r, ct); }

    static Func<CancellationToken, Task<Placement>> Probe(string? region) => _ => Task.FromResult(new Placement { Provider = region == null ? "unknown" : "azure", Region = region, Source = region == null ? null : "imds" });
    static readonly string[] Process = ["process", "frames"], Check = ["check"], Light = ["download", "sample", "refresh"];
    static RegionGate.Detected In(string? r, string src = "imds") => new(r, src);

    [Fact]
    public void A_westus2_node_with_process_runs_and_an_eastus_node_with_check_does_not()
    {
        Assert.True(RegionGate.Evaluate(Protocol.ExpandStages(Process), "westus2", In("westus2"), false).Run);
        var v = RegionGate.Evaluate(Check, "westus2", In("eastus"), false);
        Assert.False(v.Run); Assert.Contains("check", v.Error); Assert.Contains("eastus", v.Error); Assert.Contains("westus2", v.Error);
    }

    [Fact]
    public void A_non_azure_node_fails_with_process_but_runs_download_sample_and_refresh()
    {
        var none = In(null, "none");
        Assert.False(RegionGate.Evaluate(Protocol.ExpandStages(Process), "westus2", none, false).Run);
        Assert.True(RegionGate.Evaluate(Light, "westus2", none, false).Run);
    }

    [Fact]
    public void Every_region_only_stage_is_listed_in_one_place()
    {
        foreach (var s in new[] { "check", "process", "group", "search-index", "frames", "sscd", "centers", "transcribe", "describe" }) Assert.Contains(s, RegionGate.OnlyStages);
        foreach (var s in new[] { "download", "sample", "refresh", "classify", "availability" }) Assert.DoesNotContain(s, RegionGate.OnlyStages);
    }

    [Fact]
    public void The_override_runs_out_of_region_with_a_warning_and_no_region_set_warns_and_runs()
    {
        var o = RegionGate.Evaluate(Check, "westus2", In("eastus"), true); Assert.True(o.Run); Assert.Contains("--allow-out-of-region", o.Warning);
        var n = RegionGate.Evaluate(Check, null, In("eastus"), false); Assert.True(n.Run); Assert.Contains("no region", n.Warning);
        Assert.Null(RegionGate.Evaluate(Light, null, In(null, "none"), false).Warning);   // nothing region-only: nothing to say
    }

    [Fact]
    public async Task IMDS_answering_in_time_gives_its_region()
    {
        var h = new H((r, _) => { Assert.Contains("metadata/instance/compute/location", r.RequestUri!.ToString()); Assert.True(r.Headers.Contains("Metadata")); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("westus2") }); });
        var d = await RegionGate.DetectAsync(h, null, null, "westus2", default); Assert.Equal(("westus2", "imds"), (d.Region, d.Source));
    }

    [Fact]
    public async Task IMDS_hanging_is_cut_at_2_seconds_and_the_placement_region_is_used_and_the_whole_check_stays_under_2_5_seconds()
    {
        var h = new H(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = await RegionGate.DetectAsync(h, null, Probe("westus2"), "westus2", default); sw.Stop();
        Assert.Equal(("westus2", "placement"), (d.Region, d.Source)); Assert.InRange(sw.Elapsed.TotalSeconds, 1.8, 2.5);
        var hang2 = new H(async (_, _) => { await Task.Delay(Timeout.Infinite); return new HttpResponseMessage(); });   // a handler that ignores cancellation is cut as well
        sw.Restart(); var n = await RegionGate.DetectAsync(hang2, null, Probe(null), "westus2", default); sw.Stop();
        Assert.Equal((null, "none"), (n.Region, n.Source)); Assert.True(sw.Elapsed.TotalSeconds < 2.5, sw.Elapsed.ToString());
    }

    [Fact]
    public async Task IMDS_failing_or_answering_an_error_falls_back_and_neither_source_means_none()
    {
        var fail = new H((_, _) => throw new HttpRequestException("refused")); Assert.Equal("none", (await RegionGate.DetectAsync(fail, null, Probe(null), "westus2", default)).Source);
        var nf = new H((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))); var q = await RegionGate.DetectAsync(nf, null, Probe("eastus"), "westus2", default); Assert.Equal(("eastus", "placement"), (q.Region, q.Source));
    }

    [Fact]
    public async Task Control_region_is_written_normalized_kept_with_the_rest_of_the_file_and_can_be_turned_off()
    {
        var s = new FakeStore(); var p = new Paths("index/");
        var c = await ControlWatcher.SetRegionAsync(s, p, "West US 2", "t", default); Assert.Equal("westus2", c.Region);
        Assert.Equal("westus2", ControlFile.Parse((await s.GetAsync(p.Control, default))!.Data).Region);
        Assert.Null((await ControlWatcher.SetRegionAsync(s, p, "off", "t", default)).Region);
    }

    [Fact]
    public async Task The_env_override_wins_over_imds_and_placement_and_a_slow_placement_probe_is_cut_at_its_limit()
    {
        var imds = new H((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("eastus") }));
        var e = await RegionGate.DetectAsync(imds, "westus2", Probe("northeurope"), "westus2", default); Assert.Equal(("westus2", "env"), (e.Region, e.Source));
        var slow = new H(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = await RegionGate.DetectAsync(slow, null, async ct => { await Task.Delay(Timeout.Infinite, ct); return new Placement(); }, "westus2", default, TimeSpan.FromMilliseconds(500));
        Assert.Equal((null, "none"), (d.Region, d.Source)); Assert.Contains("timeout", d.PlacementNote); Assert.True(sw.Elapsed.TotalSeconds < 3, sw.Elapsed.ToString());
    }

    [Fact]
    public async Task The_region_command_prints_every_source_and_the_verdict_and_writes_nothing()
    {
        var s = new FakeStore(); var p = new Paths("index/"); await ControlWatcher.SetRegionAsync(s, p, "westus2", "t", default); var puts = s.Puts.Count;
        var imds = new H((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var w = new StringWriter(); var rc = await RegionCommand.RunAsync(Args.Parse([]), s, p, imds, Probe("westus2"), "", w); var o = w.ToString();
        Assert.Equal(0, rc); Assert.Contains("imds:", o); Assert.Contains("placement: westus2", o); Assert.Contains("(source: placement)", o); Assert.Contains("control.json region: westus2", o); Assert.Contains("COULD run", o);
        var w2 = new StringWriter(); var rc2 = await RegionCommand.RunAsync(Args.Parse([]), s, p, imds, Probe("eastus"), "", w2);
        Assert.Equal(4, rc2); Assert.Contains("could NOT run", w2.ToString()); Assert.Equal(puts, s.Puts.Count);
    }
}
