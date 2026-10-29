package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.util.concurrent.Executor

class JobManagerTest {
    @get:Rule val tmp = TemporaryFolder()

    private var now = 1_000_000L
    private var fetches = 0
    private val queue = ArrayList<Runnable>()
    private val deferred = Executor { queue.add(it) }
    private fun runQueued() { while (queue.isNotEmpty()) queue.removeAt(0).run() }
    private val seen = ArrayList<Pair<JobState, Double?>>()

    private var videoIdSeen = "vid"
    private fun outcome(video: File? = null, subs: Boolean = true) = CaptureOutcome(
        "标题", 60, "youtube", videoIdSeen,
        if (subs) SubtitlesData("captions", "zh-Hans", listOf(Line(1.0, 2.0, "你好"))) else null,
        video, if (subs) null else FetchException("No captions", Codes.NO_CAPTIONS), null,
    )

    private fun manager(fetch: (CaptureRequest, File) -> CaptureOutcome): JobManager {
        val root = tmp.root
        return JobManager(Storage(root) { now }, MediaStore(root) { now }, { req, dir, p ->
            fetches++
            p(JobState.DOWNLOADING, "Downloading", 0.4); seen.add(JobState.DOWNLOADING to 0.4)
            fetch(req, dir)
        }, deferred) { now }
    }

    private fun mp4(dir: File) = File(dir, "video.mp4").also { it.writeBytes(ByteArray(2048) { b -> b.toByte() }) }
    private fun sub(id: String?, url: String, langs: List<String>? = null, want: Want = Want.DEFAULT) = JobSubmit(id, url, langs, want)

    @Test fun job_captures_subtitles_and_video_with_progress_and_persists_media() {
        val m = manager { _, d -> outcome(mp4(d)) }
        val id = m.submit(sub("j1", "https://y/x"))
        assertEquals(JobState.QUEUED, m.info(id)!!.state)
        runQueued()
        val i = m.info(id)!!
        assertEquals(JobState.DONE, i.state)
        assertEquals(1.0, i.progress!!, 0.0)
        val json = org.json.JSONObject(m.resultJson(id)!!)
        assertEquals("标题", json.getString("title"))
        assertEquals("你好", json.getJSONObject("subtitles").getJSONArray("lines").getJSONObject(0).getString("text"))
        assertEquals("video/mp4", json.getJSONObject("video").getString("contentType"))
        assertEquals(2048L, json.getJSONObject("video").getLong("bytes"))
        assertTrue(m.media.videoFile(m.media.idFor("https://y/x"))!!.isFile)
    }

    @Test fun already_captured_media_completes_without_fetching_and_uses_the_requested_id() {
        val m = manager { _, d -> outcome(mp4(d)) }
        m.submit(sub("j1", "https://y/x")); runQueued()
        val id2 = m.submit(sub("j2", "https://y/x"))
        assertEquals("j2", id2)
        assertEquals(JobState.DONE, m.info(id2)!!.state)
        assertEquals(1, fetches)
        assertNotNull(m.resultJson("j2"))
        // wanting a part that is missing fetches again
        val m2 = manager { _, d -> outcome(null) }
        m2.submit(sub("k1", "https://z/y", want = Want(true, false))); runQueued()
        m2.submit(sub("k2", "https://z/y", want = Want(true, true))); runQueued()
        assertEquals(3, fetches)
    }

    @Test fun same_url_while_running_returns_the_running_job() {
        val m = manager { _, d -> outcome(mp4(d)) }
        val a = m.submit(sub("a", "https://y/x"))
        val b = m.submit(sub("b", "https://y/x"))
        assertEquals("a", a)
        assertEquals("b", b) // the requested id answers too: it is an alias of the running job
        assertEquals(JobState.QUEUED, m.info("b")!!.state)
        runQueued()
        assertEquals(JobState.DONE, m.info("b")!!.state)
        assertEquals(1, fetches)
    }

    @Test fun partial_success_is_done_with_a_note() {
        val m = manager { _, d -> outcome(mp4(d), subs = false) }
        val id = m.submit(sub("j", "https://y/x")); runQueued()
        assertEquals(JobState.DONE, m.info(id)!!.state)
        assertTrue(m.info(id)!!.message!!.contains("No captions"))
        assertEquals("null", org.json.JSONObject(m.resultJson(id)!!).get("subtitles").toString())
    }

    @Test fun failure_carries_code_and_message() {
        val m = manager { _, _ -> throw FetchException("YouTube asks for a sign-in", Codes.BOT_CHECK) }
        val id = m.submit(sub("j", "https://y/x")); runQueued()
        val i = m.info(id)!!
        assertEquals(JobState.FAILED, i.state)
        assertEquals(Codes.BOT_CHECK, i.code)
        assertEquals("YouTube asks for a sign-in", i.message)
        assertNull(m.resultJson(id))
    }

    @Test fun nothing_wanted_captured_is_a_failure() {
        val m = manager { _, _ -> outcome(null, subs = false) }
        val id = m.submit(sub("j", "https://y/x", want = Want(true, false))); runQueued()
        assertEquals(Codes.NO_CAPTIONS, m.info(id)!!.code)
    }

    @Test fun jobs_survive_a_new_manager_and_old_jobs_expire() {
        val m = manager { _, d -> outcome(mp4(d)) }
        m.submit(sub("j", "https://y/x")); runQueued()
        val m2 = manager { _, d -> outcome(mp4(d)) }
        assertEquals(JobState.DONE, m2.info("j")!!.state)
        assertNotNull(m2.resultJson("j"))
        now += Storage.MAX_AGE_MS + 1
        File(tmp.root, "jobs").listFiles()!!.forEach { it.setLastModified(1_000_000L) }
        assertNull(manager { _, d -> outcome(mp4(d)) }.info("j"))
    }

    @Test fun media_list_and_delete() {
        var n = 0
        val m = manager { _, d -> videoIdSeen = "vid" + (n++); outcome(mp4(d)) }
        m.submit(sub("a", "https://y/a")); m.submit(sub("b", "https://y/b")); runQueued()
        assertEquals(2, m.media.list().size)
        val id = m.media.idFor("https://y/a")
        assertEquals(0, m.media.sync(id).size)
        assertTrue(m.media.putSync(id, listOf(Anchor(10.0, 0.5))))
        assertEquals(listOf(Anchor(10.0, 0.5)), m.media.sync(id))
        assertFalse(m.media.putSync("nope", emptyList()))
        assertTrue(m.media.delete(id))
        assertNull(m.media.get(id))
        assertFalse(m.media.delete(id))
        assertFalse(m.media.delete("../x"))
        assertEquals(1, m.media.list().size)
    }

    @Test fun unknown_id_and_bad_id() {
        val m = manager { _, d -> outcome(mp4(d)) }
        assertNull(m.info("nope"))
        assertNull(m.info("../etc"))
    }

    // ---- v1.2: tracks and on-demand subtitles ----

    private val tracks = listOf(TrackInfo("en", "captions", "English"), TrackInfo("fr", "auto-captions"))
    private val enLines = listOf(Line(1.0, 2.0, "hello"))
    private val frLines = listOf(Line(1.0, 2.0, "bonjour"))
    private var trackFetches = 0
    private var languagesSeen: List<String>? = null

    private fun trackManager(): JobManager {
        val root = tmp.root
        val fetcher = object : MediaFetcher {
            override fun fetch(req: CaptureRequest, workDir: File, progress: (JobState, String?, Double?) -> Unit): CaptureOutcome {
                languagesSeen = req.languages
                return CaptureOutcome("T", 10, "youtube", "v", SubtitlesData("captions", "en", enLines), null, null, null, tracks)
            }
            override fun fetchTrack(url: String, language: String, workDir: File): SubtitlesData {
                trackFetches++
                if (language == "fr") return SubtitlesData("auto-captions", "fr", frLines)
                throw FetchException("No captions in $language", Codes.NO_CAPTIONS)
            }
        }
        return JobManager(Storage(root) { now }, MediaStore(root) { now }, fetcher, deferred) { now }
    }

    @Test fun job_without_languages_asks_for_the_original_and_the_item_lists_tracks() {
        val m = trackManager()
        val id = m.submit(JobSubmit("j", "https://y/x", null, Want(true, false))); runQueued()
        assertEquals(emptyList<String>(), languagesSeen)
        val j = org.json.JSONObject(m.resultJson(id)!!)
        val t = j.getJSONArray("tracks")
        assertEquals(2, t.length())
        assertEquals("English", t.getJSONObject(0).getString("name"))
        assertEquals("auto-captions", t.getJSONObject(1).getString("source"))
        assertFalse(t.getJSONObject(1).has("name"))
        assertEquals("en", j.getJSONObject("subtitles").getString("language"))
    }

    @Test fun v11_job_with_zh_languages_gets_the_same_result_shape() {
        val m = trackManager()
        val id = m.submit(JobSubmit("j", "https://y/x", listOf("zh-Hans", "zh"), Want(true, false))); runQueued()
        assertEquals(listOf("zh-Hans", "zh"), languagesSeen)
        val j = org.json.JSONObject(m.resultJson(id)!!)
        for (k in listOf("mediaId", "url", "title", "durationSec", "extractor", "videoId", "video", "subtitles", "tracks", "sync")) assertTrue(k, j.has(k))
    }

    @Test fun other_track_is_downloaded_once_and_the_main_track_is_unchanged() {
        val m = trackManager()
        m.submit(JobSubmit("j", "https://y/x", null, Want(true, false))); runQueued()
        val mid = m.media.idFor("https://y/x")
        assertEquals("bonjour", m.subtitlesFor(mid, "fr")!!.lines[0].text)
        assertEquals("bonjour", m.subtitlesFor(mid, "fr")!!.lines[0].text)
        assertEquals(1, trackFetches)
        assertEquals("en", m.media.get(mid)!!.subtitles!!.language)
        assertEquals("hello", m.subtitlesFor(mid, "en")!!.lines[0].text) // main track: no download
        assertEquals(1, trackFetches)
        assertNull(m.subtitlesFor("nope", "fr"))
    }

    @Test fun unknown_track_fails_with_no_captions_naming_what_exists() {
        val m = trackManager()
        m.submit(JobSubmit("j", "https://y/x", null, Want(true, false))); runQueued()
        val e = try { m.subtitlesFor(m.media.idFor("https://y/x"), "ko"); null } catch (e: FetchException) { e }!!
        assertEquals(Codes.NO_CAPTIONS, e.code)
        assertEquals("No captions in ko (available: en, fr)", e.message)
    }

    @Test fun changing_the_main_track_keeps_sync_and_tracks() {
        val m = trackManager()
        m.submit(JobSubmit("j", "https://y/x", null, Want(true, false))); runQueued()
        val mid = m.media.idFor("https://y/x")
        m.media.putSync(mid, listOf(Anchor(5.0, 0.5)))
        val updated = m.setMainTrack(mid, "fr")!!
        assertEquals("fr", updated.subtitles!!.language)
        assertEquals("bonjour", m.media.get(mid)!!.subtitles!!.lines[0].text)
        assertEquals(tracks, m.media.get(mid)!!.tracks)
        assertEquals(listOf(Anchor(5.0, 0.5)), m.media.sync(mid)) // per item, not per track
        assertNull(m.setMainTrack("nope", "fr"))
    }

    @Test fun captured_item_with_other_language_is_fetched_again_when_a_language_is_requested() {
        val m = trackManager()
        m.submit(JobSubmit("a", "https://y/x", null, Want(true, false))); runQueued()
        m.submit(JobSubmit("b", "https://y/x", listOf("en"), Want(true, false)))
        assertEquals(JobState.DONE, m.info("b")!!.state) // en is already the main track
        assertEquals(emptyList<String>(), languagesSeen)
        m.submit(JobSubmit("c", "https://y/x", listOf("de"), Want(true, false)))
        assertEquals(JobState.QUEUED, m.info("c")!!.state)
    }

    // ---- 1.3.1: ids, aliases, persistence, idempotent capture ----

    private class CountingFetcher(val subsByLang: Map<String, SubtitlesData>) : MediaFetcher {
        var videoDownloads = 0
        val subtitleFetches = ArrayList<List<String>>()
        override fun fetch(req: CaptureRequest, workDir: File, progress: (JobState, String?, Double?) -> Unit): CaptureOutcome {
            val stored = req.videoAlreadyStored("youtube", "4mcienmpsnk")
            var video: File? = null
            if (req.wantVideo && !stored) { videoDownloads++; video = File(workDir, "video.mp4").also { it.writeBytes(ByteArray(100)) } }
            var subs: SubtitlesData? = null
            if (req.wantSubtitles) {
                subtitleFetches.add(req.languages)
                subs = if (req.languages.isEmpty()) subsByLang.values.first() else req.languages.firstNotNullOfOrNull { subsByLang[it] }
            }
            return CaptureOutcome("Video", 60, "youtube", "4mcienmpsnk", subs, video,
                if (req.wantSubtitles && subs == null) FetchException("No captions", Codes.NO_CAPTIONS) else null, null,
                subsByLang.values.map { TrackInfo(it.language, it.source) })
        }
    }

    private val subs = mapOf(
        "en" to SubtitlesData("captions", "en", listOf(Line(1.0, 2.0, "hello"))),
        "fr" to SubtitlesData("captions", "fr", listOf(Line(1.0, 2.0, "bonjour"))),
    )

    private fun idManager(f: MediaFetcher, root: File = tmp.root): JobManager =
        JobManager(Storage(root) { now }, MediaStore(root) { now }, f, deferred) { now }

    @Test fun deep_link_id_becomes_the_job_id_for_a_new_capture() {
        val m = idManager(CountingFetcher(subs))
        val id = m.submit(JobSubmit("link-job-1", "https://m.youtube.com/watch?v=4mcienmpsnk&feature=share", null))
        assertEquals("link-job-1", id)
        assertEquals(JobState.QUEUED, m.info("link-job-1")!!.state)
        runQueued()
        assertEquals(JobState.DONE, m.info("link-job-1")!!.state)
        assertNotNull(m.resultJson("link-job-1"))
    }

    @Test fun deep_link_id_is_an_alias_of_a_running_job_for_the_same_video() {
        val m = idManager(CountingFetcher(subs))
        m.submit(JobSubmit("first", "https://www.youtube.com/watch?v=4mcienmpsnk", null))
        val id = m.submit(JobSubmit("second", "https://youtu.be/4mcienmpsnk?si=x", null)) // still queued
        assertEquals("second", id)
        assertEquals(JobState.QUEUED, m.info("second")!!.state)
        assertEquals("second", m.info("second")!!.id) // answered under the id the client knows
        runQueued()
        assertEquals(JobState.DONE, m.info("second")!!.state)
        assertNotNull(m.resultJson("second"))
        assertEquals(1, m.activeCount() + 1) // only one real job ever ran
    }

    @Test fun alias_survives_a_restart() {
        val f = CountingFetcher(subs)
        val m = idManager(f)
        m.submit(JobSubmit("first", "https://www.youtube.com/watch?v=4mcienmpsnk", null))
        m.submit(JobSubmit("second", "https://youtu.be/4mcienmpsnk", null)); runQueued()
        val m2 = idManager(f)
        assertEquals(JobState.DONE, m2.info("second")!!.state)
        assertNotNull(m2.resultJson("second"))
    }

    @Test fun deep_link_id_for_an_already_captured_url_is_a_done_job_at_once() {
        val f = CountingFetcher(subs)
        val m = idManager(f)
        m.submit(JobSubmit("a", "https://www.youtube.com/watch?v=4mcienmpsnk", null)); runQueued()
        val id = m.submit(JobSubmit("from-phone", "https://m.youtube.com/watch?v=4mcienmpsnk&feature=share", null))
        assertEquals("from-phone", id)
        assertEquals(JobState.DONE, m.info("from-phone")!!.state)
        assertEquals(1.0, m.info("from-phone")!!.progress!!, 0.0)
        assertEquals(0, queue.size) // nothing queued
        assertEquals(1, f.videoDownloads)
    }

    @Test fun same_video_by_four_url_forms_is_one_item_and_one_download() {
        val f = CountingFetcher(subs)
        val m = idManager(f)
        for ((i, u) in listOf("https://www.youtube.com/watch?v=4mcienmpsnk", "https://m.youtube.com/watch?v=4mcienmpsnk&t=5",
            "https://youtu.be/4mcienmpsnk", "https://www.youtube.com/shorts/4mcienmpsnk").withIndex()) {
            m.submit(JobSubmit("j$i", u, null)); runQueued()
            assertEquals(JobState.DONE, m.info("j$i")!!.state)
        }
        assertEquals(1, m.media.list().size)
        assertEquals(1, f.videoDownloads)
    }

    @Test fun same_video_found_by_identity_when_the_url_is_not_recognised() {
        val f = CountingFetcher(subs)
        val m = idManager(f)
        m.submit(JobSubmit("a", "https://www.youtube.com/watch?v=4mcienmpsnk", null)); runQueued()
        m.submit(JobSubmit("b", "https://some-redirector.example/x/4mcienmpsnk", null)); runQueued() // yt-dlp names the same video
        assertEquals(1, m.media.list().size)
        assertEquals(1, f.videoDownloads)
        assertEquals(m.media.list()[0].mediaId, m.record("b")!!.mediaId)
    }

    @Test fun second_capture_with_a_new_language_downloads_only_that_track() {
        val f = CountingFetcher(subs)
        val m = idManager(f)
        m.submit(JobSubmit("a", "https://youtu.be/4mcienmpsnk", listOf("en"))); runQueued()
        m.submit(JobSubmit("b", "https://youtu.be/4mcienmpsnk", listOf("fr"))); runQueued()
        assertEquals(1, f.videoDownloads)
        assertEquals(listOf(listOf("en"), listOf("fr")), f.subtitleFetches)
        val item = m.media.list().single()
        assertEquals("fr", item.subtitles!!.language)
        assertEquals("hello", m.media.loadTrack(item.mediaId, "en")!!.lines[0].text) // the English track is kept
        // asking for English again is a no-op: stored, so nothing is fetched at all
        m.submit(JobSubmit("c", "https://youtu.be/4mcienmpsnk", listOf("fr")))
        assertEquals(JobState.DONE, m.info("c")!!.state)
        assertEquals(2, f.subtitleFetches.size)
    }

    @Test fun a_job_that_was_running_when_the_process_died_is_failed_with_a_clear_message() {
        val m = idManager(CountingFetcher(subs))
        m.submit(JobSubmit("x", "https://www.youtube.com/watch?v=4mcienmpsnk", null)) // queued, never run
        val m2 = idManager(CountingFetcher(subs)) // the process was killed: a new manager on the same files
        val i = m2.info("x")!!
        assertEquals(JobState.FAILED, i.state)
        assertEquals(Codes.INTERRUPTED, i.code)
        assertTrue(i.message!!.contains("closed before"))
        assertEquals(JobState.FAILED, idManager(CountingFetcher(subs)).info("x")!!.state) // and it stays that way
    }

    @Test fun done_and_failed_jobs_are_served_after_a_restart_for_over_a_day() {
        val f = CountingFetcher(subs)
        val m = idManager(f)
        m.submit(JobSubmit("done1", "https://www.youtube.com/watch?v=4mcienmpsnk", null)); runQueued()
        val failing = idManager(MediaFetcher { _, _, _ -> throw FetchException("YouTube asks for a sign-in", Codes.BOT_CHECK) })
        failing.submit(JobSubmit("bad1", "https://www.youtube.com/watch?v=AAAAAAAAAAA", null)); runQueued()
        now += 25L * 3600 * 1000
        val m2 = idManager(f)
        assertEquals(JobState.DONE, m2.info("done1")!!.state)
        assertNotNull(m2.resultJson("done1"))
        val bad = m2.info("bad1")!!
        assertEquals(JobState.FAILED, bad.state); assertEquals(Codes.BOT_CHECK, bad.code)
    }
}

