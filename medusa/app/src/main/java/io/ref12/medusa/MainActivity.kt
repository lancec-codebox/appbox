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
        add(label("Try a link"))
        linkEdit = EditText(this).apply { hint = "Paste a media link"; inputType = InputType.TYPE_TEXT_VARIATION_URI or InputType.TYPE_CLASS_TEXT }
        add(linkEdit)
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
        val id = app.jobs.submit(JobSubmit(null, url, null))
        startActivity(Intent(this, JobActivity::class.java).putExtra(JobActivity.EXTRA_JOB, id))
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
