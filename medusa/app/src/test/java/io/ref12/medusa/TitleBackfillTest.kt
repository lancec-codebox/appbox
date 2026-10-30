package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TitleBackfillTest {
    @Test fun looks_up_each_needed_item_once_with_a_pause_between() {
        val done = HashSet<String>(); val order = ArrayList<String>(); val pauses = ArrayList<Long>()
        TitleBackfill({ listOf("a", "b", "c") }, { it !in done && it != "b" }, { false }, { done.add(it); order.add(it) }, { pauses.add(it) }, 3000).run()
        assertEquals(listOf("a", "c"), order)
        assertEquals(listOf(3000L, 3000L), pauses)
    }
    @Test fun an_item_that_keeps_needing_a_lookup_is_tried_only_once_per_run() {
        var calls = 0
        TitleBackfill({ listOf("a") }, { true }, { false }, { calls++ }, {}).run()
        assertEquals(1, calls)
    }
    @Test fun a_failing_lookup_does_not_stop_the_others() {
        val order = ArrayList<String>()
        TitleBackfill({ listOf("a", "b") }, { true }, { false }, { order.add(it); if (it == "a") throw RuntimeException("x") }, {}).run()
        assertEquals(listOf("a", "b"), order)
    }
    @Test fun it_waits_while_a_download_is_active_and_gives_up_if_it_stays_busy() {
        var calls = 0; val pauses = ArrayList<Long>()
        TitleBackfill({ listOf("a") }, { true }, { true }, { calls++ }, { pauses.add(it) }, 1000).run()
        assertEquals(0, calls); assertEquals(listOf(5000L), pauses)
    }
    @Test fun it_does_nothing_when_no_item_needs_a_lookup() {
        var calls = 0
        TitleBackfill({ listOf("a") }, { false }, { false }, { calls++ }, {}).run()
        assertTrue(calls == 0)
    }
}
