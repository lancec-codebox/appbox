package io.ref12.medusa.node

import io.ref12.medusa.VideoRequest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.File
import java.nio.file.Files

class Medusa186Test {
    private fun job(h: Int = 480) = DownloadJob("aaaaaaaaaaa", "https://www.youtube.com/watch?v=aaaaaaaaaaa", h, null, true)
    private val e403 = "ERROR: unable to download video data: HTTP Error 403: Forbidden"
    private fun forbidden() = NodeFetchException(FailureKind.FORBIDDEN, e403)

    class Prefs(override var webmFirst: Boolean = true, override var profile: DownloadProfile? = null) : DownloadPrefs

    /** A yt-dlp stand-in: [onVideo] and [onProbe] decide each call; every call is recorded. */
    class FakeIo(val version: String = "2026.11.01", val onVideo: (Variant, Attempt) -> Unit = { _, _ -> }, val onProbe: (Variant) -> TryResult = { TryResult(TryOutcome.PASS) }) : VideoIo {
        val videos = ArrayList<Pair<Variant, Attempt>>(); val probes = ArrayList<Variant>()
        override fun video(job: DownloadJob, dir: File, v: Variant, a: Attempt, onPct: (Int) -> Unit) { videos.add(v to a); onVideo(v, a) }
        override fun probe(job: DownloadJob, dir: File, v: Variant, a: Attempt): TryResult { probes.add(v); return onProbe(v) }
        override fun ytdlpVersion() = version
    }

    private fun downloader(io: VideoIo, prefs: DownloadPrefs, sleeps: MutableList<Long> = ArrayList()) = NodeDownloader(io, prefs, { sleeps.add(it) }, { 25_000 }, { "2026-11-17T12:00:00Z" })
    private fun tmp() = Files.createTempDirectory("m186").toFile()

    // ---- the library's request builder and selector ----

    @Test fun theNodeUsesTheLibrarysSelectorAndOptions() {
        val opts = VideoRequest.options("/w/video.%(ext)s", 480).map { it.toString() }
        assertTrue(opts.contains("-f " + VideoRequest.selector(480)))
        assertEquals(VideoRequest.selector(480), Mp4Fallback.selector(480))
        assertEquals("bv*[height<=480][ext=mp4]+ba[ext=m4a]/b[height<=480][ext=mp4]/b[height<=480]", VideoRequest.selector(480))
        // nothing extra rides on the video request
        assertTrue(opts.none { it.contains("write-info-json") || it.contains("write-thumbnail") || it.contains("convert-thumbnails") })
        assertEquals(listOf(360, 480, 720), VideoRequest.HEIGHTS)
        assertEquals(720, VideoRequest.nextHeightAbove(480)); assertNull(VideoRequest.nextHeightAbove(720))
    }

    @Test fun withWebmFirstOffTheNodeGoesStraightToTheMp4SelectorAndTheDefaultIsOn() {
        assertEquals(listOf(true to 480, false to 480, false to 720), NodeAttempts.plan(true, 480).map { it.webm to it.height })
        assertEquals(listOf(false to 480, false to 720), NodeAttempts.plan(false, 480).map { it.webm to it.height })
        assertEquals(listOf(false to 720), NodeAttempts.plan(false, 720).map { it.webm to it.height })
        val io = FakeIo(); val p = Prefs(webmFirst = false)
        downloader(io, p).download(job(), tmp(), {})
        assertEquals(listOf(false), io.videos.map { it.second.webm })
        val io2 = FakeIo(); downloader(io2, Prefs()).download(job(), tmp(), {}) // default: on
        assertEquals(listOf(true), io2.videos.map { it.second.webm })
        assertTrue(Prefs().webmFirst)
    }

    @Test fun infoJsonAndThumbnailAreNotOnTheVideoRequestOfANormalDownload() {
        val io = FakeIo(); downloader(io, Prefs(webmFirst = false)).download(job(), tmp(), {})
        val v = io.videos.single().first
        assertFalse(v.infoWithVideo); assertFalse(v.thumbWithVideo)
    }

    // ---- the 403 retry ----

    @Test fun a403IsRetriedOnceAtTheNextHeightAndThenReleased() {
        val io = FakeIo(onVideo = { _, a -> if (a.height == 480) throw forbidden() })
        val r = downloader(io, Prefs(webmFirst = false, profile = DownloadProfile(false, false, false, false, null, "2026.11.01", "t", emptyList()))).download(job(), tmp(), {})
        assertEquals(listOf(480, 720), io.videos.map { it.second.height }); assertEquals("mp4@720", r.formatFallback)
        // a 403 at every height gives up with the "twice" text, in the 403 kind
        val ex = try { NodeAttempts.run(false, 480, {}) { throw forbidden() }; null } catch (e: NodeFetchException) { e }
        assertEquals(FailureKind.FORBIDDEN, ex!!.kind); assertTrue(ex.message!!.startsWith(Mp4Fallback.TWICE_TEXT))
        // other kinds are not retried
        for (k in listOf(FailureKind.BOT, FailureKind.TRANSIENT, FailureKind.PERMANENT)) {
            var n = 0
            try { NodeAttempts.run(true, 480, {}) { n++; throw NodeFetchException(k, "x") } } catch (e: NodeFetchException) { assertEquals(k, e.kind) }
            assertEquals(1, n)
        }
    }

    // ---- the failure trace ----

    private val RAW = (1..30).joinToString("\n") { "line $it https://rr1.googlevideo.com/videoplayback?expire=1&sig=SECRET$it&ip=1.2.3.4 end" } +
        "\n[youtube] 7q84opi7IWA: Downloading ios player API JSON\n[info] 7q84opi7IWA: Downloading 1 format(s): 399+251\n" + e403

    @Test fun theTraceHasVersionFormatsClientAndTheLast20LinesWithoutQueryStrings() {
        val t = FailureTrace.of(RAW, "2026.11.01")
        assertEquals("2026.11.01", t.ytdlp); assertEquals(listOf("399", "251"), t.formats); assertEquals("ios", t.client)
        assertEquals(20, t.tail.size); assertEquals(e403, t.tail.last())
        val all = t.tail.joinToString("\n") + t.text()
        assertFalse(all, all.contains("SECRET")); assertFalse(all.contains("expire=")); assertFalse(all.contains("?"))
        assertTrue(t.tail.any { it.contains("https://rr1.googlevideo.com/videoplayback") })
        val o = t.putInto(JSONObject())
        for (k in listOf("ytdlp", "formats", "client", "tail")) assertTrue(k, o.has(k))
    }

    lateinit var api: FakeBlobs; lateinit var state: MemState; lateinit var work: File
    var now = 1_800_000_000_000L
    @Before fun up() { api = FakeBlobs(); state = MemState(); work = Files.createTempDirectory("node").toFile() }
    private fun runner(f: NodeFetcher, st: NodeState = state) = NodeRunner(api, f, NodeConfig("phone-a1b2c3", "phone", "wifi+charging", "1.8.6", "android-arm64-v8a", 8, work), st,
        NodeEnv(clock = { now }, sleep = {}, gapMs = { 0 }, renewMs = 20, netFetch = { u -> if (u == NetworkProbe.V4_URL) "ip=203.0.113.7\nloc=US\ncolo=SEA\n" else null }, deviceNet = { DeviceNet("wifi", false) }))

    @Test fun everyFailureKindWritesNetworkYtdlpFormatsClientAndTailIntoTheJob() {
        for ((i, kind) in listOf(FailureKind.FORBIDDEN, FailureKind.BOT, FailureKind.TRANSIENT, FailureKind.PERMANENT).withIndex()) {
            api = FakeBlobs(); state = MemState()
            val id = "k" + "abcdefghij"[i].toString().repeat(10)
            api.addJob(id, 1)
            runner(FakeFetcher { _, _, _ -> throw NodeFetchException(kind, "failed: " + kind, FailureTrace.of(RAW, "2026.11.01")) }).run()
            val j = api.jobJson(id)
            assertTrue(kind.name, j.has("lastError"))
            assertEquals(kind.name, "203.0.113.7", j.getJSONObject("network").getString("ipv4"))
            assertEquals("2026.11.01", j.getString("ytdlp")); assertEquals(2, j.getJSONArray("formats").length()); assertEquals("ios", j.getString("client"))
            val tail = j.getJSONArray("tail"); assertEquals(20, tail.length())
            assertFalse(tail.toString().contains("SECRET")); assertFalse(tail.toString().contains("expire="))
        }
    }

    // ---- Test download ----

    @Test fun testDownloadWritesNothingToTheStoreAndItsSwitchesChangeTheRequest() {
        val dir = tmp(); val p = Prefs(webmFirst = true)
        val io = FakeIo(onProbe = { TryResult(TryOutcome.PASS, listOf("399", "251"), "ios", 6_000_000) })
        val r = TestDownload.run(io, job(), dir, TestDownload.Switches(webmFirst = false, infoWithVideo = true, thumbWithVideo = true))
        assertTrue(r.passed); assertEquals(listOf("399", "251"), r.formats); assertEquals("ios", r.client); assertEquals("2026.11.01", r.ytdlp)
        val v = io.probes.single()
        assertFalse(v.webmFirst); assertTrue(v.infoWithVideo); assertTrue(v.thumbWithVideo)
        assertTrue(r.text().contains("PASS")); assertTrue(r.variant, r.variant.contains("thumbnail") && r.variant.contains("info JSON") && r.variant.contains("WebM first off"))
        // nothing is left on disk, no profile is saved, and the test has no way to reach the store at all
        assertFalse(dir.exists()); assertNull(p.profile); assertTrue(api.log.isEmpty())
        // WebM first on goes WebM first
        val io2 = FakeIo(); TestDownload.run(io2, job(), tmp(), TestDownload.Switches(true, false, false)); assertTrue(io2.probes.single().webmFirst)
        // a failure shows the same fields
        val io3 = FakeIo(onProbe = { TryResult(TryOutcome.FORBIDDEN, listOf("18"), "web", 0, e403) })
        val f = TestDownload.run(io3, job(), tmp(), TestDownload.Switches(false, false, false))
        assertFalse(f.passed); assertTrue(f.text().contains("FAIL")); assertEquals(listOf("18"), f.formats)
    }

    // ---- self-diagnosis ----

    @Test fun diagnosisVariantsChangeOneThingAtATimeAndAreAtMostEight() {
        val base = Variant(true, false, false, 480)
        val vs = Diagnosis.variants(base)
        assertEquals(base.copy(thumbWithVideo = true), vs[0]); assertEquals(base.copy(infoWithVideo = true), vs[1])
        assertEquals(base.copy(webmFirst = false), vs[2]); assertEquals(base.copy(height = 720), vs[3])
        assertTrue(vs.size <= Diagnosis.MAX_TRIES); assertEquals(8, Diagnosis.MAX_TRIES); assertEquals(vs.size, vs.distinct().size); assertFalse(vs.contains(base))
        assertTrue(Diagnosis.variants(Variant(true, false, false, 720)).none { it.height != 720 })
    }

    @Test fun aTryPassesAt5MbAndTheFirstPassingVariantIsKept() {
        assertEquals(5L * 1024 * 1024, Diagnosis.PASS_BYTES)
        val sleeps = ArrayList<Long>()
        val base = Variant(true, false, false, 480)
        val r = Diagnosis.run(base, "2026.11.01", "t", { sleeps.add(it) }, { 25_000 }) { v -> if (!v.webmFirst) TryResult(TryOutcome.PASS, listOf("18"), "ios") else TryResult(TryOutcome.FORBIDDEN) }
        assertTrue(r.profile.found); assertFalse(r.profile.webmFirst); assertEquals(3, r.profile.tries.size) // thumb, info, webm off
        assertEquals("pass", r.profile.tries.last().result); assertEquals(listOf("18"), r.profile.tries.last().formats); assertEquals(3, sleeps.size)
    }

    @Test fun diagnosisStopsOnABotBlockOrA429AndNeverExceedsEightTries() {
        var n = 0
        val bot = Diagnosis.run(Variant(true, false, false, 480), "v", "t", {}, { 0 }) { n++; if (n == 2) TryResult(TryOutcome.BOT, error = "HTTP Error 429") else TryResult(TryOutcome.FORBIDDEN) }
        assertTrue(bot.botBlocked); assertEquals(2, bot.profile.tries.size); assertFalse(bot.profile.found)
        n = 0
        val none = Diagnosis.run(Variant(true, false, false, 480), "v", "t", {}, { 0 }) { n++; TryResult(TryOutcome.FORBIDDEN) }
        assertFalse(none.profile.found); assertFalse(none.botBlocked); assertEquals(8, n); assertEquals(8, none.profile.tries.size)
    }

    @Test fun aVideo403RunsTheDiagnosisAndSavesTheProfile() {
        val prefs = Prefs(webmFirst = true)
        val io = FakeIo(onVideo = { v, _ -> if (v.webmFirst) throw forbidden() }, onProbe = { v -> if (!v.webmFirst) TryResult(TryOutcome.PASS, listOf("18"), "web") else TryResult(TryOutcome.FORBIDDEN) })
        val r = downloader(io, prefs).download(job(), tmp(), {})
        val p = prefs.profile!!
        assertTrue(p.found); assertFalse(p.webmFirst); assertEquals("2026.11.01", p.ytdlp); assertTrue(p.tries.isNotEmpty()); assertEquals(r.diagnosed, p)
        assertFalse(io.videos.last().first.webmFirst)
        // the next download starts from the profile, with no diagnosis
        val io2 = FakeIo(); downloader(io2, prefs).download(job(), tmp(), {}); assertEquals(listOf(false), io2.videos.map { it.second.webm }); assertTrue(io2.probes.isEmpty())
        // a bot block during diagnosis ends in a bot failure and saves what it tried
        val prefs3 = Prefs(); val io3 = FakeIo(onVideo = { _, _ -> throw forbidden() }, onProbe = { TryResult(TryOutcome.BOT, error = "429") })
        val ex = try { downloader(io3, prefs3).download(job(), tmp(), {}); null } catch (e: NodeFetchException) { e }
        assertEquals(FailureKind.BOT, ex!!.kind); assertEquals(1, prefs3.profile!!.tries.size)
        // a bot block on the video itself is not diagnosed at all
        val io4 = FakeIo(onVideo = { _, _ -> throw NodeFetchException(FailureKind.BOT, "429") })
        try { downloader(io4, Prefs()).download(job(), tmp(), {}) } catch (e: NodeFetchException) {}
        assertTrue(io4.probes.isEmpty())
    }

    @Test fun theProfileRoundTripsAndIsWrittenToTheNodeRecordAsDownloadProfile() {
        val p = DownloadProfile(true, false, false, true, 720, "2026.11.01", "2026-11-17T12:00:00Z", listOf(TryRecord("thumbnail with video on", "403", listOf("399"), "ios"), TryRecord("WebM first off", "pass", listOf("18"), "web")))
        val back = DownloadProfile.parse(p.toJson().toString())!!
        assertEquals(p.toJson().toString(), back.toJson().toString()); assertEquals(720, back.maxHeight); assertEquals(2, back.tries.size)
        class ProfState : NodeState by MemState() { override fun downloadProfile() = p }
        runner(FakeFetcher(), ProfState()).run()
        val o = JSONObject(api.blobText("index/nodes/phone-a1b2c3.json")).getJSONObject("downloadProfile")
        assertEquals(2, o.getJSONArray("tries").length()); assertEquals("2026.11.01", o.getString("ytdlp")); assertEquals(720, o.getInt("maxHeight"))
        // no profile, no field
        api = FakeBlobs(); runner(FakeFetcher()).run(); assertFalse(JSONObject(api.blobText("index/nodes/phone-a1b2c3.json")).has("downloadProfile"))
    }

    // ---- storing library downloads in the index ----

    private fun files(h: Int? = 720): StoreFiles {
        val d = tmp(); val v = File(d, "video.mp4").apply { writeBytes(ByteArray(100)) }
        val info = File(d, "info.json").apply { writeText(SAMPLE_INFO) }; val sub = File(d, "en.vtt").apply { writeText("WEBVTT") }
        return StoreFiles("aaaaaaaaaaa", "https://www.youtube.com/watch?v=aaaaaaaaaaa", v,
            listOf(Triple("info.json", info, "application/json"), Triple("subs/en.vtt", sub, "text/vtt")), listOf("en"), h, 61.5, NodeInfo.parse(SAMPLE_INFO))
    }
    private val V = "index/videos/aaaaaaaaaaa/"

    private val Z = V + "download.zip"

    @Test fun videoGoesFirstAndTheCreateOnlyDownloadZipLastWithTheSummary() {
        val res = IndexStore.store(api, files(), StoreMode.STORE_ONLY, "phone-a1b2c3", 0, "2026-11-17T12:00:00Z", LinkedHashSet())
        assertEquals(StoreResult.STORED, res)
        val puts = api.log.filter { it.startsWith("put:") }
        assertEquals("put:" + V + "video.mp4", puts.first()); assertEquals("put:" + Z, puts[1])   // the zip is the completion signal: nothing of the drama comes after the video but it
        assertFalse(api.blobs.containsKey(V + "downloaded.json")); assertFalse(api.blobs.containsKey(V + "info.json"))
        val m = api.summary(Z)
        assertEquals("medusa-library", m.getString("origin")); assertEquals(720, m.getInt("height")); assertEquals(100, m.getInt("bytes"))
        assertEquals(100, api.blobs[V + "video.mp4"]!!.bytes.size)
        assertEquals(listOf("info.json", "subs/en.vtt"), api.members(Z).keys.toList())
        val md = api.meta[Z]!!
        assertEquals(setOf("idx_v", "idx_sha256", "idx_inputs", "idx_node", "idx_at", "idx_summary"), md.keys)
        assertEquals("1", md["idx_v"]); assertEquals("phone-a1b2c3", md["idx_node"]); assertTrue(md.all { e -> e.value.all { it.code < 128 } }); assertTrue(md.entries.sumOf { it.key.length + it.value.length } < 8000)
    }

    @Test fun theZipIsAStandardStoredZipWithRealCrcs() {
        IndexStore.store(api, files(), StoreMode.STORE_ONLY, "n", 0, "t", LinkedHashSet())
        java.util.zip.ZipInputStream(api.blobs[Z]!!.bytes.inputStream()).use { z -> while (true) { val e = z.nextEntry ?: break; z.readBytes() } }   // CRC-32 is checked on read
        val zf = File.createTempFile("dlzip", ".zip"); zf.writeBytes(api.blobs[Z]!!.bytes)
        java.util.zip.ZipFile(zf).use { z -> for (e in z.entries()) { assertEquals(java.util.zip.ZipEntry.STORED, e.method); assertTrue(e.crc != 0L || e.size == 0L) } }
        assertEquals(api.meta[Z]!!["idx_sha256"], StageZip.sha256(zf))
    }

    @Test fun anExistingDownloadZipMeansSkipAndNothingIsUploadedOrReplaced() {
        api.blobs[Z] = FakeBlobs.B("old".toByteArray(), emptyMap())
        val res = IndexStore.store(api, files(), StoreMode.STORE_AND_PROCESS, "phone-a1b2c3", 0, "t", LinkedHashSet())
        assertEquals(StoreResult.ALREADY, res)
        assertTrue(api.log.isEmpty()); assertEquals("old", api.blobText(Z)); assertEquals(1, api.blobs.size)
    }

    @Test fun aZipThatAppearsDuringTheUploadWinsAndIsNotReplaced() {
        val racing = object : BlobApi by api {
            override fun putBlocks(path: String, file: File, contentType: String, tags: Map<String, String>, cancelled: () -> Boolean, onProgress: (Long, Long) -> Unit) {
                api.putBlocks(path, file, contentType, tags, cancelled, onProgress); api.blobs[Z] = FakeBlobs.B("theirs".toByteArray(), emptyMap())   // another writer finished first
            }
        }
        assertEquals(StoreResult.ALREADY, IndexStore.store(racing, files(), StoreMode.STORE_AND_PROCESS, "n", 0, "t", LinkedHashSet()))
        assertEquals("theirs", api.blobText(Z)); assertFalse(api.blobs.keys.any { it.startsWith("index/jobs/") })
    }

    @Test fun aHalfWrittenDramaWithAVideoButNoZipIsNotComplete() {
        val failing = object : BlobApi by api { override fun putZip(path: String, file: File, metadata: Map<String, String>, tags: Map<String, String>, ifAbsent: Boolean, cancelled: () -> Boolean): Boolean = throw BlobException(500, "x", "boom") }
        assertThrows(BlobException::class.java) { IndexStore.store(failing, files(), StoreMode.STORE_ONLY, "n", 0, "t", LinkedHashSet()) }
        assertTrue(api.blobs.containsKey(V + "video.mp4")); assertFalse(api.blobs.containsKey(Z)); assertFalse(api.blobs.keys.any { it.startsWith("index/jobs/") })
    }

    @Test fun aResumedStoreContinuesFromWhatIsDone() {
        val done = LinkedHashSet(listOf("video.mp4"))
        IndexStore.store(api, files(), StoreMode.STORE_ONLY, "n", 0, "t", done)
        val puts = api.log.filter { it.startsWith("put:") }
        assertFalse(puts.contains("put:" + V + "video.mp4")); assertTrue(puts.contains("put:" + Z))
    }

    @Test fun storeOnlyCreatesTheDownloadJobAsDoneAndNoStepJobs() {
        IndexStore.store(api, files(), StoreMode.STORE_ONLY, "phone-a1b2c3", 0, "2026-11-17T12:00:00Z", LinkedHashSet())
        val j = api.blobs["index/jobs/download/aaaaaaaaaaa.json"]!!
        assertEquals("done", j.tags["idx_state"]); assertEquals("medusa-library", JSONObject(String(j.bytes)).getString("origin"))
        assertTrue(api.blobs.keys.none { it.startsWith("index/jobs/") && !it.startsWith("index/jobs/download/") })
        // a pending download job is set to done, not replaced by a new one
        api = FakeBlobs(); api.addJob("aaaaaaaaaaa", 1)
        IndexStore.store(api, files(), StoreMode.STORE_ONLY, "phone-a1b2c3", 0, "2026-11-17T12:00:00Z", LinkedHashSet())
        val p = api.job("aaaaaaaaaaa"); assertEquals("done", p.tags["idx_state"]); assertEquals("medusa-library", api.jobJson("aaaaaaaaaaa").getString("origin")); assertEquals(480, api.jobJson("aaaaaaaaaaa").getInt("maxHeight"))
    }

    @Test fun storeAndProcessCreatesTheSameStepJobsAsANodeDownload() {
        IndexStore.store(api, files(), StoreMode.STORE_AND_PROCESS, "phone-a1b2c3", 0, "2026-11-17T12:00:00Z", LinkedHashSet())
        val mine = api.blobs.filterKeys { it.startsWith("index/jobs/") && !it.startsWith("index/jobs/download/") }
        val node = FakeBlobs(); node.addJob("aaaaaaaaaaa", 1); val saved = api; api = node
        runner(FakeFetcher()).run(); api = saved
        val theirs = node.blobs.filterKeys { it.startsWith("index/jobs/") && !it.startsWith("index/jobs/download/") }
        assertEquals(theirs.keys, mine.keys); assertTrue(mine.containsKey("index/jobs/comments/aaaaaaaaaaa.json"))
        for (k in mine.keys) { assertEquals(k, String(theirs[k]!!.bytes).let { JSONObject(it).apply { remove("addedAt") }.toString() }, JSONObject(String(mine[k]!!.bytes)).apply { remove("addedAt") }.toString()); assertEquals(k, theirs[k]!!.tags["idx_state"], mine[k]!!.tags["idx_state"]) }
        assertEquals("done", api.blobs["index/jobs/download/aaaaaaaaaaa.json"]!!.tags["idx_state"])
        // at a priority the owner set, the steps carry it
        api = FakeBlobs(); IndexStore.store(api, files(), StoreMode.STORE_AND_PROCESS, "n", 3, "t", LinkedHashSet())
        assertEquals("3", api.blobs["index/jobs/frames/aaaaaaaaaaa.json"]!!.tags[Prio.TAG])
    }
}
