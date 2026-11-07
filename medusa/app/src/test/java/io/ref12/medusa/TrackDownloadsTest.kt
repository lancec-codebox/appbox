package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.Executor

class TrackDownloadsTest {
    private val en = TrackInfo("en", "captions")
    private val fr = TrackInfo("fr", "auto-captions")
    private val de = TrackInfo("de", "captions")
    private val added = TrackInfo("pt", "added", "Portuguese", trackId = "t1")
    private val ocr = TrackInfo("ja", "ocr", trackId = "o1")
    private val meta = MediaMeta("m1", "https://youtu.be/abc", "T", 60, "youtube", "abc", SubtitlesData("captions", "en", emptyList()), null, 1L)
    private val sub = { l: String -> SubtitlesData("captions", l, listOf(Line(1.0, 2.0, "x"))) }

    // ---- per-track state
    @Test fun added_and_ocr_tracks_are_always_on_device() {
        assertTrue(TrackDevice.onDevice(meta, added) { false })
        assertTrue(TrackDevice.onDevice(meta, ocr) { false })
    }
    @Test fun captured_track_is_on_device_when_main_or_saved() {
        assertTrue(TrackDevice.onDevice(meta, en) { false })   // the item's own main track
        assertFalse(TrackDevice.onDevice(meta, fr) { false })
        assertTrue(TrackDevice.onDevice(meta, fr) { it == "fr" })
    }

    // ---- download all
    private fun downloader(saved: MutableSet<String>, calls: MutableList<String>, fail: Map<String, Throwable> = emptyMap(), online: Boolean = true): TrackDownloads {
        val now = Executor { it.run() }
        return TrackDownloads({ _, t -> t.added || t.language in saved }, { _, t -> calls.add(t.language); fail[t.language]?.let { throw it }; saved.add(t.language); sub(t.language) },
            { online }, now, { it() })
    }

    @Test fun download_all_goes_in_order_and_skips_what_is_on_device() {
        val saved = mutableSetOf("de"); val calls = ArrayList<String>(); val changed = ArrayList<String>()
        val d = downloader(saved, calls)
        val tracks = listOf(en, fr, de, added, ocr)
        d.downloadAll("m1", tracks, { t -> TrackDevice.missing(meta, listOf(t)) { it in saved }.isNotEmpty() }) { t, e -> assertNull(e); changed.add(t.language) }
        assertEquals(listOf("fr"), calls)           // en is main, de saved, added/ocr are local
        assertEquals(listOf("fr"), changed)
        assertEquals(TrackState.ON_DEVICE, d.state("m1", fr))
        assertEquals(listOf(fr), TrackDevice.missing(meta, tracks) { it in setOf("de") })
    }
    @Test fun download_all_continues_after_a_failure_and_keeps_the_reason() {
        val saved = mutableSetOf<String>(); val calls = ArrayList<String>()
        val d = downloader(saved, calls, mapOf("fr" to FetchException("ERROR: [youtube] abc: Video unavailable", Codes.YTDLP)))
        val errs = HashMap<String, String?>()
        d.downloadAll("m1", listOf(fr, de), { true }) { t, e -> errs[t.language] = e }
        assertEquals(listOf("fr", "de"), calls)
        assertNotNull(errs["fr"]); assertNull(errs["de"])
        assertEquals(TrackState.FAILED, d.state("m1", fr))
        assertEquals(TrackState.ON_DEVICE, d.state("m1", de))
    }
    @Test fun a_failed_track_can_be_retried() {
        val saved = mutableSetOf<String>(); val calls = ArrayList<String>()
        var ok = false
        val d = TrackDownloads({ _, t -> t.language in saved }, { _, t -> if (!ok) throw FetchException("boom") else { saved.add(t.language); sub(t.language) } },
            { true }, { it.run() }, { it() })
        var err: String? = "unset"
        d.download("m1", fr) { _, e -> err = e }
        assertEquals(TrackState.FAILED, d.state("m1", fr))
        ok = true
        d.download("m1", fr) { s, e -> err = e; assertNotNull(s) }
        assertNull(err); assertEquals(TrackState.ON_DEVICE, d.state("m1", fr)); assertNull(d.failure("m1", fr))
    }
    @Test fun state_is_downloading_while_the_worker_has_not_run() {
        val queue = ArrayList<Runnable>()
        val d = TrackDownloads({ _, _ -> false }, { _, t -> sub(t.language) }, { true }, { queue.add(it) }, { it() })
        d.download("m1", fr) { _, _ -> }
        assertEquals(TrackState.DOWNLOADING, d.state("m1", fr)); assertTrue(d.anyRunning("m1"))
        d.download("m1", fr) { _, _ -> fail2() }   // already running: ignored
        assertEquals(1, queue.size)
    }
    private fun fail2() = org.junit.Assert.fail("second request must be ignored")

    // ---- error messages
    @Test fun offline_says_needs_a_network() {
        assertTrue(DownloadErrors.message(FetchException("anything"), online = false).contains("No network connection"))
    }
    @Test fun online_failures_never_claim_the_network_is_missing() {
        val m = DownloadErrors.message(FetchException("ERROR: [youtube] abc: Unable to download webpage: <urlopen error>"), online = true)
        assertFalse(m.contains("network connection", true)); assertEquals("Unable to download webpage: <urlopen error>", m)
    }
    @Test fun ytdlp_message_is_cut_to_one_line() {
        val long = "WARNING: x\nERROR: [youtube] abc: " + "very long reason ".repeat(30) + "\nmore"
        val m = DownloadErrors.message(FetchException(long), online = true)
        assertFalse(m.contains("\n")); assertTrue(m.length <= 160); assertTrue(m.endsWith("\u2026")); assertTrue(m.startsWith("very long reason"))
    }
    @Test fun gone_signin_and_missing_track_have_their_own_words() {
        assertTrue(DownloadErrors.message(FetchException("ERROR: [youtube] abc: Video unavailable"), true).contains("gone"))
        assertTrue(DownloadErrors.message(FetchException("ERROR: HTTP Error 404: Not Found"), true).contains("gone"))
        assertTrue(DownloadErrors.message(FetchException("ERROR: Sign in to confirm you're not a bot", Codes.BOT_CHECK), true).contains("sign in"))
        assertTrue(DownloadErrors.message(FetchException("No fr track", Codes.NO_CAPTIONS), true).contains("no longer available"))
    }
}
