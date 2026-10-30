package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Test

class LanguageToolsTest {
    @Test fun file_name_tags() {
        assertEquals("en", LanguageTools.fromFileName("movie-en.srt"))
        assertEquals("en", LanguageTools.fromFileName("movie [en].srt"))
        assertEquals("fr", LanguageTools.fromFileName("movie.fr.srt"))
        assertEquals("zh-Hans", LanguageTools.fromFileName("movie_zh-Hans.srt"))
        assertEquals("pt-BR", LanguageTools.fromFileName("movie.pt-BR.vtt"))
        assertEquals(null, LanguageTools.fromFileName("movie.srt"))
    }

    @Test fun script_heuristic() {
        assertEquals("ja", LanguageTools.heuristic("これは日本語です"))
        assertEquals("ko", LanguageTools.heuristic("안녕하세요"))
        assertEquals("zh-Hans", LanguageTools.heuristic("我们说这个"))
        assertEquals("zh-Hant", LanguageTools.heuristic("我們說這個"))
        assertEquals("en", LanguageTools.heuristic("hello there"))
    }

    @Test fun detect_prefers_name_then_file_then_text() {
        assertEquals("de", LanguageTools.detect(null, "a.de.srt", "fr", listOf("x")))
        assertEquals("fr", LanguageTools.detect(null, "a.srt", "fr", listOf("x")))
        assertEquals("ko", LanguageTools.detect(null, "a.srt", null, listOf("안녕하세요")))
    }

    @Test fun ordering_original_then_english_then_alphabetical() {
        val l = listOf("fr", "de", "en", "ja", "es")
        assertEquals(listOf("ja", "en", "de", "es", "fr"), LanguageTools.order(l, "ja", { it }))
        assertEquals(listOf("en", "de", "es", "fr", "ja"), LanguageTools.order(l, null, { it }))
    }

    @Test fun track_menu_order() {
        val t = listOf(TrackInfo("fr", "captions"), TrackInfo("en", "auto-captions"), TrackInfo("de", "captions"))
        assertEquals(listOf("de", "en", "fr"), TrackUi.ordered(t, "de").map { it.language }.let { listOf(it[0]) + it.drop(1).sorted() })
        assertEquals("Off", TrackUi.menuEntries(TrackUi.ordered(t, "fr")).first[0])
        assertEquals("fr", TrackUi.ordered(t, "fr")[0].language)
        assertEquals("en", TrackUi.ordered(t, "fr")[1].language)
    }
}
