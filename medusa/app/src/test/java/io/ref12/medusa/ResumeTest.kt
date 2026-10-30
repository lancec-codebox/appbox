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

class ResumeTest {
    @get:Rule val tmp = TemporaryFolder()
    private val queue = ArrayList<Runnable>()
    private fun runQueued() { while (queue.isNotEmpty()) queue.removeAt(0).run() }

    private var failNext = true
    private var sawPartialBytes = -1L
    private lateinit var media: MediaStore

    private fun manager(): JobManager {
        val root = tmp.root
        media = MediaStore(root)
        val fetcher = object : MediaFetcher {
            override fun fetch(req: CaptureRequest, workDir: File, progress: (JobState, String?, Double?) -> Unit): CaptureOutcome {
                val video = File(workDir, "video").also { it.mkdirs() }
                sawPartialBytes = Resume.partialBytes(workDir)
                if (failNext) { File(video, "v.mp4.part").writeBytes(ByteArray(2000)); throw FetchException("network dropped", Codes.NETWORK) }
                val f = File(video, "v.mp4"); f.writeBytes(ByteArray(10)); File(video, "v.mp4.part").delete()
                return CaptureOutcome("T", 5, "youtube", "vid", null, f, null, null)
            }
        }
        return JobManager(Storage(root), media, fetcher, Executor { queue.add(it) })
    }

    private fun submit(m: JobManager): String { val id = m.submit(JobSubmit("a", "https://y/a", null, Want.DEFAULT.copy(subtitles = false))); runQueued(); return id }

    @Test fun options_continue_only_when_a_partial_file_exists() {
        val dir = tmp.newFolder("v")
        assertEquals(emptyList<String>(), Resume.videoOptions(dir))
        File(dir, "x.mp4.part").writeBytes(ByteArray(5))
        assertEquals(listOf("--continue"), Resume.videoOptions(dir))
        assertTrue(Resume.message(dir, "720p").startsWith("Resuming video"))
    }

    @Test fun a_failed_download_keeps_its_partial_files_and_resume_continues_from_them() {
        val m = manager(); val id = submit(m)
        assertEquals(JobState.FAILED, m.info(id)!!.state)
        assertEquals(2000L, m.partialBytes(id))
        failNext = false
        val n = m.resume(id)!!; runQueued()
        assertEquals(2000L, sawPartialBytes) // the retry started with the partial file present
        assertEquals(JobState.DONE, m.info(n)!!.state)
        assertEquals(0L, media.partialBytes(m.record(n)!!.mediaId)) // cleaned up once complete
        assertNotNull(media.get(m.record(n)!!.mediaId)!!.video)
    }

    @Test fun cancel_keeps_partials_and_remove_deletes_them() {
        val m = manager(); val id = submit(m)
        val mediaId = m.record(id)!!.mediaId
        assertTrue(media.partialBytes(mediaId) > 0)
        assertTrue(m.remove(id)) // failed row: Remove deletes the partial files
        assertEquals(0L, media.partialBytes(mediaId))
        // a cancelled job keeps its partials until removed
        failNext = true
        val c = m.submit(JobSubmit("c", "https://y/a", null, Want.DEFAULT.copy(subtitles = false))); runQueued()
        assertEquals(JobState.FAILED, m.info(c)!!.state)
        assertTrue(media.partialBytes(mediaId) > 0)
        m.cancel(c) // already finished: no effect, partials stay
        assertTrue(media.partialBytes(mediaId) > 0)
    }

    @Test fun restart_deletes_the_files_and_downloads_everything_again() {
        val m = manager(); val id = submit(m)
        val mediaId = m.record(id)!!.mediaId
        media.save(MediaMeta(mediaId, "https://y/a", "old", 1, "youtube", "vid", null, null, 1L))
        File(tmp.root, "sentinel").writeText("")
        failNext = false
        val n = m.restart(id)!!
        assertEquals(0L, media.partialBytes(mediaId)) // removed before the new job runs
        assertNull(media.get(mediaId))
        runQueued()
        assertEquals(0L, sawPartialBytes) // started from scratch
        assertEquals(JobState.DONE, m.info(n)!!.state)
        assertFalse(m.list().any { it.id == id })
    }

    @Test fun resume_is_refused_while_the_item_is_downloading() {
        val m = manager()
        val id = m.submit(JobSubmit("a", "https://y/a", null, Want.DEFAULT.copy(subtitles = false)))
        assertNull(m.resume(id)); assertNull(m.restart(id))
        runQueued()
    }
}
