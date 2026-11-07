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

    private fun showError(t: TrackInfo, msg: String) {
        if (a.isFinishing) return
        AlertDialog.Builder(a).setTitle("Could not download " + TrackUi.label(t)).setMessage(msg).setPositiveButton("OK", null).show()
    }

    private fun redraw() { if (!a.isFinishing && (a.currentPage as? ItemPage)?.mediaId == mediaId) a.show() }

    /** Downloads one captured track off the main thread; the page shows its progress, then refreshes (a failure shows its reason). */
    private fun download(t: TrackInfo) {
        a.app.trackDl.download(mediaId, t, started = { redraw() }) { _, err -> redraw(); if (err != null) showError(t, err) }
    }

    /** Fetches every captured track not yet on the device, one after another. */
    private fun downloadAll(m: MediaMeta, tracks: List<TrackInfo>) {
        var first: String? = null; var failures = 0
        a.app.trackDl.downloadAll(mediaId, tracks, { t -> TrackDevice.missing(m, listOf(t)) { l -> a.app.media.loadTrack(mediaId, l) != null }.isNotEmpty() }) { t, err ->
            if (err != null) { failures++; if (first == null) first = TrackUi.label(t) + ": " + err }
            redraw()
            if (err != null && !a.isFinishing && !a.app.trackDl.anyRunning(mediaId)) showError(t, if (failures > 1) first + "\n(" + failures + " tracks failed)" else err)
        }
        redraw()
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
        Titles.machineTranslated(m, mode)?.let { tr -> sum.addView(Ui.muted(a, tr + "  \u00b7  machine-translated", 13f).apply { tag = "item-translated-title"; setPadding(0, Ui.dp(a, 2), 0, 0) }) }
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
            if (m.video == null || a.app.media.partialBytes(m.mediaId) > 0) {
                addView(Ui.button(a, if (a.app.media.partialBytes(m.mediaId) > 0) "Resume download" else "Download video") {
                    val id = a.app.jobs.resumeItem(m.mediaId); HelperService.start(a); if (id != null) a.go(Dest.DOWNLOADS, id)
                }.apply { tag = "resume-item" }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
                addView(Ui.button(a, "Download again") {
                    AlertDialog.Builder(a).setTitle("Download again?").setMessage("This deletes this item's files and downloads it from scratch.")
                        .setPositiveButton("Download again") { _, _ ->
                            val id = a.app.jobs.restartItem(m.mediaId); HelperService.start(a); if (id != null) a.go(Dest.DOWNLOADS, id)
                        }.setNegativeButton("Cancel", null).show()
                }.apply { tag = "restart-item" }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
            }
            addView(Ui.button(a, "Rename") { RenameUi.show(a, m) { a.show() } }.apply { tag = "rename" }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
            addView(Ui.button(a, "Delete", danger = true) {
                AlertDialog.Builder(a).setTitle("Delete?").setMessage(m.title)
                    .setPositiveButton("Delete") { _, _ -> a.app.media.delete(m.mediaId); a.goBack() }.setNegativeButton("Cancel", null).show()
            })
        }, Ui.lp(-1, -2, 0f, 0, 10, 0, 0, a))
        OcrUi.section(a, m)?.let { col.addView(it, Ui.lp(-1, -2, 0f, 0, 10, 0, 0, a)) }
        // tracks
        val tracks = TrackUi.tracksOf(m)
        val c = TrackUi.resolve(m.subtitles?.language, tracks, a.app.settings.trackChoice(m.mediaId, "main"), a.app.settings.trackChoice(m.mediaId, "second"))
        col.addView(Ui.header(a, "Subtitle tracks (" + tracks.size + ")").apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 18), 0, Ui.dp(a, 6)) })
        if (tracks.isEmpty()) col.addView(Ui.muted(a, "This item has no subtitle tracks. Import a file to add one.", 14f))
        val hasSaved = { l: String -> a.app.media.loadTrack(m.mediaId, l) != null }
        if (TrackDevice.missing(m, tracks, hasSaved).isNotEmpty()) {
            val allBusy = TrackDevice.missing(m, tracks, hasSaved).all { a.app.trackDl.isRunning(m.mediaId, it) }
            col.addView(Ui.chipButton(a, if (allBusy) "Downloading\u2026" else "Download all subtitles", false, "download-all") { if (!allBusy) downloadAll(m, tracks) }.apply { alpha = if (allBusy) 0.5f else 1f },
                Ui.lp(-2, -2, 0f, 0, 0, 0, 8, a))
        }
        val card = Ui.card(a)
        for ((i, t) in tracks.withIndex()) {
            if (i > 0) card.addView(Ui.divider(a))
            val box = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(0, Ui.dp(a, 8), 0, Ui.dp(a, 8)); tag = "track:" + t.key }
            box.addView(LinearLayout(a).apply {
                orientation = LinearLayout.HORIZONTAL; gravity = android.view.Gravity.CENTER_VERTICAL
                addView(Ui.text(a, TrackUi.label(t), 14f, bold = true), LinearLayout.LayoutParams(0, -2, 1f))
                if (c.main.equals(t.key, true)) addView(Ui.badge(a, "MAIN", true).apply { tag = "badge-main:" + t.key }, Ui.lp(-2, -2, 0f, 0, 0, 4, 0, a))
                if (c.second.equals(t.key, true)) addView(Ui.badge(a, "2nd", true).apply { tag = "badge-second:" + t.key }, Ui.lp(-2, -2, 0f, 0, 0, 4, 0, a))
                val st = a.app.trackDl.state(m.mediaId, t)
                if (!t.added) addView(Ui.badge(a, when (st) {
                    TrackState.ON_DEVICE -> "on device"; TrackState.DOWNLOADING -> "Downloading\u2026"
                    TrackState.FAILED -> "download failed"; TrackState.NOT_DOWNLOADED -> "not downloaded"
                }, st == TrackState.ON_DEVICE).apply { tag = "state:" + t.key + ":" + st.name.lowercase() }, Ui.lp(-2, -2, 0f, 0, 0, 4, 0, a))
                addView(Ui.badge(a, if (t.ocr) "from picture" else if (t.added) "added" else t.source.replace("-", " "), t.added))
            })
            a.app.trackDl.failure(m.mediaId, t)?.takeIf { a.app.trackDl.state(m.mediaId, t) == TrackState.FAILED }?.let { r ->
                box.addView(Ui.muted(a, r, 12f).apply { tag = "failure:" + t.key; setPadding(0, Ui.dp(a, 4), 0, 0) })
            }
            val isMain = c.main.equals(t.key, true); val isSecond = c.second.equals(t.key, true)
            box.addView(LinearLayout(a).apply {
                orientation = LinearLayout.HORIZONTAL
                fun chip(txt: String, on: Boolean, tg: String, f: () -> Unit) = addView(Ui.chipButton(a, txt, on, tg + ":" + t.key, f), Ui.lp(-2, -2, 0f, 0, 0, 6, 0, a))
                val st = a.app.trackDl.state(m.mediaId, t)
                fun choose(slot: String, on: Boolean) {
                    a.app.settings.setTrackChoice(m.mediaId, slot, if (on) null else t.key)
                    if (!on && (st == TrackState.NOT_DOWNLOADED || st == TrackState.FAILED)) download(t) else a.show()
                }
                chip(if (isMain) "\u2713 Main" else "Main", isMain, "main") { choose("main", isMain) }
                chip(if (isSecond) "\u2713 Second line" else "Second line", isSecond, "second") { choose("second", isSecond) }
                if (st == TrackState.NOT_DOWNLOADED) chip("Download", false, "download") { download(t) }
                if (st == TrackState.FAILED) chip("Retry", false, "retry") { download(t) }
                chip("Export", false, "export") { ExportUi.export(a, m, listOf(t)) }
                if (t.language.substringBefore('-') != "en") chip("Translate on this device", false, "translate") { TranslationUi.translateTrack(a, m, t) { a.show() } }
                addView(Ui.chipButton(a, "\u22EE", false, "export-more:" + t.key) { ExportUi.overflow(a, m, t, this) }, Ui.lp(-2, -2, 0f, 0, 0, 6, 0, a))
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
