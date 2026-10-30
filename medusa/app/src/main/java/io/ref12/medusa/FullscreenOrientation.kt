package io.ref12.medusa

/** Which orientation fullscreen should use for a video. */
enum class FsOrientation { LANDSCAPE, PORTRAIT, KEEP }

object FullscreenOrientation {
    /** Aspect (width/height) as displayed: pixel aspect applied, 90/270 rotation swapped. Null when the size is unknown. */
    fun displayAspect(width: Int, height: Int, rotationDegrees: Int = 0, pixelAspect: Float = 1f): Double? {
        if (width <= 0 || height <= 0) return null
        val par = if (pixelAspect > 0f) pixelAspect.toDouble() else 1.0
        val a = width * par / height
        return if (((rotationDegrees % 180) + 180) % 180 == 90) 1.0 / a else a
    }

    /** Wider than tall: landscape; taller: portrait; 0.9..1.1 or unknown: keep the current orientation. */
    fun choose(aspect: Double?): FsOrientation = when {
        aspect == null -> FsOrientation.KEEP
        aspect in 0.9..1.1 -> FsOrientation.KEEP
        aspect > 1.0 -> FsOrientation.LANDSCAPE
        else -> FsOrientation.PORTRAIT
    }
}
