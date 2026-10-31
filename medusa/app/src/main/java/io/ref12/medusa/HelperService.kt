package io.ref12.medusa

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
import fi.iki.elonen.NanoHTTPD

/** Foreground service: runs the local server and keeps the process alive while jobs run. */
class HelperService : Service() {
    private var server: HelperServer? = null
    private val handler = Handler(Looper.getMainLooper())
    private var lastRequest = System.currentTimeMillis()
    private val versions by lazy { VersionCache({ app.fetcher.version() }, java.util.concurrent.Executors.newSingleThreadExecutor()) }
    private val tick = object : Runnable {
        override fun run() {
            val jobs = app.jobs
            if (KeepAlive.shouldStop(app.settings.keepAlive, System.currentTimeMillis(), lastRequest, jobs.lastActivity, app.lastUiLeft, jobs.activeCount() + app.ocr.activeCount(), app.visibleScreens > 0)) {
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
        versions.get() // warm the yt-dlp version in the background so /v1/status never waits for it
        startServer()
        handler.removeCallbacks(tick)
        handler.postDelayed(tick, 2000)
        return START_NOT_STICKY
    }

    private fun startServer() {
        if (server != null) return
        val api = object : HelperApi {
            override fun appVersion() = BuildConfig.VERSION_NAME
            override fun ytDlpVersion() = versions.get()
            override fun submit(req: JobSubmit) = app.jobs.submit(req)
            override fun jobInfo(id: String) = app.jobs.info(id)
            override fun resultJson(id: String) = app.jobs.resultJson(id)
            override fun mediaListJson() = org.json.JSONArray().also { a ->
                for (m in app.media.list()) a.put(MediaJson.item(m, app.media.sync(m.mediaId), withLines = false))
            }.toString()
            override fun mediaJson(mediaId: String) = app.jobs.itemJson(mediaId)
            override fun deleteMedia(mediaId: String) = app.media.delete(mediaId)
            override fun videoFile(mediaId: String) = app.media.videoFile(mediaId)?.let { it to (app.media.get(mediaId)?.video?.contentType ?: "video/mp4") }
            override fun sync(mediaId: String) = if (app.media.get(mediaId) == null) null else app.media.sync(mediaId)
            override fun putSync(mediaId: String, anchors: List<Anchor>) = app.media.putSync(mediaId, anchors)
            override fun subtitles(mediaId: String, language: String, trackId: String?) = app.jobs.subtitlesFor(mediaId, language, trackId)
            override fun addTrack(mediaId: String, track: AddedTrack) = app.media.addTrack(mediaId, track)?.trackId
            override fun removeTrack(mediaId: String, trackId: String) = app.media.removeTrack(mediaId, trackId)
            override fun setSubtitles(mediaId: String, language: String) =
                app.jobs.setMainTrack(mediaId, language)?.let { MediaJson.item(it, app.media.sync(mediaId)).toString() }
            override fun touch() { lastRequest = System.currentTimeMillis() }
        }
        val s = HelperServer(Router(api) { app.settings.origins })
        try {
            s.start(NanoHTTPD.SOCKET_READ_TIMEOUT, false)
            server = s
            running = true
        } catch (e: java.io.IOException) {
            // port taken (another instance?): the job screen and Save transcript still work
            lastError = "Could not listen on 127.0.0.1:$SERVER_PORT: " + e.message
        }
    }

    /** Android 15 limits data-sync foreground services to about 6 hours a day: stop cleanly instead of being killed. */
    override fun onTimeout(startId: Int, fgsType: Int) { stopSelf() }

    override fun onDestroy() {
        handler.removeCallbacks(tick)
        server?.stop()
        server = null
        running = false
        super.onDestroy()
    }

    private fun notification(): Notification {
        val jobs = app.jobs
        val ocr = app.ocr.state?.takeIf { it.active }
        if (ocr != null) {
            val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
            return Notification.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.stat_sys_download)
                .setContentTitle("Reading subtitles from the picture")
                .setContentText(OcrUi.notificationText(ocr))
                .setProgress(100, ocr.percent, ocr.phase == OcrState.Phase.DETECTING)
                .setContentIntent(open).setOngoing(true).build()
        }
        val active = jobs.activeCount()
        val text = if (active > 0) {
            val j = jobs.recent().firstOrNull { !it.state.finished }
            (j?.let { jobs.info(it.id)?.message } ?: "Working") + (if (active > 1) " (+" + (active - 1) + " queued)" else "")
        } else "Web apps can use Medusa now (127.0.0.1:$SERVER_PORT)"
        val open = PendingIntent.getActivity(this, 0,
            Intent(this, MainActivity::class.java).putExtra(MainActivity.EXTRA_DOWNLOADS, true).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle(if (active > 0) "Downloading media" else "Medusa is ready")
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    private fun notify(n: Notification) =
        (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).notify(NOTIF_ID, n)

    private fun createChannel() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Medusa", NotificationManager.IMPORTANCE_LOW))
    }

    companion object {
        const val CHANNEL = "media_helper"
        const val NOTIF_ID = 1
        @Volatile var running = false
        @Volatile var lastError: String? = null

        fun start(ctx: Context) {
            ctx.startForegroundService(Intent(ctx, HelperService::class.java))
        }
    }
}
