namespace IndexNode.Tests;

public class RedactTests
{
    [Fact]
    public void Sig_is_redacted_in_urls_and_messages()
    {
        var s = "GET https://a.blob.core.windows.net/c/b?sv=2021&sig=ab%2Bcd%3D&se=2030 failed";
        var r = Redact.Sas(s);
        Assert.DoesNotContain("ab%2Bcd", r);
        Assert.Contains("sig=REDACTED&se=2030", r);
        Assert.DoesNotContain("SIG=xyz", Redact.Sas("x?SIG=xyz"));
    }

    [Fact]
    public void Known_sas_is_removed_even_when_unusual()
    {
        Assert.Equal("url REDACTED end", Redact.Sas("url SECRET end", "SECRET"));
    }

    [Fact]
    public void Log_redacts()
    {
        var w = new StringWriter();
        var old = Log.Out; Log.Out = w;
        try { Log.Info("fetching https://x/y?sig=TOPSECRET"); } finally { Log.Out = old; }
        Assert.DoesNotContain("TOPSECRET", w.ToString());
    }
}

public class FrameMathTests
{
    [Fact]
    public void Plan_splits_evenly_and_covers_the_whole_duration()
    {
        var p = FrameMath.Plan(100, 4);
        Assert.Equal(4, p.Count);
        Assert.Equal((0.0, 25.0), p[0]);
        Assert.Equal((75.0, 100.0), p[3]);
        for (int i = 1; i < p.Count; i++) Assert.Equal(p[i - 1].End, p[i].Start);
    }

    [Fact]
    public void Plan_never_makes_segments_shorter_than_the_minimum_and_has_at_least_one()
    {
        Assert.Single(FrameMath.Plan(5, 16));
        Assert.Equal(3, FrameMath.Plan(35, 16).Count);
        Assert.Single(FrameMath.Plan(100, 0));
    }

    [Fact]
    public void Absolute_time_is_segment_start_plus_pts_time()
    {
        var plan = FrameMath.Plan(7200, 16); // 2 h in 16 segments of 450 s
        Assert.Equal(450 * 3 + 12.5, FrameMath.AbsoluteTime(plan[3].Start, 12.5), 6);
    }

    [Fact]
    public void Metadata_parser_reads_pts_time_and_scene_score()
    {
        var text = "frame:0    pts:0       pts_time:0\nlavfi.scene_score=0.000000\nframe:1    pts:25      pts_time:1.04167\nlavfi.scene_score=0.512300\nframe:2    pts:50      pts_time:2.5\n";
        var m = FrameMath.ParseMetadata(text);
        Assert.Equal(3, m.Count);
        Assert.Equal(1.04167, m[1].Pts, 5);
        Assert.Equal("scene", FrameMath.Kind(m[1].Scene));
        Assert.Equal("tick", FrameMath.Kind(m[0].Scene));
        Assert.Equal("tick", FrameMath.Kind(m[2].Scene));
    }

    [Fact]
    public void Select_filter_scales_to_288_and_uses_scene_threshold_and_one_second_tick()
    {
        var f = FrameMath.SelectFilter();
        Assert.Contains("scale=288:-2", f);
        Assert.Contains("gt(scene,0.3)", f);
        Assert.Contains("gte(t-prev_selected_t,1)", f);
    }
}

public class SscdMathTests
{
    [Fact]
    public void Target_size_puts_the_short_side_at_288()
    {
        Assert.Equal((512, 288), Sscd.TargetSize(288, 162));
        Assert.Equal((288, 512), Sscd.TargetSize(162, 288));
    }

    [Fact]
    public void L2_normalise_and_f16_layout()
    {
        var v = new float[512]; v[0] = 3; v[1] = 4;
        Sscd.L2Normalize(v);
        Assert.Equal(0.6f, v[0], 4); Assert.Equal(0.8f, v[1], 4);
        var bytes = Sscd.ToF16Bytes([v, v]);
        Assert.Equal(2 * 512 * 2, bytes.Length);
        Assert.Equal((Half)0.6f, BitConverter.ToHalf(bytes, 0));
    }
}

public class ProtocolTests
{
    [Fact]
    public void Job_id_is_the_youtube_id_or_a_hash()
    {
        Assert.Equal("dQw4w9WgXcQ", Protocol.JobIdFor("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=3"));
        Assert.Equal("dQw4w9WgXcQ", Protocol.JobIdFor("https://youtu.be/dQw4w9WgXcQ"));
        var h = Protocol.JobIdFor("https://example.com/v/1");
        Assert.StartsWith("u", h);
        Assert.Equal(h, Protocol.JobIdFor("https://example.com/v/1"));
        Assert.NotEqual(h, Protocol.JobIdFor("https://example.com/v/2"));
    }

    [Fact]
    public void Effective_control_is_the_stricter_of_mode_and_stage()
    {
        var c = new ControlFile { Mode = "run", Stages = new() { ["download"] = "drain", ["process"] = "run" } };
        Assert.Equal(Level.Drain, c.Effective("download"));
        Assert.Equal(Level.Run, c.Effective("process"));
        c.Mode = "stop";
        Assert.Equal(Level.Stop, c.Effective("process"));
        c.Mode = "drain"; c.Stages!["process"] = "stop";
        Assert.Equal(Level.Stop, c.Effective("process"));
        Assert.Equal(Level.Drain, c.Effective("download"));
        Assert.Equal(Level.Run, new ControlFile().Effective("download"));
        Assert.Equal(Level.Run, ControlFile.Parse(null).Effective("process"));
    }

    [Fact]
    public void Node_older_than_5_minutes_is_offline()
    {
        var now = DateTimeOffset.UtcNow;
        var n = new NodeInfo { State = "working", LastSeen = Protocol.Utc(now.AddMinutes(-6)) };
        Assert.Equal("offline", n.EffectiveState(now));
        n.LastSeen = Protocol.Utc(now.AddMinutes(-1));
        Assert.Equal("working", n.EffectiveState(now));
        n.State = "offline";
        Assert.Equal("offline", n.EffectiveState(now));
    }

    [Fact]
    public void Jobs_option_parses_per_stage_values()
    {
        var j = NodeConfig.ParseJobs("process=2,download=1");
        Assert.Equal(2, j["process"]); Assert.Equal(1, j["download"]);
        Assert.Throws<ArgumentException>(() => NodeConfig.ParseJobs("bogus=2"));
        Assert.Equal(1, new NodeConfig().JobsFor("download"));
        Assert.Equal(1, new NodeConfig().JobsFor("process"));
    }
}

public class YtDlpTests
{
    [Theory]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you’re not a bot. Use --cookies", FailureKind.BotBlock)]
    [InlineData("ERROR: Unable to download: HTTP Error 429: Too Many Requests", FailureKind.BotBlock)]
    [InlineData("ERROR: [youtube] abc: Video unavailable. This video has been removed by the uploader", FailureKind.Permanent)]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access", FailureKind.Permanent)]
    [InlineData("ERROR: [youtube] abc: Join this channel to get access to members-only content", FailureKind.Permanent)]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm your age. This video may be inappropriate for some users", FailureKind.Permanent)]
    [InlineData("ERROR: [youtube] abc: The uploader has not made this video available in your country", FailureKind.Permanent)]
    [InlineData("ERROR: [youtube] jNQXAC9IJRY: This video is unavailable", FailureKind.Transient)] // also what a blocked datacenter IP gets: three strikes, not permanent
    [InlineData("ERROR: Unsupported URL: https://example.com/x", FailureKind.Permanent)]
    [InlineData("ERROR: unable to download video data: <urlopen error timed out>", FailureKind.Transient)]
    public void Classifies_errors(string stderr, FailureKind expected) => Assert.Equal(expected, YtDlp.Classify(stderr));

    [Fact]
    public void Default_selector_matches_the_protocol()
    {
        Assert.Equal("bv*[height<=480][ext=mp4]+ba[ext=m4a]/b[height<=480]/b", YtDlp.Selector(480, null));
        Assert.Equal("worst", YtDlp.Selector(480, "worst"));
    }

    [Fact]
    public void Single_video_urls_skip_playlist_expansion()
    {
        Assert.True(YtDlp.IsSingleVideoUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));
        Assert.False(YtDlp.IsSingleVideoUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL123"));
        Assert.False(YtDlp.IsSingleVideoUrl("https://www.youtube.com/playlist?list=PL123"));
    }

    [Fact]
    public async Task Download_gate_spaces_downloads_20_to_40_seconds_apart()
    {
        var waits = new List<TimeSpan>();
        var g = new DownloadGate((t, _) => { waits.Add(t); return Task.CompletedTask; }, () => 0.5);
        await g.WaitTurnAsync(default); // first one goes at once
        await g.WaitTurnAsync(default);
        await g.WaitTurnAsync(default);
        Assert.Equal(2, waits.Count);
        Assert.InRange(waits[0].TotalSeconds, 29, 30.1);
        Assert.InRange(waits[1].TotalSeconds, 59, 60.1); // gaps hold across all workers
    }
}

public class NexisTouchTests
{
    [Fact]
    public async Task Touch_sets_only_last_access_and_keeps_every_other_tag()
    {
        var s = new FakeStore();
        await s.PutAsync("index/videos/x/video.mp4", [1], new Dictionary<string, string>
        {
            ["ghostd_state"] = "ghost", ["ghostd_block_prefix"] = "gd+1+", ["ghostd_size"] = "9", ["idx_kind"] = "keep",
        }, null, false, default);
        await NexisAccess.TouchAsync(s, "index/videos/x/video.mp4", default);
        var t = s["index/videos/x/video.mp4"].Tags;
        Assert.Equal("ghost", t["ghostd_state"]);
        Assert.Equal("gd+1+", t["ghostd_block_prefix"]);
        Assert.Equal("9", t["ghostd_size"]);
        Assert.Equal("keep", t["idx_kind"]);
        Assert.True(Math.Abs((DateTime.UtcNow - DateTime.Parse(t["ghostd_last_access"], null, System.Globalization.DateTimeStyles.RoundtripKind)).TotalSeconds) < 10);
        Assert.Equal(5, t.Count);
    }
}
