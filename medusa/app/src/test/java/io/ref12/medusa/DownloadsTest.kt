package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.util.concurrent.Executor

class DownloadsTest {
    @get:Rule val tmp = TemporaryFolder()
    private val own = "io.ref12.medusa"
    private val link = "medusa://open?job=j1&url=https%3A%2F%2Fm.youtube.com%2Fwatch%3Fv%3DugFBF4gsdk0%23bottom-sheet"

    @Test fun deep_link_routes_to_downloads_with_the_job_highlighted() {
        val r = LinkRouting.resolve(link, null, false, "com.android.chrome", own)
        assertTrue(r.show); assertEquals("j1", r.jobId)
        assertEquals("https://m.youtube.com/watch?v=ugFBF4gsdk0", r.request!!.url)
    }

    @Test fun notification_and_job_extra_route_to_downloads() {
        val n = LinkRouting.resolve(null, null, true, null, own)
        assertTrue(n.show); assertNull(n.jobId); assertNull(n.request)
        assertEquals("abc", LinkRouting.resolve(null, "abc", false, null, own).jobId)
        assertFalse(LinkRouting.resolve(null, null, false, null, own).show)
    }

    @Test fun invalid_link_is_reported_not_started() {
        val r = LinkRouting.resolve("medusa://open?url=ftp://x", null, false, "com.android.chrome", own)
        assertTrue(r.invalidLink); assertNull(r.request)
    }

    @Test fun back_rules_from_another_app_versus_inside_medusa() {
        assertTrue(LinkRouting.resolve(link, null, false, "com.android.chrome", own).leaveOnBack)
        assertFalse(LinkRouting.resolve(link, null, false, own, own).leaveOnBack)
        assertFalse(LinkRouting.resolve(link, null, false, null, own).leaveOnBack)
        assertFalse(LinkRouting.resolve(null, "j", false, "com.android.chrome", own).leaveOnBack)
    }

    @Test fun navstack_downloads_keeps_the_highlighted_job_and_back_goes_to_library() {
        val n = NavStack()
        n.goTop(Dest.DOWNLOADS, "j1")
        assertEquals(Dest.DOWNLOADS, n.highlighted); assertEquals("j1", n.current.arg)
        assertTrue(n.back()); assertEquals(Dest.LIBRARY, n.current.dest); assertFalse(n.back())
        assertEquals(Screen(Dest.DOWNLOADS, "x"), Screen.decode(Screen(Dest.DOWNLOADS, "x").encode()))
    }

    @Test fun url_hygiene_strips_fragments_and_tracking() {
        assertEquals("https://m.youtube.com/watch?v=ugFBF4gsdk0", UrlNormalizer.clean("https://m.youtube.com/watch?v=ugFBF4gsdk0#bottom-sheet"))
        assertEquals("https://x.com/a?id=5", UrlNormalizer.clean("https://x.com/a?utm_source=t&id=5&fbclid=z#frag"))
        assertEquals("https://x.com/a", UrlNormalizer.clean(" https://x.com/a?utm_campaign=q "))
        assertEquals("https://www.youtube.com/watch?v=ugFBF4gsdk0", UrlNormalizer.canonical("https://youtu.be/ugFBF4gsdk0?si=abc#t"))
        assertEquals(UrlNormalizer.canonical("https://m.youtube.com/watch?v=ugFBF4gsdk0#bottom-sheet"), UrlNormalizer.canonical("https://www.youtube.com/watch?v=ugFBF4gsdk0"))
    }

    private fun meta(id: String, st: JobState, at: Long, p: Double? = null) =
        JobMeta(id, "https://y/$id", emptyList(), Want.DEFAULT, Quality.P720, st, p, null, null, null, null, null, at)

    @Test fun grouping_active_today_and_week() {
        val day = 24L * 3600 * 1000; val today = 100 * day; val now = today + 5000
        val all = listOf(meta("a", JobState.DOWNLOADING, now), meta("t", JobState.DONE, today + 10), meta("w", JobState.FAILED, today - 2 * day),
            meta("old", JobState.DONE, today - 9 * day), meta("q", JobState.QUEUED, now - 100))
        val g = DownloadsLogic.group(all, now, today)
        assertEquals(listOf("q", "a"), g.active.map { it.id })
        assertEquals(listOf("t"), g.today.map { it.id }); assertEquals(listOf("w"), g.week.map { it.id })
    }

    @Test fun stage_text_and_indeterminate_not_zero_percent() {
        assertTrue(DownloadsLogic.indeterminate(JobState.FETCHING, null)); assertTrue(DownloadsLogic.indeterminate(JobState.FETCHING, 0.0))
        assertFalse(DownloadsLogic.indeterminate(JobState.DOWNLOADING, 0.4)); assertFalse(DownloadsLogic.indeterminate(JobState.DONE, null))
        assertEquals("Reading video information\u2026", DownloadsLogic.stageText(JobState.FETCHING, "Reading video information\u2026", 0.0))
        assertEquals("Downloading video 40%", DownloadsLogic.stageText(JobState.DOWNLOADING, "Downloading video", 0.4))
        assertEquals("Cancelled", DownloadsLogic.stageText(JobState.FAILED, "Cancelled", null))
    }

    // --- JobManager: list / cancel / remove / retry and URL cleaning
    private val queue = ArrayList<Runnable>()
    private fun runQueued() { while (queue.isNotEmpty()) queue.removeAt(0).run() }
    private var cancelled = false
    private fun manager(onFetch: () -> Unit = {}): JobManager {
        val root = tmp.root
        val fetcher = object : MediaFetcher {
            override fun fetch(req: CaptureRequest, workDir: File, progress: (JobState, String?, Double?) -> Unit): CaptureOutcome {
                onFetch(); progress(JobState.FETCHING, "Reading", null)
                return CaptureOutcome("T", 5, "youtube", "vid", SubtitlesData("captions", "en", listOf(Line(0.0, 1.0, "hi"))), null, null, null)
            }
            override fun cancel() { cancelled = true }
        }
        return JobManager(Storage(root), MediaStore(root), fetcher, Executor { queue.add(it) })
    }

    @Test fun submit_cleans_the_url() {
        val m = manager(); val id = m.submit(JobSubmit("j", "https://m.youtube.com/watch?v=ugFBF4gsdk0#bottom-sheet", null, Want.DEFAULT))
        assertEquals("https://m.youtube.com/watch?v=ugFBF4gsdk0", m.record(id)!!.url)
    }

    @Test fun list_shows_active_then_finished_and_remove_retry_work() {
        val m = manager()
        val a = m.submit(JobSubmit("a", "https://y/a", null, Want.DEFAULT)); runQueued()
        val b = m.submit(JobSubmit("b", "https://y/b", null, Want.DEFAULT))
        assertEquals(1, m.activeCount()); assertEquals(setOf("a", "b"), m.list().map { it.id }.toSet())
        assertFalse(m.remove(b)) // still active
        runQueued()
        assertTrue(m.remove(a)); assertEquals(listOf("b"), m.list().map { it.id })
        val n = m.retry(b)!!; assertTrue(n != b); assertEquals(listOf(n), m.list().map { it.id })
    }

    @Test fun cancel_a_queued_job_never_runs_it() {
        var fetched = 0
        val m = manager { fetched++ }
        val id = m.submit(JobSubmit("c", "https://y/c", null, Want.DEFAULT))
        assertTrue(m.cancel(id)); runQueued()
        assertEquals(0, fetched); assertEquals(JobState.FAILED, m.info(id)!!.state); assertEquals(Codes.CANCELLED, m.info(id)!!.code)
        assertEquals("Cancelled", m.record(id)!!.message); assertFalse(m.cancel(id))
        assertEquals(0, m.activeCount())
    }
}
