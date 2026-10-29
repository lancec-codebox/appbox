package io.ref12.mediahelper

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper

/** Foreground service: runs the local server and keeps the process alive while jobs run. */
class HelperService : Service() {
    private var server: HelperServer? = null
    private val handler = Handler(Looper.getMainLooper())
    private var lastRequest = System.currentTimeMillis()
    private val tick = object : Runnable {
        override fun run() {
            val jobs = app.jobs
            val idleSince = maxOf(jobs.lastActivity, lastRequest)
            if (jobs.activeCount() == 0 && System.currentTimeMillis() - idleSince > IDLE_MS) {
                stopSelf()
                return
            }
            notify(notification())
            handler.postDelayed(this, 2000)
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        createChannel()
        val n = notification()
        if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        else startForeground(NOTIF_ID, n)
        lastRequest = System.currentTimeMillis()
        startServer()
        handler.removeCallbacks(tick)
        handler.postDelayed(tick, 2000)
        return START_NOT_STICKY
    }

    private fun startServer() {
        if (server != null) return
        val api = object : HelperApi {
            override fun appVersion() = BuildConfig.VERSION_NAME
            override fun ytDlpVersion() = app.fetcher.version()
            override fun submit(id: String?, url: String, languages: List<String>?) = app.jobs.submit(id, url, languages)
            override fun jobInfo(id: String) = app.jobs.info(id)
            override fun resultJson(id: String) = app.jobs.resultJson(id)
            override fun touch() { lastRequest = System.currentTimeMillis() }
        }
        val s = HelperServer(Router(api) { app.settings.origins })
        try {
            s.start(5000, false)
            server = s
            running = true
        } catch (e: java.io.IOException) {
            // port taken (another instance?): the job screen and Save transcript still work
            lastError = "Could not listen on 127.0.0.1:$SERVER_PORT: " + e.message
        }
    }

    override fun onDestroy() {
        handler.removeCallbacks(tick)
        server?.stop()
        server = null
        running = false
        super.onDestroy()
    }

    private fun notification(): Notification {
        val jobs = app.jobs
        val active = jobs.activeCount()
        val text = if (active > 0) {
            val j = jobs.recent().firstOrNull { !it.state.finished }
            (j?.let { jobs.info(it.id)?.message } ?: "Working") + (if (active > 1) " (+" + (active - 1) + " queued)" else "")
        } else "Idle. Serving on 127.0.0.1:$SERVER_PORT, stops soon."
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle(if (active > 0) "Fetching transcript" else "Media Helper")
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    private fun notify(n: Notification) =
        (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).notify(NOTIF_ID, n)

    private fun createChannel() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Media Helper", NotificationManager.IMPORTANCE_LOW))
    }

    companion object {
        const val CHANNEL = "media_helper"
        const val NOTIF_ID = 1
        const val IDLE_MS = 10L * 60 * 1000
        @Volatile var running = false
        @Volatile var lastError: String? = null

        fun start(ctx: Context) {
            ctx.startForegroundService(Intent(ctx, HelperService::class.java))
        }
    }
}
