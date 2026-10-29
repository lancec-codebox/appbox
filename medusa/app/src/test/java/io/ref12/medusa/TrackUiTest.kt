package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TrackUiTest {
    private val tracks = listOf(TrackInfo("en", "captions", "English"), TrackInfo("fr", "auto-captions"))

    @Test fun labels_name_the_language_and_mark_auto_tracks() {
        assertEquals("English (en)", TrackUi.label(tracks[0]))
        assertEquals("fr \u00b7 auto".length > 0, true)
        assert(TrackUi.label(tracks[1]).endsWith("\u00b7 auto"))
        assert(TrackUi.label(tracks[1]).contains("(fr)"))
    }

    @Test fun items_from_before_1_2_offer_their_main_track() {
        val m = MediaMeta("m", "u", "t", null, "x", "i", SubtitlesData("captions", "de", emptyList()), null, 0)
        assertEquals(listOf(TrackInfo("de", "captions")), TrackUi.tracksOf(m))
        assertEquals(emptyList<TrackInfo>(), TrackUi.tracksOf(m.copy(subtitles = null)))
    }

    @Test fun saved_choice_wins_when_the_track_exists_else_the_item_default() {
        assertEquals(TrackUi.Choice("en", null), TrackUi.resolve("en", tracks, null, null))
        assertEquals(TrackUi.Choice("fr", "en"), TrackUi.resolve("en", tracks, "fr", "en"))
        assertEquals(TrackUi.Choice(null, null), TrackUi.resolve("en", tracks, "", ""))
        assertEquals(TrackUi.Choice("en", null), TrackUi.resolve("en", tracks, "ko", "ko")) // track no longer exists
        assertNull(TrackUi.resolve(null, emptyList(), null, null).main)
    }
}

class TrackUiMenuTest {
    private val tracks = listOf(
        TrackInfo("en", "captions", "English"),
        TrackInfo("en", "added", "English (AI)", "t-aa", "lw"),
        TrackInfo("fr", "added", null, "t-bb", "hexad"),
    )
    @Test fun cc_menu_lists_added_tracks_with_their_label_and_a_unique_key() {
        val (labels, tags) = TrackUi.menuEntries(tracks)
        org.junit.Assert.assertEquals(listOf("Off", "English (en)", "English (AI)", "French (added)"), labels)
        org.junit.Assert.assertEquals(listOf("off", "en", "t-aa", "t-bb"), tags)
        org.junit.Assert.assertEquals(2, TrackUi.indexOfKey(tracks, "t-aa"))
        org.junit.Assert.assertEquals(0, TrackUi.indexOfKey(tracks, null))
    }
    @Test fun a_saved_added_track_choice_is_kept_and_a_removed_one_falls_back() {
        org.junit.Assert.assertEquals(TrackUi.Choice("t-aa", "t-bb"), TrackUi.resolve("en", tracks, "t-aa", "t-bb"))
        org.junit.Assert.assertEquals(TrackUi.Choice("en", null), TrackUi.resolve("en", tracks.take(1), "t-aa", "t-bb"))
    }
}
