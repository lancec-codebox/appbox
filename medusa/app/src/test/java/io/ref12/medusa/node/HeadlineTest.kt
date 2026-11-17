package io.ref12.medusa.node

import org.junit.Assert.*
import org.junit.Test

class HeadlineTest {
    private fun h(s: StatusInput) = NodeStatus.text(s)
    private val back = "12:35 AM"

    @Test fun eachStateHasItsHeadline() {
        assertEquals("Downloading: T \u2014 video 45%", h(StatusInput(downloading = true, title = "T", progress = "download 45%")))
        assertEquals("Downloading: T \u2014 subtitles", h(StatusInput(downloading = true, title = "T", progress = "subtitles")))
        assertEquals("Downloading: T \u2014 uploading 3/12 blocks", h(StatusInput(downloading = true, title = "T", progress = "upload 3/12 blocks")))
        assertEquals("Backing off until 12:35 AM \u2014 bot block on abc (bot block on abc)".replace(" (bot block on abc)", ""), h(StatusInput(backoffUntil = back, backoffReason = "bot block on abc")))
        assertEquals("Waiting: outside the schedule window", h(StatusInput(wait = Wait.WINDOW)))
        assertEquals("Downloads stopped by the owner", h(StatusInput(stoppedByOwner = true)))
        assertEquals("Daily cap reached (200/200)", h(StatusInput(capReached = true, doneToday = 200, cap = 200)))
        assertEquals("Budget spent", h(StatusInput(budgetSpent = true)))
        assertEquals("No downloads queued", h(StatusInput(noWork = true)))
        assertEquals("Idle: next check in 10 min", h(StatusInput(nextCheckMin = 10)))
    }

    @Test fun precedenceWhenSeveralAreTrue() {
        val all = StatusInput(downloading = true, title = "T", backoffUntil = back, wait = Wait.NOT_UNMETERED, stoppedByOwner = true, capReached = true, budgetSpent = true, noWork = true, nextCheckMin = 10)
        assertTrue(h(all), h(all).startsWith("Downloading: T"))
        val b = StatusInput(backoffUntil = back, backoffReason = "bot block on x", wait = Wait.NOT_UNMETERED, stoppedByOwner = true, capReached = true, noWork = true, nextCheckMin = 10)
        assertTrue(h(b), h(b).startsWith("Backing off until 12:35 AM"))
        val w = StatusInput(wait = Wait.BATTERY_LOW, stoppedByOwner = true, capReached = true, noWork = true)
        assertEquals("Waiting: battery low", h(w))
        assertEquals("Downloads stopped by the owner", h(StatusInput(stoppedByOwner = true, capReached = true, budgetSpent = true, noWork = true)))
        assertEquals("Daily cap reached (5/5)", h(StatusInput(capReached = true, doneToday = 5, cap = 5, budgetSpent = true, noWork = true)))
        assertEquals("Budget spent", h(StatusInput(budgetSpent = true, noWork = true, nextCheckMin = 3)))
        assertEquals("No downloads queued", h(StatusInput(noWork = true, nextCheckMin = 3)))
        assertEquals("Index node is off", h(StatusInput(enabled = false, downloading = true, backoffUntil = back)))
    }

    @Test fun backOffNeverShowsAsIdle() {
        assertFalse(h(StatusInput(backoffUntil = back, nextCheckMin = 10)).startsWith("Idle"))
    }

    @Test fun historyShowsTimeAndVersionAndMarksOlderEntries() {
        val fmt = { t: Long -> "T" + t }
        val now = JobLogEntry("abc", "A title", "done", 5, null, "1.8.3")
        assertEquals("T5 \u00b7 abc A title - done \u00b7 Medusa 1.8.3", NodeStatus.historyLine(now, fmt))
        val old = JobLogEntry("def", null, "bot block", 7, "blocked", null)
        assertEquals("T7 \u00b7 def  - bot block (blocked) \u00b7 older version", NodeStatus.historyLine(old, fmt))
        assertEquals("Last run: never", NodeStatus.lastRunLine(0, null, "", fmt))
        assertEquals("Last run: T9 \u00b7 Medusa 1.8.3 - downloaded 2, failed 1, backing off", NodeStatus.lastRunLine(9, "1.8.3", NodeStatus.runSummary(2, 1, 0, "bot block: backing off for an hour"), fmt))
        assertTrue(NodeStatus.lastRunLine(9, null, "x", fmt).contains("older version"))
    }

    @Test fun runSummaryWords() {
        assertEquals("downloaded 0, failed 0, no downloads queued", NodeStatus.runSummary(0, 0, 0, "no work"))
        assertEquals("downloaded 1, failed 0, released 1, stopped by the owner", NodeStatus.runSummary(1, 0, 1, "control: stop"))
    }

    @Test fun progressWords() {
        assertEquals("video 7%", NodeStatus.friendlyProgress("download 7%")); assertEquals("uploading 40%", NodeStatus.friendlyProgress("upload 40%")); assertEquals("starting", NodeStatus.friendlyProgress("starting"))
    }
}
