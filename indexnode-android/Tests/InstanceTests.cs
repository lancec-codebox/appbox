using IndexNode.Hosting;
using IndexNode.Plugin;

namespace IndexNode.Android.Tests;

public class InstanceTests
{
    [Fact]
    public void The_instance_id_is_made_once_and_kept_so_a_restart_is_the_same_instance()
    {
        var h = new FakeHost();
        var a = NodePlugin.InstanceId(h);
        Assert.False(string.IsNullOrEmpty(a));
        Assert.Equal(a, NodePlugin.InstanceId(h));                       // app UI, WorkManager worker, later runs
        var restarted = new FakeHost(); restarted.Settings["instanceId"] = h.Settings["instanceId"];   // the app process restarts: the setting survives
        Assert.Equal(a, NodePlugin.InstanceId(restarted));
        Assert.NotEqual(a, NodePlugin.InstanceId(new FakeHost()));      // another phone
    }
}
