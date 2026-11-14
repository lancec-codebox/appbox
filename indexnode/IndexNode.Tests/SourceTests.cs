namespace IndexNode.Tests;

public class SourceTests
{
    [Fact]
    public void Cover_crop_is_the_9x16_window_as_fractions()
    {
        var c = Publish.CoverCrop(854, 480, 600, 200); // 270x480 window centred on x=600
        Assert.Equal(465d / 854, c[0], 4); Assert.Equal(0, c[1]); Assert.Equal(270d / 854, c[2], 4); Assert.Equal(1, c[3]);
        Assert.Equal(0, Publish.CoverCrop(854, 480, 10, 10)[0], 4); // clamped inside the frame
    }

    [Fact]
    public void Head_crop_matches_the_cast_stage_and_clamps()
    {
        var (crop, focus) = Publish.HeadCrop([400, 100, 40, 50], 854, 480);
        Assert.InRange(crop[0], 0, 1); Assert.True(crop[0] + crop[2] <= 1.0000001 && crop[1] + crop[3] <= 1.0000001);
        Assert.Equal(0.5, focus[0], 2); // face centred horizontally when unclamped
        var (c2, f2) = Publish.HeadCrop([0, 0, 40, 50], 854, 480);
        Assert.Equal(0, c2[0]); Assert.Equal(0, c2[1]); Assert.True(f2[0] < 0.5);
    }

    [Fact]
    public void Source_shape()
    {
        var s = Publish.Source("abc", 12.346, [0.1, 0.2, 0.3, 0.4], [0.5, 0.25]);
        Assert.Equal("abc", (string)s["upload"]!); Assert.Equal(12.35, (double)s["t"]!, 2);
        Assert.Equal(4, s["crop"]!.AsArray().Count); Assert.Equal(2, s["focus"]!.AsArray().Count);
    }
}
