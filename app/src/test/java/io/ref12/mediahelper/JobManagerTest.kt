package io.ref12.mediahelper

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.util.concurrent.Executor

class JobManagerTest {
    @get:Rule val tmp = TemporaryFolder()

    private var now = 1_000_000L
    private var fetches = 0
    private val queue = ArrayList<Runnable>()
    private val deferred = Executor { queue.add(it) }
    private fun runQueued() { while (queue.isNotEmpty()) queue.removeAt(0).run() }

    private fun transcript(url: String) = Transcript(url, "标题", 60, "youtube", "vid", "captions", "zh-Hans", listOf(Line(1.0, 2.0, "你好")))

    private fun manager(fetch: (String) -> Transcript) =
        JobManager(Storage(tmp.root) { now }, { u, _, p -> fetches++; p("Listing captions"); fetch(u) }, deferred) { now }

    @Test fun job_runs_and_result_is_available_and_cached() {
        val m = manager { transcript(it) }
        val id = m.submit("j1", "https://y/x", null)
        assertEquals(JobState.QUEUED, m.info(id)!!.state)
        runQueued()
        assertEquals(JobState.DONE, m.info(id)!!.state)
        assertTrue(m.resultJson(id)!!.contains("你好"))
        // same URL + languages: served from the cache, no second fetch
        val id2 = m.submit("j2", "https://y/x", null)
        assertEquals("j2", id2)
        assertEquals(JobState.DONE, m.info(id2)!!.state)
        assertEquals(1, fetches)
        assertNotNull(m.resultJson("j2"))
        // other languages: a new fetch
        m.submit("j3", "https://y/x", listOf("zh-TW"))
        runQueued()
        assertEquals(2, fetches)
    }

    @Test fun same_url_while_running_returns_the_running_job() {
        val m = manager { transcript(it) }
        val a = m.submit("a", "https://y/x", null)
        val b = m.submit("b", "https://y/x", null)
        assertEquals(a, b)
        runQueued()
        assertEquals(1, fetches)
    }

    @Test fun failure_is_recorded_with_message_and_not_cached() {
        val m = manager { throw FetchException("No Chinese captions") }
        val id = m.submit("j", "https://y/x", null)
        runQueued()
        assertEquals(JobInfo("j", JobState.FAILED, "No Chinese captions"), m.info(id))
        assertNull(m.resultJson(id))
        m.submit("k", "https://y/x", null); runQueued()
        assertEquals(2, fetches)
    }

    @Test fun jobs_and_results_survive_a_new_manager_and_expire_after_30_days() {
        val m = manager { transcript(it) }
        m.submit("j", "https://y/x", null); runQueued()
        val m2 = manager { transcript(it) }
        assertEquals(JobState.DONE, m2.info("j")!!.state)
        assertNotNull(m2.resultJson("j"))
        assertEquals(1, m2.recent().size)
        // age every stored file past 30 days; a new Storage purges them
        now += Storage.MAX_AGE_MS + 1
        tmp.root.walkTopDown().filter { it.isFile }.forEach { it.setLastModified(1_000_000L) }
        val m3 = manager { transcript(it) }
        assertNull(m3.info("j"))
    }

    @Test fun unknown_id_and_bad_id() {
        val m = manager { transcript(it) }
        assertNull(m.info("nope"))
        assertNull(m.info("../etc"))
    }
}
