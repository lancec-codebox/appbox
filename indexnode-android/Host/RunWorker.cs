using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Work;
using Java.Util.Concurrent;

namespace IndexNode.Host;

/// <summary>WorkManager: a periodic job (constraints: connected, plus unmetered and charging as the settings say) and an expedited "Run now". A foreground notification shows during a run.</summary>
public sealed class RunWorker(Context context, WorkerParameters p) : Worker(context, p)
{
    public const string PeriodicName = "indexnode-periodic", NowName = "indexnode-now", Channel = "indexnode-run";

    public override Result DoWork()
    {
        var rt = NodeRuntime.Get(ApplicationContext);
        try
        {
            SetForegroundAsync(Notification("IndexNode", "starting"));
            rt.Changed += Update;
            void Update() { try { SetForegroundAsync(Notification("IndexNode", rt.State + (rt.Job != null ? " " + rt.Job : "") + (rt.Progress is { } pr ? $" {pr * 100:0}%" : ""))); } catch { } }
            rt.RunAsync(false, default).GetAwaiter().GetResult();
            rt.Changed -= Update;
            return Result.InvokeSuccess()!;
        }
        catch (System.OperationCanceledException) { return Result.InvokeRetry()!; }
        catch (Exception) { return Result.InvokeFailure()!; }   // the error is in the log and in the state; a periodic job runs again at its next period
    }

    ForegroundInfo Notification(string title, string text)
    {
        var nm = (NotificationManager)ApplicationContext.GetSystemService(Context.NotificationService)!;
        if (nm.GetNotificationChannel(Channel) == null) nm.CreateNotificationChannel(new NotificationChannel(Channel, "Node runs", NotificationImportance.Low));
        var n = new Notification.Builder(ApplicationContext, Channel).SetContentTitle(title).SetContentText(text).SetSmallIcon(Resource.Drawable.icon).SetOngoing(true).Build()!;   // a static icon
        return Build.VERSION.SdkInt >= BuildVersionCodes.Q ? new ForegroundInfo(1, n, (int)Android.Content.PM.ForegroundService.TypeDataSync) : new ForegroundInfo(1, n);
    }

    public static Constraints ConstraintsFor(Prefs prefs) => new Constraints.Builder()
        .SetRequiredNetworkType(prefs.Bool("wifiOnly", true) ? NetworkType.Unmetered : NetworkType.Connected)!
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
