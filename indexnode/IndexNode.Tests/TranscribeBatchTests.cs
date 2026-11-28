using IndexNode;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.83: on CUDA the voice segments are decoded in batches by audio length, longest first.</summary>
public class TranscribeBatchTests
{
    static int S(double seconds) => (int)(seconds * Transcribe.SampleRate);

    [Fact]
    public void Batches_go_longest_first_and_stay_under_the_audio_and_count_limits()
    {
        var lengths = new[] { S(5), S(20), S(1), S(15), S(10), S(2) };
        var b = Transcribe.Batches(lengths, 30, 64);
        Assert.Equal([1], b[0]);                 // 20 s, then 15 would make 35 > 30
        Assert.Equal([3, 4, 0], b[1]);           // 15 + 10 + 5 = 30
        Assert.Equal([5, 2], b[2]);              // 2 + 1
        Assert.Equal(lengths.Length, b.Sum(x => x.Count));
        Assert.All(b, x => Assert.True(x.Count == 1 || x.Sum(i => lengths[i]) <= S(30)));
    }

    [Fact]
    public void A_segment_longer_than_the_limit_is_a_batch_of_its_own_and_the_count_limit_holds()
    {
        var b = Transcribe.Batches([S(50), S(1), S(1), S(1)], 30, 2);
        Assert.Equal([0], b[0]);
        Assert.Equal(2, b[1].Count); Assert.Single(b[2]);
        Assert.Empty(Transcribe.Batches([], 30, 2));
    }

    [Fact]
    public void The_cpu_build_keeps_the_cpu_provider() => Assert.Equal(OrtOptions.Cuda ? "cuda" : "cpu", Transcribe.Provider);
}
