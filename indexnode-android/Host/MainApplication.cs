using Android.App;
using Android.Runtime;

namespace IndexNode.Host;

/// <summary>Installs the crash recorder before anything else runs (the activity, WorkManager's worker, a Java thread).</summary>
[Application]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : Application(handle, ownership)
{
    public override void OnCreate()
    {
        base.OnCreate();
        var prefs = new Prefs(this);
        CrashRecorder.Install(this, () => prefs.Secrets, () => { try { return NodeRuntime.Get(this).Plugins.Current?.Version; } catch { return null; } });
    }
}
