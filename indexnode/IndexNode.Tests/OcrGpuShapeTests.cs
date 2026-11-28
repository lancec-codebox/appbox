using IndexNode;
using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.85: OCR on CUDA rounds the reader's batch width to a bucket and uses cuDNN's heuristic search; the CPU path is unchanged.</summary>
public class OcrGpuShapeTests
{
    [Theory]
    [InlineData(10, 64, 64)]       // the minimum (48), rounded up to the bucket
    [InlineData(100, 64, 128)]
    [InlineData(128, 64, 128)]
    [InlineData(129, 64, 192)]
    [InlineData(3190, 64, 3200)]   // capped at the maximum
    [InlineData(137, 1, 137)]      // bucket 1 = off (the CPU path)
    public void Reader_widths_are_rounded_up_to_the_bucket(int w, int bucket, int expected) => Assert.Equal(expected, OcrEngine.BucketWidth(w, bucket));

    [Fact]
    public void On_the_cpu_nothing_changes_and_bad_search_names_are_refused()
    {
        if (!OrtOptions.Cuda) { Assert.Equal(1, OcrEngine.RecBucket); Assert.Null(OcrEngine.ConvSearch); }
        Assert.Equal("HEURISTIC", OrtOptions.CheckConvSearch(" heuristic "));
        Assert.Null(OrtOptions.CheckConvSearch(null));
        Assert.Throws<ArgumentException>(() => OrtOptions.CheckConvSearch("fast"));
    }
}
