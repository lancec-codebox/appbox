package io.ref12.medusa.node

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.BatteryManager
import android.os.Build
import android.os.StatFs
import androidx.core.app.NotificationCompat
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.ForegroundInfo
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import io.ref12.medusa.BuildConfig
import io.ref12.medusa.app
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Runs every 15 minutes under the chosen constraints, or once now ("Run now"); becomes a dataSync foreground worker while it has work.
 * The status notification (id [STATUS_ID]) shows what the node is doing; during a run the foreground notification says it instead.
 */
class NodeWorker(private val ctx: Context, params: WorkerParameters) : CoroutineWorker(ctx, params) {
    @Volatile private var runner: NodeRunner? = null
    private val throttle = NodeStatus.Throttle()

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val settings = NodeSettings(ctx)
        val sas = settings.vault.load()
        if (!settings.enabled || sas == null) return@withContext Result.success()
        // one run at a time (a periodic and a "Run now" run could overlap); the leases would protect the jobs, the cap counter is shared
        if (!RUNNING.compareAndSet(false, true)) return@withContext Result.success()
        try {
            val cfg = settings.config(ctx, BuildConfig.VERSION_NAME)
            val client = BlobClient(sas)
            val today = { settings.doneToday(NodeRunner.day(System.currentTimeMillis())) }
            fun show(status: StatusInput, progressOnly: Boolean) {
                NodeLive.input = status
                val text = NodeStatus.text(status)
                if (throttle.allow(text, progressOnly)) goForeground(text)
            }
            val r = NodeRunner(client, YtDlpNodeFetcher(ctx.app.fetcher), cfg, settings, env = NodeEnv(deviceNet = { StatusNotifier.deviceNet(ctx) }), cap = settings.dailyCap, listener = object : NodeListener {
                override fun onWork(left: Int) { show(StatusInput(downloading = true, doneToday = today(), cap = settings.dailyCap, title = null), false) }
                override fun onJob(jobId: String, title: String?, left: Int, text: String?) {
                    show(StatusInput(downloading = true, doneToday = today(), cap = settings.dailyCap, title = title ?: jobId, progress = text), true)
                }
            })
            runner = r
            ACTIVE = r
            StatusNotifier.cancel(ctx) // the foreground notification speaks during the run
            // The system ended the work (the Android 15 dataSync time limit, lost constraints, cancel): WorkManager cancels this coroutine, and the runner lets go of the lease.
            settings.lastRunAt = System.currentTimeMillis(); settings.lastRunVersion = BuildConfig.VERSION_NAME; settings.lastRun = "running"; NodeLive.input = StatusInput(downloading = true, doneToday = today(), cap = settings.dailyCap)
            var reason = ""
            try {
                val s = kotlinx.coroutines.coroutineScope {
                    val job = async(Dispatchers.IO) { r.run() }
                    try { job.await() } catch (e: CancellationException) {
                        r.abort(); withContext(NonCancellable) { job.join() }; throw e
                    }
                }
                reason = s.endReason; settings.lastEndReason = s.endReason
                settings.lastRun = Redact.sas(NodeStatus.runSummary(s.done, s.failed, s.released, s.endReason))
            } catch (e: CancellationException) { settings.lastRun = "stopped by the system"; settings.lastRunAt = System.currentTimeMillis(); throw e
            } catch (e: Throwable) {
                settings.lastRun = Redact.sas("error: " + (e.message ?: e.javaClass.simpleName))
            } finally {
                runner = null; ACTIVE = null; NodeLive.input = null
                settings.lastRunAt = System.currentTimeMillis()
                settings.lastStatus = ""
                StatusNotifier.refresh(ctx, settings, if (reason.isNotEmpty()) NodeStatus.afterRun(reason, today(), settings.dailyCap, message = settings.lastControl.substringAfter(" - ", "").ifEmpty { null }) else null)
            }
            Result.success()
        } finally { RUNNING.set(false) }
    }

    private fun goForeground(text: String) {
        try { runBlocking { setForeground(StatusNotifier.foregroundInfo(ctx, text)) } } catch (e: Exception) { /* not allowed right now: the work then runs as an ordinary worker and ends when the system stops it */ }
    }

    /** Needed for an expedited request on Android 11 and older. */
    override suspend fun getForegroundInfo(): ForegroundInfo = StatusNotifier.foregroundInfo(ctx, "Index node: starting")

    companion object {
        const val CHANNEL = "index_node"; const val NOTIF_ID = 4217; const val STATUS_ID = 4218; const val UNIQUE = "index-node"; const val UNIQUE_NOW = "index-node-now"
        private val RUNNING = AtomicBoolean(false)
        /** The runner of the run in progress, so a network change can make it look its address up again. */
        @Volatile var ACTIVE: NodeRunner? = null
        val isRunning get() = RUNNING.get()

        private fun constraints(needsCharging: Boolean) = Constraints.Builder().setRequiredNetworkType(NetworkType.UNMETERED).setRequiresBatteryNotLow(true).setRequiresStorageNotLow(true)
            .setRequiresCharging(needsCharging).build()

        /** Schedules (or cancels) the periodic worker from the settings, and brings the status notification in line. Always needs battery and storage not low. */
        fun schedule(ctx: Context, s: NodeSettings) {
            val wm = WorkManager.getInstance(ctx)
            if (!s.enabled || !s.vault.has()) { wm.cancelUniqueWork(UNIQUE); StatusNotifier.cancel(ctx); return }
            wm.enqueueUniquePeriodicWork(UNIQUE, ExistingPeriodicWorkPolicy.UPDATE,
                PeriodicWorkRequestBuilder<NodeWorker>(15, TimeUnit.MINUTES).setConstraints(constraints(s.conditions == NodeConditions.CHARGING_WIFI)).build())
            StatusNotifier.refresh(ctx, s, null)
        }

        /**
         * "Run now": one expedited run at once, under the same cap, budget and control rules. It keeps only the Wi-Fi (unmetered) condition (expedited work allows no more);
         * the daily cap, budget and control rules still apply.
         */
        fun runNow(ctx: Context, s: NodeSettings = NodeSettings(ctx)) {
            if (!s.enabled || !s.vault.has()) return
            // WorkManager refuses (IllegalArgumentException from build()) an expedited request with a battery-not-low, storage-not-low or charging constraint: only the network one is allowed
            val req = OneTimeWorkRequestBuilder<NodeWorker>().setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.UNMETERED).build())
                .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST).build()
            WorkManager.getInstance(ctx).enqueueUniqueWork(UNIQUE_NOW, ExistingWorkPolicy.KEEP, req)
        }
    }
}

/** The status notification and the device conditions behind its waiting texts. */
object StatusNotifier {
    private fun channel(ctx: Context): NotificationManager {
        val nm = ctx.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        // low importance: no sound, no heads-up
        nm.createNotificationChannel(NotificationChannel(NodeWorker.CHANNEL, "Index node", NotificationManager.IMPORTANCE_LOW).apply { setSound(null, null); enableVibration(false) })
        return nm
    }

    private fun runNowIntent(ctx: Context): PendingIntent = PendingIntent.getBroadcast(ctx, 1, Intent(ctx, RunNowReceiver::class.java).setAction(RunNowReceiver.ACTION),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)

    private fun build(ctx: Context, text: String, ongoing: Boolean): Notification {
        val launch = ctx.packageManager.getLaunchIntentForPackage(ctx.packageName)
        val open = launch?.let { PendingIntent.getActivity(ctx, 0, it, PendingIntent.FLAG_IMMUTABLE) }
        return NotificationCompat.Builder(ctx, NodeWorker.CHANNEL).setSmallIcon(android.R.drawable.stat_sys_download).setContentTitle("Index node").setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text)).setOngoing(ongoing).setOnlyAlertOnce(true).setSilent(true).setPriority(NotificationCompat.PRIORITY_LOW)
            .also { b -> open?.let { b.setContentIntent(it) } }.addAction(0, "Run now", runNowIntent(ctx)).build()
    }

    fun foregroundInfo(ctx: Context, text: String): ForegroundInfo {
        channel(ctx)
        val n = build(ctx, text, true)
        return if (Build.VERSION.SDK_INT >= 29) ForegroundInfo(NodeWorker.NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC) else ForegroundInfo(NodeWorker.NOTIF_ID, n)
    }

    fun cancel(ctx: Context) { try { (ctx.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).cancel(NodeWorker.STATUS_ID) } catch (e: Exception) {} }

    fun notificationsAllowed(ctx: Context): Boolean = androidx.core.app.NotificationManagerCompat.from(ctx).areNotificationsEnabled()

    /** The status as the node screen and the notification show it, worked out from the settings and the device. [after] is the state at the end of a run. */
    fun statusInput(ctx: Context, s: NodeSettings, after: StatusInput? = null): StatusInput {
        if (!s.enabled) return StatusInput(enabled = false)
        val day = NodeRunner.day(System.currentTimeMillis())
        val wait = deviceWait(ctx, s.conditions == NodeConditions.CHARGING_WIFI)
        val sinceMin = if (s.lastRunAt == 0L) 0 else ((System.currentTimeMillis() - s.lastRunAt) / 60_000).toInt()
        return when {
            NodeWorker.isRunning -> NodeLive.input ?: StatusInput(downloading = true, doneToday = s.doneToday(day), cap = s.dailyCap)
            s.backoffUntil() > System.currentTimeMillis() -> StatusInput(backoffUntil = java.text.DateFormat.getTimeInstance(java.text.DateFormat.SHORT).format(java.util.Date(s.backoffUntil())),
                backoffReason = backoffReason(s))
            wait != Wait.NONE -> StatusInput(wait = wait)
            s.lastControl.startsWith("stop") -> StatusInput(stoppedByOwner = true, message = s.lastControl.substringAfter(" - ", "").ifEmpty { null })
            after != null && after.downloading.not() && (after.capReached || after.budgetSpent || after.drain || after.ipCapRunning != null) -> after
            s.doneToday(day) >= s.dailyCap -> StatusInput(doneToday = s.doneToday(day), cap = s.dailyCap, capReached = true)
            s.lastEndReason == "no work" -> StatusInput(noWork = true, nextCheckMin = (15 - sinceMin).coerceIn(1, 15))
            else -> StatusInput(nextCheckMin = (15 - sinceMin).coerceIn(1, 15))
        }
    }

    /** "bot block on <job>": from the newest back-off entry in the job log. */
    fun backoffReason(s: NodeSettings): String? = s.recent().firstOrNull { it.result == "bot block" }?.let { "bot block on " + it.jobId }

    fun statusText(ctx: Context, s: NodeSettings): String = try { NodeStatus.text(statusInput(ctx, s)) } catch (e: Exception) { "Status unavailable" }

    /** Posts the status when it changed (a new state, not a timer). [after] is the state at the end of a run; null: work it out from the device. */
    fun refresh(ctx: Context, s: NodeSettings, after: StatusInput?) {
        try {
            if (!s.enabled || !s.vault.has() || !s.statusNotification) { cancel(ctx); return }
            val text = NodeStatus.text(statusInput(ctx, s, after))
            if (text == s.lastStatus) return
            s.lastStatus = text
            channel(ctx).notify(NodeWorker.STATUS_ID, build(ctx, text, false))
        } catch (e: SecurityException) { /* notification permission denied: Settings shows a warning */ } catch (e: Exception) { }
    }

    /** The active network's transport (wifi, cellular, ethernet) and whether it is a VPN. */
    fun deviceNet(ctx: Context): DeviceNet = try {
        val cm = ctx.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val caps = cm.getNetworkCapabilities(cm.activeNetwork)
        if (caps == null) DeviceNet(null, null) else DeviceNet(
            NetworkProbe.transportOf(caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI), caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR), caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)),
            caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN))
    } catch (e: Exception) { DeviceNet(null, null) }

    /** The first condition the phone does not meet (Wi-Fi, charging, battery, storage). */
    fun deviceWait(ctx: Context, needsCharging: Boolean): Wait {
        val cm = ctx.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val caps = cm.getNetworkCapabilities(cm.activeNetwork)
        val unmetered = caps != null && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
        val b = ctx.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val plugged = (b?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0
        val pct = b?.let { it.getIntExtra(BatteryManager.EXTRA_LEVEL, 100) * 100 / it.getIntExtra(BatteryManager.EXTRA_SCALE, 100).coerceAtLeast(1) } ?: 100
        val sf = StatFs(ctx.filesDir.absolutePath)
        val storageLow = sf.availableBytes < sf.totalBytes / 10 && sf.availableBytes < 2_000_000_000L
        return NodeStatus.waitReason(needsCharging, plugged, unmetered, pct <= 15 && !plugged, storageLow)
    }

    /** Re-evaluates the status when the phone's conditions change (power, battery, storage, network). Registered once by the app. */
    fun watch(app: android.app.Application) {
        val r = object : BroadcastReceiver() { override fun onReceive(c: Context, i: Intent) { Thread { refresh(c, NodeSettings(c), null) }.start() } }
        val f = IntentFilter().apply { listOf(Intent.ACTION_POWER_CONNECTED, Intent.ACTION_POWER_DISCONNECTED, Intent.ACTION_BATTERY_LOW, Intent.ACTION_BATTERY_OKAY,
            Intent.ACTION_DEVICE_STORAGE_LOW, Intent.ACTION_DEVICE_STORAGE_OK).forEach { addAction(it) } }
        if (Build.VERSION.SDK_INT >= 33) app.registerReceiver(r, f, Context.RECEIVER_NOT_EXPORTED) else app.registerReceiver(r, f)
        try {
            (app.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager).registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
                override fun onCapabilitiesChanged(n: android.net.Network, c: NetworkCapabilities) { NodeWorker.ACTIVE?.onNetworkChanged(); Thread { refresh(app, NodeSettings(app), null) }.start() }
                override fun onLost(n: android.net.Network) { NodeWorker.ACTIVE?.onNetworkChanged(); Thread { refresh(app, NodeSettings(app), null) }.start() }
            })
        } catch (e: Exception) { }
    }
}

/** The "Run now" action of the status notification. */
class RunNowReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == ACTION) SafeClick.run("Run now", { android.widget.Toast.makeText(context, it, android.widget.Toast.LENGTH_LONG).show() }) { NodeWorker.runNow(context); null }
    }
    companion object { const val ACTION = "io.ref12.medusa.NODE_RUN_NOW" }
}
