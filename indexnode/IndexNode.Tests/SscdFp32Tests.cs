namespace IndexNode.Tests;

public class SscdFp32Tests
{
    private static readonly Paths P = new("index-test/");
    private static byte[] Man(string model) => System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"step\":\"sscd\",\"info\":{\"model\":\"" + model + "\"}}");

    private static async Task Done(FakeStore s, string id, string? model, string state = "done")
    {
        await s.PutAsync(P.Job("sscd", id), JobDescriptor.NewStep("sscd", id).ToBytes(), Tags.Job("sscd", state, "2026-11-10T00:00:00Z"), null, false, default);
        if (model != null) await s.PutAsync(P.Video(id, "sscd/manifest.json"), Man(model), null, null, false, default);
    }

    [Fact]
    public void An_int8_manifest_is_recognised_and_fp32_or_garbage_is_not()
    {
        Assert.True(Sscd.IsInt8Manifest(Man("sscd_disc_mixup_int8_static")));
        Assert.False(Sscd.IsInt8Manifest(Man("sscd_disc_mixup")));
        Assert.False(Sscd.IsInt8Manifest([1, 2, 3]));
    }

    [Fact]
    public async Task The_pipeline_model_is_fp32_even_with_an_int8_file_in_the_tools_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ixfp32-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(dir, "tools", "models"));
        var old = Environment.GetEnvironmentVariable("INDEXNODE_CONFIG_DIR"); Environment.SetEnvironmentVariable("INDEXNODE_CONFIG_DIR", dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "tools", "models", "sscd_disc_mixup_int8_static.onnx"), "x");
            File.WriteAllText(Models.PathOf(Models.SscdFp32), "fp32 stand-in"); // present: no download
            Assert.Equal(Models.PathOf(Models.SscdFp32), await Sscd.EnsureModelAsync(new NodeConfig(), new HttpClient(), default));
            Assert.DoesNotContain(Models.All, m => m.File.Contains("int8_static"));
        }
        finally { Environment.SetEnvironmentVariable("INDEXNODE_CONFIG_DIR", old); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Dry_redo_counts_and_changes_nothing()
    {
        var s = new FakeStore();
        await Done(s, "a", "sscd_disc_mixup_int8_static"); await Done(s, "b", "sscd_disc_mixup"); await Done(s, "c", null);
        var r = await Redo.Sscd.RunAsync(s, P, true, default);
        Assert.Equal(new Redo.SscdRedoResult(3, 1, 1, 0, 0, 1, 1), r);
        Assert.Equal("done", s.State(P.Job("sscd", "a")));
        Assert.False(s.Exists(GpuRouting.MarkerName(P, "sscd", "a")));
    }

    [Fact]
    public async Task Redo_resets_only_int8_jobs_to_pending_keeps_the_old_output_and_writes_a_fresh_marker()
    {
        var s = new FakeStore();
        await Done(s, "a", "sscd_disc_mixup_int8_static"); await Done(s, "b", "sscd_disc_mixup");
        await s.PutAsync(P.Video("a", "sscd/000000.f16"), [9, 9], null, null, false, default);
        await GpuRouting.WriteMarkerAsync(s, P, "sscd", "a", default, DateTimeOffset.UtcNow.AddHours(-5)); // a stale one from the first run
        var r = await Redo.Sscd.RunAsync(s, P, false, default);
        Assert.Equal(1, r.Reset); Assert.Equal(1, r.Marked);
        Assert.Equal("pending", s.State(P.Job("sscd", "a"))); Assert.Equal("done", s.State(P.Job("sscd", "b")));
        Assert.Null(s.LeaseOf(P.Job("sscd", "a")));
        Assert.True(s.Exists(P.Video("a", "sscd/000000.f16"))); Assert.True(s.Exists(P.Video("a", "sscd/manifest.json"))); // kept until the new ones are written
        var at = GpuRouting.ReadyAtOf(s[GpuRouting.MarkerName(P, "sscd", "a")].Data)!.Value;
        Assert.True(DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(1)); // fresh readyAt: CPU nodes wait the 30 minutes again
        Assert.Equal("2026-11-10T00:00:00Z", s[P.Job("sscd", "a")].Tags[Tags.Added]);
    }

    [Fact]
    public async Task Redo_leaves_a_leased_job_alone()
    {
        var s = new FakeStore();
        await Done(s, "a", "sscd_disc_mixup_int8_static", "pending");
        var lease = await s.AcquireLeaseAsync(P.Job("sscd", "a"), 60, default);
        var r = await Redo.Sscd.RunAsync(s, P, false, default);
        Assert.Equal(1, r.Leased); Assert.Equal(0, r.Reset);
        Assert.False(s.Exists(GpuRouting.MarkerName(P, "sscd", "a")));
        Assert.NotNull(lease);
    }

    [Fact]
    public void A_redo_gives_the_frames_their_pre_pass_spans_back()
    {
        var f = new List<ChunkFrame>
        {
            new(0, 0, 0, [[0, 3]]), new(1, 3, 3, [], 0), new(2, 6, 6, [[6, 9]]),
        };
        var again = FrameChunks.ResetSscdSpans(f);
        Assert.All(again, x => Assert.Null(x.Rep));
        Assert.Equal([[0.0, 3.0]], again[0].Spans); Assert.Equal([[3.0, 6.0]], again[1].Spans); Assert.Equal([[6.0, 9.0]], again[2].Spans);
        var fresh = new List<ChunkFrame> { new(0, 0, 0, [[0, 2]]) };
        Assert.Same(fresh, FrameChunks.ResetSscdSpans(fresh)); // nothing merged yet: untouched
    }
}
