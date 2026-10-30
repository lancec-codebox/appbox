package io.ref12.medusa

import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.ScrollView
import android.widget.TextView

/** Home: a dense list of captured items, a paste-a-link field and a title filter. */
class LibraryPage(a: MainActivity) : Page(a) {
    override val title = "Library"
    private lateinit var list: LinearLayout
    private lateinit var banner: LinearLayout
    private lateinit var activeBox: LinearLayout
    private var activeKey = ""
    private var query = ""

    override fun build(): View {
        val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(Ui.dp(a, 12), Ui.dp(a, 10), Ui.dp(a, 12), Ui.dp(a, 16)) }
        banner = UpdateUi.box(a)
        col.addView(banner, Ui.lp(-1, -2, 0f, 0, 0, 0, 8, a))
        val paste = EditText(a).apply {
            hint = "Paste a link to download"; textSize = 14f; setSingleLine(); tag = "paste-link"; contentDescription = "Paste a link"
            inputType = InputType.TYPE_TEXT_VARIATION_URI or InputType.TYPE_CLASS_TEXT
            setTextColor(Ui.color(a, R.color.on_surface)); setHintTextColor(Ui.color(a, R.color.muted))
        }
        val go = { a.capture(paste.text.toString().trim().takeIf { DeepLink.isHttp(it) } ?: a.clipboardLink()) }
        col.addView(LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            addView(paste, LinearLayout.LayoutParams(0, -2, 1f))
            addView(Ui.button(a, "Download", primary = true) { go() }, Ui.lp(-2, -2, 0f, 8, 0, 0, 0, a))
        })
        activeBox = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; tag = "active-downloads" }
        col.addView(activeBox, Ui.lp(-1, -2, 0f, 0, 8, 0, 0, a))
        fillActive()
        val items = a.app.media.list()
        if (items.size > 4) col.addView(EditText(a).apply {
            hint = "Filter by title"; textSize = 14f; setSingleLine(); tag = "filter"; contentDescription = "Filter by title"
            setTextColor(Ui.color(a, R.color.on_surface)); setHintTextColor(Ui.color(a, R.color.muted))
            addTextChangedListener(object : TextWatcher {
                override fun afterTextChanged(s: Editable?) { query = s?.toString().orEmpty().trim(); fill() }
                override fun beforeTextChanged(s: CharSequence?, st: Int, c: Int, af: Int) {}
                override fun onTextChanged(s: CharSequence?, st: Int, b: Int, c: Int) {}
            })
        })
        list = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL }
        col.addView(list, Ui.lp(-1, -2, 0f, 0, 6, 0, 0, a))
        fill()
        UpdateUi.render(a, banner, quietWhenCurrent = true) {}
        return ScrollView(a).apply { addView(col) }
    }

    override fun tick() {
        if (::banner.isInitialized) UpdateUi.render(a, banner, quietWhenCurrent = true) {}
        if (::activeBox.isInitialized) fillActive()
    }

    /** In-progress downloads, compact, above the items; tapping goes to the Downloads page. */
    private fun fillActive() {
        val act = a.app.jobs.list().filter { !it.state.finished }.sortedBy { it.updatedAt }
        val key = act.joinToString("|") { it.id + it.state + it.progress + it.message + it.title }
        if (key == activeKey) return
        activeKey = key
        activeBox.removeAllViews()
        for (j in act) {
            val r = LinearLayout(a).apply {
                orientation = LinearLayout.VERTICAL; tag = "active:" + j.id; contentDescription = DownloadsLogic.displayTitle(j)
                setPadding(Ui.dp(a, 10), Ui.dp(a, 8), Ui.dp(a, 10), Ui.dp(a, 8))
                background = Ui.rounded(a, Ui.color(a, R.color.selected), 8)
                setOnClickListener { a.go(Dest.DOWNLOADS, j.id) }
            }
            r.addView(Ui.oneLine(Ui.text(a, DownloadsLogic.displayTitle(j), 14f, bold = true)))
            r.addView(Ui.oneLine(Ui.muted(a, DownloadsLogic.stageText(j.state, j.message, j.progress), 12f)))
            r.addView(android.widget.ProgressBar(a, null, android.R.attr.progressBarStyleHorizontal).apply {
                isIndeterminate = DownloadsLogic.indeterminate(j.state, j.progress); max = 1000; progress = ((j.progress ?: 0.0) * 1000).toInt()
            }, Ui.lp(-1, Ui.dp(a, 5), 0f, 0, 4, 0, 0, a))
            activeBox.addView(r, Ui.lp(-1, -2, 0f, 0, 0, 0, 6, a))
        }
    }

    private fun fill() {
        list.removeAllViews()
        val all = a.app.media.list()
        if (all.isEmpty()) {
            list.addView(Ui.text(a, "Nothing downloaded yet", 16f, bold = true).apply { setPadding(0, Ui.dp(a, 28), 0, Ui.dp(a, 6)) })
            list.addView(Ui.muted(a, "Medusa downloads a video's transcript and video from a link, keeps them on this phone and plays them with subtitles you can sync.", 14f))
            list.addView(Ui.muted(a, "Paste a link above, or share one to Medusa from any app. Web apps can ask Medusa for transcripts through its local API.", 14f).apply { setPadding(0, Ui.dp(a, 8), 0, 0) })
            return
        }
        val items = all.filter { Titles.matches(it, query) }
        list.addView(Ui.muted(a, if (query.isEmpty()) "${all.size} item" + (if (all.size > 1) "s" else "") else "${items.size} of ${all.size}", 12f).apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 4), 0, Ui.dp(a, 4)) })
        if (items.isEmpty()) list.addView(Ui.muted(a, "No title matches", 14f).apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 12), 0, 0) })
        for (m in items) { list.addView(row(m)); list.addView(Ui.divider(a)) }
    }

    private fun row(m: MediaMeta): View {
        val tracks = TrackUi.tracksOf(m)
        val r = LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; tag = "item:" + m.mediaId; contentDescription = Titles.primary(m, a.app.settings.titleMode)
            setPadding(0, Ui.dp(a, 8), 0, Ui.dp(a, 8)); setOnClickListener { a.openItem(m.mediaId) }
            setOnLongClickListener { menu(this, m); true }
        }
        r.addView(LinearLayout(a).apply {
            gravity = Gravity.CENTER; background = Ui.rounded(a, Ui.color(a, R.color.chip), 6)
            addView(android.widget.ImageView(a).apply { setImageResource(if (m.video != null) R.drawable.ic_play else R.drawable.ic_library); setColorFilter(Ui.color(a, R.color.muted)) }, LinearLayout.LayoutParams(Ui.dp(a, 22), Ui.dp(a, 22)))
        }, LinearLayout.LayoutParams(Ui.dp(a, 52), Ui.dp(a, 40)).apply { rightMargin = Ui.dp(a, 12) })
        val mid = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL }
        val mode = a.app.settings.titleMode
        mid.addView(Ui.text(a, Titles.primary(m, mode), 15f, bold = true).apply { maxLines = 2; ellipsize = android.text.TextUtils.TruncateAt.END; tag = "title:" + m.mediaId })
        Titles.secondary(m, mode)?.let { o -> mid.addView(Ui.oneLine(Ui.muted(a, o, 12f)).apply { tag = "original:" + m.mediaId }) }
        Titles.machineTranslated(m, mode)?.let { tr -> mid.addView(Ui.oneLine(Ui.muted(a, tr + " \u00b7 machine-translated", 12f)).apply { tag = "translated:" + m.mediaId }) }
        mid.addView(Ui.oneLine(Ui.muted(a, listOfNotNull(Ui.duration(m.durationSec), Ui.size(a.app.media.bytesOf(m.mediaId)), m.createdAt.takeIf { it > 0 }?.let { Ui.date(it) }).joinToString("  \u00b7  "), 12f)))
        val badges = LinearLayout(a).apply { orientation = LinearLayout.HORIZONTAL; setPadding(0, Ui.dp(a, 3), 0, 0) }
        for (t in tracks.take(6)) badges.addView(Ui.badge(a, t.language + (if (t.added) " +" else ""), t.added), Ui.lp(-2, -2, 0f, 0, 0, 4, 0, a))
        if (tracks.size > 6) badges.addView(Ui.badge(a, "+" + (tracks.size - 6)))
        if (tracks.isEmpty()) badges.addView(Ui.badge(a, "no subtitles"))
        if (m.video == null) badges.addView(Ui.badge(a, "no video"))
        mid.addView(badges)
        r.addView(mid, LinearLayout.LayoutParams(0, -2, 1f))
        lateinit var more: View
        more = Ui.icon(a, R.drawable.ic_more, "More", R.color.muted) { menu(more, m) }
        r.addView(more)
        return r
    }

    private fun menu(anchor: View, m: MediaMeta) {
        PopupMenu(a, anchor).apply {
            menu.add("Open"); menu.add("Rename"); menu.add("Export subtitles"); menu.add("Share subtitles\u2026"); menu.add("Delete")
            setOnMenuItemClickListener {
                when (it.title) {
                    "Open" -> a.openItem(m.mediaId)
                    "Rename" -> RenameUi.show(a, m) { a.show() }
                    "Export subtitles" -> ExportUi.export(a, m, listOfNotNull(ExportUi.defaultTrack(m)))
                    "Share subtitles\u2026" -> ExportUi.export(a, m, listOfNotNull(ExportUi.defaultTrack(m)), share = true)
                    else -> android.app.AlertDialog.Builder(a).setTitle("Delete?").setMessage(m.title)
                        .setPositiveButton("Delete") { _, _ -> a.app.media.delete(m.mediaId); fill() }.setNegativeButton("Cancel", null).show()
                }
                true
            }
        }.show()
    }
}
