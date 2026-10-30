package io.ref12.medusa

import android.app.AlertDialog
import android.content.Intent
import android.net.Uri
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView

/** One captured item: details, its subtitle tracks with actions, import, play and delete. */
class ItemPage(a: MainActivity, private val mediaId: String) : Page(a) {
    override val title = "Item"

    override fun resume() { a.show() } // the item may have changed (imports) while another screen was open

    /** Fetches the localized title off the main thread, then redraws. [force] repeats an earlier lookup. */
    private fun refresh(btn: View?, force: Boolean) {
        btn?.isEnabled = false
        val app = a.app
        Thread {
            val updated = try {
                app.jobs.refreshTitle(mediaId, done = { l -> !force && app.settings.titleChecked(mediaId) == l }, mark = { l -> app.settings.setTitleChecked(mediaId, l) })
            } catch (e: Exception) { null }
            a.runOnUiThread {
                btn?.isEnabled = true
                if (updated != null && a.currentPage === this) a.show()
                else if (force) android.widget.Toast.makeText(a, "No change", android.widget.Toast.LENGTH_SHORT).show()
            }
        }.start()
    }

    override fun build(): View {
        val m = a.app.media.get(mediaId) ?: return View(a)
        val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(Ui.dp(a, 12), Ui.dp(a, 12), Ui.dp(a, 12), Ui.dp(a, 24)) }
        // a one-off lookup of the title in the device language for items captured before it existed
        val lang = a.app.settings.titleLanguage()
        if (lang != null && Titles.supportsLocalized(m.extractor) && a.app.settings.titleChecked(mediaId) != lang) refresh(null, force = false)
        // summary
        val sum = Ui.card(a)
        val mode = a.app.settings.titleMode
        sum.addView(Ui.text(a, Titles.primary(m, mode), 17f, bold = true).apply { tag = "item-title" })
        Titles.secondary(m, mode)?.let { o -> sum.addView(Ui.muted(a, o, 13f).apply { tag = "item-original-title"; setPadding(0, Ui.dp(a, 2), 0, 0) }) }
        sum.addView(Ui.text(a, m.url, 13f, R.color.accent).apply {
            maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.MIDDLE; tag = "source-link"; setPadding(0, Ui.dp(a, 4), 0, Ui.dp(a, 4))
            setOnClickListener { try { a.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(m.url))) } catch (e: Exception) {} }
        })
        sum.addView(Ui.muted(a, listOfNotNull(Ui.duration(m.durationSec), m.extractor.takeIf { it.isNotEmpty() }, Ui.size(a.app.media.bytesOf(m.mediaId)),
            m.createdAt.takeIf { it > 0 }?.let { "downloaded " + Ui.date(it) }).joinToString("  \u00b7  ")))
        if (Titles.supportsLocalized(m.extractor) && a.app.settings.titleLanguage() != null)
            {
                lateinit var chip: View
                chip = Ui.chipButton(a, "Refresh details", false, "refresh-details") { refresh(chip, force = true) }
                sum.addView(chip, Ui.lp(-2, -2, 0f, 0, 8, 0, 0, a))
            }
        col.addView(sum)
        // actions
        col.addView(LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL
            val hasVideo = m.video != null
            addView(Ui.button(a, if (hasVideo) "Play" else "Play (no video)", primary = hasVideo) {
                if (hasVideo) a.startActivity(Intent(a, PlayerActivity::class.java).putExtra(PlayerActivity.EXTRA_MEDIA, m.mediaId))
            }.apply { isEnabled = hasVideo; alpha = if (hasVideo) 1f else 0.45f; tag = "play" }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
            addView(Ui.button(a, "Import subtitles") {
                a.startActivity(Intent(a, ImportActivity::class.java).putExtra(ImportActivity.EXTRA_MEDIA, m.mediaId))
            }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
            addView(Ui.button(a, "Delete", danger = true) {
                AlertDialog.Builder(a).setTitle("Delete?").setMessage(m.title)
                    .setPositiveButton("Delete") { _, _ -> a.app.media.delete(m.mediaId); a.goBack() }.setNegativeButton("Cancel", null).show()
            })
        }, Ui.lp(-1, -2, 0f, 0, 10, 0, 0, a))
        // tracks
        val tracks = TrackUi.tracksOf(m)
        val c = TrackUi.resolve(m.subtitles?.language, tracks, a.app.settings.trackChoice(m.mediaId, "main"), a.app.settings.trackChoice(m.mediaId, "second"))
        col.addView(Ui.header(a, "Subtitle tracks (" + tracks.size + ")").apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 18), 0, Ui.dp(a, 6)) })
        if (tracks.isEmpty()) col.addView(Ui.muted(a, "This item has no subtitle tracks. Import a file to add one.", 14f))
        val card = Ui.card(a)
        for ((i, t) in tracks.withIndex()) {
            if (i > 0) card.addView(Ui.divider(a))
            val box = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(0, Ui.dp(a, 8), 0, Ui.dp(a, 8)); tag = "track:" + t.key }
            box.addView(LinearLayout(a).apply {
                orientation = LinearLayout.HORIZONTAL; gravity = android.view.Gravity.CENTER_VERTICAL
                addView(Ui.text(a, TrackUi.label(t), 14f, bold = true), LinearLayout.LayoutParams(0, -2, 1f))
                if (c.main.equals(t.key, true)) addView(Ui.badge(a, "MAIN", true).apply { tag = "badge-main:" + t.key }, Ui.lp(-2, -2, 0f, 0, 0, 4, 0, a))
                if (c.second.equals(t.key, true)) addView(Ui.badge(a, "2nd", true).apply { tag = "badge-second:" + t.key }, Ui.lp(-2, -2, 0f, 0, 0, 4, 0, a))
                addView(Ui.badge(a, if (t.added) "added" else t.source.replace("-", " "), t.added))
            })
            val isMain = c.main.equals(t.key, true); val isSecond = c.second.equals(t.key, true)
            box.addView(LinearLayout(a).apply {
                orientation = LinearLayout.HORIZONTAL
                fun chip(txt: String, on: Boolean, tg: String, f: () -> Unit) = addView(Ui.chipButton(a, txt, on, tg + ":" + t.key, f), Ui.lp(-2, -2, 0f, 0, 0, 6, 0, a))
                chip(if (isMain) "\u2713 Main" else "Main", isMain, "main") { a.app.settings.setTrackChoice(m.mediaId, "main", if (isMain) null else t.key); a.show() }
                chip(if (isSecond) "\u2713 Second line" else "Second line", isSecond, "second") { a.app.settings.setTrackChoice(m.mediaId, "second", if (isSecond) null else t.key); a.show() }
                chip("Export", false, "export") { ExportUi.show(a, m, t.key) }
                if (t.added) chip("Remove", false, "remove") {
                    AlertDialog.Builder(a).setTitle("Remove this track?").setMessage(TrackUi.label(t)).setPositiveButton("Remove") { _, _ ->
                        a.app.media.removeTrack(m.mediaId, t.trackId!!)
                        for (slot in listOf("main", "second")) if (t.key.equals(a.app.settings.trackChoice(m.mediaId, slot), true)) a.app.settings.setTrackChoice(m.mediaId, slot, null)
                        a.show()
                    }.setNegativeButton("Cancel", null).show()
                }
            }, Ui.lp(-1, -2, 0f, 0, 6, 0, 0, a))
            card.addView(box)
        }
        if (tracks.isNotEmpty()) col.addView(card)
        return ScrollView(a).apply { addView(col) }
    }
}
