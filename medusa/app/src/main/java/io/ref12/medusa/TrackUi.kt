package io.ref12.medusa

import android.content.Context
import android.widget.RadioButton
import android.widget.RadioGroup
import java.util.Locale

/** Labels and choices for the subtitle track pickers (player, library item, capture screen). */
object TrackUi {
    /** "English (en)" or "English (en) · auto". Uses yt-dlp's own name for the track when it has one. */
    fun label(t: TrackInfo): String {
        val tag = t.language
        if (t.added) return t.name?.takeIf { it.isNotBlank() }
            ?: ((Locale.forLanguageTag(tag).getDisplayName(Locale.ENGLISH).takeIf { it.isNotBlank() && !it.equals(tag, true) } ?: tag) + " (added)")
        val shown = t.name?.takeIf { it.isNotBlank() }
            ?: Locale.forLanguageTag(tag).getDisplayName(Locale.ENGLISH).takeIf { it.isNotBlank() && !it.equals(tag, true) }
        return (if (shown != null) "$shown ($tag)" else tag) + (if (t.source == "auto-captions") " \u00b7 auto" else "")
    }

    /** The tracks to offer for an item: its list, or just the main track for items captured before 1.2. */
    fun tracksOf(m: MediaMeta): List<TrackInfo> =
        m.tracks.ifEmpty { m.subtitles?.let { listOf(TrackInfo(it.language, it.source)) } ?: emptyList() }

    /** What the CC menu and the item screen list for one slot: "Off" first, then every track; the tags are the tracks' keys. */
    fun menuEntries(tracks: List<TrackInfo>): Pair<List<String>, List<String>> =
        (listOf("Off") + tracks.map { label(it) }) to (listOf("off") + tracks.map { it.key })

    fun indexOfKey(tracks: List<TrackInfo>, key: String?): Int =
        if (key == null) 0 else tracks.indexOfFirst { it.key.equals(key, true) }.let { if (it < 0) 0 else it + 1 }

    data class Choice(val main: String?, val second: String?) // null = off

    /**
     * The tracks to show when playback opens. Saved choices ("" = off) win when that track still exists;
     * otherwise the item's own main track and no second line.
     */
    fun resolve(itemMain: String?, tracks: List<TrackInfo>, savedMain: String?, savedSecond: String?): Choice {
        fun valid(l: String) = tracks.any { it.key.equals(l, true) } || l.equals(itemMain, true)
        val main = when {
            savedMain == null -> itemMain
            savedMain.isEmpty() -> null
            valid(savedMain) -> savedMain
            else -> itemMain
        }
        val second = when {
            savedSecond == null || savedSecond.isEmpty() -> null
            valid(savedSecond) -> savedSecond
            else -> null
        }
        return Choice(main, second)
    }

    /** A vertical list of radio buttons; [tags] become the buttons' contentDescription (for tests and accessibility). */
    fun radios(ctx: Context, labels: List<String>, tags: List<String>, checked: Int, onPick: (Int) -> Unit): RadioGroup {
        val g = RadioGroup(ctx).apply { orientation = RadioGroup.VERTICAL }
        labels.forEachIndexed { i, l ->
            g.addView(RadioButton(ctx).apply {
                id = 1000 + i; text = l; textSize = 16f; contentDescription = tags[i]; tag = tags[i]
                setPadding(8, 14, 8, 14)
            }, RadioGroup.LayoutParams(-1, -2))
        }
        if (checked in labels.indices) g.check(1000 + checked)
        g.setOnCheckedChangeListener { _, id -> onPick(id - 1000) }
        return g
    }
}
