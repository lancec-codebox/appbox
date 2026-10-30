package io.ref12.medusa

import org.junit.Assert.*
import org.junit.Test

class FullscreenOrientationTest {
    private fun pick(w: Int, h: Int, r: Int = 0, par: Float = 1f) = FullscreenOrientation.choose(FullscreenOrientation.displayAspect(w, h, r, par))

    @Test fun wideIsLandscape() = assertEquals(FsOrientation.LANDSCAPE, pick(1920, 1080))
    @Test fun tallIsPortrait() = assertEquals(FsOrientation.PORTRAIT, pick(1080, 1920))
    @Test fun nearSquareKeeps() { assertEquals(FsOrientation.KEEP, pick(1000, 1000)); assertEquals(FsOrientation.KEEP, pick(1080, 1000)); assertEquals(FsOrientation.KEEP, pick(900, 1000)) }
    @Test fun rotationSwaps() = assertEquals(FsOrientation.PORTRAIT, pick(1920, 1080, 90))
    @Test fun pixelAspectApplies() = assertEquals(FsOrientation.LANDSCAPE, pick(1000, 1200, 0, 1.5f))
    @Test fun unknownKeeps() = assertEquals(FsOrientation.KEEP, FullscreenOrientation.choose(FullscreenOrientation.displayAspect(0, 0)))
}
