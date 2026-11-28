using Xunit;

namespace IndexNode.Tests;

/// <summary>0.13.92: GPU steps on a CUDA node take one process-wide lock; CPU steps and CPU nodes never wait.</summary>
public class GpuGateTests
{
    [Fact]
    public async Task Two_GPU_steps_on_cuda_run_one_at_a_time_and_cpu_steps_do_not_wait()
    {
        Assert.Null(await GpuGate.EnterAsync("cpu", "sscd", default));      // a CPU node: no lock
        Assert.Null(await GpuGate.EnterAsync("cuda", "frames", default));   // a CPU step: no lock
        var first = await GpuGate.EnterAsync("cuda", "sscd", default); Assert.NotNull(first);
        var second = GpuGate.EnterAsync("cuda", "transcribe", default);
        await Task.Delay(100); Assert.False(second.IsCompleted);           // waits for the card
        first!.Dispose(); first.Dispose();                                  // a second Dispose releases nothing
        using var got = await second.WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(got);
        var third = GpuGate.EnterAsync("cuda", "cast", default); await Task.Delay(50); Assert.False(third.IsCompleted);
        got!.Dispose(); (await third.WaitAsync(TimeSpan.FromSeconds(5)))!.Dispose();
    }
}
