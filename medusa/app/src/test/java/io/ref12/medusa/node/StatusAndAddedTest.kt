package io.ref12.medusa.node

import org.junit.Assert.*
import org.junit.Test

class StatusAndAddedTest {
    @Test fun statusTextsForEachState() {
        assertEquals("Downloading 3/20 today: Some Title 64%", NodeStatus.text(StatusInput(downloading = true, doneToday = 2, cap = 20, title = "Some Title", progress = "64%")))
        assertEquals("Waiting: not charging", NodeStatus.text(StatusInput(wait = Wait.NOT_CHARGING)))
        assertEquals("Waiting: not on Wi-Fi", NodeStatus.text(StatusInput(wait = Wait.NOT_UNMETERED)))
        assertEquals("Waiting: battery low", NodeStatus.text(StatusInput(wait = Wait.BATTERY_LOW)))
        assertEquals("Waiting: storage low", NodeStatus.text(StatusInput(wait = Wait.STORAGE_LOW)))
        assertEquals("Daily cap reached: resumes tomorrow", NodeStatus.text(StatusInput(capReached = true)))
        assertEquals("Budget spent", NodeStatus.text(StatusInput(budgetSpent = true)))
        assertEquals("Downloads stopped by the owner", NodeStatus.text(StatusInput(stoppedByOwner = true)))
        assertEquals("Idle: next check in 12 min", NodeStatus.text(StatusInput(nextCheckMin = 12)))
    }

    @Test fun afterARunTheEndReasonPicksTheStatus() {
        assertEquals("Daily cap reached: resumes tomorrow", NodeStatus.text(NodeStatus.afterRun("daily cap reached", 20, 20)))
        assertEquals("Budget spent", NodeStatus.text(NodeStatus.afterRun("budget spent", 0, 20)))
        assertEquals("Downloads stopped by the owner", NodeStatus.text(NodeStatus.afterRun("control: stop", 0, 20)))
        assertEquals("Idle: next check in 15 min", NodeStatus.text(NodeStatus.afterRun("no work", 0, 20)))
    }

    @Test fun theFirstUnmetConditionIsReported() {
        assertEquals(Wait.NOT_UNMETERED, NodeStatus.waitReason(true, false, false, true, true))
        assertEquals(Wait.NOT_CHARGING, NodeStatus.waitReason(true, false, true, false, false))
        assertEquals(Wait.NONE, NodeStatus.waitReason(false, false, true, false, false))
        assertEquals(Wait.BATTERY_LOW, NodeStatus.waitReason(false, false, true, true, false))
        assertEquals(Wait.STORAGE_LOW, NodeStatus.waitReason(false, true, true, false, true))
    }

    @Test fun progressIsThrottledButNewStatesGoThrough() {
        var now = 0L
        val t = NodeStatus.Throttle(4000) { now }
        assertTrue(t.allow("a 1%", true)); now = 1000; assertFalse(t.allow("a 2%", true)); assertTrue(t.allow("Waiting: battery low", false))
        now = 6000; assertTrue(t.allow("a 3%", true)); assertFalse(t.allow("a 3%", true))
    }

    private class Mem : TextStore { var s: String? = null; override fun load() = s; override fun save(text: String) { s = text } }

    @Test fun theLocalListKeepsWhatThisPhoneAdded() {
        val m = Mem(); val l = AddedItems(m)
        assertTrue(l.all().isEmpty())
        l.add(AddedItem("video", "v-aaaaaaaaaaa", "https://www.youtube.com/watch?v=aaaaaaaaaaa", "aaaaaaaaaaa", 100, 0))
        l.add(AddedItem("playlist", "pl-PLx", "https://www.youtube.com/playlist?list=PLx", "Drops", 200, 5))
        assertEquals(listOf("pl-PLx", "v-aaaaaaaaaaa"), AddedItems(m).all().map { it.jobId }) // newest first, survives a reload
        l.setPriority("v-aaaaaaaaaaa", -4); assertEquals(-4, l.find("v-aaaaaaaaaaa")!!.priority)
        l.add(AddedItem("video", "v-aaaaaaaaaaa", "u", "x", 300, 1)); assertEquals(2, l.all().size)
        l.remove("pl-PLx"); assertEquals(listOf("v-aaaaaaaaaaa"), l.all().map { it.jobId })
        m.s = "garbage"; assertTrue(l.all().isEmpty())
    }

    @Test fun addingFromThePhoneIsRecordedLocallyWithItsPriority() {
        val api = FakeBlobs(); val l = AddedItems(Mem())
        val msg = IndexAdd.add(api, l, "https://youtu.be/dQw4w9WgXcQ", FollowOptions(), 6, 1_800_000_000_000L, { null }, { null })
        assertEquals("Added", msg)
        assertEquals(6, l.find("v-dQw4w9WgXcQ")!!.priority); assertEquals("6", api.job("dQw4w9WgXcQ").tags["idx_priority"])
        IndexAdd.add(api, l, "https://www.youtube.com/playlist?list=PLabcdefghijk123", FollowOptions(priority = 2), 2, 1_800_000_001_000L, { null }, { null })
        assertEquals("playlist", l.find("pl-PLabcdefghijk123")!!.kind)
        assertEquals("That is not a YouTube video, playlist or channel URL", IndexAdd.add(api, l, "https://example.com/x", FollowOptions(), 0, 1L, { null }, { null }))
        assertEquals(2, l.all().size)
    }

    @Test fun theStateComesFromTheStoreAndRemoveTouchesOnlyPendingJobs() {
        val api = FakeBlobs(); val l = AddedItems(Mem())
        IndexAdd.add(api, l, "https://www.youtube.com/playlist?list=PLabcdefghijk123", FollowOptions(), 0, 1L, { null }, { null })
        api.addJob("aaaaaaaaaaa", 1, extra = ",\"sources\":[\"pl-PLabcdefghijk123\"]"); api.addJob("bbbbbbbbbbb", 2, extra = ",\"sources\":[\"pl-PLabcdefghijk123\"]")
        api.job("bbbbbbbbbbb").tags = api.job("bbbbbbbbbbb").tags + ("idx_state" to "done")
        assertEquals("pending 1 \u00b7 done 1 \u00b7 failed 0", AddedState.describe(l.find("pl-PLabcdefghijk123")!!, api))
        IndexAdd.add(api, l, "https://youtu.be/cccccccccc1", FollowOptions(), 0, 2L, { null }, { null })
        assertEquals("download pending", AddedState.describe(l.find("v-cccccccccc1")!!, api))
        AddedState.removeFromIndex(api, l.find("v-cccccccccc1")!!); assertNull(api.blobs["index/jobs/download/cccccccccc1.json"])
        AddedState.removeFromIndex(api, l.find("pl-PLabcdefghijk123")!!); assertNull(api.blobs["index/jobs/watch/pl-PLabcdefghijk123.json"])
        assertNotNull(api.blobs["index/jobs/download/aaaaaaaaaaa.json"])
    }
}
