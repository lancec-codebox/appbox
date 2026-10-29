package io.ref12.medusa

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/** Captured media on this phone: tap an item to choose its subtitles and play it. */
class LibraryActivity : Activity() {
    private lateinit var box: LinearLayout

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 80, 40, 40) }
        col.addView(TextView(this).apply { text = "Library"; textSize = 24f })
        box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        col.addView(box, ViewGroup.LayoutParams(-1, -2))
        setContentView(ScrollView(this).apply { addView(col) })
    }

    override fun onResume() { super.onResume(); render() }

    private fun render() {
        box.removeAllViews()
        val items = app.media.list()
        if (items.isEmpty()) box.addView(TextView(this).apply { text = "Nothing captured yet"; setPadding(0, 24, 0, 0) })
        for (m in items) {
            val mb = app.media.bytesOf(m.mediaId) / (1024 * 1024)
            box.addView(TextView(this).apply {
                text = m.title + "\n" + listOfNotNull(
                    m.durationSec?.let { "%d:%02d".format(it / 60, it % 60) },
                    TrackUi.tracksOf(m).size.let { if (it == 0) "no subtitles" else "$it subtitle track" + (if (it > 1) "s" else "") },
                    if (m.video != null) "video" else "no video",
                    "$mb MB",
                ).joinToString(" - ")
                textSize = 16f
                setPadding(0, 20, 0, 20)
                setOnClickListener { showItem(m) }
            })
        }
    }

    /** The item screen: choose the subtitle track and the second line before playing. */
    private fun showItem(m: MediaMeta) {
        val tracks = TrackUi.tracksOf(m)
        val c = TrackUi.resolve(m.subtitles?.language, tracks, app.settings.trackChoice(m.mediaId, "main"), app.settings.trackChoice(m.mediaId, "second"))
        val body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 16, 40, 0) }
        fun header(t: String) = TextView(this).apply { text = t; textSize = 14f; setPadding(0, 24, 0, 4); setTypeface(typeface, android.graphics.Typeface.BOLD) }
        fun group(second: Boolean): android.widget.RadioGroup {
            val cur = if (second) c.second else c.main
            val slot = if (second) "second" else "main"
            val (labels, tags) = TrackUi.menuEntries(tracks)
            return TrackUi.radios(this, labels, tags.map { "$slot:$it" }, TrackUi.indexOfKey(tracks, cur)) { i ->
                app.settings.setTrackChoice(m.mediaId, slot, if (i == 0) null else tracks[i - 1].key)
            }
        }
        body.addView(header("Subtitles"))
        body.addView(group(false))
        body.addView(header("Second line"))
        body.addView(group(true))
        if (tracks.isEmpty()) body.addView(TextView(this).apply { text = "This item has no subtitle tracks."; setPadding(0, 16, 0, 0) })
        fun action(t: String, desc: String, f: () -> Unit) = android.widget.Button(this).apply { text = t; isAllCaps = false; contentDescription = desc; tag = desc; setOnClickListener { f() } }
        body.addView(header("Added subtitles"))
        body.addView(action("Import subtitles\u2026", "Import subtitles") {
            startActivity(Intent(this, ImportActivity::class.java).putExtra(ImportActivity.EXTRA_MEDIA, m.mediaId))
        })
        body.addView(action("Export subtitles\u2026", "Export subtitles") { ExportUi.show(this, m) })
        for (t in tracks.filter { it.added }) body.addView(action("Remove: " + TrackUi.label(t), "remove:" + t.key) {
            AlertDialog.Builder(this).setTitle("Remove this track?").setMessage(TrackUi.label(t))
                .setPositiveButton("Remove") { _, _ ->
                    app.media.removeTrack(m.mediaId, t.trackId!!)
                    for (slot in listOf("main", "second")) if (t.key.equals(app.settings.trackChoice(m.mediaId, slot), true)) app.settings.setTrackChoice(m.mediaId, slot, null)
                    render(); app.media.get(m.mediaId)?.let { showItem(it) }
                }.setNegativeButton("Cancel", null).show()
        })
        val b = AlertDialog.Builder(this).setTitle(m.title).setView(ScrollView(this).apply { addView(body) })
            .setNeutralButton("Delete") { _, _ ->
                AlertDialog.Builder(this).setTitle("Delete?").setMessage(m.title)
                    .setPositiveButton("Delete") { _, _ -> app.media.delete(m.mediaId); render() }
                    .setNegativeButton("Cancel", null).show()
            }
            .setNegativeButton("Close", null)
        if (m.video != null) b.setPositiveButton("Play") { _, _ ->
            startActivity(Intent(this, PlayerActivity::class.java).putExtra(PlayerActivity.EXTRA_MEDIA, m.mediaId))
        }
        b.show()
    }
}
