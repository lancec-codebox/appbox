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
}
