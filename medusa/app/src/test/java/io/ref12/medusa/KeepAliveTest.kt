package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class KeepAliveTest {
    private val min = 60_000L

    @Test fun modes_and_default() {
        assertEquals(listOf(10, 30, 120, 0), KeepAliveMode.values().map { it.minutes })
        assertEquals(KeepAliveMode.MIN30, KeepAliveMode.DEFAULT)
        assertEquals(KeepAliveMode.HOUR2, KeepAliveMode.fromMinutes(120))
        assertEquals(KeepAliveMode.ALWAYS, KeepAliveMode.fromMinutes(0))
        assertEquals(KeepAliveMode.MIN30, KeepAliveMode.fromMinutes(7)) // unknown stored value
        assertEquals(30 * min, KeepAliveMode.MIN30.millis)
    }

    @Test fun stops_only_after_the_chosen_time_with_nothing_happening() {
        val left = 1000L * min
        for (mode in listOf(KeepAliveMode.MIN10, KeepAliveMode.MIN30, KeepAliveMode.HOUR2)) {
            assertFalse(KeepAlive.shouldStop(mode, left + mode.millis - 1, 0, 0, left, 0, false))
            assertTrue(KeepAlive.shouldStop(mode, left + mode.millis, 0, 0, left, 0, false))
        }
    }

    @Test fun requests_and_job_changes_reset_the_timer() {
        val m = KeepAliveMode.MIN30
        val left = 100 * min
        assertTrue(KeepAlive.shouldStop(m, left + 31 * min, 0, 0, left, 0, false))
        assertFalse(KeepAlive.shouldStop(m, left + 31 * min, left + 10 * min, 0, left, 0, false)) // a web app request at +10 min
        assertFalse(KeepAlive.shouldStop(m, left + 31 * min, 0, left + 5 * min, left, 0, false)) // a job finished at +5 min
        assertTrue(KeepAlive.shouldStop(m, left + 36 * min, 0, left + 5 * min, left, 0, false))
    }

    @Test fun never_stops_while_a_screen_is_open_or_a_job_runs_or_when_always() {
        val m = KeepAliveMode.MIN10
        assertFalse(KeepAlive.shouldStop(m, 10_000 * min, 0, 0, 0, 0, uiOpen = true))
        assertFalse(KeepAlive.shouldStop(m, 10_000 * min, 0, 0, 0, 1, uiOpen = false))
        assertFalse(KeepAlive.shouldStop(KeepAliveMode.ALWAYS, 10_000 * min, 0, 0, 0, 0, uiOpen = false))
    }
}
