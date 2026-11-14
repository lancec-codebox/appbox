namespace IndexNode.Tests;

public class OrtOptionsTests
{
    [Fact]
    public void Sessions_never_spin_and_get_at_least_one_thread()
    {
        using var so = OrtOptions.Make(0);
        Assert.Equal(1, so.IntraOpNumThreads); Assert.Equal(1, so.InterOpNumThreads);
        Assert.Equal(1, OcrSubs.IntraThreads);
    }
}
