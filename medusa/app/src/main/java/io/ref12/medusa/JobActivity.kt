package io.ref12.medusa

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast

/** Deep link target (medusa://open?...) and job screen. Also opened for a job id from the main screen. */
class JobActivity : Activity() {
    private lateinit var title: TextView
    private lateinit var status: TextView
    private lateinit var bar: ProgressBar
    private lateinit var back: Button
    private lateinit var save: Button
    private lateinit var play: Button
    private val ui = Handler(Looper.getMainLooper())
    private var jobId: String? = null
    private var returned = false
    private var resumed = false

    private val poll = object : Runnable {
        override fun run() { refresh(); ui.postDelayed(this, 500) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(48, 96, 48, 48) }
        title = TextView(this).apply { textSize = 18f }
        status = TextView(this).apply { textSize = 16f; setPadding(0, 24, 0, 24) }
        bar = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply { isIndeterminate = true }
        back = Button(this).apply { text = "Back to app"; setOnClickListener { goBack() } }
        save = Button(this).apply { text = "Save transcript"; setOnClickListener { save() } }
        play = Button(this).apply { text = "Play"; setOnClickListener {
            jobId?.let { id -> app.jobs.record(id)?.let { r -> startActivity(Intent(this@JobActivity, PlayerActivity::class.java).putExtra(PlayerActivity.EXTRA_MEDIA, r.mediaId)) } }
        } }
        for (v in listOf(title, status, bar, play, back, save)) col.addView(v, ViewGroup.LayoutParams(-1, -2))
        setContentView(col)
        handle(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        returned = false
        handle(intent)
    }

    private fun handle(intent: Intent) {
        val data = intent.dataString
        if (data != null) {
            val req = DeepLink.parse(data)
            if (req == null) { status.text = "This link is not a valid Medusa link."; jobId = null; return }
            askNotificationPermission()
            HelperService.start(this)
            jobId = app.jobs.submit(JobSubmit(req.id, req.url, req.languages ?: app.settings.defaultLanguages, Want.DEFAULT, Quality.P720, req.returnUrl))
        } else {
            jobId = intent.getStringExtra(EXTRA_JOB)
        }
        refresh()
    }

    private fun askNotificationPermission() {
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
    }

    override fun onResume() { super.onResume(); resumed = true; ui.post(poll) }
    override fun onPause() { resumed = false; ui.removeCallbacks(poll); super.onPause() }

    private fun refresh() {
        val id = jobId ?: return
        val rec = app.jobs.record(id)
        if (rec == null) { status.text = "Unknown job"; bar.visibility = android.view.View.GONE; return }
        title.text = rec.title ?: rec.url
        val finished = rec.state.finished
        bar.visibility = if (finished) android.view.View.GONE else android.view.View.VISIBLE
        val pr = rec.progress
        bar.isIndeterminate = pr == null
        if (pr != null) { bar.max = 1000; bar.progress = (pr * 1000).toInt() }
        play.visibility = if (rec.state == JobState.DONE && app.media.get(rec.mediaId)?.video != null) android.view.View.VISIBLE else android.view.View.GONE
        status.text = when (rec.state) {
            JobState.QUEUED -> "Waiting"
            JobState.FETCHING, JobState.DOWNLOADING -> (rec.message ?: "Working") + (rec.progress?.let { " " + (it * 100).toInt() + "%" } ?: "")
            JobState.DONE -> "Done. Captured" + (rec.message?.let { "\n$it" } ?: "")
            JobState.FAILED -> "Failed: " + (rec.message ?: "unknown error")
        }
        back.visibility = if (rec.returnUrl != null) android.view.View.VISIBLE else android.view.View.GONE
        save.visibility = if (rec.state == JobState.DONE) android.view.View.VISIBLE else android.view.View.GONE
        if (finished && !returned && resumed && rec.returnUrl != null && intent.dataString != null) {
            returned = true
            goBack()
        }
    }

    private fun goBack() {
        val rec = jobId?.let { app.jobs.record(it) } ?: return
        val ret = rec.returnUrl ?: return
        val url = DeepLink.buildReturnUrl(ret, rec.id, if (rec.state == JobState.DONE) "done" else "failed")
        startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
    }

    private fun save() {
        if (Build.VERSION.SDK_INT < 29 && checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.WRITE_EXTERNAL_STORAGE), 2)
            return
        }
        val id = jobId ?: return
        val json = app.jobs.resultJson(id)
        if (json == null) { Toast.makeText(this, "No result to save (expired?)", Toast.LENGTH_LONG).show(); return }
        try {
            val where = TranscriptSaver.save(this, app.jobs.record(id)?.title, json)
            Toast.makeText(this, "Saved to $where", Toast.LENGTH_LONG).show()
        } catch (e: Exception) {
            Toast.makeText(this, "Save failed: " + e.message, Toast.LENGTH_LONG).show()
        }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 2 && grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) save()
    }

    companion object { const val EXTRA_JOB = "job" }
}
