package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ProgressMeterTest {
    @Test fun two_streams_map_to_0_to_1_and_never_go_back() {
        val m = ProgressMeter(2)
        assertEquals(0.25, m.update(50.0), 1e-9)
        assertEquals(0.495, m.update(99.0), 1e-9)
        val second = m.update(5.0) // the audio stream starts over
        assertTrue(second >= 0.495)
        assertEquals(0.75, m.update(50.0), 1e-9)
        assertTrue(m.update(100.0) <= 0.99)
    }

    @Test fun values_are_clamped() {
        val m = ProgressMeter(1)
        assertEquals(0.0, m.update(-5.0), 0.0)
        assertTrue(m.update(500.0) <= 0.99)
    }
}
