using System.Text.Json;

namespace IndexNode.Tests;

public class WatchLaterTests
{
    private static readonly Paths P = new("index-test/");
    private const string Secret = "SECRET-COOKIE-VALUE-123";

    private static BrowserCookie C(string name, string domain, string value = Secret, bool http = false) => new(name, value, domain, "/", 1900000000, http, true);

    private static readonly BrowserCookie[] Signed = [C("SAPISID", ".youtube.com"), C("__Secure-3PSID", ".google.com", Secret + "B", true), C("other", ".example.com")];

    private const string Playlist = """
    {"id":"WL","entries":[
      {"id":"aaaaaaaaaa1","title":"one","channel_id":"UC1","channel":"Chan A"},
      {"id":"aaaaaaaaaa2","title":"two","channel_id":"UC1","channel":"Chan A"},
      {"id":"aaaaaaaaaa3","title":"three","channel_id":"UC2","channel":"Chan B"},
      {"id":"aaaaaaaaaa1","title":"dup","channel_id":"UC1","channel":"Chan A"},
      {"title":"no id"},
      {"id":"aaaaaaaaaa4","title":"four","uploader_id":"@c3","uploader":"Chan C"},
      {"id":"aaaaaaaaaa5","title":"five","channel_id":"UC2","channel":"Chan B"},
      {"id":"aaaaaaaaaa6","title":"six","channel_id":"UC2","channel":"Chan B"}]}
    """;

    [Fact]
    public void Playlist_parsing_keeps_ids_once_and_falls_back_to_the_uploader()
    {
        var v = WatchLater.ParsePlaylist(Playlist);
        Assert.Equal(["aaaaaaaaaa1", "aaaaaaaaaa2", "aaaaaaaaaa3", "aaaaaaaaaa4", "aaaaaaaaaa5", "aaaaaaaaaa6"], v.Select(x => x.Id));
        Assert.Equal(("@c3", "Chan C"), (v[3].ChannelId, v[3].ChannelName));
        Assert.Empty(WatchLater.ParsePlaylist("""{"id":"WL"}"""));
    }

    [Fact]
    public void Channels_are_counted_and_sorted_by_count()
    {
        var ch = WatchLater.CountChannels(WatchLater.ParsePlaylist(Playlist));
        Assert.Equal([("UC2", 3), ("UC1", 2), ("@c3", 1)], ch.Select(c => (c.ChannelId, c.Count)));
        Assert.Equal("Chan B", ch[0].Name);
    }

    [Fact]
    public void Netscape_file_format_httponly_and_session_cookies()
    {
        var text = WatchLater.ToNetscape([C("a", ".youtube.com", "v1", true), new BrowserCookie("s", "v2", "www.youtube.com", "/x", -1, false, false)]);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("# Netscape HTTP Cookie File", lines[0]);
        Assert.Equal("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1900000000\ta\tv1", lines[1]);
        Assert.Equal("www.youtube.com\tFALSE\t/x\tFALSE\t0\ts\tv2", lines[2]);
    }

    [Fact]
    public void Only_youtube_and_google_cookies_are_used_and_signed_in_needs_a_session_cookie()
    {
        Assert.Equal(2, WatchLater.ForYouTube(Signed).Count);
        Assert.True(WatchLater.LooksSignedIn(Signed));
        Assert.False(WatchLater.LooksSignedIn([C("VISITOR_INFO1_LIVE", ".youtube.com")]));
        Assert.False(WatchLater.LooksSignedIn([]));
    }

    private static async Task<(WatchLater.SeedResult R, FakeStore S, List<string> Args, string? CookieFile, string? CookieText, string? DirAtCall)> Seed(
        Func<IReadOnlyList<string>, ProcResult> yt, BrowserCookie[]? cookies = null)
    {
        var s = new FakeStore(); var args = new List<string>(); string? file = null, text = null, dir = null;
        var r = await WatchLater.WriteSeedsAsync(s, P, _ => Task.FromResult<IReadOnlyList<BrowserCookie>>(cookies ?? Signed),
            (a, _) => { args.AddRange(a); file = a[a.ToList().IndexOf("--cookies") + 1]; text = File.ReadAllText(file); dir = Path.GetDirectoryName(file); return Task.FromResult(yt(a)); },
            new DateTimeOffset(2026, 11, 13, 20, 0, 0, TimeSpan.Zero), default);
        return (r, s, args, file, text, dir);
    }

    [Fact]
    public async Task Seeds_are_written_with_dates_nothing_is_enqueued_and_the_cookie_file_is_gone()
    {
        var (r, s, args, file, text, dir) = await Seed(_ => new ProcResult(0, Playlist, ""));
        Assert.Equal(SourceState.Ok, r.State);
        Assert.Equal((6, 3), (r.Videos, r.Channels));
        Assert.Equal(["--flat-playlist", "-J"], args.Take(2));
        Assert.Equal("https://www.youtube.com/playlist?list=WL", args[^1]);
        Assert.Contains(Secret, text);            // the file had the cookies while yt-dlp ran
        Assert.False(File.Exists(file));          // and is deleted afterwards
        Assert.False(Directory.Exists(dir));
        Assert.Equal(["index-test/seeds/channels-20261113.json", "index-test/seeds/watch-later-20261113.json"], s.Blobs.Keys.OrderBy(k => k));
        Assert.DoesNotContain(s.Blobs.Keys, k => k.Contains("/jobs/"));
        var vids = JsonDocument.Parse(s["index-test/seeds/watch-later-20261113.json"].Data).RootElement.GetProperty("videos");
        Assert.Equal(6, vids.GetArrayLength());
        Assert.Equal("UC1", vids[0].GetProperty("channelId").GetString());
        var chans = JsonDocument.Parse(s["index-test/seeds/channels-20261113.json"].Data).RootElement.GetProperty("channels");
        Assert.Equal(["UC2", "UC1", "@c3"], chans.EnumerateArray().Select(c => c.GetProperty("channelId").GetString()!));
        Assert.Equal(3, chans[0].GetProperty("count").GetInt32());
        Assert.DoesNotContain(Secret, System.Text.Encoding.UTF8.GetString(s["index-test/seeds/channels-20261113.json"].Data));
    }

    [Fact]
    public async Task The_cookie_file_is_deleted_when_yt_dlp_fails_and_the_error_never_shows_a_cookie()
    {
        var (_, _, _, _, _, _) = (0, 0, 0, 0, 0, 0);
        string? file = null; var s = new FakeStore();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => WatchLater.WriteSeedsAsync(s, P, _ => Task.FromResult<IReadOnlyList<BrowserCookie>>(Signed),
            (a, _) => { file = a[a.ToList().IndexOf("--cookies") + 1]; return Task.FromResult(new ProcResult(1, "", "ERROR: HTTP Error 500 " + Secret)); }, DateTimeOffset.UtcNow, default));
        Assert.NotNull(file);
        Assert.False(File.Exists(file));
        Assert.DoesNotContain(Secret, Redact.Sas(ex.Message));
        Assert.Empty(s.Blobs);
    }

    [Fact]
    public async Task Signed_out_profiles_run_nothing()
    {
        int calls = 0;
        var (r, s, _, _, _, _) = await Seed(_ => { calls++; return new ProcResult(0, Playlist, ""); }, [C("VISITOR_INFO1_LIVE", ".youtube.com")]);
        Assert.Equal(SourceState.SignedOut, r.State);
        Assert.Equal(0, calls); Assert.Empty(s.Blobs);
        var (r2, _, _, _, _, _) = await Seed(_ => new ProcResult(1, "", "ERROR: Sign in to see this playlist"));
        Assert.Equal(SourceState.SignedOut, r2.State); // cookies expired
    }

    [Fact]
    public void Cookies_never_reach_the_log()
    {
        Redact.Register(Secret);
        try
        {
            Assert.DoesNotContain(Secret, Redact.Sas("failed with " + Secret + " here"));
            Assert.DoesNotContain("abc123", Redact.Sas("Cookie: SAPISID=abc123; SID=x"));
            Assert.DoesNotContain("zzz999", Redact.Sas("x\n#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1900000000\tSID\tzzz999\ny"));
        }
        finally { Redact.ClearKnown(); }
    }

    [Fact]
    public async Task Cookies_come_from_the_browser_over_CDP_Storage_getCookies()
    {
        using var server = new FakeCdpServer { Cookies = [new { name = "SAPISID", value = "v1", domain = ".youtube.com", path = "/", expires = 1900000000.5, httpOnly = true, secure = true }] }.Start();
        var cfg = new BrowserConfig { Port = server.Port, ProfileDir = Path.Combine(Path.GetTempPath(), "cdp-test-profile") };
        await using var b = await CdpBrowser.OpenAsync(cfg, new HttpClient(), default);
        var c = Assert.Single(await b.GetCookiesAsync(default));
        Assert.Equal(("SAPISID", "v1", ".youtube.com", true, true), (c.Name, c.Value, c.Domain, c.HttpOnly, c.Secure));
        Assert.Contains("Storage.getCookies", server.Methods);
        Assert.DoesNotContain(server.Methods, m => m.StartsWith("Page.navigate") || m.StartsWith("Runtime.evaluate")); // no page is driven
    }

    [Fact]
    public async Task Signed_in_state_comes_from_the_library_page()
    {
        var page = new FakePage(_ => JsonDocument.Parse("true").RootElement.Clone());
        Assert.True(await WatchLater.IsSignedInAsync(page, default));
        Assert.Equal([WatchLater.LibraryUrl], page.Navigated);
    }

    [Fact]
    public void Signed_out_message_names_the_login_command() =>
        Assert.Equal("Watch Later: profile not signed in, run `indexnode browser login`", WatchLater.SignedOutMessage);

    private sealed class FakePage(Func<string, JsonElement> eval) : IPage
    {
        public List<string> Navigated { get; } = [];
        public Task NavigateAsync(string url, CancellationToken ct) { Navigated.Add(url); return Task.CompletedTask; }
        public Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct) => Task.FromResult(eval(expression));
        public Task CloseAsync() => Task.CompletedTask;
    }
}
