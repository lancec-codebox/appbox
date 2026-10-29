package io.ref12.medusa

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TrackSelectorTest {
    private fun fmts(vararg exts: String) = exts.joinToString(",", "[", "]") { """{"ext":"$it","url":"https://x/$it"}""" }
    private fun info(subs: String = "{}", auto: String = "{}") =
        JSONObject("""{"subtitles":$subs,"automatic_captions":$auto}""")

    @Test fun manual_beats_auto_even_for_a_later_language() {
        val i = info("""{"zh-Hant":${fmts("vtt")}}""", """{"zh-Hans":${fmts("json3")}}""")
        val t = TrackSelector.select(i, DEFAULT_LANGUAGES)!!
        assertEquals(Track("zh-Hant", "zh-Hant", "captions", "vtt"), t)
    }

    @Test fun first_requested_language_wins_and_json3_is_preferred() {
        val i = info("""{"zh-TW":${fmts("vtt", "json3")},"zh-CN":${fmts("vtt")}}""")
        val t = TrackSelector.select(i, listOf("zh-Hans", "zh-CN", "zh-TW"))!!
        assertEquals("zh-CN", t.language)
        assertEquals("vtt", t.ext)
        assertEquals("json3", TrackSelector.select(i, listOf("zh-TW"))!!.ext)
    }

    @Test fun zh_family_matches_but_hans_does_not_match_hant() {
        assertEquals("zh", TrackSelector.select(info("""{"zh":${fmts("vtt")}}"""), listOf("zh-Hans"))!!.language)
        assertEquals("zh-Hans", TrackSelector.select(info("""{"zh-Hans":${fmts("vtt")}}"""), listOf("zh"))!!.language)
        assertNull(TrackSelector.select(info("""{"zh-Hant":${fmts("vtt")}}"""), listOf("zh-Hans")))
    }

    @Test fun auto_captions_use_only_the_spoken_language_when_orig_is_marked() {
        val auto = """{"zh-Hans":${fmts("json3")},"en":${fmts("json3")},"zh-orig":${fmts("json3")}}"""
        val t = TrackSelector.select(info(auto = auto), DEFAULT_LANGUAGES)!!
        assertEquals(Track("zh-orig", "zh", "auto-captions", "json3"), t)
        // spoken language is English: Chinese entries are machine translations, so no track
        val en = """{"zh-Hans":${fmts("json3")},"en-orig":${fmts("json3")}}"""
        assertNull(TrackSelector.select(info(auto = en), DEFAULT_LANGUAGES))
    }

    @Test fun auto_captions_without_orig_marker_are_accepted() {
        val t = TrackSelector.select(info(auto = """{"zh-CN":${fmts("vtt")}}"""), DEFAULT_LANGUAGES)!!
        assertEquals("auto-captions", t.source)
        assertEquals("zh-CN", t.language)
    }

    @Test fun tracks_without_json3_or_vtt_are_skipped() {
        val i = info("""{"zh-Hans":${fmts("ttml", "srv3")},"zh-CN":${fmts("vtt")}}""")
        assertEquals("zh-CN", TrackSelector.select(i, DEFAULT_LANGUAGES)!!.language)
    }

    @Test fun nothing_found() {
        assertNull(TrackSelector.select(info(), DEFAULT_LANGUAGES))
        assertNull(TrackSelector.select(JSONObject("{}"), DEFAULT_LANGUAGES))
    }
}
