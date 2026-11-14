using Xunit;

namespace IndexNode.Tests;

public class ColorGridTests
{
    static Rgb Solid(byte r, byte g, byte b, int w = 36, int h = 64) { var d = new byte[w * h * 3]; for (int i = 0; i < d.Length; i += 3) { d[i] = r; d[i + 1] = g; d[i + 2] = b; } return new Rgb(d, w, h); }

    [Fact]
    public void Identical_frames_are_distance_zero_and_different_colours_are_far()
    {
        var a = ColorGrid.Compute(Solid(200, 30, 30)); var b = ColorGrid.Compute(Solid(200, 30, 30)); var c = ColorGrid.Compute(Solid(30, 30, 200));
        Assert.Equal(ColorGrid.Length, a.Length);
        Assert.Equal(0, ColorGrid.Distance(a, b), 6);
        Assert.True(ColorGrid.Distance(a, c) > 0.3);
    }

    [Fact]
    public void A_frame_is_pruned_to_a_kept_frame_inside_the_look_back_only()
    {
        var p = new FramePruner(new FrameSettings(GridThreshold: 0.05, LookbackSeconds: 30));
        var f = ColorGrid.Compute(Solid(100, 100, 100)); var other = ColorGrid.Compute(Solid(250, 20, 20));
        Assert.Equal(-1, p.Offer(0, f)); p.Keep(0, 0, f);
        Assert.Equal(0, p.Offer(1, f));            // same picture a second later: stands for frame 0
        Assert.Equal(-1, p.Offer(2, other)); p.Keep(1, 2, other);
        Assert.Equal(0, p.Offer(29, f));           // still inside the 30 s look-back of frame 0
        Assert.Equal(-1, p.Offer(40, f));          // frame 0 is 40 s back: kept again
    }

    [Fact]
    public void Spans_of_a_kept_frame_merge_when_they_touch_and_stay_separate_otherwise()
    {
        var s = new List<double[]> { new[] { 0.0, 1.0 } };
        FramePruner.AddSpan(s, 1, 2); FramePruner.AddSpan(s, 5, 6); FramePruner.AddSpan(s, 6, 7);
        Assert.Equal([[0.0, 2.0], [5.0, 7.0]], s);
    }

    [Fact]
    public void The_sscd_pass_drops_rows_close_to_a_kept_row_within_the_window()
    {
        int dim = 4; float[][] v = [[1, 0, 0, 0], [0.99f, 0.1f, 0, 0], [0, 1, 0, 0], [1, 0, 0, 0]];
        var f16 = new byte[4 * dim * 2];
        for (int i = 0; i < 4; i++) { var n = MathF.Sqrt(v[i].Sum(x => x * x)); for (int d = 0; d < dim; d++) BitConverter.TryWriteBytes(f16.AsSpan((i * dim + d) * 2, 2), (Half)(v[i][d] / n)); }
        var (keep, rep) = FrameChunks.SscdPass([0, 1, 2, 100], f16, dim, 0.9, 30);
        Assert.Equal([0, 2, 3], keep); Assert.Equal([0, 0, 2, 3], rep); // row 3 equals row 0 but is 100 s later
    }
}
