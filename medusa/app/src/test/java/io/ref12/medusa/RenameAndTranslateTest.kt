package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class RenameAndTranslateTest {
    @get:Rule val tmp = TemporaryFolder()

    private fun meta(id: String = "m1", title: String = "Original", loc: String? = null, user: String? = null, tr: String? = null, trLang: String? = null) =
        MediaMeta(id, "https://y/$id", title, 5, "youtube", id, null, null, 1L, localizedTitle = loc, userTitle = user, translatedTitle = tr, translatedLanguage = trLang)

    @Test fun user_title_wins_over_localized_and_original_and_reset_restores() {
        val store = MediaStore(tmp.root)
        store.save(meta(loc = "Titre"))
        assertEquals("Titre", Titles.primary(store.get("m1")!!, TitleMode.DEVICE))
        store.rename("m1", "  My name  ")
        val m = store.get("m1")!!
        assertEquals("My name", m.userTitle)
        assertEquals("My name", Titles.primary(m, TitleMode.DEVICE)); assertEquals("My name", Titles.primary(m, TitleMode.ORIGINAL))
        assertEquals("Original", Titles.secondary(m, TitleMode.DEVICE))
        store.rename("m1", "   ") // blank = reset
        assertNull(store.get("m1")!!.userTitle)
        assertEquals("Titre", Titles.primary(store.get("m1")!!, TitleMode.DEVICE))
    }

    @Test fun api_json_carries_userTitle_but_keeps_title_and_localizedTitle() {
        val j = MediaJson.item(meta(loc = "Titre", user = "Mine"), emptyList())
        assertEquals("Original", j.getString("title")); assertEquals("Titre", j.getString("localizedTitle")); assertEquals("Mine", j.getString("userTitle"))
        assertFalse(MediaJson.item(meta(), emptyList()).has("userTitle"))
    }

    @Test fun search_matches_title_localized_translated_and_user_title() {
        val m = meta(title = "Alpha", loc = "Bravo", user = "Charlie", tr = "Delta")
        for (q in listOf("alph", "brav", "charl", "delt")) assertTrue(q, Titles.matches(m, q))
        assertFalse(Titles.matches(m, "zulu")); assertTrue(Titles.matches(m, ""))
    }

    @Test fun localized_title_wins_over_translated_and_user_title_hides_it() {
        assertNull(Titles.machineTranslated(meta(loc = "Titre", tr = "Translated"), TitleMode.DEVICE))
        assertEquals("Translated", Titles.machineTranslated(meta(tr = "Translated"), TitleMode.DEVICE))
        assertNull(Titles.machineTranslated(meta(tr = "Translated", user = "Mine"), TitleMode.DEVICE))
        assertNull(Titles.machineTranslated(meta(tr = "Translated"), TitleMode.ORIGINAL))
        assertNull(Titles.machineTranslated(meta(title = "Same", tr = "Same"), TitleMode.DEVICE))
    }

    @Test fun needs_translation_rules() {
        assertTrue(Titles.needsTranslation(meta(), "en"))
        assertFalse(Titles.needsTranslation(meta(loc = "Titre"), "en"))
        assertFalse(Titles.needsTranslation(meta(tr = "x", trLang = "en"), "en"))
        assertTrue(Titles.needsTranslation(meta(tr = "x", trLang = "fr"), "en"))
    }

    @Test fun rename_survives_in_storage_json_roundtrip() {
        val store = MediaStore(tmp.root)
        store.save(meta(user = "Mine", tr = "Tr", trLang = "en"))
        val m = MediaStore(tmp.root).get("m1")!!
        assertEquals("Mine", m.userTitle); assertEquals("Tr", m.translatedTitle); assertEquals("en", m.translatedLanguage)
    }

    // ---- consent path with a mocked engine
    private class FakeEngine(var ready: Boolean = false) : TranslationEngine {
        var downloads = 0; var translatorsMade = 0; var calls = 0
        override fun modelReady(source: String, target: String) = ready
        override fun downloadModel(source: String, target: String, wifiOnly: Boolean) { downloads++; ready = true }
        override fun removeModel(language: String) { ready = false }
        override fun supported(tag: String) = tag.substringBefore('-').takeIf { it in setOf("en", "fr", "zh", "ja", "ko") }
        override fun translator(source: String, target: String): TextTranslator { translatorsMade++; return object : TextTranslator {
            override fun translate(texts: List<String>): List<String> { calls++; return texts.map { "[$source>$target] $it" } }
            override fun close() {}
        } }
    }

    @Test fun consent_asks_before_first_download_then_downloads_then_just_translates() {
        assertEquals(TranslationConsent.Step.OFF, TranslationConsent.next(false, true, true))
        assertEquals(TranslationConsent.Step.ASK_DOWNLOAD, TranslationConsent.next(true, false, false))
        assertEquals(TranslationConsent.Step.DOWNLOAD, TranslationConsent.next(true, true, false))
        assertEquals(TranslationConsent.Step.NOT_NEEDED, TranslationConsent.next(true, false, true))
    }

    @Test fun title_translation_runs_only_when_model_present_and_skips_localized() {
        val store = MediaStore(tmp.root)
        store.save(meta("a", title = "Bonjour le monde"))
        store.save(meta("b", title = "Hello", loc = "Salut"))
        store.save(meta("c", title = "你好世界"))
        val eng = FakeEngine(ready = false)
        val tt = TitleTranslator(store, eng)
        assertEquals(0, tt.run("fr")) // no model: nothing translated, nothing downloaded
        assertEquals(0, eng.downloads)
        eng.downloadModel("en", "fr", true); assertEquals(1, eng.downloads)
        val n = tt.run("fr")
        assertTrue(n >= 2)
        assertEquals("[zh>fr] 你好世界", store.get("c")!!.translatedTitle); assertEquals("fr", store.get("c")!!.translatedLanguage)
        assertNull(store.get("b")!!.translatedTitle) // the site's localized title wins: never translated
        assertEquals(2, eng.translatorsMade) // one translator per source language (fr and zh... en->fr, zh->fr), not one per title
    }

    @Test fun track_translation_batches_cues_and_uses_one_translator() {
        val eng = FakeEngine(true)
        val lines = (0 until 95).map { Line(it.toDouble(), it + 1.0, "line $it") }
        val out = eng.translator("fr", "en").use { TrackTranslation.translate(lines, it, batch = 40) }
        assertEquals(1, eng.translatorsMade); assertEquals(3, eng.calls) // 40 + 40 + 15, not 95 calls
        assertEquals(95, out.size); assertEquals("[fr>en] line 94", out.last().text); assertEquals(lines[7].start, out[7].start, 0.0)
        val t = TrackTranslation.toAdded("en", out)
        assertEquals("English (on-device)", t.label); assertEquals("mlkit", t.origin); assertEquals(TrackInfo.SOURCE_ADDED, t.source)
    }
}
