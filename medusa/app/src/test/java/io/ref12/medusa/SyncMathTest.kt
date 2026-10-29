package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SyncMathTest {
    private val lines = listOf(Line(10.0, 12.0, "a"), Line(20.0, 22.0, "b"), Line(100.0, 102.0, "c"))
    private fun a(at: Double, off: Double) = Anchor(at, off)

    @Test fun no_anchors_means_no_offset() {
        assertEquals(0.0, SyncMath.offsetAt(emptyList(), 50.0), 0.0)
        assertEquals(0, SyncMath.currentLine(lines, emptyList(), 11.0))
        assertNull(SyncMath.currentLine(lines, emptyList(), 15.0))
    }

    @Test fun anchor_applies_from_its_time_and_shifts_lines() {
        val an = listOf(a(10.0, 1.5))
        assertEquals(0.0, SyncMath.offsetAt(an, 9.9), 0.0)
        assertEquals(1.5, SyncMath.offsetAt(an, 10.0), 0.0)
        assertNull(SyncMath.currentLine(lines, an, 11.0)) // line a now shows 11.5-13.5
        assertEquals(0, SyncMath.currentLine(lines, an, 12.0))
    }

    @Test fun normalize_sorts_dedupes_and_rounds() {
        val n = SyncMath.normalize(listOf(a(30.0, 1.0), a(10.0, 0.12345), a(30.0, 2.0)))
        assertEquals(listOf(a(10.0, 0.123), a(30.0, 2.0)), n)
    }

    @Test fun nudge_without_anchor_creates_one_at_the_line_start() {
        val r = SyncMath.nudge(emptyList(), 11.0, 10.0, 0.25)
        assertEquals(listOf(a(10.0, 0.25)), r)
    }

    @Test fun nudge_within_30s_changes_the_existing_anchor() {
        val r = SyncMath.nudge(listOf(a(10.0, 0.25)), 39.0, 20.0, 0.25)
        assertEquals(listOf(a(10.0, 0.5)), r)
    }

    @Test fun nudge_after_30s_adds_a_new_anchor_carrying_the_old_offset() {
        val r = SyncMath.nudge(listOf(a(10.0, 0.25)), 41.0, 100.0, -0.25)
        assertEquals(listOf(a(10.0, 0.25), a(100.0, 0.0)), r)
        val exactly = SyncMath.nudge(listOf(a(10.0, 0.25)), 40.0, 20.0, 0.25) // exactly 30 s: still the same anchor
        assertEquals(listOf(a(10.0, 0.5)), exactly)
    }

    @Test fun nudge_with_no_current_line_anchors_at_now() {
        assertEquals(listOf(a(50.0, -0.25)), SyncMath.nudge(emptyList(), 50.0, null, -0.25))
    }

    @Test fun sync_to_line_sets_offset_so_the_line_plays_now() {
        val r = SyncMath.syncToLine(emptyList(), 20.0, 21.4)
        assertEquals(listOf(a(20.0, 1.4)), r)
        assertEquals(1, SyncMath.currentLine(lines, r, 21.4))
        // replacing an anchor at the same time
        assertEquals(listOf(a(20.0, 2.0)), SyncMath.syncToLine(r, 20.0, 22.0))
    }

    @Test fun reset_here_removes_only_the_anchor_in_effect() {
        val an = listOf(a(10.0, 1.0), a(100.0, 2.0))
        assertEquals(listOf(a(10.0, 1.0)), SyncMath.resetHere(an, 150.0))
        assertEquals(listOf(a(100.0, 2.0)), SyncMath.resetHere(an, 50.0))
        assertEquals(an, SyncMath.resetHere(an, 5.0))
        assertTrue(SyncMath.resetAll().isEmpty())
    }

    @Test fun next_line_and_validation() {
        assertEquals(1, SyncMath.nextLine(lines, emptyList(), 15.0))
        assertNull(SyncMath.nextLine(lines, emptyList(), 101.0))
        assertTrue(!SyncMath.isValid(listOf(a(-1.0, 0.0))))
        assertTrue(!SyncMath.isValid(listOf(a(1.0, Double.NaN))))
    }
}
