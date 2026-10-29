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

    private fun outcome(video: File? = null, subs: Boolean = true) = CaptureOutcome(
        "标题", 60, "youtube", "vid",
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
        assertEquals(a, b)
        runQueued()
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
        val m = manager { _, d -> outcome(mp4(d)) }
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
}
