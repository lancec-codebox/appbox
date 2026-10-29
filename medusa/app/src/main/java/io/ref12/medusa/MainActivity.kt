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
    private lateinit var langSpinner: android.widget.Spinner
    private lateinit var defaultLangEdit: EditText
    /** Spinner entries: label to (null = no subtitles, "" = original, else a language code). */
    private var langChoices: List<Pair<String, String?>> = listOf("Original language" to "", "None" to null)
    private lateinit var recentBox: LinearLayout
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
        statusView = TextView(this).apply { textSize = 16f; setPadding(0, 16, 0, 0) }
        versionView = TextView(this).apply { textSize = 14f }
        updateBtn = Button(this).apply { text = "Update yt-dlp now"; setOnClickListener { updateNow() } }
        add(statusView); add(versionView); add(updateBtn)

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
        langSpinner = android.widget.Spinner(this)
        setChoices(emptyList())
        add(label("Subtitles"))
        add(langSpinner)
        add(Button(this).apply { text = "List this video's languages"; setOnClickListener { listLanguages(this) } })
        add(Button(this).apply { text = "Capture"; setOnClickListener { fetch() } })

        add(label("Recent jobs"))
        recentBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        add(recentBox)
        setContentView(ScrollView(this).apply { addView(col) })
    }

    override fun onResume() { super.onResume(); ui.post(poll) }
    override fun onPause() { ui.removeCallbacks(poll); super.onPause() }

    private fun fetch() {
        val url = linkEdit.text.toString().trim()
        if (!DeepLink.isHttp(url)) { linkEdit.error = "Needs an http(s) link"; return }
        HelperService.start(this)
        val choice = langChoices.getOrNull(langSpinner.selectedItemPosition)?.second
        val want = if (choice == null) Want(subtitles = false, video = true) else Want.DEFAULT
        val langs = if (choice.isNullOrEmpty()) app.settings.defaultLanguages.takeIf { choice == "" } ?: emptyList() else listOf(choice)
        val id = app.jobs.submit(JobSubmit(null, url, langs, want))
        startActivity(Intent(this, JobActivity::class.java).putExtra(JobActivity.EXTRA_JOB, id))
    }

    private fun setChoices(tracks: List<TrackInfo>) {
        val def = app.settings.defaultLanguage
        langChoices = listOf((if (def.isEmpty()) "Original language" else "Default ($def)") to "", "None" to null) +
            tracks.map { (it.name ?: it.language) + " (" + it.language + ", " + (if (it.source == "captions") "captions" else "auto") + ")" to it.language }
        langSpinner.adapter = android.widget.ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, langChoices.map { it.first })
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
            setOnClickListener { startActivity(Intent(this@MainActivity, JobActivity::class.java).putExtra(JobActivity.EXTRA_JOB, j.id)) }
        })
    }
}
