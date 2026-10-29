package io.ref12.medusa

import android.app.Activity
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.ViewGroup
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.media3.common.MediaItem
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView

/** Standalone player: captured video, subtitles (any language) drawn by the app, live sync controls. */
class PlayerActivity : Activity() {
    private var player: ExoPlayer? = null
    private lateinit var mediaId: String
    private var lines: List<Line> = emptyList()
    private var anchors: List<Anchor> = emptyList()
    private lateinit var subView: TextView
    private lateinit var sub2View: TextView
    private var lines2: List<Line> = emptyList()
    private var tracks: List<TrackInfo> = emptyList()
    private var meta: MediaMeta? = null
    private var main: String? = null
    private lateinit var info: TextView
    private val ui = Handler(Looper.getMainLooper())
    private val tick = object : Runnable {
        override fun run() { update(); ui.postDelayed(this, 100) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        mediaId = intent.getStringExtra(EXTRA_MEDIA) ?: run { finish(); return }
        val meta = app.media.get(mediaId)
        val file = app.media.videoFile(mediaId)
        if (meta == null || file == null) { Toast.makeText(this, "No video to play", Toast.LENGTH_LONG).show(); finish(); return }
        this.meta = meta
        tracks = meta.tracks.ifEmpty { meta.subtitles?.let { listOf(TrackInfo(it.language, it.source)) } ?: emptyList() }
        main = meta.subtitles?.language
        lines = meta.subtitles?.lines ?: emptyList()
        anchors = app.media.sync(mediaId)

        val p = ExoPlayer.Builder(this).build().also { player = it }
        p.setMediaItem(MediaItem.fromUri(Uri.fromFile(file)))
        p.prepare()
        p.playWhenReady = true

        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(0xFF000000.toInt()) }
        val stage = FrameLayout(this)
        stage.addView(PlayerView(this).apply { this.player = p; useController = true; subtitleView?.visibility = android.view.View.GONE },
            FrameLayout.LayoutParams(-1, -1))
        subView = TextView(this).apply {
            textSize = 20f; setTextColor(0xFFFFFFFF.toInt()); setShadowLayer(6f, 0f, 0f, 0xFF000000.toInt())
            gravity = Gravity.CENTER; setPadding(24, 8, 24, 120)
        }
        sub2View = TextView(this).apply {
            textSize = 16f; setTextColor(0xFFFFE08A.toInt()); setShadowLayer(6f, 0f, 0f, 0xFF000000.toInt())
            gravity = Gravity.CENTER; setPadding(24, 0, 24, 8)
        }
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(0, 0, 0, 112) }
        subView.setPadding(24, 8, 24, 4)
        box.addView(subView, ViewGroup.LayoutParams(-1, -2)); box.addView(sub2View, ViewGroup.LayoutParams(-1, -2))
        stage.addView(box, FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM))
        root.addView(stage, LinearLayout.LayoutParams(-1, 0, 1f))

        info = TextView(this).apply { setTextColor(0xFFCCCCCC.toInt()); setPadding(24, 8, 24, 0) }
        root.addView(info, ViewGroup.LayoutParams(-1, -2))
        fun row(vararg b: Pair<String, () -> Unit>) = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            for ((t, f) in b) addView(Button(context).apply { text = t; isAllCaps = false; setOnClickListener { f() } }, LinearLayout.LayoutParams(0, -2, 1f))
        }
        root.addView(row("Subtitles: " + (main ?: "off") to { pickTrack(false) }, "Second track" to { pickTrack(true) }))
        root.addView(row("Earlier 0.25s" to { nudge(-SyncMath.STEP) }, "Later 0.25s" to { nudge(SyncMath.STEP) }))
        root.addView(row("Sync to this line" to { syncLine() }, "Reset here" to { change(SyncMath.resetHere(anchors, now())) }, "Reset all" to { change(SyncMath.resetAll()) }))
        setContentView(root)
    }

    private fun now() = (player?.currentPosition ?: 0L) / 1000.0

    private fun curLine(t: Double) = SyncMath.currentLine(lines, anchors, t)

    private fun change(a: List<Anchor>) {
        anchors = a
        app.media.putSync(mediaId, a)
        update()
    }

    private fun nudge(delta: Double) {
        val t = now()
        change(SyncMath.nudge(anchors, t, curLine(t)?.let { lines[it].start }, delta))
    }

    private fun syncLine() {
        val t = now()
        val i = curLine(t) ?: SyncMath.nextLine(lines, anchors, t)
        if (i == null) { Toast.makeText(this, "No subtitle line to sync", Toast.LENGTH_SHORT).show(); return }
        change(SyncMath.syncToLine(anchors, lines[i].start, t))
    }

    private fun pickTrack(second: Boolean) {
        val labels = (listOf("Off") + tracks.map { (it.name ?: it.language) + " (" + it.language + ", " + (if (it.source == "captions") "captions" else "auto") + ")" }).toTypedArray()
        android.app.AlertDialog.Builder(this).setTitle(if (second) "Second subtitle track" else "Subtitle track")
            .setItems(labels) { _, which ->
                if (which == 0) { if (second) lines2 = emptyList() else lines = emptyList(); update(); return@setItems }
                val lang = tracks[which - 1].language
                Toast.makeText(this, "Loading $lang", Toast.LENGTH_SHORT).show()
                Thread {
                    val s = try { app.jobs.subtitlesFor(mediaId, lang) } catch (e: Exception) { null }
                    runOnUiThread {
                        if (s == null) Toast.makeText(this, "Could not load $lang (needs network)", Toast.LENGTH_LONG).show()
                        else { if (second) lines2 = s.lines else { lines = s.lines; main = s.language }; update() }
                    }
                }.start()
            }.show()
    }

    private fun update() {
        val t = now()
        subView.text = curLine(t)?.let { lines[it].text } ?: ""
        sub2View.text = SyncMath.currentLine(lines2, anchors, t)?.let { lines2[it].text } ?: ""
        info.text = "Sync offset here: %+.2fs  (%d anchors)".format(SyncMath.offsetAt(anchors, t), anchors.size)
    }

    override fun onResume() { super.onResume(); ui.post(tick) }
    override fun onPause() { ui.removeCallbacks(tick); player?.pause(); super.onPause() }
    override fun onDestroy() { player?.release(); player = null; super.onDestroy() }

    companion object { const val EXTRA_MEDIA = "media" }
}
