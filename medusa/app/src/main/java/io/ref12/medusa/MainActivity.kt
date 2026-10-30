package io.ref12.medusa

import android.Manifest
import android.app.Activity
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.LinearLayout
import android.widget.TextView

/**
 * The app shell: a top bar (menu or Up, page title, settings gear), a navigation drawer (permanent on wide screens)
 * and one page at a time. Back rules are in [NavStack]: Back goes up one step, then to Library, and Library exits.
 */
class MainActivity : Activity() {
    internal var nav = NavStack()
    private var page: Page? = null
    private lateinit var content: FrameLayout
    private lateinit var title: TextView
    private lateinit var bar: LinearLayout
    private var barPad = 0
    private var navBottom = 0
    private lateinit var rootFrame: FrameLayout
    private lateinit var leading: ImageButton
    private lateinit var drawerPanel: LinearLayout
    private lateinit var scrim: View
    private lateinit var drawerItems: LinearLayout
    private var wide = false
    private var drawerOpen = false
    /** A link to put in the Capture screen's field (from Library's paste field or the clipboard). */
    internal var prefillLink: String? = null
    private val ui = Handler(Looper.getMainLooper())
    private val poll = object : Runnable { override fun run() {
        page?.tick()
        val n = app.jobs.activeCount()
        if (n != lastBadge) { lastBadge = n; if (wide || drawerOpen) refreshDrawer() }
        ui.postDelayed(this, 1500)
    } }
    internal val currentPage get() = page
    /** True when this screen was opened by a link from another app: Back (and the chip on the job card) finish it and return there. */
    internal var leaveOnBack = false
        private set
    private var callerPackage: String? = null
    private var lastBadge = -1

    /** Name of the app a link came from, for "← Back to <app>". */
    internal fun returnLabel(): String = callerPackage?.let { p ->
        try { packageManager.getApplicationLabel(packageManager.getApplicationInfo(p, 0)).toString() } catch (e: Exception) { null }
    } ?: "previous app"

    internal fun leaveToCaller() = finish()

    /** Routes a link, a notification tap or a job id to the Downloads page; a link also starts its job. */
    internal fun handleIntent(i: Intent) {
        val ref = try { referrer?.authority } catch (e: Exception) { null }
        val route = LinkRouting.resolve(i.dataString, i.getStringExtra(EXTRA_JOB), i.getBooleanExtra(EXTRA_DOWNLOADS, false), ref, packageName, i.getBooleanExtra(EXTRA_FROM_APP, false))
        if (!route.show) return
        if (route.invalidLink) { snack("This link is not a valid Medusa link."); return }
        if (route.request != null) {
            val req = route.request
            HelperService.start(this)
            app.jobs.submit(JobSubmit(req.id, req.url, req.languages ?: app.settings.defaultLanguages, Want.DEFAULT, Quality.P720, req.returnUrl))
        }
        if (i.dataString != null) { leaveOnBack = route.leaveOnBack; callerPackage = if (route.leaveOnBack) ref?.takeIf { it != packageName } else null }
        go(Dest.DOWNLOADS, route.jobId)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleIntent(intent)
    }

    /** A short message bar at the bottom of the screen (above the navigation bar) that goes away by itself. */
    fun snack(text: String) {
        val v = TextView(this).apply {
            this.text = text; tag = "snackbar"; setTextColor(0xFFFFFFFF.toInt()); textSize = 14f
            setBackgroundColor(0xF2323232.toInt()); setPadding(Ui.dp(this@MainActivity, 16), Ui.dp(this@MainActivity, 14), Ui.dp(this@MainActivity, 16), Ui.dp(this@MainActivity, 14))
        }
        rootFrame.addView(v, FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM).apply { setMargins(Ui.dp(this@MainActivity, 8), 0, Ui.dp(this@MainActivity, 8), navBottom + Ui.dp(this@MainActivity, 8)) })
        v.postDelayed({ rootFrame.removeView(v) }, 3500)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        setTheme(R.style.Theme_Medusa)
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        nav = NavStack.restore(savedInstanceState?.getStringArray("nav"))
        wide = resources.configuration.screenWidthDp >= 720

        val root = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; setBackgroundColor(Ui.color(this@MainActivity, R.color.bg)) }
        val frame = FrameLayout(this)
        val column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        bar = topBar(); barPad = Ui.dp(this, 56)
        column.addView(bar, LinearLayout.LayoutParams(-1, barPad))
        column.addView(Ui.divider(this))
        content = FrameLayout(this)
        column.addView(content, LinearLayout.LayoutParams(-1, 0, 1f))
        buildDrawer()
        if (wide) {
            root.addView(drawerPanel, LinearLayout.LayoutParams(Ui.dp(this, 240), -1))
            root.addView(column, LinearLayout.LayoutParams(0, -1, 1f))
            frame.addView(root, FrameLayout.LayoutParams(-1, -1))
        } else {
            frame.addView(column, FrameLayout.LayoutParams(-1, -1))
            scrim = View(this).apply { setBackgroundColor(0x88000000.toInt()); visibility = View.GONE; setOnClickListener { setDrawer(false) } }
            frame.addView(scrim, FrameLayout.LayoutParams(-1, -1))
            drawerPanel.visibility = View.GONE
            frame.addView(drawerPanel, FrameLayout.LayoutParams(Ui.dp(this, 280), -1, Gravity.START))
        }
        setContentView(frame)
        // the bar's background runs under the status bar, its content sits below it; the pages end above the navigation bar
        rootFrame = frame
        Edge.setup(this, frame) { i ->
            navBottom = i.bottom
            bar.setPadding(Ui.dp(this, 4), i.top, Ui.dp(this, 4), 0)
            bar.layoutParams = bar.layoutParams.apply { height = barPad + i.top }
            if (wide) {
                drawerPanel.setPadding(i.left, i.top + Ui.dp(this, 12), 0, i.bottom)
                content.setPadding(0, 0, i.right, i.bottom)
                bar.setPadding(Ui.dp(this, 4), i.top, i.right + Ui.dp(this, 4), 0)
            } else {
                drawerPanel.setPadding(i.left, i.top + Ui.dp(this, 16), 0, i.bottom)
                content.setPadding(i.left, 0, i.right, i.bottom)
                bar.setPadding(i.left + Ui.dp(this, 4), i.top, i.right + Ui.dp(this, 4), 0)
            }
        }
        show()
        if (savedInstanceState == null) handleIntent(intent) else leaveOnBack = savedInstanceState.getBoolean("leave"); callerPackage = savedInstanceState?.getString("caller")
    }

    override fun onSaveInstanceState(outState: Bundle) { super.onSaveInstanceState(outState); outState.putStringArray("nav", nav.save()); outState.putBoolean("leave", leaveOnBack); outState.putString("caller", callerPackage) }

    private fun topBar() = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
        setBackgroundColor(Ui.color(this@MainActivity, R.color.surface)); setPadding(Ui.dp(this@MainActivity, 4), 0, Ui.dp(this@MainActivity, 4), 0)
        leading = Ui.icon(this@MainActivity, R.drawable.ic_menu, "Menu") { onLeading() }
        addView(leading)
        title = Ui.text(this@MainActivity, "", 19f, bold = true).apply { maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END }
        addView(title, LinearLayout.LayoutParams(0, -2, 1f).apply { leftMargin = Ui.dp(this@MainActivity, 8) })
        addView(Ui.icon(this@MainActivity, R.drawable.ic_settings, "Settings") { go(Dest.SETTINGS) })
    }

    private fun buildDrawer() {
        drawerPanel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; setBackgroundColor(Ui.color(this@MainActivity, R.color.surface)); isClickable = true
        }
        drawerPanel.addView(Ui.text(this, "Medusa", 22f, R.color.accent, true).apply { setPadding(Ui.dp(this@MainActivity, 20), Ui.dp(this@MainActivity, 8), 0, 0) })
        drawerPanel.addView(Ui.muted(this, "Transcripts and videos on this phone").apply { setPadding(Ui.dp(this@MainActivity, 20), 0, Ui.dp(this@MainActivity, 16), Ui.dp(this@MainActivity, 14)) })
        drawerItems = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        drawerPanel.addView(drawerItems, LinearLayout.LayoutParams(-1, 0, 1f))
        drawerPanel.addView(Ui.muted(this, "Version " + BuildConfig.VERSION_NAME, 12f).apply { setPadding(Ui.dp(this@MainActivity, 20), Ui.dp(this@MainActivity, 8), 0, Ui.dp(this@MainActivity, 16)) })
    }

    private fun refreshDrawer() {
        drawerItems.removeAllViews()
        val icons = mapOf(Dest.LIBRARY to R.drawable.ic_library, Dest.CAPTURE to R.drawable.ic_capture, Dest.DOWNLOADS to R.drawable.ic_downloads, Dest.SETTINGS to R.drawable.ic_settings, Dest.ABOUT to R.drawable.ic_info)
        for (d in Dest.values().filter { it.top }) {
            val on = d == nav.highlighted
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; tag = "drawer:" + d.name; contentDescription = d.title
                setPadding(Ui.dp(this@MainActivity, 20), 0, Ui.dp(this@MainActivity, 12), 0)
                if (on) background = Ui.rounded(this@MainActivity, Ui.color(this@MainActivity, R.color.selected), 8)
                setOnClickListener { go(d) }
            }
            row.addView(android.widget.ImageView(this).apply { setImageResource(icons.getValue(d)); setColorFilter(Ui.color(this@MainActivity, if (on) R.color.accent else R.color.muted)) }, LinearLayout.LayoutParams(Ui.dp(this, 22), Ui.dp(this, 22)))
            row.addView(Ui.text(this, d.title, 15f, if (on) R.color.accent else R.color.on_surface, on).apply { setPadding(Ui.dp(this@MainActivity, 16), 0, 0, 0) }, LinearLayout.LayoutParams(0, -2, 1f))
            if (d == Dest.DOWNLOADS && app.jobs.activeCount() > 0)
                row.addView(Ui.badge(this, app.jobs.activeCount().toString(), true).apply { tag = "downloads-badge"; contentDescription = app.jobs.activeCount().toString() + " active downloads" })
            drawerItems.addView(row, LinearLayout.LayoutParams(-1, Ui.dp(this, 46)).apply { setMargins(Ui.dp(this@MainActivity, 8), Ui.dp(this@MainActivity, 2), Ui.dp(this@MainActivity, 8), Ui.dp(this@MainActivity, 2)) })
        }
    }

    private fun setDrawer(open: Boolean) {
        if (wide) return
        drawerOpen = open
        drawerPanel.visibility = if (open) View.VISIBLE else View.GONE
        scrim.visibility = if (open) View.VISIBLE else View.GONE
        if (open) refreshDrawer()
    }

    private fun onLeading() { if (nav.isTopLevel) setDrawer(!drawerOpen) else goBack() }

    /** A drawer destination (Library, Capture, Settings, About). */
    fun go(d: Dest, arg: String? = null) { setDrawer(false); nav.goTop(d, arg); show() }

    fun openItem(mediaId: String) { setDrawer(false); nav.open(Screen(Dest.ITEM, mediaId)); show() }

    /** Opens Capture; [link] fills its field. */
    fun capture(link: String? = null) { prefillLink = link; go(Dest.CAPTURE) }

    fun goBack() { if (!nav.back()) finish() else show() }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (drawerOpen) { setDrawer(false); return }
        // opened by a link from another app: Back returns there, as it was, from wherever we are in Medusa
        if (leaveOnBack) { finish(); return }
        if (!nav.back()) finish() else show()
    }

    /** The link on the clipboard when it is an http(s) one. */
    fun clipboardLink(): String? = try {
        (getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).primaryClip?.takeIf { it.itemCount > 0 }
            ?.getItemAt(0)?.coerceToText(this)?.toString()?.trim()?.takeIf { DeepLink.isHttp(it) }
    } catch (e: Exception) { null }

    internal fun show() {
        val s = nav.current
        val item = if (s.dest == Dest.ITEM) s.arg?.let { app.media.get(it) } else null
        if (s.dest == Dest.ITEM && item == null) { nav.back(); show(); return }
        val p: Page = when (s.dest) {
            Dest.LIBRARY -> LibraryPage(this)
            Dest.CAPTURE -> CapturePage(this)
            Dest.DOWNLOADS -> DownloadsPage(this, s.arg)
            Dest.SETTINGS -> SettingsPage(this)
            Dest.ABOUT -> AboutPage(this)
            Dest.ITEM -> ItemPage(this, s.arg!!)
        }
        page = p
        title.text = if (s.dest == Dest.ITEM) Titles.primary(item!!, app.settings.titleMode) else p.title
        leading.setImageResource(if (nav.isTopLevel) R.drawable.ic_menu else R.drawable.ic_back)
        leading.contentDescription = if (nav.isTopLevel) "Menu" else "Back"; leading.tag = leading.contentDescription
        leading.visibility = if (wide && nav.isTopLevel) View.GONE else View.VISIBLE
        content.removeAllViews()
        content.addView(p.build(), FrameLayout.LayoutParams(-1, -1))
        if (wide) refreshDrawer()
    }

    override fun onResume() {
        super.onResume()
        app.updates.checkIfDue()
        page?.resume()
        ui.post(poll)
    }

    override fun onPause() { ui.removeCallbacks(poll); super.onPause() }

    companion object { const val EXTRA_JOB = "job"; const val EXTRA_DOWNLOADS = "downloads"; const val EXTRA_FROM_APP = "from_app" }

    /** For the emulator test: the Capture screen's language choices. */
    internal fun setChoices(tracks: List<TrackInfo>) { (page as? CapturePage)?.setChoices(tracks) }
}
