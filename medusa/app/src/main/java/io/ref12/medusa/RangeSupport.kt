package io.ref12.medusa

/** HTTP Range header handling for the video endpoint (a single byte range). */
sealed class RangeResult {
    object Full : RangeResult()
    data class Partial(val start: Long, val end: Long) : RangeResult() { val length get() = end - start + 1 }
    object Unsatisfiable : RangeResult()
}

object RangeSupport {
    fun parse(header: String?, size: Long): RangeResult {
        val h = header?.trim() ?: return RangeResult.Full
        if (!h.startsWith("bytes=", ignoreCase = true)) return RangeResult.Full
        val spec = h.substring(6).trim()
        if (spec.isEmpty() || spec.contains(',')) return RangeResult.Full // several ranges: just send everything
        val dash = spec.indexOf('-')
        if (dash < 0) return RangeResult.Full
        val a = spec.substring(0, dash).trim()
        val b = spec.substring(dash + 1).trim()
        if (size <= 0) return RangeResult.Unsatisfiable
        if (a.isEmpty()) { // suffix: the last n bytes
            val n = b.toLongOrNull() ?: return RangeResult.Full
            if (n <= 0) return RangeResult.Unsatisfiable
            return RangeResult.Partial(maxOf(0L, size - n), size - 1)
        }
        val start = a.toLongOrNull() ?: return RangeResult.Full
        val end = if (b.isEmpty()) size - 1 else (b.toLongOrNull() ?: return RangeResult.Full)
        if (end < start) return RangeResult.Full
        if (start >= size) return RangeResult.Unsatisfiable
        return RangeResult.Partial(start, minOf(end, size - 1))
    }
}
