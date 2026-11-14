using Azure;

namespace IndexNode.Tests;

/// <summary>0.5.0.1: Windows file locks, per-step work folders, the hydrate race.</summary>
public class HotfixTests
{
    [Fact]
    public async Task Raw_frame_file_is_closed_and_deleted_after_reading()
    {
        // On Windows deleting an open file throws IOException; this is the frames read-then-delete path.
        var f = Path.Combine(Path.GetTempPath(), "indexnode-test-" + Guid.NewGuid().ToString("N") + ".rgb");
        File.WriteAllBytes(f, new byte[12 * 3]); // three frames of 12 bytes
        var seen = new List<int>();
        await Frames.ConsumeRawAsync(f, new byte[12], i => { seen.Add(i); return Task.FromResult(true); }, default);
        Assert.Equal([0, 1, 2], seen);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public async Task Raw_frame_file_is_deleted_when_the_consumer_stops_early()
    {
        var f = Path.Combine(Path.GetTempPath(), "indexnode-test-" + Guid.NewGuid().ToString("N") + ".rgb");
        File.WriteAllBytes(f, new byte[12 * 5]);
        await Frames.ConsumeRawAsync(f, new byte[12], i => Task.FromResult(i < 1), default);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void Work_folders_differ_per_step_and_per_video()
    {
        var a = ProcessStage.WorkDir("vid", "frames"); var b = ProcessStage.WorkDir("vid", "ocrsubs");
        Assert.NotEqual(a, b);
        Assert.EndsWith("p-vid-frames", a);
        Assert.NotEqual(a, ProcessStage.WorkDir("other", "frames"));
    }

    [Fact]
    public async Task Hydrate_race_is_not_a_failure_when_the_blob_is_no_longer_a_ghost()
    {
        var ok = await NexisAccess.HydrateOrYieldAsync("v", () => throw new RequestFailedException(412, "The condition specified using HTTP conditional header(s) is not met.", "ConditionNotMet", null), () => Task.FromResult(false));
        Assert.False(ok);
    }

    [Fact]
    public async Task Hydrate_race_still_a_ghost_or_other_error_is_thrown()
    {
        await Assert.ThrowsAsync<RequestFailedException>(() => NexisAccess.HydrateOrYieldAsync("v", () => throw new RequestFailedException(412, "not met", "ConditionNotMet", null), () => Task.FromResult(true)));
        await Assert.ThrowsAsync<RequestFailedException>(() => NexisAccess.HydrateOrYieldAsync("v", () => throw new RequestFailedException(500, "boom"), () => Task.FromResult(false)));
        Assert.True(await NexisAccess.HydrateOrYieldAsync("v", () => Task.FromResult(true), () => Task.FromResult(false)));
    }
}
