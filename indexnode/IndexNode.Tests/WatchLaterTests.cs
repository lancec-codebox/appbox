using System.Text.Json;

namespace IndexNode.Tests;

public class WatchLaterTests
{
    // hand-written, made-up ids (11 chars); not real data
    private const string Initial = """
    {"contents":{"twoColumnBrowseResultsRenderer":{"tabs":[{"tabRenderer":{"content":{"sectionListRenderer":{"contents":[{"itemSectionRenderer":{"contents":[
      {"playlistVideoListRenderer":{"contents":[
        {"playlistVideoRenderer":{"videoId":"aaaaaaaaaa1","title":{"runs":[{"text":"one"}]}}},
        {"playlistVideoRenderer":{"videoId":"aaaaaaaaaa2"}},
        {"playlistVideoRenderer":{"videoId":"aaaaaaaaaa1"}},
        {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"TOKEN"}}}}
      ]}}]}}]}}}}]}}}
    """;

    private const string Continuation = """
    {"onResponseReceivedActions":[{"appendContinuationItemsAction":{"continuationItems":[
      {"playlistVideoRenderer":{"videoId":"bbbbbbbbbb1"}},
      {"playlistVideoRenderer":{"videoId":"aaaaaaaaaa2"}},
      {"playlistVideoRenderer":{"videoId":"short"}},
      {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"T2"}}}}
    ]}}]}
    """;

    [Fact]
    public void Parses_ids_from_initial_data_in_order_without_duplicates()
    {
        Assert.Equal(["aaaaaaaaaa1", "aaaaaaaaaa2"], WatchLater.ParseIds(Initial));
    }

    [Fact]
    public void Continuation_items_are_found_and_merged_and_bad_ids_dropped()
    {
        var ids = WatchLater.ParseIds(Initial);
        foreach (var i in WatchLater.ParseIds(Continuation)) if (!ids.Contains(i)) ids.Add(i);
        Assert.Equal(["aaaaaaaaaa1", "aaaaaaaaaa2", "bbbbbbbbbb1"], ids);
        Assert.Equal(["bbbbbbbbbb1", "aaaaaaaaaa2"], WatchLater.ParseIds(Continuation));
    }

    [Fact]
    public void Empty_or_null_initial_data_gives_no_ids()
    {
        Assert.Empty(WatchLater.ParseIds("null"));
        Assert.Empty(WatchLater.ParseIds(""));
        Assert.Empty(WatchLater.ParseIds("""{"alerts":[{"text":"sign in"}]}"""));
    }

    [Fact]
    public void Newer_layout_video_lockups_count_but_other_lockups_do_not()
    {
        var json = """{"x":[{"lockupViewModel":{"contentId":"cccccccccc1","contentType":"LOCKUP_CONTENT_TYPE_VIDEO"}},{"lockupViewModel":{"contentId":"PLxxxxxxxxx","contentType":"LOCKUP_CONTENT_TYPE_PLAYLIST"}}]}""";
        Assert.Equal(["cccccccccc1"], WatchLater.ParseIds(json));
    }

    [Fact]
    public void Id_from_href()
    {
        Assert.Equal("aaaaaaaaaa1", WatchLater.IdFromHref("https://www.youtube.com/watch?v=aaaaaaaaaa1&list=WL&index=1"));
        Assert.Equal("aaaaaaaaaa1", WatchLater.IdFromHref("/watch?list=WL&v=aaaaaaaaaa1"));
        Assert.Null(WatchLater.IdFromHref("/playlist?list=WL"));
    }

    private sealed class FakePage(Func<string, JsonElement> eval) : IPage
    {
        public readonly List<string> Navigated = new();
        public bool Closed;
        public Task NavigateAsync(string url, CancellationToken ct) { Navigated.Add(url); return Task.CompletedTask; }
        public Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct) => Task.FromResult(eval(expression));
        public Task CloseAsync() { Closed = true; return Task.CompletedTask; }
    }

    private static JsonElement Str(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();
    private static JsonElement Bool(bool b) => JsonDocument.Parse(b ? "true" : "false").RootElement.Clone();
    private static string Links(params string[] ids) => JsonSerializer.Serialize(ids.Select(i => "https://www.youtube.com/watch?v=" + i + "&list=WL"));

    [Fact]
    public async Task Scrolls_until_no_new_items_load()
    {
        // each scroll reveals more; the list stops growing after the 3rd call, and three quiet rounds end it
        var steps = new Queue<string>(
        [
            Links("aaaaaaaaaa1", "aaaaaaaaaa2"),
            Links("aaaaaaaaaa1", "aaaaaaaaaa2", "bbbbbbbbbb1"),
            Links("aaaaaaaaaa1", "aaaaaaaaaa2", "bbbbbbbbbb1", "bbbbbbbbbb2"),
        ]);
        string last = "";
        int scrolls = 0;
        var page = new FakePage(expr =>
        {
            if (expr == WatchLater.InitialDataExpression) return Str(Initial);
            scrolls++;
            if (steps.Count > 0) last = steps.Dequeue();
            return Str(last);
        });
        var ids = await WatchLater.ReadIdsAsync(page, default);
        Assert.Equal(["aaaaaaaaaa1", "aaaaaaaaaa2", "bbbbbbbbbb1", "bbbbbbbbbb2"], ids);
        Assert.Equal(6, scrolls); // 3 growing rounds + 3 quiet ones
        Assert.Equal([WatchLater.ListUrl], page.Navigated);
    }

    [Fact]
    public async Task Stops_at_the_round_limit_even_if_the_list_keeps_growing()
    {
        int n = 0;
        var page = new FakePage(expr => expr == WatchLater.InitialDataExpression ? Str("null") : Str(Links(Enumerable.Range(0, ++n).Select(i => "id" + i.ToString("D9")).ToArray())));
        var ids = await WatchLater.ReadIdsAsync(page, default, maxRounds: 5);
        Assert.Equal(5, ids.Count);
    }

    [Fact]
    public async Task Signed_in_state_comes_from_the_library_page()
    {
        var page = new FakePage(_ => Bool(true));
        Assert.True(await WatchLater.IsSignedInAsync(page, default));
        Assert.Equal([WatchLater.LibraryUrl], page.Navigated);
        Assert.False(await WatchLater.IsSignedInAsync(new FakePage(_ => Bool(false)), default));
        Assert.False(await WatchLater.IsSignedInAsync(new FakePage(_ => default), default)); // no value: not signed in
    }

    [Fact]
    public async Task Enqueue_adds_only_ids_without_a_job_in_any_state()
    {
        var p = new Paths("index-test/");
        var s = new FakeStore();
        await s.AddJob(p, "download", "aaaaaaaaaa1", "2026-11-13T19:00:00Z");                 // pending
        await s.AddJob(p, "download", "aaaaaaaaaa2", "2026-11-13T19:00:00Z");
        await s.SetTagsAsync(p.Job("download", "aaaaaaaaaa2"), Tags.Job("download", "done", "2026-11-13T19:00:00Z"), null, default); // done
        await s.AddJob(p, "download", "aaaaaaaaaa3", "2026-11-13T19:00:00Z");
        await s.SetTagsAsync(p.Job("download", "aaaaaaaaaa3"), Tags.Job("download", "failed", "2026-11-13T19:00:00Z"), null, default); // failed

        var c = await WatchLater.EnqueueAsync(s, p, ["aaaaaaaaaa1", "aaaaaaaaaa2", "aaaaaaaaaa3", "bbbbbbbbbb1", "bbbbbbbbbb2", "bbbbbbbbbb1"], 360, default);
        Assert.Equal(new WatchLater.Counts(5, 2, 3), c);
        var j = s.Job(p.Job("download", "bbbbbbbbbb1"));
        Assert.Equal(360, j.MaxHeight);
        Assert.Equal("https://www.youtube.com/watch?v=bbbbbbbbbb1", j.Url);
        Assert.Equal("pending", s.State(p.Job("download", "bbbbbbbbbb1")));
        Assert.Equal("done", s.State(p.Job("download", "aaaaaaaaaa2"))); // untouched

        var again = await WatchLater.EnqueueAsync(s, p, ["bbbbbbbbbb1", "bbbbbbbbbb2"], 480, default);
        Assert.Equal(new WatchLater.Counts(2, 0, 2), again);
    }

    [Theory]
    [InlineData("6h", 6 * 3600)]
    [InlineData("30m", 1800)]
    [InlineData("1d", 86400)]
    [InlineData("90s", 90)]
    public void Source_interval_parses(string s, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), WatchLater.ParseInterval(s));

    [Fact]
    public void Bad_interval_is_rejected() => Assert.Throws<ArgumentException>(() => WatchLater.ParseInterval("soon"));

    [Fact]
    public void Signed_out_message_names_the_login_command() =>
        Assert.Equal("Watch Later: profile not signed in, run `indexnode browser login`", WatchLater.SignedOutMessage);

    [Fact]
    public async Task Source_state_lands_in_the_node_registry()
    {
        var s = new FakeStore(); var p = new Paths("index-test/");
        var e = new NodeEngine(s, p, new EngineOptions { NodeId = "n-1", Stages = ["download"], Jobs = new() { ["download"] = 1 }, Once = true,
            PollInterval = TimeSpan.FromMilliseconds(20), ControlInterval = TimeSpan.FromMilliseconds(20), RegistryInterval = TimeSpan.FromSeconds(30) }, new Dictionary<string, IStageHandler>());
        e.SetSource("watch-later", "signed-out");
        await e.RunAsync(default);
        var info = NodeInfo.Parse(s[p.Node("n-1")].Data)!;
        Assert.Equal("signed-out", info.Sources!["watch-later"]);
    }
}
