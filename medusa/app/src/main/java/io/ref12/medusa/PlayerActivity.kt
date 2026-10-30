package io.ref12.medusa

import android.app.Activity
import android.app.AlertDialog
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.content.pm.ActivityInfo
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.media3.common.MediaItem
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView

/** Standalone player: captured video, subtitles (any language) drawn by the app, a CC menu and live sync controls. */
class PlayerActivity : Activity() {
    private var player: ExoPlayer? = null
    private lateinit var mediaId: String
    private var lines: List<Line> = emptyList()
    private var lines2: List<Line> = emptyList()
    private var anchors: List<Anchor> = emptyList()
    private var tracks: List<TrackInfo> = emptyList()
    private var itemMain: String? = null
    private var mainLang: String? = null
    private var secondLang: String? = null
    private lateinit var subView: TextView
    private lateinit var sub2View: TextView
    private lateinit var info: TextView
    private lateinit var playerView: PlayerView
    private lateinit var topBar: LinearLayout
    private lateinit var bottomPanel: LinearLayout
    private lateinit var subBox: LinearLayout
    private var fullscreen = false
    private var controllerShown = true
    private var ccDialog: AlertDialog? = null
    private var ccSpinner: ProgressBar? = null
    private val ui = Handler(Looper.getMainLooper())
    private val tick = object : Runnable {
        override fun run() { update(); ui.postDelayed(this, 100) }
    }

    /** For the emulator test: what is drawn right now. */
    internal val subtitleText get() = subView.text.toString()
    internal val secondText get() = sub2View.text.toString()
    internal val chosenMain get() = mainLang
    internal val chosenSecond get() = secondLang
    internal val isFullscreen get() = fullscreen

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        mediaId = intent.getStringExtra(EXTRA_MEDIA) ?: run { finish(); return }
        val meta = app.media.get(mediaId)
        val file = app.media.videoFile(mediaId)
        if (meta == null || file == null) { Toast.makeText(this, "No video to play", Toast.LENGTH_LONG).show(); finish(); return }
        tracks = TrackUi.tracksOf(meta)
        itemMain = meta.subtitles?.language
        anchors = app.media.sync(mediaId)

        val p = ExoPlayer.Builder(this).build().also { player = it }
        p.setMediaItem(MediaItem.fromUri(Uri.fromFile(file)))
        p.prepare()
        p.playWhenReady = !intent.getBooleanExtra(EXTRA_PAUSED, false)

        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(0xFF000000.toInt()) }
        val stage = FrameLayout(this)
        playerView = PlayerView(this).apply {
            this.player = p; useController = true; subtitleView?.visibility = View.GONE
            setFullscreenButtonClickListener { toggleFullscreen() }
            setControllerVisibilityListener(PlayerView.ControllerVisibilityListener { v -> controllerShown = v == View.VISIBLE; applyChrome() })
        }
        stage.addView(playerView, FrameLayout.LayoutParams(-1, -1))
        // subtitles: white text on a near-solid black box, one box per line, drawn over the video
        fun subtitleBox(): TextView = TextView(this).apply {
            setTextColor(0xFFFFFFFF.toInt()); gravity = Gravity.CENTER
            setBackgroundColor(0xD9000000.toInt()); setPadding(20, 6, 20, 6); visibility = View.GONE
        }
        subView = subtitleBox(); sub2View = subtitleBox()
        applySize()
        subBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER_HORIZONTAL; setPadding(24, 0, 24, 112) }
        subBox.addView(subView, LinearLayout.LayoutParams(-2, -2).apply { bottomMargin = 6 })
        subBox.addView(sub2View, LinearLayout.LayoutParams(-2, -2))
        stage.addView(subBox, FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM))
        // tools along the top: CC menu, sync nudges and text size. They stay reachable in fullscreen (shown with the controls).
        fun tool(t: String, desc: String, f: () -> Unit) = Button(this).apply {
            text = t; isAllCaps = false; textSize = 14f; setTextColor(0xFFFFFFFF.toInt()); minWidth = 0; minimumWidth = 0
            setBackgroundColor(0xAA000000.toInt()); contentDescription = desc; tag = desc; setOnClickListener { f() }
        }
        topBar = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.END }
        for ((t, d, f) in listOf(
            Triple("CC", "Subtitles menu") { showMenu() },
            Triple("\u2212 0.25", "Earlier") { nudge(-SyncMath.STEP) },
            Triple("+ 0.25", "Later") { nudge(SyncMath.STEP) },
            Triple("Sync line", "Sync to this line") { syncLine() },
            Triple("A\u2212", "Smaller subtitles") { resize(-2f) },
            Triple("A+", "Larger subtitles") { resize(2f) },
        )) topBar.addView(tool(t, d, f), LinearLayout.LayoutParams(-2, -2).apply { setMargins(6, 0, 0, 0) })
        stage.addView(topBar, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.END).apply { setMargins(0, 24, 18, 0) })
        root.addView(stage, LinearLayout.LayoutParams(-1, 0, 1f))

        bottomPanel = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        bottomPanel.addView(TextView(this).apply {
            text = Titles.primary(meta, app.settings.titleMode); tag = "player-title"; maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END
            setTextColor(0xFFFFFFFF.toInt()); textSize = 15f; setPadding(24, 10, 24, 0)
        }, ViewGroup.LayoutParams(-1, -2))
        info = TextView(this).apply { setTextColor(0xFFCCCCCC.toInt()); setPadding(24, 8, 24, 0) }
        bottomPanel.addView(info, ViewGroup.LayoutParams(-1, -2))
        fun row(vararg b: Pair<String, () -> Unit>) = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            for ((t, f) in b) addView(Button(context).apply { text = t; isAllCaps = false; setOnClickListener { f() } }, LinearLayout.LayoutParams(0, -2, 1f))
        }
        bottomPanel.addView(row("Earlier 0.25s" to { nudge(-SyncMath.STEP) }, "Later 0.25s" to { nudge(SyncMath.STEP) }))
        bottomPanel.addView(row("Sync to this line" to { syncLine() }, "Reset here" to { change(SyncMath.resetHere(anchors, now())) }, "Reset all" to { change(SyncMath.resetAll()) }))
        root.addView(bottomPanel, ViewGroup.LayoutParams(-1, -2))
        setContentView(root)
        // windowed: respect the bars and cutouts; fullscreen hides the bars, so only a cutout is left to avoid
        Edge.setup(this, root, lightBar = false) { i -> root.setPadding(i.left, i.top, i.right, i.bottom) }

        // the remembered choice for this item, else its own main track
        val c = TrackUi.resolve(itemMain, tracks, app.settings.trackChoice(mediaId, "main"), app.settings.trackChoice(mediaId, "second"))
        mainLang = c.main; secondLang = c.second
        loadInto(false, c.main, remember = false)
        loadInto(true, c.second, remember = false)
        if (intent.getBooleanExtra(EXTRA_FULLSCREEN, false)) toggleFullscreen()
    }

    /** Puts a track's lines in the main or second slot; downloads it first when needed (in a thread). */
    private fun loadInto(second: Boolean, lang: String?, remember: Boolean, done: (Boolean) -> Unit = {}) {
        fun apply(l: List<Line>) {
            if (second) { lines2 = l; secondLang = lang } else { lines = l; mainLang = lang }
            if (remember) app.settings.setTrackChoice(mediaId, if (second) "second" else "main", lang)
            update()
        }
        if (lang == null) { apply(emptyList()); done(true); return }
        ccSpinner?.visibility = View.VISIBLE
        Thread {
            val t = tracks.firstOrNull { it.key.equals(lang, true) }
            val s = try { if (t != null && t.added) app.jobs.subtitlesFor(mediaId, t.language, t.trackId) else app.jobs.subtitlesFor(mediaId, lang) } catch (e: Exception) { null }
            runOnUiThread {
                ccSpinner?.visibility = View.GONE
                if (s == null) {
                    Toast.makeText(this, "Could not load $lang (it needs a network connection)", Toast.LENGTH_LONG).show()
                    done(false)
                } else { apply(s.lines); done(true) }
            }
        }.start()
    }

    private fun showMenu() {
        val body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 16, 40, 0) }
        fun header(t: String) = TextView(this).apply { text = t; textSize = 14f; setPadding(0, 24, 0, 4); setTypeface(typeface, android.graphics.Typeface.BOLD) }
        fun group(second: Boolean): android.widget.RadioGroup {
            val cur = if (second) secondLang else mainLang
            val (labels, tags) = TrackUi.menuEntries(tracks)
            val checked = TrackUi.indexOfKey(tracks, cur)
            val slot = if (second) "second" else "main"
            return TrackUi.radios(this, labels, tags.map { "$slot:$it" }, checked) { i ->
                loadInto(second, if (i == 0) null else tracks[i - 1].key, remember = true)
            }
        }
        body.addView(header("Subtitles"))
        body.addView(group(false))
        body.addView(header("Second line"))
        body.addView(group(true))
        if (tracks.isEmpty()) body.addView(TextView(this).apply { text = "This video has no subtitle tracks."; setPadding(0, 16, 0, 0) })
        ccSpinner = ProgressBar(this).apply { visibility = View.GONE }
        body.addView(ccSpinner, LinearLayout.LayoutParams(-2, -2).apply { gravity = Gravity.CENTER_HORIZONTAL })
        ccDialog = AlertDialog.Builder(this).setTitle("Subtitles").setView(ScrollView(this).apply { addView(body) })
            .setNeutralButton("Export\u2026") { _, _ -> app.media.get(mediaId)?.let { ExportUi.show(this, it) } }
            .setPositiveButton("Close", null).show()
    }

    private fun applySize() {
        val sp = app.settings.subtitleSp
        subView.textSize = sp
        sub2View.textSize = sp * 0.85f
    }

    private fun resize(delta: Float) {
        app.settings.subtitleSp = (app.settings.subtitleSp + delta).coerceIn(12f, 34f)
        applySize()
    }

    /** Fullscreen: landscape, system bars hidden, the bottom panel gone; the top tools show with the controls. */
    private fun toggleFullscreen() {
        fullscreen = !fullscreen
        requestedOrientation = if (fullscreen) ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE else ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        applyImmersive(fullscreen)
        subBox.setPadding(24, 0, 24, if (fullscreen) 96 else 112)
        applyChrome()
    }

    @Suppress("DEPRECATION")
    private fun applyImmersive(on: Boolean) {
        if (android.os.Build.VERSION.SDK_INT >= 30) {
            window.setDecorFitsSystemWindows(!on)
            window.insetsController?.let {
                if (on) {
                    it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                    it.hide(WindowInsets.Type.systemBars())
                } else it.show(WindowInsets.Type.systemBars())
            }
        } else {
            window.decorView.systemUiVisibility = if (on) (View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_LAYOUT_STABLE) else View.SYSTEM_UI_FLAG_VISIBLE
        }
    }

    private fun applyChrome() {
        if (!::bottomPanel.isInitialized) return
        bottomPanel.visibility = if (fullscreen) View.GONE else View.VISIBLE
        topBar.visibility = if (!fullscreen || controllerShown) View.VISIBLE else View.GONE
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() { if (fullscreen) toggleFullscreen() else @Suppress("DEPRECATION") super.onBackPressed() }

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

    private fun update() {
        if (!::subView.isInitialized) return
        val t = now()
        val a = curLine(t)?.let { lines[it].text } ?: ""
        val b = SyncMath.currentLine(lines2, anchors, t)?.let { lines2[it].text } ?: ""
        subView.text = a; subView.visibility = if (a.isEmpty()) View.GONE else View.VISIBLE
        sub2View.text = b; sub2View.visibility = if (b.isEmpty()) View.GONE else View.VISIBLE
        info.text = "Sync offset here: %+.2fs  (%d anchors)".format(SyncMath.offsetAt(anchors, t), anchors.size)
    }

    override fun onResume() { super.onResume(); ui.post(tick) }
    override fun onPause() { ui.removeCallbacks(tick); player?.pause(); super.onPause() }
    override fun onDestroy() { player?.release(); player = null; super.onDestroy() }

    companion object {
        const val EXTRA_MEDIA = "media"
        const val EXTRA_PAUSED = "paused"
        const val EXTRA_FULLSCREEN = "fullscreen"
    }
}
