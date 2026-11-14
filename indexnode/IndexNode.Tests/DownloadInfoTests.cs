using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndexNode.Tests;

public class DownloadInfoTests
{
    // a trimmed info.json of a drama with an original zh track and a dubbed en track (what yt-dlp writes after merging av01 + opus)
    internal const string Info = """
    {
      "id": "abcdefghij1", "height": 480, "duration": 5400.5, "playable_in_embed": true,
      "format_id": "398+251-0", "vcodec": "av01.0.04M.08", "acodec": "opus",
      "requested_formats": [
        { "format_id": "398", "vcodec": "av01.0.04M.08", "acodec": "none", "height": 480 },
        { "format_id": "251-0", "vcodec": "none", "acodec": "opus", "language": "zh-Hans", "format_note": "Chinese (China), original (default), low" }
      ],
      "formats": [
        { "format_id": "398", "vcodec": "av01.0.04M.08", "acodec": "none" },
        { "format_id": "251-0", "vcodec": "none", "acodec": "opus", "language": "zh-Hans", "format_note": "Chinese (China), original (default), low" },
        { "format_id": "251-1", "vcodec": "none", "acodec": "opus", "language": "en-US", "format_note": "English (US), dubbed-auto, low" },
        { "format_id": "140-1", "vcodec": "none", "acodec": "mp4a.40.2", "language": "en-US", "format_note": "English (US), dubbed-auto, medium" },
        { "format_id": "sb0", "vcodec": "none", "acodec": "none" }
      ]
    }
    """;

    [Fact]
    public void The_original_language_audio_is_recorded_and_the_dub_is_only_listed()
    {
        var f = DownloadedFormats.FromInfo(JsonNode.Parse(Info)!.AsObject());
        Assert.Equal("av01.0.04M.08", f.VCodec);
        Assert.Equal("opus", f.ACodec);
        Assert.Equal("398+251-0", f.FormatId);
        Assert.Equal("zh-Hans", f.AudioLanguage);
        Assert.Equal("Chinese (China), original (default)", f.AudioNote);
        Assert.True(f.Embeddable);
        Assert.Equal(480, f.Height);
        Assert.Equal(5400.5, f.Duration);
        Assert.Equal([("zh-Hans", "Chinese (China), original (default)"), ("en-US", "English (US), dubbed-auto")], f.AudioTracks.Select(t => (t.Language, t.Note)));
    }

    [Fact]
    public void A_muxed_single_format_and_missing_fields_do_not_break_it()
    {
        var f = DownloadedFormats.FromInfo(JsonNode.Parse("""{"format_id":"18","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360}""")!.AsObject());
        Assert.Equal(("avc1.42001E", "mp4a.40.2", "18"), (f.VCodec, f.ACodec, f.FormatId));
        Assert.Null(f.AudioLanguage); Assert.Null(f.AudioNote); Assert.False(f.Embeddable); Assert.Empty(f.AudioTracks);
    }

    [Theory]
    [InlineData("English (US), original (default), low", "English (US), original (default)")]
    [InlineData("Chinese, dubbed-auto, ultralow", "Chinese, dubbed-auto")]
    [InlineData("original (default)", "original (default)")]
    [InlineData(null, null)]
    public void Track_note_drops_only_the_quality_word(string? raw, string? want) => Assert.Equal(want, DownloadedFormats.TrackNote(raw));

    [Fact]
    public void The_selector_sorts_by_resolution_first_then_takes_compact_opus()
    {
        Assert.Equal(["-S", "res:480", "-f", "bv*+ba[acodec=opus][abr<=80]/bv*+ba/b"], YtDlp.FormatArgs(480, null));
        Assert.Equal(["-S", "res:360", "-f", "bv*+ba[acodec=opus][abr<=80]/bv*+ba/b"], YtDlp.FormatArgs(360, ""));
        Assert.Equal(["-f", "worst"], YtDlp.FormatArgs(480, "worst")); // an explicit format wins and there is no -S
        Assert.Equal("worst", YtDlp.Selector(480, "worst"));
    }

    [Fact]
    public void With_240p_av01_480p_vp9_480p_avc1_the_arguments_put_resolution_before_codec()
    {
        // yt-dlp does the choosing: -S res:480 ranks 480p above 240p whatever the codec, then its default codec order (av01 > vp9 > avc1) breaks the tie.
        // So the arguments must carry no height or codec filter that could rank the 240p AV1 first.
        var args = YtDlp.FormatArgs(480, null);
        Assert.Equal("res:480", args[Array.IndexOf(args, "-S") + 1]);
        var f = args[Array.IndexOf(args, "-f") + 1];
        Assert.DoesNotContain("height", f);
        Assert.DoesNotContain("vcodec", f);
    }

    [Fact]
    public void Js_runtime_is_configured_else_node_else_deno()
    {
        Assert.Equal("deno:/x/deno", Tools.ChooseJsRuntime("deno:/x/deno", _ => "/usr/bin/node"));
        Assert.Equal("node:/usr/bin/node", Tools.ChooseJsRuntime(null, n => n is "node" or "deno" ? "/usr/bin/" + n : null));
        Assert.Equal("deno:/usr/bin/deno", Tools.ChooseJsRuntime(" ", n => n == "deno" ? "/usr/bin/deno" : null));
        Assert.Null(Tools.ChooseJsRuntime(null, _ => null));
        Assert.Null(Tools.ChooseJsRuntime("none", _ => "/usr/bin/node"));
    }

    [Fact]
    public async Task Yt_dlp_gets_the_js_runtime_flag_and_tools_status_says_which()
    {
        var seen = new List<string>();
        var cfg = new NodeConfig { Ytdlp = new YtDlpConfig { JsRuntime = "node:/opt/node" } };
        var tools = new Tools(new HttpClient(), cfg) { YtDlpOverride = (a, _, _) => { seen.AddRange(a); return Task.FromResult(new ProcResult(0, "", "")); } };
        await tools.RunYtDlpAsync(["--version"], default);
        Assert.Equal(["--js-runtimes", "node:/opt/node", "--version"], seen);
        Assert.Contains(tools.Status(), l => l.StartsWith("js runtime:") && l.Contains("node:/opt/node") && l.Contains("configured"));
        Assert.Contains(new Tools(new HttpClient(), new NodeConfig { Ytdlp = new YtDlpConfig { JsRuntime = "none" } }).Status(), l => l.Contains("NONE"));
    }

    [Fact]
    public void Ffmpeg_is_pinned_with_a_version_and_sha256_per_platform()
    {
        foreach (var (win, arm) in new[] { (true, false), (false, false), (false, true) })
        {
            var a = Tools.FfmpegFor(win, arm);
            Assert.Contains("autobuild-2026-11-13-12-43", a.Url);
            Assert.DoesNotContain("latest", a.Url);
            Assert.Matches("^[0-9a-f]{64}$", a.Sha256);
            Assert.Equal(win, a.Zip);
        }
        Assert.Contains(Tools.FfmpegVersion, Tools.FfmpegFor(false, false).Url);
    }

    [Fact]
    public async Task A_download_with_the_wrong_sha256_is_refused_and_left_nowhere()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fetch-" + Guid.NewGuid().ToString("N")[..8]);
        var http = new HttpClient(new FakeHandler("hello"));
        var dest = Path.Combine(dir, "f.bin");
        const string good = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
        await Assert.ThrowsAsync<ChecksumMismatch>(() => Fetch.VerifiedAsync(http, "http://x/f", dest, new string('0', 64), default));
        Assert.False(File.Exists(dest)); Assert.False(File.Exists(dest + ".part"));
        await Fetch.VerifiedAsync(http, "http://x/f", dest, good, default);
        Assert.Equal("hello", File.ReadAllText(dest));
    }

    private sealed class FakeHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Fact]
    public void Follow_up_process_job_has_the_new_default_steps_and_no_compress_block()
    {
        var j = JobDescriptor.NewProcess("abcdefghij1");
        Assert.Equal(["frames", "sscd", "clip", "transcribe", "ocrsubs"], j.Steps);
        Assert.Null(j.Compress);
        Assert.DoesNotContain("compress", System.Text.Encoding.UTF8.GetString(j.ToBytes()));
    }

    [Fact]
    public void Compress_still_parses_when_a_job_asks_for_it()
    {
        var j = JobDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes("""{"v":1,"stage":"process","jobId":"x","steps":["frames","compress"],"compress":{"codec":"av1","crf":30,"preset":6,"audioKbps":64,"keepOriginal":true}}"""));
        Assert.Contains("compress", j.Steps!);
        Assert.Equal((30, true), (j.Compress!.Crf, j.Compress.KeepOriginal));
    }
}
