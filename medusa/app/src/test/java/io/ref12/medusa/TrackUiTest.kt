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
