package io.ref12.medusa

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.text.DateFormat
import java.util.Date

/** Plain main screen: status, versions, origins, recent jobs and a paste-a-link test field. */
class MainActivity : Activity() {
    private lateinit var statusView: TextView
    private lateinit var versionView: TextView
    private lateinit var updateBtn: Button
    private lateinit var originsEdit: EditText
    private lateinit var linkEdit: EditText
    private lateinit var libraryRow: LinearLayout
    private lateinit var libraryText: TextView
    private var libraryId: String? = null
    private lateinit var langBox: LinearLayout
    private var langPicked = 0
    private lateinit var defaultLangEdit: EditText
    /** Spinner entries: label to (null = no subtitles, "" = original, else a language code). */
    private var langChoices: List<Pair<String, String?>> = listOf("Original language" to "", "None" to null)
    private lateinit var recentBox: LinearLayout
    private lateinit var updateBox: LinearLayout
    private var wentToPermission = false
    private val ui = Handler(Looper.getMainLooper())
    private val poll = object : Runnable {
        override fun run() { refresh(); ui.postDelayed(this, 1500) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 80, 40, 40) }
        fun label(t: String) = TextView(this).apply { text = t; textSize = 14f; setPadding(0, 32, 0, 8); setTypeface(typeface, android.graphics.Typeface.BOLD) }
        fun add(v: android.view.View) = col.addView(v, ViewGroup.LayoutParams(-1, -2))

        add(TextView(this).apply { text = "Medusa"; textSize = 24f })
        updateBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(0xFFE8F0FE.toInt()); setPadding(24, 16, 24, 16); visibility = android.view.View.GONE }
        add(updateBox)
        statusView = TextView(this).apply { textSize = 16f; setPadding(0, 16, 0, 0) }
        versionView = TextView(this).apply { textSize = 14f }
        updateBtn = Button(this).apply { text = "Update yt-dlp now"; setOnClickListener { updateNow() } }
        add(statusView); add(versionView); add(updateBtn)

        add(label("App updates"))
        add(android.widget.CheckBox(this).apply {
            text = "Check for Medusa updates automatically"
            isChecked = app.settings.autoUpdateCheck
            setOnCheckedChangeListener { _, on -> app.settings.autoUpdateCheck = on }
        })
        add(Button(this).apply { text = "Check for updates"; setOnClickListener { app.updates.check(manual = true) } })
        add(label("Keep Medusa ready after you leave it"))
        add(TrackUi.radios(this, KeepAliveMode.values().map { it.label }, KeepAliveMode.values().map { "keepalive:" + it.minutes }, app.settings.keepAlive.ordinal) {
            app.settings.keepAlive = KeepAliveMode.values()[it]
        })
        add(label("Allowed origins (one per line)"))
        originsEdit = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_VARIATION_URI
            setText(app.settings.origins.joinToString("\n"))
        }
        add(originsEdit)
        add(Button(this).apply {
            text = "Save origins"
            setOnClickListener { app.settings.origins = originsEdit.text.toString().lines().map { it.trim() }.filter { it.isNotEmpty() } }
        })

        add(Button(this).apply { text = "Library"; setOnClickListener { startActivity(Intent(this@MainActivity, LibraryActivity::class.java)) } })
        add(label("Default subtitle language (blank = original)"))
        defaultLangEdit = EditText(this).apply { hint = "Original, or a code like en, zh-Hans"; setText(app.settings.defaultLanguage) }
        add(defaultLangEdit)
        add(Button(this).apply {
            text = "Save default language"
            setOnClickListener { app.settings.defaultLanguage = defaultLangEdit.text.toString(); setChoices(emptyList()) }
        })
        add(label("Try a link"))
        linkEdit = EditText(this).apply { hint = "Paste a media link"; inputType = InputType.TYPE_TEXT_VARIATION_URI or InputType.TYPE_CLASS_TEXT }
        add(linkEdit)
        libraryText = TextView(this).apply { textSize = 14f; setTextColor(0xFF1B5E20.toInt()) }
        libraryRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; visibility = android.view.View.GONE; tag = "in-library"
            addView(libraryText, LinearLayout.LayoutParams(0, -2, 1f))
            addView(Button(context).apply { text = "Open"; setOnClickListener { openLibraryItem() } })
        }
        add(libraryRow)
        linkEdit.addTextChangedListener(object : android.text.TextWatcher {
            override fun afterTextChanged(s: android.text.Editable?) { checkLibrary(s?.toString()?.trim().orEmpty()) }
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
        })
        langBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        add(label("Subtitles"))
        add(langBox)
        setChoices(emptyList())
        add(Button(this).apply { text = "List this video's languages"; setOnClickListener { listLanguages(this) } })
        add(Button(this).apply { text = "Capture"; setOnClickListener { fetch() } })

        add(label("Recent jobs"))
        recentBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        add(recentBox)
        setContentView(ScrollView(this).apply { addView(col) })
    }

    override fun onResume() {
        super.onResume()
        if (wentToPermission) { wentToPermission = false; app.updates.resumeAfterPermission() }
        app.updates.checkIfDue()
        ui.post(poll)
    }

    private fun renderUpdate() {
        val u = app.updates
        updateBox.removeAllViews()
        fun text(t: String, bold: Boolean = false) = updateBox.addView(TextView(this).apply {
            text = t; textSize = 14f; if (bold) setTypeface(typeface, android.graphics.Typeface.BOLD)
        })
        fun buttons(vararg b: Pair<String, () -> Unit>) = updateBox.addView(LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            for ((t, f) in b) addView(Button(context).apply { text = t; setOnClickListener { f() } })
        })
        when (val s = u.state) {
            is UpdateState.Available -> {
                text("Medusa " + s.offer.version + " is available", true)
                if (s.offer.notes.isNotEmpty()) text(s.offer.notes.take(1200))
                buttons("Update" to { u.update() }, "Later" to { u.later() })
            }
            is UpdateState.Downloading -> text("Downloading Medusa " + s.offer.version + (s.fraction?.let { " " + (it * 100).toInt() + "%" } ?: "..."), true)
            is UpdateState.NeedsPermission -> {
                text("Allow Medusa to install apps", true)
                text("Android needs your OK once. Turn on Allow from this source, then come back: the update continues.")
                buttons("Open settings" to { wentToPermission = true; startActivity(PackageApkInstaller.permissionIntent(this)) })
            }
            is UpdateState.Installing -> text(s.message + " Medusa " + s.offer.version + "...", true)
            is UpdateState.Done -> text(s.message, true)
            is UpdateState.Failed -> {
                text(s.message, true)
                buttons(*(listOfNotNull(if (s.offer != null) "Try again" to { u.update() } else null, "Dismiss" to { u.later() }).toTypedArray()))
            }
            is UpdateState.UpToDate -> text("Medusa " + s.version + " is up to date")
            is UpdateState.Checking -> text("Checking for updates...")
            UpdateState.Idle -> {}
        }
        updateBox.visibility = if (u.state is UpdateState.Idle) android.view.View.GONE else android.view.View.VISIBLE
    }
    override fun onPause() { ui.removeCallbacks(poll); super.onPause() }

    /** Shows "Already in your library" when the pasted link is a video that is already captured. */
    internal fun checkLibrary(url: String) {
        val id = if (DeepLink.isHttp(url)) app.media.lookupId(url) else null
        val meta = id?.let { app.media.get(it) }
        libraryId = meta?.mediaId
        libraryRow.visibility = if (meta == null) android.view.View.GONE else android.view.View.VISIBLE
        if (meta != null) libraryText.text = "Already in your library: " + meta.title + "\nCapturing again only adds subtitle tracks you don't have yet."
    }

    private fun openLibraryItem() {
        val id = libraryId ?: return
        if (app.media.get(id)?.video != null) startActivity(Intent(this, PlayerActivity::class.java).putExtra(PlayerActivity.EXTRA_MEDIA, id))
        else startActivity(Intent(this, LibraryActivity::class.java))
    }

    private fun fetch() {
        val url = linkEdit.text.toString().trim()
        if (!DeepLink.isHttp(url)) { linkEdit.error = "Needs an http(s) link"; return }
        HelperService.start(this)
        val choice = langChoices.getOrNull(langPicked)?.second
        val want = if (choice == null) Want(subtitles = false, video = true) else Want.DEFAULT
        val langs = if (choice.isNullOrEmpty()) app.settings.defaultLanguages.takeIf { choice == "" } ?: emptyList() else listOf(choice)
        val id = app.jobs.submit(JobSubmit(null, url, langs, want))
        startActivity(Intent(this, JobActivity::class.java).putExtra(JobActivity.EXTRA_JOB, id).putExtra(JobActivity.EXTRA_FROM_APP, true))
    }

    /** Shows the subtitle choices as radio buttons: original language, each listed track, none. */
    internal fun setChoices(tracks: List<TrackInfo>) {
        val def = app.settings.defaultLanguage
        langChoices = listOf((if (def.isEmpty()) "Original language" else "Default ($def)") to "", "None" to null) +
            tracks.map { TrackUi.label(it) to it.language }
        // keep the order: original, tracks, none
        langChoices = listOf(langChoices[0]) + langChoices.drop(2) + listOf(langChoices[1])
        langPicked = 0
        langBox.removeAllViews()
        langBox.addView(TrackUi.radios(this, langChoices.map { it.first }, langChoices.map { "capture:" + (it.second ?: "none").ifEmpty { "original" } }, 0) { langPicked = it })
    }

    private fun listLanguages(btn: Button) {
        val url = linkEdit.text.toString().trim()
        if (!DeepLink.isHttp(url)) { linkEdit.error = "Needs an http(s) link"; return }
        btn.isEnabled = false
        Thread {
            val res = try { app.fetcher.listTracks(url) } catch (e: Exception) { null to (e.message ?: "failed") }
            runOnUiThread {
                btn.isEnabled = true
                if (res is List<*>) {
                    @Suppress("UNCHECKED_CAST") setChoices(res as List<TrackInfo>)
                    if (res.isEmpty()) android.widget.Toast.makeText(this, "This video offers no subtitles", android.widget.Toast.LENGTH_LONG).show()
                } else android.widget.Toast.makeText(this, (res as Pair<*, *>).second.toString(), android.widget.Toast.LENGTH_LONG).show()
            }
        }.start()
    }

    private fun updateNow() {
        updateBtn.isEnabled = false
        updateBtn.text = "Updating..."
        Thread {
            val msg = try { app.fetcher.update() } catch (e: Exception) { "Update failed: " + e.message }
            runOnUiThread { updateBtn.isEnabled = true; updateBtn.text = "Update yt-dlp now"; versionView.text = msg }
        }.start()
    }

    private var versionLoaded = false

    private fun refresh() {
        renderUpdate()
        statusView.text = when {
            HelperService.running -> "Server running on 127.0.0.1:$SERVER_PORT"
            HelperService.lastError != null -> HelperService.lastError
            else -> "Server stopped (starts when a web app or a test link asks for a transcript)"
        }
        if (!versionLoaded && !app.fetcher.updating) {
            versionLoaded = true
            versionView.text = "Medusa " + BuildConfig.VERSION_NAME + "\nyt-dlp: ..."
            Thread {
                val v = app.fetcher.version()
                runOnUiThread { versionView.text = "Medusa " + BuildConfig.VERSION_NAME + "\nyt-dlp: " + v }
            }.start()
        }
        recentBox.removeAllViews()
        val df = DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT)
        val recent = app.jobs.recent()
        if (recent.isEmpty()) recentBox.addView(TextView(this).apply { text = "None yet" })
        for (j in recent) recentBox.addView(TextView(this).apply {
            text = (j.title ?: j.url) + "\n" + j.state.wire + " - " + df.format(Date(j.updatedAt)) + (j.message?.let { "\n$it" } ?: "")
            setPadding(0, 12, 0, 12)
            setOnClickListener { startActivity(Intent(this@MainActivity, JobActivity::class.java).putExtra(JobActivity.EXTRA_JOB, j.id).putExtra(JobActivity.EXTRA_FROM_APP, true)) }
        })
    }
}
