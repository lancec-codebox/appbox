using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.76: the per-exit pace of YouTube jobs.</summary>
public class YoutubePaceTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 26, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Jobs_start_at_least_the_gap_apart_within_the_jitter()
    {
        var p = new YoutubePace(() => 0.5);   // no jitter: exactly the gap
        Assert.True(p.Allows(120, T0)); Assert.Equal(T0.AddSeconds(120), p.Next);
        Assert.False(p.Allows(120, T0.AddSeconds(60))); Assert.False(p.Allows(120, T0.AddSeconds(119)));
        Assert.True(p.Allows(120, T0.AddSeconds(120)));
        var lo = new YoutubePace(() => 0.0); lo.Allows(100, T0); Assert.Equal(T0.AddSeconds(80), lo.Next);    // -20%
        var hi = new YoutubePace(() => 1.0); hi.Allows(100, T0); Assert.Equal(T0.AddSeconds(120), hi.Next);   // +20%
    }

    [Fact]
    public void No_gap_never_holds_and_books_nothing()
    {
        var p = new YoutubePace();
        for (int i = 0; i < 5; i++) Assert.True(p.Allows(null, T0));
        Assert.True(p.Allows(0, T0)); Assert.Equal(DateTimeOffset.MinValue, p.Next);
    }

    [Fact]
    public void A_held_start_is_said_at_most_every_five_minutes()
    {
        var p = new YoutubePace(() => 0.5); var said = new List<string>();
        p.Allows(3600, T0);
        for (int s = 1; s <= 600; s += 30) p.Allows(3600, T0.AddSeconds(s), said.Add);
        Assert.Equal(2, said.Count); Assert.Contains("gap 3600 s", said[0]);
    }

    [Fact]
    public void The_control_setting_keeps_the_per_ip_cap_and_the_other_way_round()
    {
        var c = new ControlFile { Downloads = new DownloadsControl { MaxPerIp = 2, YoutubeGapSeconds = 90 } };
        var back = ControlFile.Parse(c.ToBytes());
        Assert.Equal(2, back.Downloads!.MaxPerIp); Assert.Equal(90, back.Downloads.YoutubeGapSeconds);
    }
}
