package io.ref12.medusa.node

import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.File
import java.nio.file.Files

class Forbidden403Test {
    private fun job(h: Int = 480) = DownloadJob("aaaaaaaaaaa", "https://www.youtube.com/watch?v=aaaaaaaaaaa", h, null, true)
    private val e403 = "ERROR: unable to download video data: HTTP Error 403: Forbidden"

    @Test fun classification() {
        assertEquals(FailureKind.FORBIDDEN, Failures.classify(e403))
        assertEquals(FailureKind.BOT, Failures.classify("ERROR: HTTP Error 429: Too Many Requests"))
        assertEquals(FailureKind.BOT, Failures.classify("ERROR: Sign in to confirm you're not a bot"))
        // a subtitle 403 is a subtitle problem, never a video 403 and never a bot block
        assertEquals(FailureKind.TRANSIENT, Failures.classify("ERROR: Unable to download video subtitles for 'en': HTTP Error 403: Forbidden"))
        assertEquals(FailureKind.PERMANENT, Failures.classify("ERROR: Video unavailable. HTTP Error 403"))
        assertEquals(FailureKind.TRANSIENT, Failures.classify("ERROR: something else"))
    }

    @Test fun mp4SelectorCarriesTheHeight() {
        assertEquals("bv*[height<=480][ext=mp4]+ba[ext=m4a]/b[height<=480][ext=mp4]/b[height<=480]", Mp4Fallback.selector(480))
        assertTrue(Mp4Fallback.selector(720).contains("height<=720"))
    }

    @Test fun a403OnVideoIsRetriedOnceAsMp4() {
        val tried = ArrayList<String?>(); val said = ArrayList<String?>()
        val r = Mp4Fallback.run(job(), { t, _ -> said.add(t) }) { sel -> tried.add(sel); if (sel == null) throw NodeFetchException(FailureKind.FORBIDDEN, e403) }
        assertEquals("mp4", r); assertEquals(listOf(null, Mp4Fallback.selector(480)), tried); assertEquals(listOf(Mp4Fallback.RETRY_TEXT), said)
    }

    @Test fun noRetryWhenTheFirstTryWorks() {
        val tried = ArrayList<String?>()
        assertNull(Mp4Fallback.run(job(), { _, _ -> }) { sel -> tried.add(sel) }); assertEquals(1, tried.size)
    }

    @Test fun botBlockAndOtherErrorsAreNotRetried() {
        for (k in listOf(FailureKind.BOT, FailureKind.TRANSIENT, FailureKind.PERMANENT)) {
            val tried = ArrayList<String?>()
            val ex = try { Mp4Fallback.run(job(), { _, _ -> }) { sel -> tried.add(sel); throw NodeFetchException(k, "x") }; null } catch (e: NodeFetchException) { e }
            assertEquals(k, ex!!.kind); assertEquals(1, tried.size)
        }
    }

    @Test fun aSecond403ReleasesWithATwiceMessageAndAnotherErrorKeepsItsKind() {
        val ex = try { Mp4Fallback.run(job(), { _, _ -> }) { throw NodeFetchException(FailureKind.FORBIDDEN, e403) }; null } catch (e: NodeFetchException) { e }
        assertEquals(FailureKind.FORBIDDEN, ex!!.kind); assertTrue(ex.message!!, ex.message!!.startsWith("403 twice"))
        val ex2 = try { Mp4Fallback.run(job(), { _, _ -> }) { sel -> throw NodeFetchException(if (sel == null) FailureKind.FORBIDDEN else FailureKind.BOT, "x") }; null } catch (e: NodeFetchException) { e }
        assertEquals(FailureKind.BOT, ex2!!.kind)
    }

    // ---- through the runner ----
    lateinit var api: FakeBlobs; lateinit var state: MemState; lateinit var work: File
    var now = 1_800_000_000_000L
    @Before fun up() { api = FakeBlobs(); state = MemState(); work = Files.createTempDirectory("node").toFile() }
    private fun runner(f: NodeFetcher) = NodeRunner(api, f, NodeConfig("n-a1b2c3", "n", "wifi+charging", "1.8.4", "android-arm64-v8a", 8, work), state,
        NodeEnv(clock = { now }, sleep = { }, gapMs = { 25_000 }, renewMs = 20), cap = 20)

    @Test fun twice403ReleasesToPendingWithoutAnAttemptAndTheNodeSkipsItFor6Hours() {
        api.addJob("aaaaaaaaaaa", 1, attempts = 1)
        val f = FakeFetcher { _, _, _ -> throw NodeFetchException(FailureKind.FORBIDDEN, Mp4Fallback.TWICE_TEXT + ": " + e403) }
        val s = runner(f).run()
        val o = api.jobJson("aaaaaaaaaaa")
        assertEquals("pending", api.job("aaaaaaaaaaa").tags["idx_state"]); assertEquals(1, o.getInt("attempts")); assertTrue(o.getString("lastError").contains("403"))
        assertEquals(1, s.released)
        assertEquals("released: 403 twice", state.entries.last().result)
        assertEquals(now + 6 * 3_600_000L, state.skips["aaaaaaaaaaa"]); assertEquals(1, f.jobs.size)
        // the same node leaves it alone inside the window ...
        val f2 = FakeFetcher(); now += 5 * 3_600_000L
        runner(f2).run(); assertEquals(0, f2.jobs.size)
        // ... and takes it again after 6 hours
        now += 2 * 3_600_000L
        val f3 = FakeFetcher(); runner(f3).run(); assertEquals(1, f3.jobs.size)
    }

    @Test fun anotherNodeIsNotBlockedByTheSkip() {
        api.addJob("aaaaaaaaaaa", 1)
        runner(FakeFetcher { _, _, _ -> throw NodeFetchException(FailureKind.FORBIDDEN, "403 twice") }).run()
        val other = MemState(); val f = FakeFetcher()
        NodeRunner(api, f, NodeConfig("other-000000", "other", "wifi+charging", "1.8.4", "android-arm64-v8a", 8, work), other,
            NodeEnv(clock = { now }, sleep = { }, gapMs = { 25_000 }, renewMs = 20), cap = 20).run()
        assertEquals(1, f.jobs.size)
    }

    @Test fun theMp4FallbackIsRecordedInDownloadedJson() {
        api.addJob("aaaaaaaaaaa", 1)
        val f = FakeFetcher().also { it.formatFallback = "mp4" }
        runner(f).run()
        assertEquals("mp4", api.summary("index/videos/aaaaaaaaaaa/download.zip").getString("formatFallback"))
        api.addJob("bbbbbbbbbbb", 2); runner(FakeFetcher()).run()
        assertFalse(api.summary("index/videos/bbbbbbbbbbb/download.zip").has("formatFallback"))
    }
    private fun JSONObject_(s: String) = org.json.JSONObject(s)
}
