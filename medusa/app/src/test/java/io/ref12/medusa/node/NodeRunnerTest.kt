package io.ref12.medusa.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.File
import java.nio.file.Files

class FakeBlobs : BlobApi {
    class B(var bytes: ByteArray, var tags: Map<String, String>, var lease: String? = null, var etag: Int = 1)
    val blobs = LinkedHashMap<String, B>()
    val log = java.util.Collections.synchronizedList(ArrayList<String>())
    var leaseSeq = 0
    var renewFails = false

    @Synchronized fun addJob(id: String, n: Int, attempts: Int = 0, extra: String = "") {
        blobs["index/jobs/download/" + id + ".json"] = B(("{\"v\":1,\"stage\":\"download\",\"jobId\":\"" + id + "\",\"url\":\"https://www.youtube.com/watch?v=" + id + "\",\"maxHeight\":480,\"format\":null,\"subs\":true,\"attempts\":" + attempts + extra + "}").toByteArray(),
            mapOf("idx_stage" to "download", "idx_state" to "pending", "idx_added" to "2026-11-13T19:00:0" + n + "Z"))
    }
    @Synchronized fun setControl(json: String) { blobs[ControlWatcher.CONTROL_PATH] = B(json.toByteArray(), emptyMap(), null, (blobs[ControlWatcher.CONTROL_PATH]?.etag ?: 0) + 1) }
    @Synchronized fun job(id: String) = blobs["index/jobs/download/" + id + ".json"]!!
    @Synchronized fun jobJson(id: String) = JSONObject(String(job(id).bytes))
    fun count(prefix: String) = synchronized(log) { log.count { it.startsWith(prefix) } }

    @Synchronized override fun findJobs(stage: String) = blobs.filter { it.key.startsWith("index/jobs/$stage/") && it.value.tags["idx_state"] == "pending" }
        .map { JobRef(it.key, it.value.tags) }.sortedBy { it.added }
    @Synchronized override fun get(path: String, ifNoneMatch: String?): GetResult {
        val b = blobs[path] ?: return GetResult(404, ByteArray(0), null)
        if (ifNoneMatch != null && ifNoneMatch == "e" + b.etag) return GetResult(304, ByteArray(0), ifNoneMatch)
        if (path == ControlWatcher.CONTROL_PATH) log.add("getcontrol")
        return GetResult(200, b.bytes, "e" + b.etag)
    }
    @Synchronized override fun getTags(path: String) = blobs[path]?.tags ?: emptyMap()
    @Synchronized override fun put(path: String, body: ByteArray, contentType: String, tags: Map<String, String>, leaseId: String?, ifAbsent: Boolean): Boolean {
        val b = blobs[path]
        if (ifAbsent && b != null) return false
        if (b?.lease != null && b.lease != leaseId) throw BlobException(412, "LeaseIdMissing", "leased")
        blobs[path] = B(body, tags, b?.lease, (b?.etag ?: 0) + 1); log.add("put:" + path); return true
    }
    @Synchronized override fun putBlocks(path: String, file: File, contentType: String, tags: Map<String, String>, cancelled: () -> Boolean, onProgress: (Long, Long) -> Unit) {
        if (cancelled()) throw BlobCancelled()
        blobs[path] = B(file.readBytes(), tags); log.add("put:" + path); onProgress(file.length(), file.length())
    }
    @Synchronized override fun acquireLease(path: String, seconds: Int): String? { val b = blobs[path] ?: return null; if (b.lease != null) return null; b.lease = "L" + (++leaseSeq); log.add("acquire:" + path); return b.lease }
    @Synchronized override fun renewLease(path: String, leaseId: String) {
        log.add("renew:" + path)
        if (renewFails || blobs[path]?.lease != leaseId) throw BlobException(409, "LeaseIdMismatch", "lost")
    }
    @Synchronized override fun releaseLease(path: String, leaseId: String) { if (blobs[path]?.lease == leaseId) blobs[path]!!.lease = null; log.add("release:" + path) }
    @Synchronized override fun setMetadata(path: String, leaseId: String?, meta: Map<String, String>) { log.add("meta:" + path) }
    @Synchronized override fun listNames(prefix: String, max: Int) = blobs.keys.filter { it.startsWith(prefix) }
}

class MemState : NodeState {
    var done = HashMap<String, Int>(); var backoff = 0L; val entries = ArrayList<JobLogEntry>(); var control = ""
    override fun doneToday(day: String) = done[day] ?: 0
    override fun addDone(day: String) { done[day] = doneToday(day) + 1 }
    override fun backoffUntil() = backoff
    override fun setBackoffUntil(t: Long) { backoff = t }
    override fun record(entry: JobLogEntry) { entries.add(entry) }
    override fun saveControl(text: String) { control = text }
}

class FakeFetcher(private val behavior: (DownloadJob, File, FakeFetcher) -> Unit = { _, _, _ -> }) : NodeFetcher {
    @Volatile var cancelled = false
    val jobs = ArrayList<DownloadJob>()
    override fun cancel() { cancelled = true }
    override fun download(job: DownloadJob, dir: File, progress: (String?, String?) -> Unit): DownloadedFiles {
        jobs.add(job); dir.mkdirs(); progress("download 10%", "Title " + job.jobId)
        behavior(job, dir, this)
        val v = File(dir, "video.mp4").apply { writeBytes(ByteArray(100)) }
        val info = File(dir, "video.info.json").apply { writeText("{}") }
        val sub = File(dir, "video.zh-Hans.vtt").apply { writeText("WEBVTT") }
        return DownloadedFiles(v, listOf(Triple("info.json", info, "application/json"), Triple("subs/zh-Hans.vtt", sub, "text/vtt")), 480, 61.5)
    }
}

class NodeRunnerTest {
    lateinit var api: FakeBlobs; lateinit var state: MemState; lateinit var work: File
    var now = 1_800_000_000_000L
    val sleeps = ArrayList<Long>()

    @Before fun up() { api = FakeBlobs(); state = MemState(); work = Files.createTempDirectory("node").toFile() }

    private fun runner(f: NodeFetcher, cap: Int = 20, renewMs: Long = 20) = NodeRunner(api, f,
        NodeConfig("lancecm1-a1b2c3", "lancecm1", "wifi+charging", "1.7.0", "android-arm64-v8a", 8, work), state,
        NodeEnv(clock = { now }, sleep = { sleeps.add(it) }, gapMs = { 25_000 }, renewMs = renewMs), cap = cap)

    @Test fun aClaimedJobIsDownloadedUploadedMarkedAndCleaned() {
        api.addJob("aaaaaaaaaaa", 1)
        val f = FakeFetcher()
        val s = runner(f).run()
        assertEquals(1, s.done); assertEquals("no work", s.endReason)
        assertEquals("480", f.jobs[0].maxHeight.toString())
        val puts = api.log.filter { it.startsWith("put:") }
        // outputs first, the marker last of them, then the process job, then the job itself
        val vid = "put:index/videos/aaaaaaaaaaa/"
        assertTrue(puts.indexOf(vid + "downloaded.json") > puts.indexOf(vid + "video.mp4"))
        assertTrue(puts.indexOf(vid + "downloaded.json") > puts.indexOf(vid + "subs/zh-Hans.vtt"))
        assertTrue(puts.indexOf("put:index/jobs/process/aaaaaaaaaaa.json") > puts.indexOf(vid + "downloaded.json"))
        assertTrue(puts.indexOf("put:index/jobs/download/aaaaaaaaaaa.json") > puts.indexOf("put:index/jobs/process/aaaaaaaaaaa.json"))
        val marker = JSONObject(String(api.blobs["index/videos/aaaaaaaaaaa/downloaded.json"]!!.bytes))
        assertEquals(100, marker.getInt("bytes")); assertEquals(480, marker.getInt("height")); assertEquals(61.5, marker.getDouble("durationSeconds"), 0.0); assertEquals("lancecm1-a1b2c3", marker.getString("nodeId"))
        val p = api.blobs["index/jobs/process/aaaaaaaaaaa.json"]!!
        assertEquals("pending", p.tags["idx_state"]); assertEquals("process", p.tags["idx_stage"])
        assertEquals(listOf("frames", "sscd", "compress"), JSONObject(String(p.bytes)).getJSONArray("steps").let { a -> (0 until a.length()).map { a.getString(it) } })
        val j = api.job("aaaaaaaaaaa"); val o = api.jobJson("aaaaaaaaaaa")
        assertEquals("done", j.tags["idx_state"]); assertEquals("download", j.tags["idx_stage"]); assertNotNull(j.tags["idx_added"])
        assertEquals("lancecm1-a1b2c3", o.getString("nodeId")); assertTrue(o.has("finishedAt")); assertTrue(o.has("elapsedSeconds"))
        assertNull(j.lease); assertEquals(1, api.count("release:"))
        assertFalse(File(work, "aaaaaaaaaaa").exists())
        assertEquals(1, state.doneToday(NodeRunner.day(now)))
    }

    @Test fun anExistingProcessJobIsLeftAlone() {
        api.addJob("aaaaaaaaaaa", 1)
        api.blobs["index/jobs/process/aaaaaaaaaaa.json"] = FakeBlobs.B("{\"keep\":1}".toByteArray(), mapOf("idx_state" to "done"))
        runner(FakeFetcher()).run()
        assertEquals("{\"keep\":1}", String(api.blobs["index/jobs/process/aaaaaaaaaaa.json"]!!.bytes))
    }

    @Test fun theOldestJobGoesFirstAndAGapSeparatesDownloads() {
        api.addJob("bbbbbbbbbbb", 2); api.addJob("aaaaaaaaaaa", 1)
        val f = FakeFetcher(); val s = runner(f).run()
        assertEquals(listOf("aaaaaaaaaaa", "bbbbbbbbbbb"), f.jobs.map { it.jobId }); assertEquals(2, s.done)
        assertEquals(listOf(25_000L), sleeps) // one gap, none after the last job
    }

    @Test fun aJobHeldByAnotherNodeIsSkipped() {
        api.addJob("aaaaaaaaaaa", 1); api.addJob("bbbbbbbbbbb", 2); api.job("aaaaaaaaaaa").lease = "OTHER"
        val f = FakeFetcher(); runner(f).run()
        assertEquals(listOf("bbbbbbbbbbb"), f.jobs.map { it.jobId }); assertEquals("OTHER", api.job("aaaaaaaaaaa").lease)
    }

    @Test fun aPermanentFailureFailsTheJobAtOnce() {
        api.addJob("aaaaaaaaaaa", 1)
        val s = runner(FakeFetcher { _, _, _ -> throw NodeFetchException(FailureKind.PERMANENT, "Video unavailable") }).run()
        val o = api.jobJson("aaaaaaaaaaa")
        assertEquals("failed", api.job("aaaaaaaaaaa").tags["idx_state"]); assertTrue(o.getBoolean("permanent")); assertEquals("Video unavailable", o.getString("lastError"))
        assertNull(api.job("aaaaaaaaaaa").lease); assertEquals(1, s.failed); assertNull(api.blobs["index/videos/aaaaaaaaaaa/downloaded.json"])
        assertNull(api.blobs["index/jobs/process/aaaaaaaaaaa.json"]); assertEquals(0, state.doneToday(NodeRunner.day(now)))
    }

    @Test fun aTransientFailureCountsAnAttemptAndLetsGoAtOnce() {
        api.addJob("aaaaaaaaaaa", 1)
        runner(FakeFetcher { _, _, _ -> throw NodeFetchException(FailureKind.TRANSIENT, "HTTP Error 503") }).run()
        // it is pending again, so the same run takes it again until it has failed 3 times
        val o = api.jobJson("aaaaaaaaaaa")
        assertEquals(3, o.getInt("attempts")); assertEquals("failed", api.job("aaaaaaaaaaa").tags["idx_state"]); assertFalse(o.has("permanent"))
        assertNull(api.job("aaaaaaaaaaa").lease); assertEquals(3, api.count("release:"))
    }

    @Test fun aTransientFailureBelowThreeStaysPending() {
        api.addJob("aaaaaaaaaaa", 1, attempts = 0)
        var n = 0
        runner(FakeFetcher { _, _, _ -> if (n++ == 0) throw NodeFetchException(FailureKind.TRANSIENT, "disk full") }).run()
        // the second pass succeeds, but the first rewrote attempts=1 with pending tags
        assertEquals(1, api.jobJson("aaaaaaaaaaa").getInt("attempts"))
        assertEquals("done", api.job("aaaaaaaaaaa").tags["idx_state"])
        assertEquals(1, state.entries.count { it.result == "will retry" })
    }

    @Test fun anUploadErrorIsTransient() {
        api.addJob("aaaaaaaaaaa", 1)
        val broken = object : BlobApi by api {
            override fun putBlocks(path: String, file: File, contentType: String, tags: Map<String, String>, cancelled: () -> Boolean, onProgress: (Long, Long) -> Unit) { throw BlobException(500, null, "boom") }
        }
        val r = NodeRunner(broken, FakeFetcher(), NodeConfig("n-1", "n", "wifi", "1.7.0", "android", 4, work), state, NodeEnv(clock = { now }, sleep = {}, gapMs = { 1 }, renewMs = 20), cap = 20)
        r.run()
        assertEquals(3, api.jobJson("aaaaaaaaaaa").getInt("attempts")); assertNull(api.blobs["index/videos/aaaaaaaaaaa/downloaded.json"])
    }

    @Test fun aBotBlockReleasesWithoutAnAttemptAndBacksOffAnHour() {
        api.addJob("aaaaaaaaaaa", 1); api.addJob("bbbbbbbbbbb", 2)
        val f = FakeFetcher { _, _, _ -> throw NodeFetchException(FailureKind.BOT, "Sign in to confirm you're not a bot") }
        val s = runner(f).run()
        assertEquals(1, f.jobs.size); assertEquals(0, api.jobJson("aaaaaaaaaaa").getInt("attempts")); assertEquals("pending", api.job("aaaaaaaaaaa").tags["idx_state"])
        assertNull(api.job("aaaaaaaaaaa").lease); assertEquals(now + 3_600_000L, state.backoff); assertTrue(s.endReason.contains("bot"))
        // a new run during the back-off claims nothing; after it, work resumes
        val f2 = FakeFetcher(); val s2 = runner(f2).run()
        assertEquals(0, f2.jobs.size); assertEquals("backing off", s2.endReason)
        now += 3_600_001L
        runner(f2).run(); assertEquals(2, f2.jobs.size)
    }

    @Test fun theDailyCapStopsClaiming() {
        for (i in 1..4) api.addJob("job" + i + "xxxxxxx", i)
        val f = FakeFetcher(); val s = runner(f, cap = 2).run()
        assertEquals(2, f.jobs.size); assertEquals("daily cap reached", s.endReason)
        now += 24 * 3_600_000L // the next day
        runner(f, cap = 2).run(); assertEquals(4, f.jobs.size)
    }

    @Test fun renewalHappensWhileWorkingAndWritesProgressMetadata() {
        api.addJob("aaaaaaaaaaa", 1)
        runner(FakeFetcher { _, _, _ -> Thread.sleep(120) }).run()
        assertTrue(api.count("renew:") >= 2); assertTrue(api.count("meta:") >= 2)
    }

    @Test fun aLostLeaseAbandonsTheJobWithoutAnAttempt() {
        api.addJob("aaaaaaaaaaa", 1); api.renewFails = true
        val f = FakeFetcher { _, _, fe -> val t = System.currentTimeMillis(); while (!fe.cancelled && System.currentTimeMillis() - t < 3000) Thread.sleep(5); if (fe.cancelled) throw NodeFetchException(FailureKind.TRANSIENT, "cancelled") }
        runner(f).run()
        assertEquals(0, api.jobJson("aaaaaaaaaaa").getInt("attempts")); assertNull(api.blobs["index/videos/aaaaaaaaaaa/downloaded.json"])
    }

    // ---- control ----

    @Test fun stopBeforeClaimingClaimsNothing() {
        api.addJob("aaaaaaaaaaa", 1); api.setControl("{\"v\":1,\"mode\":\"stop\"}")
        val f = FakeFetcher(); val s = runner(f).run()
        assertEquals(0, f.jobs.size); assertEquals(0, api.count("acquire:")); assertEquals("stopped", s.finalState)
        assertEquals("stopped", JSONObject(String(api.blobs["index/nodes/lancecm1-a1b2c3.json"]!!.bytes)).getString("state"))
    }

    @Test fun aStageOverrideIsStricterThanRun() {
        api.addJob("aaaaaaaaaaa", 1); api.setControl("{\"v\":1,\"mode\":\"run\",\"stages\":{\"download\":\"stop\",\"process\":\"run\"}}")
        val f = FakeFetcher(); runner(f).run(); assertEquals(0, f.jobs.size)
        api.setControl("{\"v\":1,\"mode\":\"drain\",\"stages\":{\"download\":\"run\"}}")
        runner(f).run(); assertEquals(0, f.jobs.size)
        api.setControl("{\"v\":1,\"mode\":\"run\",\"stages\":{\"process\":\"stop\"}}")
        runner(f).run(); assertEquals(1, f.jobs.size)
    }

    @Test fun stopDuringADownloadCancelsItAndReleasesWithoutAnAttempt() {
        api.addJob("aaaaaaaaaaa", 1); api.addJob("bbbbbbbbbbb", 2)
        val f = FakeFetcher { _, dir, fe ->
            api.setControl("{\"v\":1,\"mode\":\"stop\",\"message\":\"maintenance\"}")
            val t = System.currentTimeMillis(); while (!fe.cancelled && System.currentTimeMillis() - t < 5000) Thread.sleep(5)
            File(dir, "partial.part").writeText("x")
            if (fe.cancelled) throw NodeFetchException(FailureKind.TRANSIENT, "cancelled")
        }
        val s = runner(f).run()
        assertTrue(f.cancelled); assertEquals(1, f.jobs.size)
        assertEquals(0, api.jobJson("aaaaaaaaaaa").getInt("attempts")); assertEquals("pending", api.job("aaaaaaaaaaa").tags["idx_state"])
        assertNull(api.job("aaaaaaaaaaa").lease); assertEquals(1, s.released); assertEquals("stopped", s.finalState)
        assertFalse(File(work, "aaaaaaaaaaa").exists()); assertEquals(0, api.count("put:index/videos"))
    }

    @Test fun drainFinishesTheClaimedJobAndClaimsNoMore() {
        api.addJob("aaaaaaaaaaa", 1); api.addJob("bbbbbbbbbbb", 2)
        val f = FakeFetcher { _, _, _ -> api.setControl("{\"v\":1,\"mode\":\"drain\"}"); Thread.sleep(80) }
        val s = runner(f).run()
        assertEquals(1, f.jobs.size); assertEquals(1, s.done); assertEquals("done", api.job("aaaaaaaaaaa").tags["idx_state"])
        assertEquals("pending", api.job("bbbbbbbbbbb").tags["idx_state"]); assertEquals("control: drain", s.endReason)
        assertFalse(f.cancelled)
    }

    @Test fun runAgainResumesWork() {
        api.addJob("aaaaaaaaaaa", 1); api.setControl("{\"v\":1,\"mode\":\"stop\"}")
        val f = FakeFetcher(); runner(f).run(); assertEquals(0, f.jobs.size)
        api.setControl("{\"v\":1,\"mode\":\"run\"}"); runner(f).run(); assertEquals(1, f.jobs.size)
    }

    @Test fun controlIsReadWithAConditionalGetAndMissingMeansRun() {
        val c = ControlWatcher(api)
        assertEquals(Level.RUN, c.refresh().effective("download"))
        api.setControl("{\"v\":1,\"mode\":\"drain\",\"message\":\"hi\"}")
        assertEquals(Level.DRAIN, c.refresh().effective("download")); c.refresh(); c.refresh()
        assertEquals(1, api.count("getcontrol")) // later reads are 304s
        assertEquals(Level.DRAIN, c.current.effective("download")); assertEquals("hi", c.current.message)
    }

    @Test fun theSystemStoppingTheWorkReleasesTheLease() {
        api.addJob("aaaaaaaaaaa", 1)
        lateinit var r: NodeRunner
        val f = FakeFetcher { _, _, fe -> r.abort(); if (fe.cancelled) throw NodeFetchException(FailureKind.TRANSIENT, "cancelled") }
        r = runner(f); val s = r.run()
        assertNull(api.job("aaaaaaaaaaa").lease); assertEquals(0, api.jobJson("aaaaaaaaaaa").getInt("attempts")); assertEquals(1, s.released)
    }

    // ---- registry ----

    @Test fun theRegistryBlobDescribesTheNode() {
        api.addJob("aaaaaaaaaaa", 1)
        var during: JSONObject? = null
        runner(FakeFetcher { _, _, _ -> during = JSONObject(String(api.blobs["index/nodes/lancecm1-a1b2c3.json"]!!.bytes)) }).run()
        val o = JSONObject(String(api.blobs["index/nodes/lancecm1-a1b2c3.json"]!!.bytes))
        assertEquals("node", api.blobs["index/nodes/lancecm1-a1b2c3.json"]!!.tags["idx_kind"])
        assertEquals(1, o.getInt("v")); assertEquals("lancecm1-a1b2c3", o.getString("nodeId")); assertEquals("lancecm1", o.getString("name")); assertEquals("medusa", o.getString("kind"))
        assertEquals("1.7.0", o.getString("version")); assertEquals("android-arm64-v8a", o.getString("platform")); assertEquals("download", o.getJSONArray("stages").getString(0))
        assertEquals(1, o.getJSONObject("jobs").getInt("download")); assertEquals(8, o.getInt("threads")); assertEquals("wifi+charging", o.getString("conditions"))
        assertEquals("run", o.getString("control")); assertEquals("idle", o.getString("state")); assertEquals(0, o.getJSONArray("running").length())
        assertEquals("2027-01-15T08:00:00Z", NodeRunner.iso(1_800_000_000_000L)); assertEquals(NodeRunner.iso(now), o.getString("lastSeen")); assertTrue(o.has("startedAt"))
        assertEquals("working", during!!.getString("state"))
        val run = during!!.getJSONArray("running").getJSONObject(0)
        assertEquals("aaaaaaaaaaa", run.getString("jobId")); assertEquals("download", run.getString("stage")); assertEquals("download", run.getString("step"))
    }

    // ---- small things ----

    @Test fun failuresAreClassified() {
        assertEquals(FailureKind.PERMANENT, Failures.classify("ERROR: [youtube] x: Video unavailable"))
        assertEquals(FailureKind.PERMANENT, Failures.classify("ERROR: Private video. Sign in if you've been granted access"))
        assertEquals(FailureKind.PERMANENT, Failures.classify("Join this channel to get access to members-only content"))
        assertEquals(FailureKind.PERMANENT, Failures.classify("Sign in to confirm your age. This video may be inappropriate"))
        assertEquals(FailureKind.PERMANENT, Failures.classify("The uploader has not made this video available in your country"))
        assertEquals(FailureKind.PERMANENT, Failures.classify("ERROR: Unsupported URL: https://example.com"))
        assertEquals(FailureKind.BOT, Failures.classify("ERROR: Sign in to confirm you're not a bot"))
        assertEquals(FailureKind.BOT, Failures.classify("HTTP Error 429: Too Many Requests"))
        assertEquals(FailureKind.TRANSIENT, Failures.classify("Unable to download webpage: <urlopen error timed out>"))
        assertEquals(FailureKind.TRANSIENT, Failures.classify("HTTP Error 503: Service Unavailable"))
        assertEquals(FailureKind.TRANSIENT, Failures.classify("No space left on device"))
    }

    @Test fun theFormatSelector() {
        val d = DownloadJob.parse("{\"jobId\":\"a\",\"url\":\"u\",\"maxHeight\":null,\"format\":null}")
        assertEquals(480, d.maxHeight); assertEquals("bv*[height<=480][ext=mp4]+ba[ext=m4a]/b[height<=480]/b", d.selector())
        assertEquals("bv*[height<=720][ext=mp4]+ba[ext=m4a]/b[height<=720]/b", DownloadJob.parse("{\"jobId\":\"a\",\"url\":\"u\",\"maxHeight\":720}").selector())
        assertEquals("best", DownloadJob.parse("{\"jobId\":\"a\",\"url\":\"u\",\"maxHeight\":720,\"format\":\"best\"}").selector())
    }
}
