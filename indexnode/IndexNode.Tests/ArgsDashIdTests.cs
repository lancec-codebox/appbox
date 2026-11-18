using Xunit;

namespace IndexNode.Tests;

public class ArgsDashIdTests
{
    [Fact]
    public void A_bare_double_dash_makes_the_rest_positional()
    {
        var a = Args.Parse(["set", "drama", "--", "--QfpuGhs0U"]);
        Assert.Equal(["set", "drama", "--QfpuGhs0U"], a.Positional);
    }

    [Fact]
    public void Video_equals_id_is_accepted_where_a_job_or_videos_id_is_taken()
    {
        var a = Args.Parse(["--video=--QfpuGhs0U"]);
        Assert.Equal("--QfpuGhs0U", a.Get("video")); Assert.Equal("--QfpuGhs0U", a.Get("job")); Assert.Equal("--QfpuGhs0U", a.Get("videos"));
    }

    [Fact]
    public async Task Classify_set_takes_a_dash_id_both_ways()
    {
        var s = new FakeStore(); var P = new Paths("index-test/");
        Assert.Contains("--QfpuGhs0U", await ClassifyCommand.SetAsync(s, P, "--QfpuGhs0U", "drama", null, DateTimeOffset.UtcNow, default));
    }
}
