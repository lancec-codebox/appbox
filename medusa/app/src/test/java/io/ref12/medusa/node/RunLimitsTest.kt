package io.ref12.medusa.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.time.Duration
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.ZonedDateTime

class CronTest {
    private val utc = ZoneId.of("UTC")
    private fun at(s: String) = LocalDateTime.parse(s).atZone(utc).toInstant().toEpochMilli()

    @Test fun parsesStarListsRangesAndSteps() {
        val c = Cron.parse("*/15 8-10,20 * * *")!!
        assertTrue(c.matches(LocalDateTime.parse("2027-01-15T08:00"))); assertTrue(c.matches(LocalDateTime.parse("2027-01-15T10:45")))
        assertTrue(c.matches(LocalDateTime.parse("2027-01-15T20:30")))
        assertFalse(c.matches(LocalDateTime.parse("2027-01-15T08:10"))); assertFalse(c.matches(LocalDateTime.parse("2027-01-15T11:00")))
        assertTrue(Cron.parse("5/20 * * * *")!!.matches(LocalDateTime.parse("2027-01-15T01:45")))
        assertFalse(Cron.parse("5/20 * * * *")!!.matches(LocalDateTime.parse("2027-01-15T01:05").plusMinutes(5)))
    }

    @Test fun rejectsBadExpressions() {
        for (b in listOf("", "* * * *", "* * * * * *", "60 * * * *", "* 24 * * *", "* * 0 * *", "* * * 13 *", "* * * * 8", "5-1 * * * *", "*/0 * * * *", "a * * * *", ",1 * * * *", "1/2/3 * * * *"))
            assertNull(b, Cron.parse(b))
        assertNull(Cron.parse(null))
    }

    @Test fun dayOfWeekSevenIsSunday() {
        val sun = Cron.parse("0 9 * * 7")!!; val zero = Cron.parse("0 9 * * 0")!!
        val sunday = LocalDateTime.parse("2027-01-17T09:00") // a Sunday
        assertTrue(sun.matches(sunday)); assertTrue(zero.matches(sunday))
        assertFalse(sun.matches(sunday.plusDays(1)))
        assertTrue(Cron.parse("0 9 * * 5-7")!!.matches(sunday)); assertTrue(Cron.parse("0 9 * * 1-5")!!.matches(sunday.minusDays(2)))
        assertEquals(zero.nextAfter(ZonedDateTime.of(sunday, utc)), sun.nextAfter(ZonedDateTime.of(sunday, utc)))
        assertEquals(LocalDateTime.parse("2027-01-24T09:00"), sun.nextAfter(ZonedDateTime.of(sunday, utc))!!.toLocalDateTime())
    }

    @Test fun dayOfMonthAndDayOfWeekAreEitherWhenBothAreSet() {
        val c = Cron.parse("0 0 13 * 5")!! // the 13th or any Friday
        assertTrue(c.matches(LocalDateTime.parse("2027-01-15T00:00"))); assertTrue(c.matches(LocalDateTime.parse("2027-01-13T00:00")))
        assertFalse(c.matches(LocalDateTime.parse("2027-01-14T00:00")))
    }

    @Test fun aWindowOpensAtACronTimeAndLastsItsLength() {
        val s = Schedule.of("0 1 * * *", "3h")!!
        assertFalse(s.isOpen(at("2027-01-15T00:59:59"), utc)); assertTrue(s.isOpen(at("2027-01-15T01:00:00"), utc))
        assertTrue(s.isOpen(at("2027-01-15T03:59:59"), utc)); assertFalse(s.isOpen(at("2027-01-15T04:00:00"), utc))
        assertEquals(at("2027-01-16T01:00:00"), s.nextStart(at("2027-01-15T08:00:00"), utc))
        assertEquals(at("2027-01-15T01:00:00"), s.nextStart(at("2027-01-15T00:30:00"), utc))
    }

    @Test fun aWindowCanCrossMidnight() {
        val s = Schedule.of("0 23 * * *", "3h")!!
        assertTrue(s.isOpen(at("2027-01-15T23:30:00"), utc)); assertTrue(s.isOpen(at("2027-01-16T00:30:00"), utc)); assertTrue(s.isOpen(at("2027-01-16T01:59:00"), utc))
        assertFalse(s.isOpen(at("2027-01-16T02:00:00"), utc)); assertFalse(s.isOpen(at("2027-01-15T22:59:00"), utc))
        assertEquals(at("2027-01-15T23:00:00"), s.windowStart(at("2027-01-16T01:00:00"), utc)!!.toInstant().toEpochMilli())
        // a window on one day of the week that runs into the next
        val sat = Schedule.of("0 22 * * 6", "6h")!!
        assertTrue(sat.isOpen(at("2027-01-17T03:00:00"), utc)) // Sunday 03:00, the window started Saturday 22:00
        assertFalse(sat.isOpen(at("2027-01-17T04:00:00"), utc)); assertFalse(sat.isOpen(at("2027-01-18T03:00:00"), utc))
    }

    @Test fun localTimeIsUsed() {
        val s = Schedule.of("0 1 * * *", "1h")!!
        val la = ZoneId.of("America/Los_Angeles")
        assertTrue(s.isOpen(at("2027-01-15T09:30:00"), la)) // 01:30 in Los Angeles (UTC-8)
        assertFalse(s.isOpen(at("2027-01-15T01:30:00"), la))
    }

    @Test fun durationsParse() {
        assertEquals(3 * 3_600_000L, Durations.parse("3h")); assertEquals(90 * 60_000L, Durations.parse("1h30m")); assertEquals(86_400_000L, Durations.parse("1d"))
        assertNull(Durations.parse("3")); assertNull(Durations.parse("0h")); assertNull(Durations.parse("")); assertNull(Durations.parse("soon"))
        assertEquals(Schedule.DEFAULT_WINDOW_MS, Schedule.of("0 1 * * *", null)!!.windowMs)
    }
}

class RunLimitsTest {
    lateinit var api: FakeBlobs; lateinit var state: MemState; lateinit var work: File
    var now = 1_800_000_000_000L // 2027-01-15T08:00:00Z
    @Before fun up() { api = FakeBlobs(); state = MemState(); work = Files.createTempDirectory("node").toFile() }

    private fun run(f: FakeFetcher = FakeFetcher()): Pair<Summary, FakeFetcher> =
        NodeRunner(api, f, NodeConfig("n-1", "n", "wifi+charging", "1.7.2", "android", 4, work), state,
            NodeEnv(clock = { now }, sleep = {}, gapMs = { 1 }, renewMs = 20, zone = ZoneOffset.UTC), cap = 100).run().let { it to f }
    private fun jobs(n: Int) { for (i in 1..n) api.addJob("job" + i + "xxxxxxx", i) }
    private fun override(json: String, changedAt: String = "2027-01-15T07:00:00Z") =
        api.setControl("{\"v\":1,\"mode\":\"run\",\"nodes\":{\"n-1\":" + json + "},\"changedAt\":\"" + changedAt + "\"}")
    private fun reg() = JSONObject(String(api.blobs["index/nodes/n-1.json"]!!.bytes))

    @Test fun noLimitsMeansNoBudget() { jobs(3); val (s, f) = run(); assertEquals(3, f.jobs.size); assertEquals("no work", s.endReason); assertFalse(reg().has("budget")) }

    @Test fun theBudgetCountsDownPerFinishedDownloadAndStopsAtZero() {
        state.local = LocalLimits(2, null, null); jobs(4)
        val (s, f) = run()
        assertEquals(2, f.jobs.size); assertEquals("budget spent", s.endReason); assertEquals(0, state.bLeft)
        assertEquals("budget spent", reg().getString("state")); assertEquals(0, reg().getInt("budget"))
        val (s2, f2) = run(); assertEquals(0, f2.jobs.size); assertEquals("budget spent", s2.endReason) // still spent on the next run
    }

    @Test fun aFailedDownloadDoesNotCountAgainstTheBudget() {
        state.local = LocalLimits(1, null, null); jobs(2)
        val bad = FakeFetcher { j, _, _ -> if (j.jobId == "job1xxxxxxx") throw NodeFetchException(FailureKind.PERMANENT, "private video") }
        val (_, f) = run(bad)
        assertEquals(2, f.jobs.size); assertEquals(0, state.bLeft)
    }

    @Test fun changingTheLocalBudgetRestartsTheCountdown() {
        state.local = LocalLimits(1, null, null); jobs(3)
        assertEquals(1, run().second.jobs.size)
        state.local = LocalLimits(2, null, null)
        assertEquals(2, run().second.jobs.size)
    }

    @Test fun theOverrideWinsOverTheLocalValue() {
        state.local = LocalLimits(5, null, null); jobs(4); override("{\"budget\":1}")
        val (s, f) = run(); assertEquals(1, f.jobs.size); assertEquals("budget spent", s.endReason)
    }

    @Test fun theOverrideCountdownRestartsWhenItsValueOrChangedAtChanges() {
        jobs(8); override("{\"budget\":1}")
        assertEquals(1, run().second.jobs.size)
        assertEquals(0, run().second.jobs.size) // same value, same changedAt: still spent
        override("{\"budget\":1}", "2027-01-15T07:30:00Z") // same value, new changedAt
        assertEquals(1, run().second.jobs.size)
        override("{\"budget\":3}", "2027-01-15T07:30:00Z") // new value
        assertEquals(3, run().second.jobs.size)
        assertEquals(0, run().second.jobs.size)
    }

    @Test fun removingTheOverrideFallsBackToTheLocalBudget() {
        state.local = LocalLimits(2, null, null); jobs(6); override("{\"budget\":1}")
        assertEquals(1, run().second.jobs.size)
        api.setControl("{\"v\":1,\"mode\":\"run\"}")
        assertEquals(2, run().second.jobs.size)
    }

    @Test fun theScheduleClaimsOnlyInsideAWindow() {
        state.local = LocalLimits(null, "0 1 * * *", "3h"); jobs(2)
        val (s, f) = run()
        assertEquals(0, f.jobs.size); assertEquals("waiting for window", s.endReason); assertEquals(0, api.count("acquire:"))
        val r = reg(); assertEquals("waiting for window", r.getString("state")); assertEquals("0 1 * * *", r.getString("schedule")); assertEquals("3h", r.getString("window"))
        assertEquals("2027-01-16T01:00:00Z", r.getString("nextWindow"))
        now = 1_800_000_000_000L + 18 * 3_600_000L // 02:00 the next day
        val (s2, f2) = run(); assertEquals(2, f2.jobs.size); assertEquals("no work", s2.endReason)
        assertEquals("idle", reg().getString("state")); assertFalse(reg().has("nextWindow"))
    }

    @Test fun theWindowIsCheckedBeforeEachClaimSoADownloadInProgressFinishes() {
        state.local = LocalLimits(null, "0 6 * * *", "1h"); jobs(3); now = 1_800_000_000_000L - 2 * 3_600_000L + 30 * 60_000L // 06:30
        val f = FakeFetcher { _, _, _ -> now += 40 * 60_000L } // each download takes 40 minutes: the window (06:00-07:00) is over after the first
        val (s, ff) = run(f)
        assertEquals(1, ff.jobs.size); assertEquals("waiting for window", s.endReason); assertEquals("done", api.jobJson("job1xxxxxxx").optString("x", "done").let { "done" })
        assertEquals("done", api.job("job1xxxxxxx").tags["idx_state"])
    }

    @Test fun theOverrideCanSetTheSchedule() {
        jobs(1); override("{\"schedule\":\"0 1 * * *\",\"window\":\"2h\"}")
        val (s, f) = run(); assertEquals(0, f.jobs.size); assertEquals("waiting for window", s.endReason)
        assertEquals("2h", reg().getString("window"))
    }

    @Test fun registryShowsTheBudgetLeft() {
        state.local = LocalLimits(5, "0 6 * * *", "3h"); jobs(2)
        run(); val r = reg()
        assertEquals(3, r.getInt("budget")); assertEquals("0 6 * * *", r.getString("schedule")); assertEquals("idle", r.getString("state"))
    }

    @Test fun budgetSpentWinsOverAWindowThatIsOpen() {
        state.local = LocalLimits(0, "0 6 * * *", "3h"); jobs(1)
        val (s, f) = run(); assertEquals(0, f.jobs.size); assertEquals("budget spent", s.endReason); assertEquals("budget spent", reg().getString("state"))
    }

    @Test fun anInvalidScheduleIsIgnored() {
        state.local = LocalLimits(null, "not a cron", "3h"); jobs(1)
        assertEquals(1, run().second.jobs.size)
    }
}
