package io.ref12.medusa

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/** Captured media on this phone: tap to play, long-press to delete. */
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
                    if (m.subtitles != null) "subtitles" else null,
                    if (m.video != null) "video" else "no video",
                    "$mb MB",
                ).joinToString(" - ")
                textSize = 16f
                setPadding(0, 20, 0, 20)
                setOnClickListener {
                    if (m.video == null) android.widget.Toast.makeText(context, "This item has no video", android.widget.Toast.LENGTH_SHORT).show()
                    else startActivity(Intent(this@LibraryActivity, PlayerActivity::class.java).putExtra(PlayerActivity.EXTRA_MEDIA, m.mediaId))
                }
                setOnLongClickListener {
                    AlertDialog.Builder(this@LibraryActivity).setTitle("Delete?").setMessage(m.title)
                        .setPositiveButton("Delete") { _, _ -> app.media.delete(m.mediaId); render() }
                        .setNegativeButton("Cancel", null).show()
                    true
                }
            })
        }
    }
}
