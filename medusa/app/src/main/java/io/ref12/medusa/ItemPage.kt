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

    override fun build(): View {
        val m = a.app.media.get(mediaId) ?: return View(a)
        val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(Ui.dp(a, 12), Ui.dp(a, 12), Ui.dp(a, 12), Ui.dp(a, 24)) }
        // summary
        val sum = Ui.card(a)
        sum.addView(Ui.text(a, m.title.ifEmpty { m.url }, 17f, bold = true))
        sum.addView(Ui.text(a, m.url, 13f, R.color.accent).apply {
            maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.MIDDLE; tag = "source-link"; setPadding(0, Ui.dp(a, 4), 0, Ui.dp(a, 4))
            setOnClickListener { try { a.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(m.url))) } catch (e: Exception) {} }
        })
        sum.addView(Ui.muted(a, listOfNotNull(Ui.duration(m.durationSec), m.extractor.takeIf { it.isNotEmpty() }, Ui.size(a.app.media.bytesOf(m.mediaId)),
            m.createdAt.takeIf { it > 0 }?.let { "captured " + Ui.date(it) }).joinToString("  \u00b7  ")))
        col.addView(sum)
        // actions
        col.addView(LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL
            if (m.video != null) addView(Ui.button(a, "Play", primary = true) {
                a.startActivity(Intent(a, PlayerActivity::class.java).putExtra(PlayerActivity.EXTRA_MEDIA, m.mediaId))
            }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
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
                addView(Ui.badge(a, if (t.added) "added" else t.source.replace("-", " "), t.added))
            })
            val isMain = c.main.equals(t.key, true); val isSecond = c.second.equals(t.key, true)
            box.addView(LinearLayout(a).apply {
                orientation = LinearLayout.HORIZONTAL
                fun chip(txt: String, on: Boolean, tg: String, f: () -> Unit) = addView(Ui.chipButton(a, txt, on, tg + ":" + t.key, f), Ui.lp(-2, -2, 0f, 0, 0, 6, 0, a))
                chip("Main", isMain, "main") { a.app.settings.setTrackChoice(m.mediaId, "main", if (isMain) null else t.key); a.show() }
                chip("Second line", isSecond, "second") { a.app.settings.setTrackChoice(m.mediaId, "second", if (isSecond) null else t.key); a.show() }
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
