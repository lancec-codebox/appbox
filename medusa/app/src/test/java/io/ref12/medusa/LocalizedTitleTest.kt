package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.util.concurrent.Executor

class LocalizedTitleTest {
    @get:Rule val tmp = TemporaryFolder()

    private fun meta(title: String = "原标题", local: String? = "Original title", lang: String? = "en", extractor: String = "youtube") =
        MediaMeta("m1", "https://youtu.be/abc", title, 60, extractor, "abc", null, null, 1L, emptyList(), local, lang)

    // ---- display and search
    @Test fun device_mode_shows_the_localized_title_first_and_the_original_under_it() {
        val m = meta()
        assertEquals("Original title", Titles.primary(m, TitleMode.DEVICE))
        assertEquals("原标题", Titles.secondary(m, TitleMode.DEVICE))
    }
    @Test fun original_only_mode_shows_just_the_original() {
        val m = meta()
        assertEquals("原标题", Titles.primary(m, TitleMode.ORIGINAL))
        assertNull(Titles.secondary(m, TitleMode.ORIGINAL))
    }
    @Test fun without_a_localized_title_only_the_original_shows() {
        val m = meta(local = null, lang = null)
        assertEquals("原标题", Titles.primary(m, TitleMode.DEVICE))
        assertNull(Titles.secondary(m, TitleMode.DEVICE))
    }
    @Test fun search_matches_both_titles_and_ignores_case() {
        val m = meta()
        assertTrue(Titles.matches(m, "原标"))
        assertTrue(Titles.matches(m, "original TITLE"))
        assertTrue(Titles.matches(m, ""))
        assertFalse(Titles.matches(m, "nothing"))
        assertFalse(Titles.matches(meta(local = null), "original"))
    }
    @Test fun a_localized_title_equal_to_the_original_or_blank_is_not_stored() {
        assertNull(Titles.localizedIfDifferent("Same", "Same"))
        assertNull(Titles.localizedIfDifferent("Same", " Same "))
        assertNull(Titles.localizedIfDifferent("Same", ""))
        assertNull(Titles.localizedIfDifferent("Same", null))
        assertEquals("Other", Titles.localizedIfDifferent("Same", " Other "))
    }
    @Test fun yt_dlp_placeholder_titles_are_not_kept() {
        assertNull(Titles.localizedIfDifferent("Real", "youtube video #abc123"))
        assertEquals("A video about cats", Titles.localizedIfDifferent("Real", "A video about cats"))
    }
    @Test fun device_locale_becomes_a_youtube_language() {
        assertEquals("en", Titles.deviceLanguage("en", "US"))
        assertEquals("zh-CN", Titles.deviceLanguage("zh", "CN"))
        assertEquals("zh-TW", Titles.deviceLanguage("zh", "TW"))
        assertEquals("he", Titles.deviceLanguage("iw"))
        assertEquals("en", Titles.deviceLanguage(""))
    }
    @Test fun only_youtube_is_asked_for_translated_titles() {
        assertTrue(Titles.supportsLocalized("youtube"))
        assertTrue(Titles.supportsLocalized("YouTube:tab"))
        assertFalse(Titles.supportsLocalized("bilibili"))
        assertFalse(Titles.supportsLocalized(""))
    }

    // ---- API fields and storage
    @Test fun the_api_item_has_both_titles_and_title_stays_the_original() {
        val j = MediaJson.item(meta(), emptyList())
        assertEquals("原标题", j.getString("title"))
        assertEquals("Original title", j.getString("localizedTitle"))
        assertEquals("en", j.getString("localizedLanguage"))
    }
    @Test fun the_api_item_omits_the_fields_when_there_is_no_localized_title() {
        val j = MediaJson.item(meta(local = null, lang = null), emptyList())
        assertFalse(j.has("localizedTitle")); assertFalse(j.has("localizedLanguage"))
    }
    @Test fun both_titles_survive_storage_and_old_items_load_without_them() {
        val store = MediaStore(tmp.newFolder())
        store.save(meta())
        val back = store.get("m1")!!
        assertEquals("原标题", back.title); assertEquals("Original title", back.localizedTitle); assertEquals("en", back.localizedLanguage)
        val old = org.json.JSONObject(MediaJson.item(meta(local = null, lang = null), emptyList(), forStorage = true).toString())
        val loaded = MediaJson.fromStorage(old)
        assertNull(loaded.localizedTitle); assertNull(loaded.localizedLanguage)
    }

    // ---- capture and backfill
    private val queue = ArrayList<Runnable>()
    private val deferred = Executor { queue.add(it) }
    private fun runQueued() { while (queue.isNotEmpty()) queue.removeAt(0).run() }

    private class Fetcher(val extractor: String, val localized: String?, val titleFor: (String) -> String? = { localized }) : MediaFetcher {
        var titleCalls = 0
        var askedFor: String? = null
        override fun fetch(req: CaptureRequest, workDir: java.io.File, progress: (JobState, String?, Double?) -> Unit): CaptureOutcome {
            askedFor = req.titleLanguage
            return CaptureOutcome("原标题", 60, extractor, "abc", SubtitlesData("captions", "zh-Hans", listOf(Line(1.0, 2.0, "你好"))), null, null, null, emptyList(),
                if (req.titleLanguage != null) localized else null)
        }
        override fun fetchTitle(url: String, language: String): String? { titleCalls++; return titleFor(language) }
    }

    private fun manager(f: MediaFetcher, lang: String? = "en") =
        JobManager(Storage(tmp.newFolder()) { 5L }, MediaStore(tmp.newFolder()) { 5L }, f, deferred, { lang }) { 5L }

    @Test fun capture_stores_the_original_and_the_localized_title() {
        val f = Fetcher("youtube", "Original title")
        val m = manager(f)
        m.submit(JobSubmit("j1", "https://youtu.be/abc", emptyList(), Want(true, false))); runQueued()
        val item = m.media.get(m.media.idFor("https://youtu.be/abc"))!!
        assertEquals("en", f.askedFor)
        assertEquals("原标题", item.title); assertEquals("Original title", item.localizedTitle); assertEquals("en", item.localizedLanguage)
    }
    @Test fun a_localized_title_equal_to_the_original_is_not_kept() {
        val m = manager(Fetcher("youtube", "原标题"))
        m.submit(JobSubmit("j1", "https://youtu.be/abc", emptyList(), Want(true, false))); runQueued()
        val item = m.media.get(m.media.idFor("https://youtu.be/abc"))!!
        assertNull(item.localizedTitle); assertNull(item.localizedLanguage)
    }
    @Test fun original_only_mode_does_not_ask_for_a_localized_title() {
        val f = Fetcher("youtube", "Original title")
        val m = manager(f, lang = null)
        m.submit(JobSubmit("j1", "https://youtu.be/abc", emptyList(), Want(true, false))); runQueued()
        assertNull(f.askedFor)
        assertNull(m.media.get(m.media.idFor("https://youtu.be/abc"))!!.localizedTitle)
    }
    @Test fun an_unsupported_extractor_keeps_the_original_and_is_never_looked_up() {
        val f = Fetcher("bilibili", null)
        val m = manager(f)
        m.submit(JobSubmit("j1", "https://b/abc", emptyList(), Want(true, false))); runQueued()
        val item = m.media.get(m.media.idFor("https://b/abc"))!!
        assertEquals("原标题", item.title); assertNull(item.localizedTitle)
        assertNull(m.refreshTitle(item.mediaId))
        assertEquals(0, f.titleCalls)
    }
    @Test fun backfill_adds_the_title_to_an_existing_item_and_skips_when_already_done() {
        val f = Fetcher("youtube", "Original title")
        val m = manager(f)
        m.media.save(meta(local = null, lang = null))
        val checked = HashSet<String>()
        val first = m.refreshTitle("m1", { it in checked }, { checked.add(it) })!!
        assertEquals("Original title", first.localizedTitle)
        assertEquals("Original title", m.media.get("m1")!!.localizedTitle)
        assertNull(m.refreshTitle("m1", { it in checked }, { checked.add(it) }))
        assertEquals(1, f.titleCalls)
        assertEquals("Original title", m.refreshTitle("m1")!!.localizedTitle) // Refresh details repeats the lookup
        assertEquals(2, f.titleCalls)
    }
    @Test fun a_failed_lookup_changes_nothing() {
        val m = manager(Fetcher("youtube", null, titleFor = { null }))
        m.media.save(meta())
        assertNull(m.refreshTitle("m1"))
        assertEquals("Original title", m.media.get("m1")!!.localizedTitle)
    }
}
