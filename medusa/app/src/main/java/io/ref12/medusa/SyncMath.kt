package io.ref12.medusa

import kotlin.math.abs

/** Subtitle sync as a list of anchors (protocol section 3). All functions are pure. */
object SyncMath {
    const val STEP = 0.25
    const val NEW_ANCHOR_AFTER_SEC = 30.0

    fun round3(v: Double) = Math.round(v * 1000.0) / 1000.0

    /** Sorted, one anchor per time, rounded to milliseconds. Later duplicates win. */
    fun normalize(anchors: List<Anchor>): List<Anchor> {
        val out = ArrayList<Anchor>()
        for (a in anchors.map { Anchor(round3(it.at), round3(it.offset)) }.sortedBy { it.at }) {
            if (out.isNotEmpty() && out.last().at == a.at) out[out.size - 1] = a else out.add(a)
        }
        return out
    }

    /** The anchor in effect at [t], null before the first one. */
    fun anchorAt(anchors: List<Anchor>, t: Double): Anchor? = anchors.lastOrNull { it.at <= t }

    fun offsetAt(anchors: List<Anchor>, t: Double): Double = anchorAt(anchors, t)?.offset ?: 0.0

    fun shownStart(anchors: List<Anchor>, l: Line) = l.start + offsetAt(anchors, l.start)
    fun shownEnd(anchors: List<Anchor>, l: Line) = l.end + offsetAt(anchors, l.start)

    /** Index of the line shown at playback time [t] (the latest started one when several overlap), or null. */
    fun currentLine(lines: List<Line>, anchors: List<Anchor>, t: Double): Int? {
        var best: Int? = null
        var bestStart = Double.NEGATIVE_INFINITY
        for (i in lines.indices) {
            val s = shownStart(anchors, lines[i])
            if (s <= t && t < shownEnd(anchors, lines[i]) && s >= bestStart) { best = i; bestStart = s }
        }
        return best
    }

    /** The first line that will be shown after [t], or null. */
    fun nextLine(lines: List<Line>, anchors: List<Anchor>, t: Double): Int? {
        var best: Int? = null
        var bestStart = Double.POSITIVE_INFINITY
        for (i in lines.indices) {
            val s = shownStart(anchors, lines[i])
            if (s > t && s < bestStart) { best = i; bestStart = s }
        }
        return best
    }

    /**
     * Earlier / Later. Changes the anchor in effect at [now]; when that anchor is more than 30 s old
     * (or there is none) a new anchor goes at [lineStart] (the current line's start, [now] when there is no line).
     */
    fun nudge(anchors: List<Anchor>, now: Double, lineStart: Double?, delta: Double): List<Anchor> {
        val cur = anchorAt(anchors, now)
        if (cur != null && now - cur.at <= NEW_ANCHOR_AFTER_SEC) {
            return normalize(anchors.map { if (it === cur) Anchor(it.at, it.offset + delta) else it })
        }
        val at = lineStart ?: now
        return normalize(anchors + Anchor(at, offsetAt(anchors, at) + delta))
    }

    /** "Sync to this line": the line starting at [lineStart] is heard at [now]. */
    fun syncToLine(anchors: List<Anchor>, lineStart: Double, now: Double): List<Anchor> =
        normalize(anchors + Anchor(lineStart, now - lineStart))

    /** Reset sync here: drops the anchor in effect at [now]. */
    fun resetHere(anchors: List<Anchor>, now: Double): List<Anchor> {
        val cur = anchorAt(anchors, now) ?: return anchors
        return anchors.filter { it !== cur }
    }

    fun resetAll(): List<Anchor> = emptyList()

    fun isValid(anchors: List<Anchor>) = anchors.size <= 1000 &&
        anchors.all { it.at.isFinite() && it.offset.isFinite() && it.at >= 0 && abs(it.offset) < 36000 }
}
