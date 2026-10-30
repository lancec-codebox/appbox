package io.ref12.medusa

import android.Manifest
import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.widget.PopupMenu
import android.view.View
import android.widget.Toast
import java.io.File

/**
 * Export a subtitle track: one tap saves it to Downloads (shows in Files and Recent), a notification offers Open and
 * Share, and the format is remembered. Share... (the chooser) is the secondary action in the track's overflow menu.
 */
object ExportUi {
    val FORMATS = listOf(ExportFormat.SRT to "SRT (default)", ExportFormat.VTT to "WebVTT", ExportFormat.JSON to "Medusa JSON (.medusa-track.json)")
    private const val CHANNEL = "exports"

    fun format(a: Activity): ExportFormat = ExportFormat.values().firstOrNull { it.ext == a.app.settings.exportFormat } ?: ExportFormat.SRT

    /** The track to export from the item's main choice when none is named: the main one, else the first. */
    fun defaultTrack(m: MediaMeta): TrackInfo? {
        val tracks = TrackUi.tracksOf(m)
        val main = m.subtitles?.language
        return tracks.firstOrNull { it.language.equals(main, true) && !it.added } ?: tracks.firstOrNull()
    }

    /** Saves [tracks] to Downloads, or hands them to the share chooser when [share]. */
    fun export(a: Activity, m: MediaMeta, tracks: List<TrackInfo>, share: Boolean = false, anchor: View? = null) {
        if (tracks.isEmpty()) { Toast.makeText(a, "This item has no subtitle tracks to export", Toast.LENGTH_LONG).show(); return }
        val f = format(a)
        if (!share) askNotificationPermission(a)
        Thread {
            val files = ArrayList<Pair<String, String>>()
            var failed: String? = null
            for (t in tracks) {
                val s = try { if (t.added) a.app.jobs.subtitlesFor(m.mediaId, t.language, t.trackId) else a.app.jobs.subtitlesFor(m.mediaId, t.language) } catch (e: Exception) { null }
                if (s == null) failed = TrackUi.label(t) else files.add(TrackExport.fileName(m.title, t.language + (t.origin?.let { "-$it" } ?: ""), f) to TrackExport.content(f, m, t, s.lines))
            }
            val saved = ArrayList<SavedFile>(); var err: String? = null
            if (!share) for ((n, c) in files) try { saved.add(DownloadsSaver.save(a, n, f, c)) } catch (e: Exception) { err = e.message ?: "unknown error" }
            a.runOnUiThread {
                if (failed != null) Toast.makeText(a, "Could not load $failed (it needs a network connection)", Toast.LENGTH_LONG).show()
                if (err != null) Toast.makeText(a, "Could not save: $err", Toast.LENGTH_LONG).show()
                if (share && files.isNotEmpty()) share(a, files, f)
                for ((i, s) in saved.withIndex()) notify(a, s, i)
                if (saved.isNotEmpty()) (a as? MainActivity)?.snack("Saved " + (if (saved.size == 1) saved[0].name else "${saved.size} files") + " to Downloads")
                    ?: Toast.makeText(a, "Saved to Downloads: " + saved.joinToString { it.name }, Toast.LENGTH_LONG).show()
            }
        }.start()
    }

    /** The track's overflow menu: Share..., and the remembered format. */
    fun overflow(a: Activity, m: MediaMeta, t: TrackInfo, anchor: View) {
        PopupMenu(a, anchor).apply {
            menu.add(0, 0, 0, "Share\u2026")
            val cur = format(a)
            for ((i, p) in FORMATS.withIndex()) menu.add(1, i + 1, i + 1, (if (p.first == cur) "\u2713 " else "") + "Format: " + p.second)
            menu.setGroupCheckable(1, false, false)
            setOnMenuItemClickListener {
                if (it.itemId == 0) export(a, m, listOf(t), share = true)
                else { a.app.settings.exportFormat = FORMATS[it.itemId - 1].first.ext; Toast.makeText(a, "Export format: " + FORMATS[it.itemId - 1].second, Toast.LENGTH_SHORT).show() }
                true
            }
        }.show()
    }

    private fun askNotificationPermission(a: Activity) {
        if (Build.VERSION.SDK_INT >= 33 && a.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            a.requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 7)
    }

    private fun notify(a: Activity, s: SavedFile, index: Int) {
        val nm = a.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 33 && a.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Saved files", NotificationManager.IMPORTANCE_DEFAULT))
        val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        val id = (System.currentTimeMillis() % 100000).toInt() + index
        val open = PendingIntent.getActivity(a, id, Intent.createChooser(viewIntent(s), "Open with").takeIf { false } ?: viewIntent(s), flags)
        val send = Intent(Intent.ACTION_SEND).setType(s.mime).putExtra(Intent.EXTRA_STREAM, s.uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        val share = PendingIntent.getActivity(a, id + 50000, Intent.createChooser(send, "Share subtitles").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), flags)
        val n = Notification.Builder(a, CHANNEL)
            .setSmallIcon(R.drawable.ic_library).setContentTitle("Saved " + s.name).setContentText("Saved " + s.name + " to Downloads")
            .setContentIntent(open).setAutoCancel(true)
            .addAction(Notification.Action.Builder(null, "Open", open).build())
            .addAction(Notification.Action.Builder(null, "Share", share).build())
            .build()
        nm.notify(id, n)
    }

    private fun viewIntent(s: SavedFile): Intent = Intent(Intent.ACTION_VIEW).setDataAndType(s.uri, s.mime)
        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)

    private fun share(a: Activity, files: List<Pair<String, String>>, f: ExportFormat) {
        val dir = File(a.cacheDir, "exports").also { it.deleteRecursively(); it.mkdirs() }
        val uris = ArrayList<Uri>(files.map { (n, c) -> ExportProvider.uriFor(a, n.also { File(dir, it).writeText(c) }) })
        val i = if (uris.size == 1) Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, uris[0])
        else Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris)
        i.type = f.mime
        i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        a.startActivity(Intent.createChooser(i, "Share subtitles"))
    }
}
