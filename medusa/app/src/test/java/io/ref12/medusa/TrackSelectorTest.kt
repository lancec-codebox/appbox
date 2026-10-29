package io.ref12.medusa

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TrackSelectorTest {
    /** What v1.1 clients send (it used to be the server default). */
    private val V11_ZH = listOf("zh-Hans", "zh-CN", "zh", "zh-Hant", "zh-TW")

    private fun fmts(vararg exts: String) = exts.joinToString(",", "[", "]") { """{"ext":"$it","url":"https://x/$it"}""" }
    private fun info(subs: String = "{}", auto: String = "{}") =
        JSONObject("""{"subtitles":$subs,"automatic_captions":$auto}""")

    @Test fun manual_beats_auto_even_for_a_later_language() {
        val i = info("""{"zh-Hant":${fmts("vtt")}}""", """{"zh-Hans":${fmts("json3")}}""")
        val t = TrackSelector.select(i, V11_ZH)!!
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
        val t = TrackSelector.select(info(auto = auto), V11_ZH)!!
        assertEquals(Track("zh-orig", "zh", "auto-captions", "json3"), t)
        // spoken language is English: Chinese entries are machine translations, so no track
        val en = """{"zh-Hans":${fmts("json3")},"en-orig":${fmts("json3")}}"""
        assertNull(TrackSelector.select(info(auto = en), V11_ZH))
    }

    @Test fun auto_captions_without_orig_marker_are_accepted() {
        val t = TrackSelector.select(info(auto = """{"zh-CN":${fmts("vtt")}}"""), V11_ZH)!!
        assertEquals("auto-captions", t.source)
        assertEquals("zh-CN", t.language)
    }

    @Test fun tracks_without_json3_or_vtt_are_skipped() {
        val i = info("""{"zh-Hans":${fmts("ttml", "srv3")},"zh-CN":${fmts("vtt")}}""")
        assertEquals("zh-CN", TrackSelector.select(i, V11_ZH)!!.language)
    }

    @Test fun nothing_found() {
        assertNull(TrackSelector.select(info(), V11_ZH))
        assertNull(TrackSelector.select(JSONObject("{}"), V11_ZH))
    }
    // ---- v1.2: no default language ----

    @Test fun no_languages_picks_the_original_spoken_language_manual_first() {
        val i = info("""{"fr":${fmts("vtt")},"en":${fmts("vtt")}}""", """{"en-orig":${fmts("json3")},"en":${fmts("json3")},"de":${fmts("json3")}}""")
        val t = TrackSelector.select(i, emptyList())!!
        assertEquals(Track("en", "en", "captions", "vtt"), t)
    }

    @Test fun no_languages_falls_back_to_the_spoken_languages_auto_captions() {
        val i = info("""{"fr":${fmts("vtt")}}""", """{"ja-orig":${fmts("json3")},"fr":${fmts("json3")},"en":${fmts("json3")}}""")
        val t = TrackSelector.select(i, emptyList())!!
        assertEquals(Track("ja-orig", "ja", "auto-captions", "json3"), t)
    }

    @Test fun no_languages_uses_the_video_language_field_when_there_is_no_orig_marker() {
        val i = JSONObject("""{"language":"es","subtitles":{"en":${fmts("vtt")}},"automatic_captions":{"es":${fmts("vtt")}}}""")
        assertEquals("es", TrackSelector.select(i, emptyList())!!.language)
    }

    @Test fun no_languages_and_no_spoken_language_takes_first_manual_track() {
        val i = info("""{"de":${fmts("vtt")},"en":${fmts("vtt")}}""")
        assertEquals("de", TrackSelector.select(i, emptyList())!!.language)
        assertNull(TrackSelector.select(info(), emptyList()))
    }

    @Test fun spoken_language_without_a_usable_track_gives_none_rather_than_a_translation() {
        val i = info("""{"fr":${fmts("vtt")}}""", """{"ja-orig":${fmts("ttml")}}""")
        assertNull(TrackSelector.select(i, emptyList()))
    }

    @Test fun list_has_every_offered_track_but_no_machine_translations() {
        val i = info("""{"fr":${fmts("vtt")},"en":{"x":1}}""", """{"en-orig":${fmts("json3")},"de":${fmts("json3")},"fr":${fmts("json3")}}""")
        assertEquals(listOf("fr" to "captions", "en" to "auto-captions"), TrackSelector.list(i).map { it.language to it.source })
    }

    @Test fun list_reports_track_names() {
        val i = info("""{"en":[{"ext":"vtt","name":"English"}]}""")
        assertEquals(TrackInfo("en", "captions", "English"), TrackSelector.list(i).single().info())
    }

    @Test fun requested_languages_still_take_the_first_match_in_order() {
        val i = info("""{"en":${fmts("vtt")},"fr":${fmts("vtt")}}""")
        assertEquals("fr", TrackSelector.select(i, listOf("de", "fr", "en"))!!.language)
        assertNull(TrackSelector.select(i, listOf("ko")))
    }

    @Test fun no_captions_message_names_what_exists() {
        val tr = listOf(TrackInfo("en", "captions"), TrackInfo("fr", "auto-captions"))
        assertEquals("No captions in ko (available: en, fr)", TrackSelector.noCaptionsMessage(listOf("ko"), tr))
        assertEquals("No captions in the video's original language (available: en, fr)", TrackSelector.noCaptionsMessage(emptyList(), tr))
        assertEquals("No captions found for this video", TrackSelector.noCaptionsMessage(listOf("ko"), emptyList()))
    }
}

