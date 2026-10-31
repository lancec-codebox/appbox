package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Test

class OcrProgressTest {
    @Test fun hms() {
        assertEquals("0:00:00", Ui.hms(0.0))
        assertEquals("0:00:59", Ui.hms(59.0))
        assertEquals("1:00:00", Ui.hms(3600.0))
        assertEquals("1:23:45", Ui.hms(5025.9))
    }

    private fun st(dur: Double) = OcrState(mediaId = "m", phase = OcrState.Phase.RUNNING, positionSec = 2467.0, durationSec = dur, samples = 10, ocrCalls = 4, cues = 3, etaSec = 750.0)

    @Test fun progressWithDuration() {
        assertEquals("0:41:07 / 1:23:45 (49%)  \u00b7  10 frames, 4 read  \u00b7  3 lines  \u00b7  about 12:30 left", OcrUi.progressText(st(5025.0)))
        assertEquals("0:41:07 / 1:23:45 (49%)", OcrUi.notificationText(st(5025.0)))
    }

    @Test fun progressWithoutDuration() {
        assertEquals("0:41:07  \u00b7  10 frames, 4 read  \u00b7  3 lines  \u00b7  about 12:30 left", OcrUi.progressText(st(0.0)))
    }
}
