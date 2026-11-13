package io.ref12.medusa.node

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ForegroundInfo
import androidx.work.NetworkType
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

/** Runs every 15 minutes under the chosen constraints; becomes a dataSync foreground worker while it has work. */
class NodeWorker(private val ctx: Context, params: WorkerParameters) : CoroutineWorker(ctx, params) {
    @Volatile private var runner: NodeRunner? = null
    private var foreground = false

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val settings = NodeSettings(ctx)
        val sas = settings.vault.load()
        if (!settings.enabled || sas == null) return@withContext Result.success()
        val cfg = settings.config(ctx, BuildConfig.VERSION_NAME)
        val client = BlobClient(sas)
        val r = NodeRunner(client, YtDlpNodeFetcher(ctx.app.fetcher), cfg, settings, cap = settings.dailyCap, listener = object : NodeListener {
            override fun onWork(left: Int) { goForeground("Downloading for the index", left) }
            override fun onJob(jobId: String, title: String?, left: Int, text: String?) { goForeground("Downloading for the index: " + (title ?: jobId) + " (" + left + " left)", left, text) }
        })
        runner = r
        // The system ended the work (the Android 15 dataSync time limit, lost constraints, cancel): WorkManager cancels this coroutine, and the runner lets go of the lease.
        settings.lastRunAt = System.currentTimeMillis(); settings.lastRun = "running"
        try {
            val s = kotlinx.coroutines.coroutineScope {
                val job = async(Dispatchers.IO) { r.run() }
                try { job.await() } catch (e: CancellationException) {
                    r.abort(); withContext(NonCancellable) { job.join() }; throw e
                }
            }
            settings.lastRun = Redact.sas("done " + s.done + ", failed " + s.failed + ", released " + s.released + " - " + s.endReason)
        } catch (e: CancellationException) { settings.lastRun = "stopped by the system"; throw e
        } catch (e: Throwable) {
            settings.lastRun = Redact.sas("error: " + (e.message ?: e.javaClass.simpleName))
        } finally { runner = null }
        Result.success()
    }

    private fun goForeground(text: String, left: Int, sub: String? = null) {
        try {
            runBlocking { setForeground(info(text, sub)) }
            foreground = true
        } catch (e: Exception) { /* not allowed right now: the work then runs as an ordinary worker and ends when the system stops it */ }
    }

    private fun info(text: String, sub: String?): ForegroundInfo {
        val nm = ctx.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Index node", NotificationManager.IMPORTANCE_LOW))
        val n = NotificationCompat.Builder(ctx, CHANNEL).setSmallIcon(android.R.drawable.stat_sys_download).setContentTitle(text)
            .setContentText(sub ?: "").setOngoing(true).setOnlyAlertOnce(true).build()
        return if (Build.VERSION.SDK_INT >= 29) ForegroundInfo(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC) else ForegroundInfo(NOTIF_ID, n)
    }

    companion object {
        const val CHANNEL = "index_node"; const val NOTIF_ID = 4217; const val UNIQUE = "index-node"

        /** Schedules (or cancels) the periodic worker from the settings. Always needs battery and storage not low. */
        fun schedule(ctx: Context, s: NodeSettings) {
            val wm = WorkManager.getInstance(ctx)
            if (!s.enabled || !s.vault.has()) { wm.cancelUniqueWork(UNIQUE); return }
            val c = Constraints.Builder().setRequiredNetworkType(NetworkType.UNMETERED).setRequiresBatteryNotLow(true).setRequiresStorageNotLow(true)
                .setRequiresCharging(s.conditions == NodeConditions.CHARGING_WIFI).build()
            wm.enqueueUniquePeriodicWork(UNIQUE, ExistingPeriodicWorkPolicy.UPDATE, PeriodicWorkRequestBuilder<NodeWorker>(15, TimeUnit.MINUTES).setConstraints(c).build())
        }
    }
}
