package io.ref12.medusa

import android.app.Activity
import android.app.AlertDialog
import android.content.ContentValues
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore as AndroidMedia
import android.widget.LinearLayout
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.io.File

/** "Export subtitles": a track (or all), a format, then Share... or Save to Downloads. */
object ExportUi {
    private val FORMATS = listOf(ExportFormat.SRT to "SRT (default)", ExportFormat.VTT to "WebVTT", ExportFormat.JSON to "Medusa JSON (.medusa-track.json)")

    fun show(a: Activity, m: MediaMeta) {
        val tracks = TrackUi.tracksOf(m)
        if (tracks.isEmpty()) { Toast.makeText(a, "This item has no subtitle tracks to export", Toast.LENGTH_LONG).show(); return }
        val body = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 16, 40, 0) }
        fun header(t: String) = TextView(a).apply { text = t; textSize = 14f; setPadding(0, 24, 0, 4); setTypeface(typeface, android.graphics.Typeface.BOLD) }
        val labels = tracks.map { TrackUi.label(it) } + (if (tracks.size > 1) listOf("All tracks") else emptyList())
        val main = m.subtitles?.language
        var track = tracks.indexOfFirst { it.language.equals(main, true) && !it.added }.coerceAtLeast(0)
        var format = 0
        body.addView(header("Track"))
        body.addView(TrackUi.radios(a, labels, tracks.map { "export:" + it.key } + (if (tracks.size > 1) listOf("export:all") else emptyList()), track) { track = it })
        body.addView(header("Format"))
        body.addView(TrackUi.radios(a, FORMATS.map { it.second }, FORMATS.map { "format:" + it.first.ext }, 0) { format = it })
        fun go(share: Boolean) {
            val chosen = if (track >= tracks.size) tracks else listOf(tracks[track])
            val f = FORMATS[format].first
            Toast.makeText(a, "Preparing\u2026", Toast.LENGTH_SHORT).show()
            Thread {
                val files = ArrayList<Pair<String, String>>() // name to content
                var failed: String? = null
                for (t in chosen) {
                    val s = try { if (t.added) a.app.jobs.subtitlesFor(m.mediaId, t.language, t.trackId) else a.app.jobs.subtitlesFor(m.mediaId, t.language) } catch (e: Exception) { null }
                    if (s == null) failed = TrackUi.label(t) else files.add(TrackExport.fileName(m.title, t.language + (t.origin?.let { "-$it" } ?: ""), f) to TrackExport.content(f, m, t, s.lines))
                }
                a.runOnUiThread {
                    if (failed != null) Toast.makeText(a, "Could not load $failed (it needs a network connection)", Toast.LENGTH_LONG).show()
                    if (files.isNotEmpty()) if (share) share(a, files, f) else save(a, files, f)
                }
            }.start()
        }
        AlertDialog.Builder(a).setTitle("Export subtitles").setView(ScrollView(a).apply { addView(body) })
            .setPositiveButton("Share\u2026") { _, _ -> go(true) }
            .setNeutralButton("Save to Downloads") { _, _ -> go(false) }
            .setNegativeButton("Cancel", null).show()
    }

    private fun share(a: Activity, files: List<Pair<String, String>>, f: ExportFormat) {
        val dir = File(a.cacheDir, "exports").also { it.deleteRecursively(); it.mkdirs() }
        val uris = ArrayList<Uri>(files.map { (n, c) ->
            ExportProvider.uriFor(a, n.also { File(dir, it).writeText(c) })
        })
        val i = if (uris.size == 1) Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, uris[0])
        else Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris)
        i.type = f.mime
        i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        a.startActivity(Intent.createChooser(i, "Share subtitles"))
    }

    private fun save(a: Activity, files: List<Pair<String, String>>, f: ExportFormat) {
        try {
            for ((n, c) in files) {
                if (Build.VERSION.SDK_INT >= 29) {
                    val v = ContentValues().apply {
                        put(AndroidMedia.MediaColumns.DISPLAY_NAME, n); put(AndroidMedia.MediaColumns.MIME_TYPE, f.mime)
                        put(AndroidMedia.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS)
                    }
                    val uri = a.contentResolver.insert(AndroidMedia.Downloads.EXTERNAL_CONTENT_URI, v) ?: throw java.io.IOException("Downloads is not available")
                    a.contentResolver.openOutputStream(uri)!!.use { it.write(c.toByteArray()) }
                } else {
                    val d = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS).also { it.mkdirs() }
                    File(d, n).writeText(c)
                }
            }
            Toast.makeText(a, "Saved to Downloads: " + files.joinToString { it.first }, Toast.LENGTH_LONG).show()
        } catch (e: Exception) { Toast.makeText(a, "Could not save: " + e.message, Toast.LENGTH_LONG).show() }
    }
}
