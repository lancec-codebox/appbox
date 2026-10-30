package io.ref12.medusa

import android.app.AlertDialog
import android.view.View
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import java.util.Calendar

/** Active downloads on top (title or link, stage, progress, Cancel), then finished and failed ones of today and this week. */
class DownloadsPage(a: MainActivity, private val highlight: String?) : Page(a) {
    override val title = "Downloads"
    private lateinit var box: LinearLayout
    private var lastKey = ""

    override fun build(): View {
        box = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(Ui.dp(a, 12), Ui.dp(a, 10), Ui.dp(a, 12), Ui.dp(a, 16)) }
        fill(true)
        return ScrollView(a).apply { addView(box) }
    }

    override fun tick() { if (::box.isInitialized) fill(false) }
    override fun resume() { if (::box.isInitialized) fill(true) }

    private fun startOfToday(): Long = Calendar.getInstance().apply { set(Calendar.HOUR_OF_DAY, 0); set(Calendar.MINUTE, 0); set(Calendar.SECOND, 0); set(Calendar.MILLISECOND, 0) }.timeInMillis

    private fun fill(force: Boolean) {
        val g = DownloadsLogic.group(a.app.jobs.list(), System.currentTimeMillis(), startOfToday())
        val key = (g.active + g.today + g.week).joinToString("|") { it.id + it.state + it.progress + it.message + it.title }
        if (!force && key == lastKey) return
        lastKey = key
        box.removeAllViews()
        if (g.active.isEmpty() && g.today.isEmpty() && g.week.isEmpty()) {
            box.addView(Ui.text(a, "No downloads yet", 16f, bold = true).apply { setPadding(0, Ui.dp(a, 28), 0, Ui.dp(a, 6)) })
            box.addView(Ui.muted(a, "Downloads you start here, from another app or from a web app show up on this page.", 14f))
            return
        }
        section("Active", g.active, true)
        section("Today", g.today, false)
        section("This week", g.week, false)
    }

    private fun section(name: String, jobs: List<JobMeta>, active: Boolean) {
        if (jobs.isEmpty()) return
        box.addView(Ui.header(a, name))
        for (j in jobs) box.addView(card(j, active), Ui.lp(-1, -2, 0f, 0, 6, 0, 2, a))
    }

    private fun card(j: JobMeta, active: Boolean): View {
        val on = j.id == highlight
        val c = LinearLayout(a).apply {
            orientation = LinearLayout.VERTICAL; tag = "job:" + j.id; contentDescription = DownloadsLogic.displayTitle(j)
            setPadding(Ui.dp(a, 12), Ui.dp(a, 10), Ui.dp(a, 12), Ui.dp(a, 10))
            background = Ui.rounded(a, Ui.color(a, if (on) R.color.selected else R.color.surface), 8, if (on) Ui.color(a, R.color.accent) else Ui.color(a, R.color.divider))
        }
        c.addView(Ui.text(a, DownloadsLogic.displayTitle(j), 15f, bold = true).apply { maxLines = 2; ellipsize = android.text.TextUtils.TruncateAt.END; tag = "job-title:" + j.id })
        c.addView(Ui.muted(a, DownloadsLogic.stageText(j.state, j.message, j.progress), 13f).apply { tag = "job-stage:" + j.id; setPadding(0, Ui.dp(a, 2), 0, Ui.dp(a, 4)) })
        if (!j.state.finished) c.addView(ProgressBar(a, null, android.R.attr.progressBarStyleHorizontal).apply {
            tag = "job-bar:" + j.id
            isIndeterminate = DownloadsLogic.indeterminate(j.state, j.progress); max = 1000; progress = ((j.progress ?: 0.0) * 1000).toInt()
        }, Ui.lp(-1, Ui.dp(a, 6)))
        val actions = LinearLayout(a).apply { orientation = LinearLayout.HORIZONTAL; gravity = android.view.Gravity.CENTER_VERTICAL }
        if (on && a.leaveOnBack) actions.addView(Ui.chipButton(a, "\u2190 Back to " + a.returnLabel(), true, "back-to-app") { a.leaveToCaller() }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
        val ret = j.returnUrl
        if (on && !a.leaveOnBack && j.state.finished && BackPolicy.showReturnLink(ret, false))
            actions.addView(Ui.chipButton(a, BackPolicy.returnLabel(ret!!), false, "open-return") {
                a.startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(DeepLink.buildReturnUrl(ret, j.id, if (j.state == JobState.DONE) "done" else "failed"))))
            }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
        if (active) actions.addView(Ui.button(a, "Cancel") { a.app.jobs.cancel(j.id); fill(true) }.apply { tag = "cancel:" + j.id })
        else {
            val m = j.mediaId?.let { a.app.media.get(it) }
            if (j.state == JobState.DONE && m != null && a.app.settings.ocrOffered(m.mediaId) && m.video != null) {
                c.addView(Ui.muted(a, "No subtitle track was found. If the subtitles are in the picture, Medusa can read them.", 13f).apply { tag = "ocr-offer-text:" + j.id })
                actions.addView(Ui.button(a, "Read subtitles from picture") { OcrUi.configure(a, m) }.apply { tag = "ocr-offer:" + j.id }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
            }
            if (j.state == JobState.DONE && m != null) actions.addView(Ui.button(a, "Open", primary = true) { a.openItem(m.mediaId) }.apply { tag = "open:" + j.id })
            if (j.state == JobState.FAILED) {
                val partial = a.app.jobs.partialBytes(j.id)
                actions.addView(Ui.button(a, if (partial > 0) "Resume" else "Retry", primary = true) { a.app.jobs.resume(j.id); HelperService.start(a); fill(true) }.apply { tag = "resume:" + j.id }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
                actions.addView(Ui.button(a, "Download again") {
                    AlertDialog.Builder(a).setTitle("Download again?").setMessage("This deletes what was downloaded so far and starts over.")
                        .setPositiveButton("Download again") { _, _ -> a.app.jobs.restart(j.id); HelperService.start(a); fill(true) }.setNegativeButton("Cancel", null).show()
                }.apply { tag = "restart:" + j.id }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
            }
            actions.addView(Ui.button(a, "Remove") { a.app.jobs.remove(j.id); fill(true) }.apply { tag = "remove:" + j.id })
        }
        if (actions.childCount > 0) c.addView(actions, Ui.lp(-1, -2, 0f, 0, 6, 0, 0, a))
        return c
    }
}
