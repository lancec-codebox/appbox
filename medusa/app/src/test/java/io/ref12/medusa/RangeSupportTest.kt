package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Test

class RangeSupportTest {
    private fun p(h: String?, size: Long = 1000) = RangeSupport.parse(h, size)

    @Test fun no_or_foreign_header_is_full() {
        assertEquals(RangeResult.Full, p(null))
        assertEquals(RangeResult.Full, p("items=0-5"))
        assertEquals(RangeResult.Full, p("bytes=0-5,10-20"))
    }

    @Test fun normal_open_and_clamped_ranges() {
        assertEquals(RangeResult.Partial(0, 99), p("bytes=0-99"))
        assertEquals(RangeResult.Partial(500, 999), p("bytes=500-"))
        assertEquals(RangeResult.Partial(900, 999), p("bytes=900-5000"))
        assertEquals(100L, (p("bytes=0-99") as RangeResult.Partial).length)
    }

    @Test fun suffix_ranges() {
        assertEquals(RangeResult.Partial(950, 999), p("bytes=-50"))
        assertEquals(RangeResult.Partial(0, 999), p("bytes=-5000"))
        assertEquals(RangeResult.Unsatisfiable, p("bytes=-0"))
    }

    @Test fun unsatisfiable() {
        assertEquals(RangeResult.Unsatisfiable, p("bytes=1000-"))
        assertEquals(RangeResult.Unsatisfiable, p("bytes=0-5", 0))
    }

    @Test fun inverted_range_is_ignored() {
        assertEquals(RangeResult.Full, p("bytes=50-10"))
    }
}
