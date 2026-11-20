using System.Text.Json;

namespace IndexNode.Tests;

public class RefreshTests
{
    private static readonly Paths P = new("index-test/");
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static string Id(char c) => new string(c, 11);

    private sealed class Fake
    {
        public Dictionary<string, string> Listings = new();
        public Dictionary<string, string> WatchPages = new();
        public List<string> Calls = new();
        public Tools Tools() => new(new HttpClient()) { YtDlpOverride = (args, _, _) =>
        {
            var url = args[^1]; Calls.Add(string.Join(' ', args));
            if (url.Contains("watch?v=")) return Task.FromResult(new ProcResult(0, WatchPages.GetValueOrDefault(url.Split('=')[1], "NA\tNA") + "\n", ""));
            return Task.FromResult(new ProcResult(0, Listings.GetValueOrDefault(url, ""), ""));
        } };
    }

    private static string Lines(params string[] ids) => string.Join("\n", ids.Select(i => i + "\tNA\tNA")) + "\n";

    private static async Task<(JobContext Ctx, JobDescriptor Job)> Poll(FakeStore s, Fake f, JobDescriptor job, DateTimeOffset? now = null)
    {
        var name = P.Job("refresh", job.JobId);
        await s.PutAsync(name, job.ToBytes(), Tags.Job("refresh", "pending", job.AddedAt), null, false, default);
        var ctx = new JobContext { Job = job, Stage = "refresh", BlobName = name, LeaseId = "l", Worker = 1, Store = s, Paths = P, NodeId = "n", Token = default, ThreadShare = () => 1, Report = (_, _) => { } };
        await new RefreshStage(f.Tools(), s, () => now ?? Now).RunAsync(ctx);
        return (ctx, job);
    }

    private static JobDescriptor Playlist(string id, int backfill = -1, bool follow = false, string every = "1h") =>
        JobDescriptor.NewSource("playlist", id, "pl " + id, every, ["videos", "shorts"], follow, backfill, 360);

    private static string PlUrl(string id) => $"https://www.youtube.com/playlist?list={id}";

    [Theory]
    [InlineData("1h", 60)] [InlineData("30m", 30)] [InlineData("1d", 1440)] [InlineData(null, 60)]
    public void Every_parses(string? every, int minutes) => Assert.Equal(TimeSpan.FromMinutes(minutes), RefreshStage.ParseEvery(every));

    // ---------------------------------------------------------------- sources

    [Fact]
    public void Classify_reads_playlist_and_channel_urls_and_refuses_Watch_Later()
    {
        Assert.Equal(("playlist", "PLabc_-1"), Sources.Classify("https://www.youtube.com/playlist?list=PLabc_-1"));
        Assert.Equal(("channel", "UCaaaaaaaaaaaaaaaaaaaaaa"), Sources.Classify("https://www.youtube.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa/videos"));
        Assert.Null(Sources.Classify("https://www.youtube.com/@someone"));
        Assert.Throws<ArgumentException>(() => Sources.Classify("https://www.youtube.com/playlist?list=WL"));
    }

}
