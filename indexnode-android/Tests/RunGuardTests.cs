using IndexNode.Hosting;

namespace IndexNode.Android.Tests;

public class RunGuardTests
{
    [Fact]
    public void A_second_start_during_a_run_is_refused_without_an_error_and_a_run_after_exit_is_allowed()
    {
        var g = new RunGuard();
        Assert.False(g.Running);
        Assert.True(g.TryEnter()); Assert.True(g.Running);
        Assert.False(g.TryEnter());          // Run now / a WorkManager start while going: a no-op, not an exception
        Assert.True(g.Running);              // the first run is untouched
        g.Exit(); Assert.False(g.Running);
        Assert.True(g.TryEnter());
        Assert.Equal("a run is already in progress", RunGuard.Busy);
    }

    [Fact]
    public async Task Only_one_of_many_concurrent_starts_wins()
    {
        var g = new RunGuard(); int won = 0;
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => { if (g.TryEnter()) Interlocked.Increment(ref won); })));
        Assert.Equal(1, won);
    }
}
