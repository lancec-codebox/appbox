package io.ref12.medusa.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class SourcesTest {
    private val UC = "UCabcdefghijklmnopqrstuv"
    private val api = FakeBlobs()

    @Test fun aPlaylistUrlIsAPlaylist() {
        val r = SourceUrl.parse("https://www.youtube.com/playlist?list=PLabcdefghijk123")!!
        assertEquals("playlist", r.kind); assertEquals("pl-PLabcdefghijk123", r.jobId)
        assertEquals("index/jobs/refresh/pl-PLabcdefghijk123.json", r.path)
        // a video inside a playlist, and a shared text around the link
        assertEquals("pl-PLabcdefghijk123", SourceUrl.parse("https://youtu.be/dQw4w9WgXcQ?list=PLabcdefghijk123")!!.jobId)
        assertEquals("https://www.youtube.com/playlist?list=PLabcdefghijk123", SourceUrl.firstUrl("Look: https://www.youtube.com/playlist?list=PLabcdefghijk123 nice"))
    }

    @Test fun watchLaterLikedAndMixesAreNotSources() {
        assertNull(SourceUrl.parse("https://www.youtube.com/playlist?list=WL"))
        assertNull(SourceUrl.parse("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ"))
        assertFalse(SourceUrl.isSource("https://www.youtube.com/playlist?list=LL"))
    }

    @Test fun aChannelUrlWithItsIdNeedsNoResolver() {
        val r = SourceUrl.parse("https://www.youtube.com/channel/$UC/videos") { fail("no resolver call"); null }!!
        assertEquals("channel", r.kind); assertEquals("ch-$UC", r.jobId)
    }

    @Test fun aHandleIsResolvedWithTheChannelUrl() {
        val asked = ArrayList<String>()
        val r = SourceUrl.parse("https://m.youtube.com/@SomeDrama/shorts") { asked.add(it); "$UC\n" }!!
        assertEquals("ch-$UC", r.jobId); assertEquals(listOf("https://www.youtube.com/@SomeDrama"), asked)
        assertNull(SourceUrl.parse("https://www.youtube.com/@SomeDrama") { null })
        assertNull(SourceUrl.parse("https://www.youtube.com/@SomeDrama") { "not-a-channel-id" })
    }

    @Test fun aCOrUserUrlIsResolvedToo() {
        val asked = ArrayList<String>()
        assertEquals("ch-$UC", SourceUrl.parse("https://www.youtube.com/c/SomeDrama") { asked.add(it); UC }!!.jobId)
        assertEquals("ch-$UC", SourceUrl.parse("youtube.com/user/old") { asked.add(it); UC }!!.jobId)
        assertEquals(listOf("https://www.youtube.com/c/SomeDrama", "https://www.youtube.com/user/old"), asked)
    }

    @Test fun aPlainVideoIsNotASource() {
        assertNull(SourceUrl.parse("https://www.youtube.com/watch?v=dQw4w9WgXcQ") { fail("no resolver call"); null })
        assertNull(SourceUrl.parse("https://youtu.be/dQw4w9WgXcQ"))
        assertFalse(SourceUrl.isSource("https://www.youtube.com/watch?v=dQw4w9WgXcQ"))
        assertNull(SourceUrl.parse("https://example.com/playlist?list=PLabcdefghijk123"))
        assertTrue(SourceUrl.isSource("https://www.youtube.com/@x")); assertTrue(SourceUrl.isSource("https://www.youtube.com/c/x"))
    }

    @Test fun theRefreshJobHasTheDescriptorAndTags() {
        val now = 1_800_000_000_000L
        val res = SourceFollow.run(api, "https://www.youtube.com/playlist?list=PLabcdefghijk123", FollowOptions(followChannels = true), now) { null }
        assertTrue(res is FollowResult.Created)
        val b = api.blobs["index/jobs/refresh/pl-PLabcdefghijk123.json"]!!
        val o = JSONObject(String(b.bytes))
        assertEquals("refresh", o.getString("stage")); assertEquals("pl-PLabcdefghijk123", o.getString("jobId")); assertEquals("playlist", o.getString("kind"))
        assertEquals("PLabcdefghijk123", o.getString("id")); assertEquals("once", o.getString("schedule")); assertFalse(o.has("every")); assertFalse(o.has("backfill")); assertEquals("lookup", o.getString("run"))
        assertTrue(o.getBoolean("followChannels")); assertTrue(o.isNull("lastPolled")); assertEquals(0, o.getInt("attempts")); assertEquals(1, o.getInt("v"))
        val at = NodeRunner.iso(now)
        // due now, at the high priority: the next free downloader lists it
        assertEquals(mapOf("idx_stage" to "refresh", "idx_state" to "pending", "idx_added" to at, "idx_due" to at, "idx_priority" to "500"), b.tags)
    }

    @Test fun aChannelJobListsItsTabs() {
        SourceFollow.run(api, "https://www.youtube.com/@Show", FollowOptions(every = "6h", backfill = 5), 1_800_000_000_000L) { UC }
        val o = JSONObject(String(api.blobs["index/jobs/refresh/ch-$UC.json"]!!.bytes))
        assertEquals("channel", o.getString("kind")); assertEquals("6h", o.getString("every")); assertEquals("6h", o.getString("schedule")); assertEquals(5, o.getInt("backfill")); assertEquals("@Show", o.getString("name"))
        assertEquals(2, o.getJSONArray("tabs").length()); assertFalse(o.getBoolean("followChannels"))
    }

    @Test fun anExistingWatchJobIsNeverOverwritten() {
        val url = "https://www.youtube.com/playlist?list=PLabcdefghijk123"
        assertTrue(SourceFollow.run(api, url, FollowOptions(), 1_800_000_000_000L) { null } is FollowResult.Created)
        val before = String(api.blobs["index/jobs/refresh/pl-PLabcdefghijk123.json"]!!.bytes)
        val again = SourceFollow.run(api, url, FollowOptions(every = "1d", backfill = 9), 1_900_000_000_000L) { null }
        assertTrue(again is FollowResult.Exists)
        assertEquals(before, String(api.blobs["index/jobs/refresh/pl-PLabcdefghijk123.json"]!!.bytes))
    }

    @Test fun aVideoUrlFollowsNothing() {
        assertTrue(SourceFollow.run(api, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", FollowOptions(), 1L) { null } === FollowResult.NotASource)
        assertTrue(api.blobs.isEmpty())
        assertTrue(SourceFollow.run(api, "https://www.youtube.com/@x", FollowOptions(), 1L) { null } is FollowResult.Failed)
    }

    @Test fun removeDeletesOnlyTheSourceJob() {
        SourceFollow.run(api, "https://www.youtube.com/playlist?list=PLabcdefghijk123", FollowOptions(every = "6h", name = "Drops"), 1_800_000_000_000L) { null }
        api.addJob("aaaaaaaaaaa", 1)
        RefreshJobs.remove(api, "pl-PLabcdefghijk123")
        assertNull(api.blobs["index/jobs/refresh/pl-PLabcdefghijk123.json"])
        assertNotNull(api.blobs["index/jobs/download/aaaaaaaaaaa.json"])
    }

    @Test fun aPriorityGoesIntoTheRecordAndTheLookupKeepsItsBoost() {
        SourceFollow.run(api, "https://www.youtube.com/playlist?list=PLabcdefghijk123", FollowOptions(priority = 40), 1_800_000_000_000L) { null }
        val b = api.blobs["index/jobs/refresh/pl-PLabcdefghijk123.json"]!!
        assertEquals(40, JSONObject(String(b.bytes)).getInt("priority")); assertEquals("500", b.tags["idx_priority"]) // due now at 500; its videos get 40
    }

    @Test fun aDirectVideoRecordsOwnPriorityAndTag() {
        IndexVideo.add(api, "dQw4w9WgXcQ", 1_800_000_000_000L, 25)
        val b = api.blobs["index/jobs/download/dQw4w9WgXcQ.json"]!!; val o = JSONObject(String(b.bytes))
        assertEquals(25, o.getInt("priority")); assertEquals(25, o.getInt("ownPriority")); assertEquals("25", b.tags["idx_priority"])
        IndexVideo.add(api, "eeeeeeeeeee", 1_800_000_000_000L)
        assertFalse(api.blobs["index/jobs/download/eeeeeeeeeee.json"]!!.tags.containsKey("idx_priority"))
        assertTrue(IndexVideo.add(api, "fffffffffff", 1L, 5000) is AddResult.Failed); assertNull(api.blobs["index/jobs/download/fffffffffff.json"])
    }

    @Test fun changingASourcePriorityRetagsItsPendingJobsAndStepsOnly() {
        SourceFollow.run(api, "https://www.youtube.com/playlist?list=PLabcdefghijk123", FollowOptions(), 1_800_000_000_000L) { null }
        api.addJob("aaaaaaaaaaa", 1, extra = ",\"sources\":[\"pl-PLabcdefghijk123\"]")   // recorded
        api.addJob("bbbbbbbbbbb", 2)                                                            // only found by the listing
        api.addJob("ccccccccccc", 3)                                                            // unrelated
        FollowUps.create(api, "aaaaaaaaaaa", "u", 0, "2026-01-01T00:00:00Z")
        api.job("aaaaaaaaaaa").tags.let { }
        api.blobs["index/jobs/sync/aaaaaaaaaaa.json"]!!.tags = api.blobs["index/jobs/sync/aaaaaaaaaaa.json"]!!.tags + ("idx_state" to "done")
        val r = Priorities.setSource(api, "pl-PLabcdefghijk123", 12, listOf("bbbbbbbbbbb"))
        assertEquals(2, r.videos); assertEquals(1, r.matched)
        assertEquals("500", api.blobs["index/jobs/refresh/pl-PLabcdefghijk123.json"]!!.tags["idx_priority"]); assertEquals(12, JSONObject(String(api.blobs["index/jobs/refresh/pl-PLabcdefghijk123.json"]!!.bytes)).getInt("priority"))
        assertEquals("12", api.job("aaaaaaaaaaa").tags["idx_priority"]); assertEquals("12", api.job("bbbbbbbbbbb").tags["idx_priority"])
        assertNull(api.job("ccccccccccc").tags["idx_priority"])
        assertEquals("12", api.blobs["index/jobs/frames/aaaaaaaaaaa.json"]!!.tags["idx_priority"])
        assertEquals("12", api.blobs["index/jobs/comments/aaaaaaaaaaa.json"]!!.tags["idx_priority"])
        assertNull(api.blobs["index/jobs/sync/aaaaaaaaaaa.json"]!!.tags["idx_priority"]) // done: untouched
        assertTrue(JSONObject(String(api.job("bbbbbbbbbbb").bytes)).getJSONArray("sources").toString().contains("pl-PLabcdefghijk123"))
        assertEquals("2026-01-01T00:00:00Z", api.blobs["index/jobs/frames/aaaaaaaaaaa.json"]!!.tags["idx_added"])
    }

    @Test fun aDirectVideoPriorityChangeUsesOwnPriority() {
        IndexVideo.add(api, "dQw4w9WgXcQ", 1_800_000_000_000L, 3)
        Priorities.setSource(api, "v-dQw4w9WgXcQ", 8, null)
        assertEquals("8", api.job("dQw4w9WgXcQ").tags["idx_priority"]); assertEquals(8, api.jobJson("dQw4w9WgXcQ").getInt("ownPriority"))
    }

    private val V = "https://www.youtube.com/watch?v=dQw4w9WgXcQ"

    @Test fun videoIdsAreFoundInVideoUrls() {
        assertEquals("dQw4w9WgXcQ", SourceUrl.videoId(V)); assertEquals("dQw4w9WgXcQ", SourceUrl.videoId("https://youtu.be/dQw4w9WgXcQ?si=x"))
        assertEquals("dQw4w9WgXcQ", SourceUrl.videoId("https://www.youtube.com/shorts/dQw4w9WgXcQ"))
        assertNull(SourceUrl.videoId("https://www.youtube.com/playlist?list=PLabcdefghijk123")); assertNull(SourceUrl.videoId("https://www.youtube.com/@x"))
    }

    @Test fun aPlainVideoGetsADownloadJob() {
        val (r, f) = IndexVideo.share(api, "watch this " + V, false, 1_800_000_000_000L, 0) { fail("no channel lookup"); null }
        assertTrue(r === AddResult.Added); assertNull(f)
        val b = api.blobs["index/jobs/download/dQw4w9WgXcQ.json"]!!; val o = JSONObject(String(b.bytes))
        assertEquals("download", o.getString("stage")); assertEquals("dQw4w9WgXcQ", o.getString("jobId")); assertEquals(V, o.getString("url"))
        assertEquals(480, o.getInt("maxHeight")); assertEquals("medusa-share", o.getString("source")); assertTrue(o.isNull("format")); assertEquals(0, o.getInt("attempts"))
        val at = NodeRunner.iso(1_800_000_000_000L)
        assertEquals(mapOf("idx_stage" to "download", "idx_state" to "pending", "idx_added" to at, "idx_due" to at), b.tags)
    }

    @Test fun anExistingJobOrVideosFolderIsNotDuplicated() {
        api.addJob("dQw4w9WgXcQ", 1); val before = String(api.job("dQw4w9WgXcQ").bytes)
        assertTrue(IndexVideo.share(api, V, false, 1L) { null }.first === AddResult.Already)
        assertEquals(before, String(api.job("dQw4w9WgXcQ").bytes))
        val api2 = FakeBlobs(); api2.blobs["index/videos/dQw4w9WgXcQ/video.mp4"] = FakeBlobs.B(ByteArray(1), emptyMap())
        assertTrue(IndexVideo.share(api2, V, false, 1L) { null }.first === AddResult.Already)
        assertNull(api2.blobs["index/jobs/download/dQw4w9WgXcQ.json"])
    }

    @Test fun alsoFollowTheChannelResolvesThroughTheFakeResolver() {
        val asked = ArrayList<String>()
        val (r, f) = IndexVideo.share(api, V, true, 1_800_000_000_000L) { asked.add(it); UC + "\n" }
        assertTrue(r === AddResult.Added); assertTrue(f is FollowResult.Created); assertEquals(listOf(V), asked)
        assertEquals("channel", JSONObject(String(api.blobs["index/jobs/refresh/ch-$UC.json"]!!.bytes)).getString("kind"))
        // an already indexed video still follows the channel, once
        val (r2, f2) = IndexVideo.share(api, V, true, 2_000_000_000_000L) { UC }
        assertTrue(r2 === AddResult.Already); assertTrue(f2 is FollowResult.Exists)
        assertTrue(IndexVideo.share(FakeBlobs(), V, true, 1L) { null }.second is FollowResult.Failed)
    }

    @Test fun aNonVideoUrlIsRefused() { assertTrue(IndexVideo.share(api, "https://example.com/x", false, 1L) { null }.first is AddResult.Failed); assertTrue(api.blobs.isEmpty()) }
}
