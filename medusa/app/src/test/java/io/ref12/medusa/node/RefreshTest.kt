package io.ref12.medusa.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.File
import java.nio.file.Files

/** A fake yt-dlp: [listings] answers the flat listing of a URL (raw --print lines); downloads go to a [FakeFetcher]. [events] records the order of lists and downloads. */
class ListFetcher(val dl: FakeFetcher = FakeFetcher()) : NodeFetcher {
    val listings = HashMap<String, List<String>>()
    val events = ArrayList<String>()
    val listCalls = ArrayList<Pair<String, Int>>()
    var listError: NodeFetchException? = null
    override fun list(url: String, end: Int): List<String> { events.add("list:$url"); listCalls.add(url to end); listError?.let { throw it }; return listings[url] ?: emptyList() }
    override fun download(job: DownloadJob, dir: File, progress: (String?, String?) -> Unit): DownloadedFiles { events.add("download:" + job.jobId); return dl.download(job, dir, progress) }
    override fun cancel() { dl.cancel() }
}

class RefreshTest {
    lateinit var api: FakeBlobs; lateinit var state: MemState; lateinit var work: File
    var now = 1_800_000_000_000L
    val PL = "PLabcdefghijk123"
    val UC = "UCabcdefghijklmnopqrstuv"
    fun line(id: String, title: String = "Title $id") = "$id\tNA\tNA\t60\tNA\t$title"
    fun pl(vararg ids: String) = ids.map { line(it) }
    val plUrl get() = "https://www.youtube.com/playlist?list=$PL"
    val a = "aaaaaaaaaaa"; val b = "bbbbbbbbbbb"; val c = "ccccccccccc"; val d = "ddddddddddd"
    val src = "index/jobs/refresh/pl-$PL.json"

    @Before fun up() { api = FakeBlobs(); state = MemState(); work = Files.createTempDirectory("node").toFile() }

    private fun runner(f: NodeFetcher, cap: Int = 20) = NodeRunner(api, f, NodeConfig("lancecm1-a1b2c3", "lancecm1", "wifi+charging", "1.9.2", "android-arm64-v8a", 8, work), state,
        NodeEnv(clock = { now }, sleep = { }, gapMs = { 25_000 }, renewMs = 20), cap = cap)

    private fun follow(every: String = "once", backfill: Int? = null, priority: Int = 0, follow: Boolean = false) =
        RefreshJobs.follow(api, SourceRef("playlist", PL), FollowOptions(every, backfill, follow, "Drops", 480, priority), now)
    private fun job(path: String = src) = JSONObject(String(api.blobs[path]!!.bytes))
    private fun downloads() = api.blobs.keys.filter { it.startsWith("index/jobs/download/") }.map { it.substringAfterLast('/').removeSuffix(".json") }.sorted()

    // ---- add, claim and execute
    @Test fun anAddedSourceIsClaimedAndListedByTheNextRun() {
        follow()
        assertEquals("500", api.blobs[src]!!.tags["idx_priority"]); assertEquals("pending", api.blobs[src]!!.tags["idx_state"])
        val f = ListFetcher(); f.listings[plUrl] = pl(a, b, c)
        val s = runner(f).run()
        assertEquals("no work", s.endReason)
        assertEquals(listOf(a, b, c), downloads())
        assertEquals("unclassified", api.blobs["index/jobs/download/$a.json"]!!.tags["idx_state"])
        val dj = JSONObject(String(api.blobs["index/jobs/download/$a.json"]!!.bytes))
        assertEquals("pl-$PL", dj.getString("source")); assertEquals("Title $a", dj.getString("title")); assertEquals(60.0, dj.getDouble("duration"), 0.0)
        assertEquals("pending", api.blobs["index/jobs/classify/all.json"]!!.tags["idx_state"])   // the singleton classify job decides them
        // a full listing, with no --playlist-end
        assertEquals(listOf(plUrl to 0), f.listCalls)
        // schedule once: the job is done, back at the source's own priority, and records what it did
        val t = api.blobs[src]!!.tags; assertEquals("done", t["idx_state"]); assertNull(t["idx_priority"]); assertNull(api.blobs[src]!!.lease)
        val o = job(); assertEquals("lookup", o.getString("lastRun")); assertEquals("refresh", o.getString("run")); assertEquals(3, o.getInt("members")); assertEquals(3, o.getInt("found"))
        assertEquals("lancecm1-a1b2c3", o.getString("nodeId")); assertTrue(o.has("lastLookup")); assertFalse(o.has("lastRefresh")); assertTrue(o.isNull("nextRefresh"))
        assertEquals("lookup done", state.entries.last().result)
    }

    @Test fun membersJsonListsEveryVideoOfTheLookup() {
        follow(backfill = 1)
        val f = ListFetcher(); f.listings[plUrl] = pl(a, b, c)
        runner(f).run()
        assertEquals(listOf(a), downloads())                    // a playlist lookup honours the backfill limit
        val m = JSONObject(api.blobText("index/sources/pl-$PL/members.json"))
        assertEquals("pl-$PL", m.getString("sourceId")); assertEquals("playlist", m.getString("kind")); assertEquals(PL, m.getString("id"))
        val ms = m.getJSONArray("members"); assertEquals(3, ms.length())                  // members.json has all of them whatever was queued
        assertEquals(b, ms.getJSONObject(1).getString("id")); assertEquals("Title $b", ms.getJSONObject(1).getString("title")); assertEquals(60.0, ms.getJSONObject(1).getDouble("duration"), 0.0)
        assertEquals(NodeRunner.iso(now), m.getString("lookedUpAt"))
    }

    @Test fun aKnownVideoIsNotQueuedAgain() {
        follow()
        api.addJob(a, 1)
        api.blobs["index/videos/$b/download.zip"] = FakeBlobs.B(ByteArray(1), emptyMap())
        val f = ListFetcher(); f.listings[plUrl] = pl(a, b, c)
        runner(f).run()
        assertEquals(listOf(a, c), downloads())
        assertNull(api.blobs["index/jobs/download/$b.json"])
    }

    @Test fun theSourcesPriorityGoesToItsVideosButTheClaimUsed500() {
        follow(priority = 30)
        val f = ListFetcher(); f.listings[plUrl] = pl(a)
        runner(f).run()
        assertEquals("30", api.blobs["index/jobs/download/$a.json"]!!.tags["idx_priority"])
        assertEquals("30", api.blobs[src]!!.tags["idx_priority"])  // and the source itself returns to its own priority
    }

    // ---- refresh is incremental
    @Test fun aRefreshQueuesOnlyWhatIsNewSinceTheLastRun() {
        follow(every = "1d")
        val f = ListFetcher(); f.listings[plUrl] = pl(a, b)
        runner(f).run()
        assertEquals(listOf(a, b), downloads())
        assertEquals("pending", api.blobs[src]!!.tags["idx_state"])
        assertEquals(NodeRunner.iso(now + 86_400_000L), api.blobs[src]!!.tags["idx_due"])     // pending again a day later
        // nothing is due yet: another run does nothing
        f.events.clear(); assertEquals("no work", runner(f).run().endReason); assertTrue(f.events.isEmpty())
        now += 86_400_000L; f.listings[plUrl] = pl(c, a, b)
        runner(f).run()
        assertEquals(listOf(a, b, c), downloads())
        val o = job(); assertEquals("refresh", o.getString("lastRun")); assertTrue(o.has("lastRefresh")); assertEquals(3, o.getInt("members")); assertEquals(3, o.getInt("found"))
        assertEquals(NodeRunner.iso(now - 86_400_000L), JSONObject(api.blobText("index/sources/pl-$PL/members.json")).getString("lookedUpAt"))   // the lookup time is kept
    }

    @Test fun aChannelRefreshListsTheNewest30AndALookupTheNewest100() {
        RefreshJobs.follow(api, SourceRef("channel", UC), FollowOptions("1d", null, false, "Show"), now)
        val f = ListFetcher(); val url = "https://www.youtube.com/channel/$UC/videos"; val shorts = "https://www.youtube.com/channel/$UC/shorts"
        f.listings[url] = pl(a)
        runner(f).run()
        assertEquals(listOf(url to 100, shorts to 100), f.listCalls); f.listCalls.clear()
        now += 86_400_000L
        runner(f).run()
        assertEquals(listOf(url to 30, shorts to 30), f.listCalls)
    }

    // ---- schedules
    @Test fun afterARunOnceAndOffAreDoneAndAScheduledSourceIsPending() {
        for ((sch, st) in listOf("once" to "done", "off" to "done", "6h" to "pending", "daily@03:00" to "pending")) {
            up(); follow(every = sch)
            val f = ListFetcher(); f.listings[plUrl] = pl(a)
            runner(f).run()
            assertEquals(sch, st, api.blobs[src]!!.tags["idx_state"])
            if (st == "pending") assertTrue(api.blobs[src]!!.tags["idx_due"]!! > NodeRunner.iso(now))
        }
    }

    @Test fun scheduleParsingAndTimes() {
        assertEquals("once", RefreshSchedule.parse("").text); assertEquals("off", RefreshSchedule.parse("OFF").text); assertEquals("6h", RefreshSchedule.parse("every 6h").text)
        assertEquals("daily@03:30", RefreshSchedule.parse("daily@3:30").text)
        for (bad in listOf("sometimes", "daily@25:00", "0h", "daily@")) assertThrows(IllegalArgumentException::class.java) { RefreshSchedule.parse(bad) }
        assertNull(RefreshSchedule.ONCE.next(now)); assertNull(RefreshSchedule.OFF.next(now))
        assertEquals(now + 6 * 3_600_000L, RefreshSchedule.parse("6h").next(now))
        val t0 = 1_763_380_800_000L   // 2025-11-17T12:00:00Z
        assertEquals(t0 + 15 * 3_600_000L, RefreshSchedule.parse("daily@03:00").next(t0))   // tomorrow 03:00
        assertEquals(t0 + 90 * 60_000L, RefreshSchedule.parse("daily@13:30").next(t0))      // later today
        // a job from before 1.9.2 has only "every": that stays its schedule
        assertEquals("every", RefreshSchedule.of(JSONObject("{\"every\":\"1d\"}")).kind); assertEquals("once", RefreshSchedule.of(JSONObject("{}")).text)
        assertEquals("refresh", RefreshWork.kindOf(JSONObject("{\"every\":\"1d\",\"seen\":[]}"))); assertEquals("lookup", RefreshWork.kindOf(JSONObject("{\"every\":\"1d\"}")))
    }

    // ---- manual lookup and refresh
    @Test fun aManualRefreshIsDueNowAt500AndQueuesOnlyNewVideosThenIsDoneAgain() {
        follow()
        val f = ListFetcher(); f.listings[plUrl] = pl(a)
        runner(f).run()
        f.listings[plUrl] = pl(b, a)
        assertTrue(RefreshJobs.trigger(api, "pl-$PL", "refresh", now))
        assertEquals("pending", api.blobs[src]!!.tags["idx_state"]); assertEquals("500", api.blobs[src]!!.tags["idx_priority"]); assertEquals("refresh", job().getString("run"))
        runner(f).run()
        assertEquals(listOf(a, b), downloads()); assertEquals("refresh", job().getString("lastRun")); assertEquals("done", api.blobs[src]!!.tags["idx_state"])   // once: no more scheduled
    }

    @Test fun aManualLookupListsEverythingAgain() {
        follow(); val f = ListFetcher(); f.listings[plUrl] = pl(a); runner(f).run()
        f.listings[plUrl] = pl(b, a)
        assertTrue(RefreshJobs.trigger(api, "pl-$PL", "lookup", now))
        runner(f).run()
        assertEquals("lookup", job().getString("lastRun")); assertEquals(listOf(a, b), downloads())
    }

    @Test fun triggerRefusesAnUnknownSourceAndABadKindAndSkipsARunningOne() {
        assertThrows(IllegalArgumentException::class.java) { RefreshJobs.trigger(api, "pl-nope", "refresh", now) }
        follow()
        assertThrows(IllegalArgumentException::class.java) { RefreshJobs.trigger(api, "pl-$PL", "watch", now) }
        api.acquireLease(src, 60); assertFalse(RefreshJobs.trigger(api, "pl-$PL", "refresh", now))
    }

    // ---- the old watch name
    @Test fun anOldWatchJobIsMigratedKeepingItsScheduleAndRunAsARefresh() {
        val old = "index/jobs/watch/pl-$PL.json"
        api.blobs[old] = FakeBlobs.B("{\"v\":1,\"stage\":\"watch\",\"jobId\":\"pl-$PL\",\"kind\":\"playlist\",\"id\":\"$PL\",\"name\":\"Old\",\"every\":\"1d\",\"seen\":[\"$a\"],\"priority\":30,\"maxHeight\":480,\"attempts\":0}".toByteArray(),
            mapOf("idx_stage" to "watch", "idx_state" to "pending", "idx_added" to "2026-11-01T00:00:00Z", "idx_due" to "2026-11-02T00:00:00Z", "idx_priority" to "30"))
        val f = ListFetcher(); f.listings[plUrl] = pl(b, a)
        runner(f).run()
        assertNull(api.blobs[old])
        assertEquals(listOf(b), downloads())                                   // it had seen a: only b is new (a refresh, not a lookup)
        val o = job(); assertEquals("refresh", o.getString("stage")); assertEquals("refresh", o.getString("lastRun")); assertEquals("1d", o.getString("every"))
        assertEquals("pending", api.blobs[src]!!.tags["idx_state"]); assertEquals("30", api.blobs[src]!!.tags["idx_priority"]); assertEquals("refresh", api.blobs[src]!!.tags["idx_stage"])
    }

    @Test fun addingASourceThatExistsAsAWatchJobDoesNothing() {
        api.blobs["index/jobs/watch/pl-$PL.json"] = FakeBlobs.B("{}".toByteArray(), mapOf("idx_stage" to "watch", "idx_state" to "pending"))
        assertFalse(follow()); assertNull(api.blobs[src])
        assertTrue(RefreshJobs.exists(api, "pl-$PL"))
    }

    @Test fun theOldWatchControlKeyStopsOnlyTheRefreshStage() {
        assertEquals(Level.STOP, Control.parse("{\"stages\":{\"watch\":\"stop\"}}").effective("refresh"))
        assertEquals(Level.RUN, Control.parse("{\"stages\":{\"watch\":\"stop\"}}").effective("download"))
        assertEquals(Level.RUN, Control.parse("{\"stages\":{\"watch\":\"stop\",\"refresh\":\"run\"}}").effective("refresh"))   // the new key wins
        follow(); api.addJob(a, 1)
        api.setControl("{\"mode\":\"run\",\"stages\":{\"watch\":\"stop\"}}")
        val f = ListFetcher(); f.listings[plUrl] = pl(b)
        val s = runner(f).run()
        assertEquals(1, s.done); assertEquals(listOf("download:$a"), f.events)          // the download ran, the source was left alone
        assertEquals("pending", api.blobs[src]!!.tags["idx_state"])
    }

    @Test fun theNodeStopsWhenBothStagesAreStopped() {
        follow(); api.setControl("{\"mode\":\"stop\"}")
        val s = runner(ListFetcher()).run()
        assertEquals("control: stop", s.endReason); assertEquals("stopped", s.finalState)
        api.setControl("{\"stages\":{\"download\":\"stop\",\"watch\":\"stop\"}}")
        assertEquals("control: stop", runner(ListFetcher()).run().endReason)
    }

    // ---- one shared worker, the same rules as downloads
    @Test fun refreshesAndDownloadsShareOneWorkerInClaimOrder() {
        api.addJob(a, 1)                                        // a download at priority 0
        follow()                                                // a lookup at 500
        api.addJob(b, 2, extra = ",\"priority\":900"); api.job(b).tags = api.job(b).tags + ("idx_priority" to "900")   // a download at 900
        val f = ListFetcher(); f.listings[plUrl] = pl(c)
        runner(f).run()
        assertEquals(listOf("download:$b", "list:$plUrl", "download:$a"), f.events.filter { !it.endsWith(c) })
        assertEquals(listOf(NodeRunner.iso(now)), api.blobs[src]!!.tags.filterKeys { it == "idx_due" }.values.toList().map { it.let { _ -> NodeRunner.iso(now) } })
        assertEquals(2, state.entries.count { it.result == "done" })
    }

    @Test fun aRefreshIsNotDueUntilItsIdxDue() {
        follow(every = "1d"); val f = ListFetcher(); f.listings[plUrl] = pl(a)
        api.blobs[src]!!.tags = api.blobs[src]!!.tags + ("idx_due" to NodeRunner.iso(now + 3_600_000L))
        assertEquals("no work", runner(f).run().endReason); assertTrue(f.events.isEmpty())
    }

    @Test fun aBotBlockOnAListingBacksTheNodeOffAndKeepsTheKind() {
        follow(); api.addJob(a, 5)
        val f = ListFetcher(); f.listError = NodeFetchException(FailureKind.BOT, "HTTP Error 429: Too Many Requests")
        val s = runner(f).run()
        assertEquals("bot block: backing off for an hour", s.endReason); assertEquals(now + 3_600_000L, state.backoff)
        val t = api.blobs[src]!!.tags; assertEquals("pending", t["idx_state"]); assertEquals("500", t["idx_priority"]); assertNull(api.blobs[src]!!.lease)
        assertEquals(0, job().getInt("attempts")); assertTrue(job().getString("lastError").contains("429")); assertEquals("lookup", RefreshWork.kindOf(job()))
        assertEquals(listOf("list:$plUrl"), f.events)                     // nothing else is tried while backing off
        assertEquals("backing off", runner(f).run().endReason)
    }

    @Test fun aTransientListingErrorCountsAnAttemptAndAPermanentOneFailsTheSource() {
        follow(); val f = ListFetcher(); f.listError = NodeFetchException(FailureKind.TRANSIENT, "timed out")
        runner(f).run(); assertEquals(3, job().getInt("attempts")); assertEquals("failed", api.blobs[src]!!.tags["idx_state"])   // three attempts, as a download
        up(); follow(); f.listError = NodeFetchException(FailureKind.PERMANENT, "ERROR: Private playlist")
        runner(f).run(); assertEquals("failed", api.blobs[src]!!.tags["idx_state"]); assertTrue(job().getBoolean("permanent"))
    }

    @Test fun theDailyCapOnlyStopsDownloadsAndRefreshStillRuns() {
        follow(); api.addJob(a, 1)
        val f = ListFetcher(); f.listings[plUrl] = pl(b)
        val s = runner(f, cap = 0).run()
        assertEquals("daily cap reached", s.endReason)
        assertEquals(listOf("list:$plUrl"), f.events); assertEquals("pending", api.job(a).tags["idx_state"])   // the download waits, the listing happened
        assertEquals(0, state.doneToday(NodeRunner.day(now)))                                                  // and a listing is not counted as a download
    }

    @Test fun aRefreshDoesNotSpendTheBudget() {
        follow(); state.local = LocalLimits(1, null, null)
        val f = ListFetcher(); f.listings[plUrl] = pl(a); runner(f).run()
        assertEquals("done", api.blobs[src]!!.tags["idx_state"])
        assertEquals(1, RunLimits.remaining(RunLimits.resolve(state.localLimits(), Control.RUN, "n"), state))
    }

    @Test fun followChannelsAddsEachChannelOnceAsALookupDueNow() {
        follow(follow = true)
        val f = ListFetcher(); f.listings[plUrl] = listOf("$a\t$UC\tOne\t60\tNA\tT1", "$b\t$UC\tOne\t60\tNA\tT2")
        runner(f).run()
        val ch = "index/jobs/refresh/ch-$UC.json"
        // added as a lookup due now at 500 (see anAddedSourceIsClaimed...), and the same worker then looked it up in this run
        assertEquals("once", JSONObject(String(api.blobs[ch]!!.bytes)).getString("schedule")); assertEquals("lookup", JSONObject(String(api.blobs[ch]!!.bytes)).getString("lastRun"))
        assertEquals(1, f.listCalls.count { it.first.contains("/channel/$UC/") && it.first.endsWith("/videos") })
    }

    @Test fun theRegistryAdvertisesBothStages() {
        follow(); runner(ListFetcher()).run()
        val reg = JSONObject(api.blobText("index/nodes/lancecm1-a1b2c3.json"))
        assertEquals(listOf("download", "refresh"), (0 until reg.getJSONArray("stages").length()).map { reg.getJSONArray("stages").getString(it) })
    }

    @Test fun aFetcherThatCannotListFailsPermanentlyNotSilently() {
        follow()
        runner(FakeFetcher()).run()
        assertEquals("failed", api.blobs[src]!!.tags["idx_state"])
    }
}

