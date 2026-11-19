using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Work;
using Java.Util.Concurrent;

namespace IndexNode.Host;

/// <summary>WorkManager: a periodic job (constraints: connected, plus charging as the setting says; Wi-Fi only is decided by HostPolicy in the worker) and an expedited "Run now". A foreground notification shows during a run.</summary>
public sealed class RunWorker(Context context, WorkerParameters p) : Worker(context, p)
{
    public const string PeriodicName = "indexnode-periodic", NowName = "indexnode-now", Channel = "indexnode-run";

    public override Result DoWork()
    {
        var rt = NodeRuntime.Get(ApplicationContext);
        var foreground = false;
        try
        {
            // wait for the foreground promotion here, so a refusal (missing service type, notification permission, background start limits) is caught and logged instead of killing the app inside WorkManager's service
            foreground = TryForeground(rt, Notification("IndexNode", "starting"));
            Action update = () => { if (foreground) TryForeground(rt, Notification("IndexNode", rt.State + (rt.Job != null ? " " + rt.Job : "") + (rt.Progress is { } pr ? $" {pr * 100:0}%" : "")), quiet: true); };
            rt.Changed += update;
            try { rt.RunAsync(false, default).GetAwaiter().GetResult(); }
            finally { rt.Changed -= update; }
            return Result.InvokeSuccess()!;
        }
        catch (System.OperationCanceledException) { return Result.InvokeRetry()!; }
        catch (Exception e)
        {
            rt.Host.Log("run failed: " + e.GetType().Name + ": " + e.Message);
            CrashRecorder.Record("RunWorker.DoWork", e);
            return Result.InvokeFailure()!;   // the error is in the log and in the state; a periodic job runs again at its next period
        }
    }

    /// <summary>Promote the worker to a foreground service and wait for the answer. false = it was refused; the run goes on without the notification.</summary>
    static bool _warned;
    bool TryForeground(NodeRuntime rt, ForegroundInfo info, bool quiet = false)
    {
        try { SetForegroundAsync(info).Get(10, TimeUnit.Seconds!); return true; }
        catch (Exception e)
        {
            if (!quiet || !_warned) rt.Host.Log("foreground notification unavailable, running without it: " + e.GetType().Name + ": " + e.Message);
            _warned = true; return false;
        }
    }

    // expedited work on API < 31 asks for this (Worker.getForegroundInfoAsync calls it); the base one throws
    public override ForegroundInfo ForegroundInfo => Notification("IndexNode", "starting");

    ForegroundInfo Notification(string title, string text)
    {
        var nm = (NotificationManager)ApplicationContext.GetSystemService(Context.NotificationService)!;
        if (nm.GetNotificationChannel(Channel) == null) nm.CreateNotificationChannel(new NotificationChannel(Channel, "Node runs", NotificationImportance.Low));
        var n = new Notification.Builder(ApplicationContext, Channel).SetContentTitle(title).SetContentText(text).SetSmallIcon(Resource.Drawable.icon).SetOngoing(true).Build()!;   // a static icon
        // the type must be declared on WorkManager's SystemForegroundService in the manifest (dataSync)
        return Build.VERSION.SdkInt >= BuildVersionCodes.Q ? new ForegroundInfo(1, n, (int)Android.Content.PM.ForegroundService.TypeDataSync) : new ForegroundInfo(1, n);
    }

    public static Constraints ConstraintsFor(Prefs prefs) => new Constraints.Builder()
        .SetRequiredNetworkType(NetworkType.Connected)!   // Wi-Fi only is decided by HostPolicy in the worker: a VPN hides Wi-Fi and is metered, so NetworkType.Unmetered never ran
        .SetRequiresCharging(prefs.Bool("chargingOnly", true))!.Build()!;

    public static void Schedule(Context ctx)
    {
        var prefs = new Prefs(ctx); var wm = WorkManager.GetInstance(ctx);
        if (!prefs.Bool("periodic", true) || prefs.Sas == null) { wm.CancelUniqueWork(PeriodicName); return; }
        var req = new PeriodicWorkRequest.Builder(typeof(RunWorker), Math.Max(15, prefs.Int("periodMinutes", 60)), TimeUnit.Minutes!).SetConstraints(ConstraintsFor(prefs)).Build();
        wm.EnqueueUniquePeriodicWork(PeriodicName, ExistingPeriodicWorkPolicy.Update!, req);
    }

    public static void RunNow(Context ctx)
    {
        var req = new OneTimeWorkRequest.Builder(typeof(RunWorker)).SetExpedited(OutOfQuotaPolicy.RunAsNonExpeditedWorkRequest!)!.Build()!;
        WorkManager.GetInstance(ctx).EnqueueUniqueWork(NowName, ExistingWorkPolicy.Replace!, (OneTimeWorkRequest)req);
    }
}
