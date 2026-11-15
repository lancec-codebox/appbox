using Xunit;

namespace IndexNode.Tests;

public class HeadWindowTests
{
    [Fact]
    public void The_window_that_just_fills_the_frame_width_does_not_throw_on_a_room_of_minus_5e_14()
    {
        // 360x640, a 262.47 x 239.78 face (found on stored dramas): the window is shrunk to the frame, cw * (360 / cw) is a hair over 360 and the room 360 - cw is -5.7e-14
        var (x, y, w, h) = CastStage.HeadWindow(50, 100, 262.46511484611085, 239.77761006903722, 360, 640, 1.25);
        Assert.InRange(x, 0, 360); Assert.InRange(y, 0, 640);
        Assert.True(x + w <= 360 + 1e-9 && y + h <= 640 + 1e-9);
    }

    [Fact]
    public void Every_window_stays_inside_the_frame_for_faces_of_any_size_and_place_including_the_edges()
    {
        var rnd = new Random(7);
        for (int i = 0; i < 200000; i++)
        {
            double bw = 20 + rnd.NextDouble() * 340, bh = 20 + rnd.NextDouble() * 500;
            double bx = i % 3 == 0 ? 0 : i % 3 == 1 ? 360 - bw : rnd.NextDouble() * 360, by = i % 4 == 0 ? 0 : i % 4 == 1 ? 640 - bh : rnd.NextDouble() * 640;
            var (x, y, w, h) = CastStage.HeadWindow(bx, by, bw, bh, 360, 640, 1.25);
            Assert.True(x >= 0 && y >= 0 && x + w <= 360 + 1e-9 && y + h <= 640 + 1e-9, $"{bx} {by} {bw} {bh} -> {x} {y} {w} {h}");
        }
    }

    [Fact]
    public void Within_treats_a_room_a_hair_below_zero_as_zero_and_still_clamps()
    {
        Assert.Equal(0, CastStage.Within(5, -5.684341886080802E-14));
        Assert.Equal(0, CastStage.Within(-3, 10));
        Assert.Equal(10, CastStage.Within(30, 10));
        Assert.Equal(4, CastStage.Within(4, 10));
    }

    [Fact]
    public void The_publish_crop_and_the_tight_window_survive_the_same_edge_cases()
    {
        var (crop, focus) = Publish.HeadCrop([50, 100, 262.46511484611085, 239.77761006903722], 360, 640);
        Assert.All(crop, v => Assert.InRange(v, 0, 1 + 1e-9)); Assert.All(focus, v => Assert.InRange(v, 0, 1));
        var (c2, f2) = Publish.TightWindow([0, 0, 262.46511484611085, 239.77761006903722], [], 1.25, 360, 0.45, 360, 640);
        Assert.All(c2, v => Assert.InRange(v, 0, 1 + 1e-9)); Assert.All(f2, v => Assert.InRange(v, 0, 1));
    }

    [Fact]
    public void A_stack_trace_helper_names_the_exception_type_and_hides_the_sas()
    {
        try { throw new InvalidOperationException("boom sig=abc123secret"); }
        catch (Exception e) { var s = Redact.Trace(e); Assert.Contains("InvalidOperationException", s); Assert.Contains("A_stack_trace_helper", s); Assert.DoesNotContain("abc123secret", s); }
    }
}
