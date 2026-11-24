using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.Content.PM;
using IndexNode.Hosting;

namespace IndexNode.Host;

/// <summary>
/// 0.1.5: the app finds and installs its own updates, like Medusa. It reads host.json on the rolling release (HostUpdate.RollingUrl), downloads the APK it names,
/// checks its SHA-256, and hands it to a PackageInstaller session; Android shows its own confirmation. The rules are in HostUpdate (Loader, tested); this class is the Android part.
/// </summary>
public sealed class AppUpdater(Context ctx, Func<string> installed, Action<string> log, Func<bool> running)
{
    public const string ResultAction = "io.ref12.indexnode.INSTALL_RESULT";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly object gate = new();
    private bool busy;

    /// <summary>"idle", "checking", "current", "available", "downloading", "installing", "confirm", "needs-permission", "done" or "failed".</summary>
    public string State { get; private set; } = "idle";
    public HostOffer? Offer { get; private set; }
    public string? Message { get; private set; }
    public double? Progress { get; private set; }
    public string? CheckedAt { get; private set; }
    public event Action? Changed;

    private void Set(string state, string? message = null, double? progress = null) { State = state; Message = message; Progress = progress; Changed?.Invoke(); }

    public bool CanInstall { get { try { return ctx.PackageManager!.CanRequestPackageInstalls(); } catch { return false; } } }

    public JsonObject Json() => new()
    {
        ["state"] = State, ["installed"] = installed(), ["message"] = Message, ["progress"] = Progress, ["checkedAt"] = CheckedAt, ["canInstall"] = CanInstall,
        ["offer"] = Offer == null ? null : new JsonObject { ["version"] = Offer.Version, ["bytes"] = Offer.Bytes, ["notes"] = Offer.Notes },
    };

    private bool Enter() { lock (gate) { if (busy) return false; busy = true; return true; } }
    private void Exit() { lock (gate) busy = false; }

    /// <summary>Reads host.json. A failure is a state ("failed" with the reason), never an exception: the check also runs by itself on app start.</summary>
    public async Task<JsonObject> CheckAsync(CancellationToken ct)
    {
        if (!Enter()) return Json();
        try
        {
            Set("checking");
            var text = await http.GetStringAsync(HostUpdate.RollingUrl, ct);
            Offer = HostUpdate.Parse(text, installed());
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            if (Offer == null) Set("current", "This is the newest version");
            else Set("available", "Version " + Offer.Version + " is available");
            log("app update check: " + State + (Offer != null ? " " + Offer.Version : ""));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Set("failed", "The check failed: " + e.Message); log("app update check failed: " + e.GetType().Name + ": " + e.Message);
        }
        finally { Exit(); }
        return Json();
    }

    /// <summary>Downloads, verifies and installs the offered version. Android asks the user to confirm; the result arrives in OnInstallStatus.</summary>
    public async Task<JsonObject> InstallAsync(CancellationToken ct)
    {
        if (Offer == null) await CheckAsync(ct);
        var offer = Offer;
        if (offer == null) return Json();
        if (running()) { Set("available", "A run is in progress: stop it or wait, then update"); return Json(); }
        if (!CanInstall)
        {
            // the system screen where the user allows this app to install apps; they come back and tap Update again
            try
            {
                var allow = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + ctx.PackageName));
                allow.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(allow);
            }
            catch (Exception e) { log("could not open the install permission screen: " + e.Message); }
            Set("needs-permission", "Allow IndexNode to install apps on the screen that opened, then tap Update again");
            return Json();
        }
        if (!Enter()) return Json();
        try
        {
            var dir = Path.Combine(ctx.CacheDir!.AbsolutePath, "update"); Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir)) { try { File.Delete(old); } catch { } }
            var apk = Path.Combine(dir, "indexnode-android-" + offer.Version + ".apk");
            Set("downloading", "Downloading " + offer.Version, 0);
            using (var resp = await http.GetAsync(offer.ApkUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = offer.Bytes > 0 ? offer.Bytes : resp.Content.Headers.ContentLength ?? -1, got = 0; int lastPct = -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(apk);
                var buf = new byte[128 * 1024];
                while (true)
                {
                    int n = await src.ReadAsync(buf, ct); if (n == 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct); got += n;
                    int pct = total > 0 ? (int)(got * 100 / total) : -1;
                    if (pct != lastPct) { lastPct = pct; Set("downloading", "Downloading " + offer.Version, total > 0 ? (double)got / total : null); }
                }
            }
            Set("installing", "Checking the download");
            if (!HostUpdate.Verify(apk, offer)) { try { File.Delete(apk); } catch { } Set("failed", "The download does not match the published file; nothing was installed"); log("app update: hash mismatch for " + offer.Version); return Json(); }
            Set("installing", "Handing " + offer.Version + " to Android");
            Commit(apk);
            log("app update: " + offer.Version + " handed to the installer");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Set("failed", "The update failed: " + e.Message); log("app update failed: " + e.GetType().Name + ": " + e.Message);
        }
        finally { Exit(); }
        return Json();
    }

    /// <summary>A PackageInstaller session (not the deprecated install intent): the APK is streamed in, and the result comes back as a broadcast.</summary>
    private void Commit(string apk)
    {
        var installer = ctx.PackageManager!.PackageInstaller;
        var p = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        p.SetAppPackageName(ctx.PackageName);
        long len = new FileInfo(apk).Length;
        p.SetSize(len);
        int id = installer.CreateSession(p);
        try
        {
            using var session = installer.OpenSession(id);
            using (var src = File.OpenRead(apk))
            using (var dst = session.OpenWrite("indexnode.apk", 0, len))
            {
                src.CopyTo(dst);
                session.Fsync(dst);
            }
            var intent = new Intent(ctx, typeof(InstallResultReceiver));
            intent.SetAction(ResultAction);
            intent.SetPackage(ctx.PackageName);
            var pending = PendingIntent.GetBroadcast(ctx, id, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Mutable);
            session.Commit(pending!.IntentSender);
        }
        catch
        {
            try { installer.AbandonSession(id); } catch { }
            throw;
        }
    }

    /// <summary>The session's result (status = PackageInstaller.STATUS_*).</summary>
    public void OnInstallStatus(int status, string? systemMessage)
    {
        var (kind, message) = HostUpdate.Outcome(status, systemMessage);
        log("app update: " + kind + ": " + message);
        Set(kind switch { "confirm" => "confirm", "done" => "done", "cancelled" => "available", _ => "failed" }, message);
    }
}

/// <summary>Receives the install session's result. When Android wants the user's OK it sends its own confirmation screen along; this shows it.</summary>
[BroadcastReceiver(Exported = false)]
public sealed class InstallResultReceiver : BroadcastReceiver
{
    // the platform's extra names (PackageInstaller.EXTRA_STATUS, EXTRA_STATUS_MESSAGE; Intent.EXTRA_INTENT)
    const string ExtraStatus = "android.content.pm.extra.STATUS", ExtraStatusMessage = "android.content.pm.extra.STATUS_MESSAGE", ExtraIntent = "android.intent.extra.INTENT";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        int status = intent.GetIntExtra(ExtraStatus, 1);
        var message = intent.GetStringExtra(ExtraStatusMessage);
        var rt = NodeRuntime.Get(context);
        if (status == -1)
        {
            try
            {
#pragma warning disable CA1422, CS0618 // the typed overload needs API 33; the app supports API 26
                var confirm = intent.GetParcelableExtra(ExtraIntent) as Intent;
#pragma warning restore CA1422, CS0618
                if (confirm != null) { confirm.AddFlags(ActivityFlags.NewTask); context.StartActivity(confirm); }
                else rt.Host.Log("app update: Android sent no confirmation screen");
            }
            catch (Exception e) { rt.Host.Log("app update: could not show Android's confirmation: " + e.Message); }
        }
        rt.Updater.OnInstallStatus(status, message);
    }
}
