using Xunit;

namespace IndexNode.Tests;

public class OcrSegmentTests
{
    [Theory]
    [InlineData(6840.0, 24)]
    [InlineData(6843.37, 6)]
    [InlineData(95.1, 24)]
    [InlineData(30.0, 1)]
    public void Segments_cover_the_runtime_once_with_boundaries_on_the_sampling_grid(double duration, int workers)
    {
        var plan = OcrSubs.PlanSegments(duration, workers);
        Assert.Equal(0, plan[0].Start); Assert.Equal(duration, plan[^1].End);
        for (int i = 1; i < plan.Count; i++)
        {
            Assert.Equal(plan[i - 1].End, plan[i].Start);                                   // no gap, no overlap
            Assert.Equal(0, Math.Round(plan[i].Start * 4) - plan[i].Start * 4, 6);          // a multiple of 0.25 s
        }
    }

    [Fact]
    public void The_union_of_the_segment_grids_is_the_serial_grid()
    {
        double duration = 1234.56;
        var serial = Enumerable.Range(0, (int)Math.Ceiling(duration * 4)).Select(i => Math.Round(i / 4.0, 3)).ToList();
        var par = OcrSubs.PlanSegments(duration, 7).SelectMany(s =>
            Enumerable.Range(0, (int)Math.Ceiling((s.End - s.Start) * 4 - 1e-9)).Select(i => Math.Round(s.Start + i / 4.0, 3))).ToList();
        Assert.Equal(serial, par);
    }

    [Theory]
    [InlineData(1, 24, 12)]
    [InlineData(4, 24, 12)]
    [InlineData(16, 24, 16)]
    [InlineData(0, 2, 1)]
    public void Ocr_takes_the_larger_of_its_share_and_half_the_cores(int share, int cores, int expected) =>
        Assert.Equal(expected, OcrSubs.Segments(share, cores));

    [Fact]
    public void A_running_job_carries_startedAt_in_the_node_record()
    {
        var n = new NodeInfo { Running = [new RunningJob { JobId = "v", Stage = "ocrsubs", StartedAt = "2026-11-13T20:00:00Z" }] };
        Assert.Contains("\"startedAt\":\"2026-11-13T20:00:00Z\"", System.Text.Encoding.UTF8.GetString(n.ToBytes()));
    }
}
