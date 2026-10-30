package io.ref12.medusa

import android.content.Intent
import android.text.InputType
import android.view.View
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Toast
import java.text.DateFormat
import java.util.Date

/** Capture a link: link field, subtitle language, quality, progress of recent jobs, and "already in your library". */
class CapturePage(a: MainActivity) : Page(a) {
    override val title = "Capture"
    private lateinit var link: EditText
    private lateinit var inLib: LinearLayout
    private lateinit var inLibText: android.widget.TextView
    private lateinit var langBox: LinearLayout
    private lateinit var jobsBox: LinearLayout
    private var libraryId: String? = null
    private var langPicked = 0
    private var quality = Quality.P720
    private var langChoices: List<Pair<String, String?>> = emptyList()

    override fun build(): View {
        val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(Ui.dp(a, 12), Ui.dp(a, 8), Ui.dp(a, 12), Ui.dp(a, 24)) }
        col.addView(Ui.header(a, "Link").apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 10), 0, Ui.dp(a, 4)) })
        link = EditText(a).apply {
            hint = "Paste a media link"; setSingleLine(); inputType = InputType.TYPE_TEXT_VARIATION_URI or InputType.TYPE_CLASS_TEXT; tag = "capture-link"
            setTextColor(Ui.color(a, R.color.on_surface)); setHintTextColor(Ui.color(a, R.color.muted))
            addTextChangedListener(object : android.text.TextWatcher {
                override fun afterTextChanged(s: android.text.Editable?) { checkLibrary(s?.toString()?.trim().orEmpty()) }
                override fun beforeTextChanged(s: CharSequence?, st: Int, c: Int, af: Int) {}
                override fun onTextChanged(s: CharSequence?, st: Int, b: Int, c: Int) {}
            })
        }
        col.addView(link)
        inLibText = Ui.text(a, "", 13f, R.color.accent)
        inLib = LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL; visibility = View.GONE; tag = "in-library"; gravity = android.view.Gravity.CENTER_VERTICAL
            addView(inLibText, LinearLayout.LayoutParams(0, -2, 1f))
            addView(Ui.button(a, "Open") { libraryId?.let { a.openItem(it) } })
        }
        col.addView(inLib)
        col.addView(LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL
            addView(Ui.button(a, "List this video's languages") { b -> listLanguages(b) }, Ui.lp(-2, -2, 0f, 0, 6, 8, 0, a))
        })

        col.addView(Ui.header(a, "Subtitles").apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 14), 0, Ui.dp(a, 4)) })
        langBox = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL }
        col.addView(langBox)
        setChoices(emptyList())

        col.addView(Ui.header(a, "Video quality").apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 14), 0, Ui.dp(a, 4)) })
        col.addView(TrackUi.radios(a, Quality.values().map { it.wire }, Quality.values().map { "quality:" + it.wire }, 0) { quality = Quality.values()[it] })
        col.addView(Ui.button(a, "Capture", primary = true) { fetch() }, Ui.lp(-1, -2, 0f, 0, 14, 0, 0, a))

        col.addView(Ui.header(a, "Recent jobs").apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 20), 0, Ui.dp(a, 4)) })
        jobsBox = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL }
        col.addView(jobsBox)
        renderJobs()

        val pre = a.prefillLink ?: a.clipboardLink()
        a.prefillLink = null
        if (pre != null) link.setText(pre)
        return ScrollView(a).apply { addView(col); isFillViewport = true }
    }

    override fun tick() { if (::jobsBox.isInitialized) renderJobs() }

    internal fun checkLibrary(url: String) {
        val id = if (DeepLink.isHttp(url)) a.app.media.lookupId(url) else null
        val meta = id?.let { a.app.media.get(it) }
        libraryId = meta?.mediaId
        inLib.visibility = if (meta == null) View.GONE else View.VISIBLE
        if (meta != null) inLibText.text = "Already in your library: " + meta.title + "\nCapturing again only adds subtitle tracks you don't have yet."
    }

    internal fun setChoices(tracks: List<TrackInfo>) {
        val def = a.app.settings.defaultLanguage
        val first = (if (def.isEmpty()) "Original language" else "Default ($def)") to ""
        langChoices = listOf<Pair<String, String?>>(first) + tracks.map { TrackUi.label(it) to it.language } + listOf<Pair<String, String?>>("None" to null)
        langPicked = 0
        langBox.removeAllViews()
        langBox.addView(TrackUi.radios(a, langChoices.map { it.first }, langChoices.map { "capture:" + (it.second ?: "none").ifEmpty { "original" } }, 0) { langPicked = it })
    }

    private fun listLanguages(btn: View) {
        val url = link.text.toString().trim()
        if (!DeepLink.isHttp(url)) { link.error = "Needs an http(s) link"; return }
        btn.isEnabled = false
        Thread {
            val res = try { a.app.fetcher.listTracks(url) } catch (e: Exception) { null to (e.message ?: "failed") }
            a.runOnUiThread {
                btn.isEnabled = true
                if (res is List<*>) {
                    @Suppress("UNCHECKED_CAST") setChoices(res as List<TrackInfo>)
                    if (res.isEmpty()) Toast.makeText(a, "This video offers no subtitles", Toast.LENGTH_LONG).show()
                } else Toast.makeText(a, (res as Pair<*, *>).second.toString(), Toast.LENGTH_LONG).show()
            }
        }.start()
    }

    private fun fetch() {
        val url = link.text.toString().trim()
        if (!DeepLink.isHttp(url)) { link.error = "Needs an http(s) link"; return }
        HelperService.start(a)
        val choice = langChoices.getOrNull(langPicked)?.second
        val want = if (choice == null) Want(subtitles = false, video = true) else Want.DEFAULT
        val langs = if (choice.isNullOrEmpty()) a.app.settings.defaultLanguages.takeIf { choice == "" } ?: emptyList() else listOf(choice)
        val id = a.app.jobs.submit(JobSubmit(null, url, langs, want, quality))
        a.startActivity(Intent(a, JobActivity::class.java).putExtra(JobActivity.EXTRA_JOB, id).putExtra(JobActivity.EXTRA_FROM_APP, true))
    }

    private fun renderJobs() {
        jobsBox.removeAllViews()
        val df = DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT)
        val recent = a.app.jobs.recent().take(8)
        if (recent.isEmpty()) jobsBox.addView(Ui.muted(a, "None yet", 13f))
        for (j in recent) {
            val r = LinearLayout(a).apply {
                orientation = LinearLayout.VERTICAL; setPadding(0, Ui.dp(a, 6), 0, Ui.dp(a, 6))
                setOnClickListener { a.startActivity(Intent(a, JobActivity::class.java).putExtra(JobActivity.EXTRA_JOB, j.id).putExtra(JobActivity.EXTRA_FROM_APP, true)) }
            }
            r.addView(Ui.oneLine(Ui.text(a, j.title ?: j.url, 14f, bold = true)))
            r.addView(Ui.oneLine(Ui.muted(a, j.state.wire + (if (!j.state.finished && j.progress != null) " " + (j.progress * 100).toInt() + "%" else "") + "  \u00b7  " + df.format(Date(j.updatedAt)) + (j.message?.let { "  \u00b7  $it" } ?: ""), 12f)))
            if (!j.state.finished) r.addView(android.widget.ProgressBar(a, null, android.R.attr.progressBarStyleHorizontal).apply {
                isIndeterminate = j.progress == null; max = 100; progress = ((j.progress ?: 0.0) * 100).toInt()
            }, Ui.lp(-1, Ui.dp(a, 6)))
            jobsBox.addView(r); jobsBox.addView(Ui.divider(a))
        }
    }
}
