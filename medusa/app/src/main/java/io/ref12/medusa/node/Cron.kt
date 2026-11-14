package io.ref12.medusa.node

import java.time.Duration
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.ZonedDateTime

/** A 5-field cron expression (minute hour day-of-month month day-of-week): *, lists, ranges, steps; day-of-week 0-7 with 7 = 0 (Sunday). */
class Cron private constructor(
    private val minutes: BooleanArray, private val hours: BooleanArray, private val doms: BooleanArray, private val months: BooleanArray, private val dows: BooleanArray,
    private val domStar: Boolean, private val dowStar: Boolean, val text: String,
) {
    private fun dayMatches(d: LocalDate): Boolean {
        if (!months[d.monthValue]) return false
        val dom = doms[d.dayOfMonth]; val dow = dows[d.dayOfWeek.value % 7] // Monday 1 .. Sunday 7 -> 0
        // as in classic cron: when both day fields are restricted, either one matches
        return if (!domStar && !dowStar) dom || dow else dom && dow
    }

    fun matches(t: LocalDateTime) = minutes[t.minute] && hours[t.hour] && dayMatches(t.toLocalDate())

    /** The latest cron time at or before [t], looking back at most [maxBack]; null when there is none. */
    fun lastAtOrBefore(t: ZonedDateTime, maxBack: Duration): ZonedDateTime? {
        val zone = t.zone
        var c = t.toLocalDateTime().withSecond(0).withNano(0)
        val limit = c.minus(maxBack)
        while (!c.isBefore(limit)) {
            if (!dayMatches(c.toLocalDate())) { c = c.toLocalDate().atStartOfDay().minusMinutes(1); continue }
            if (!hours[c.hour]) { c = c.withMinute(0).minusMinutes(1); continue }
            if (minutes[c.minute]) return ZonedDateTime.of(c, zone)
            c = c.minusMinutes(1)
        }
        return null
    }

    /** The first cron time strictly after [t] (within a few years), or null for an expression that never fires (31 February). */
    fun nextAfter(t: ZonedDateTime): ZonedDateTime? {
        val zone = t.zone
        var c = t.toLocalDateTime().withSecond(0).withNano(0).plusMinutes(1)
        val limit = c.plusYears(8)
        while (c.isBefore(limit)) {
            if (!dayMatches(c.toLocalDate())) { c = c.toLocalDate().plusDays(1).atStartOfDay(); continue }
            if (!hours[c.hour]) { c = c.withMinute(0).plusHours(1); continue }
            if (minutes[c.minute]) return ZonedDateTime.of(c, zone)
            c = c.plusMinutes(1)
        }
        return null
    }

    companion object {
        /** Null when [expr] is not a valid 5-field expression. */
        fun parse(expr: String?): Cron? {
            val f = expr?.trim()?.split(Regex("\\s+")) ?: return null
            if (f.size != 5) return null
            val m = field(f[0], 0, 59) ?: return null
            val h = field(f[1], 0, 23) ?: return null
            val dm = field(f[2], 1, 31) ?: return null
            val mo = field(f[3], 1, 12) ?: return null
            val dwRaw = field(f[4], 0, 7) ?: return null
            val dw = BooleanArray(8).also { for (i in 0..7) if (dwRaw[i]) it[i % 7] = true } // 7 is Sunday too
            return Cron(m, h, dm, mo, dw, f[2].startsWith("*"), f[4].startsWith("*"), f.joinToString(" "))
        }

        private fun field(s: String, lo: Int, hi: Int): BooleanArray? {
            val out = BooleanArray(maxOf(hi + 1, 8))
            for (item in s.split(',')) {
                if (item.isEmpty()) return null
                val parts = item.split('/')
                if (parts.size > 2) return null
                val step = if (parts.size == 2) (parts[1].toIntOrNull() ?: return null) else 1
                if (step < 1) return null
                val range = parts[0]
                val from: Int; val to: Int
                if (range == "*") { from = lo; to = hi }
                else if (range.contains('-')) {
                    val ab = range.split('-'); if (ab.size != 2) return null
                    from = ab[0].toIntOrNull() ?: return null; to = ab[1].toIntOrNull() ?: return null
                } else {
                    from = range.toIntOrNull() ?: return null
                    to = if (parts.size == 2) hi else from // "5/15" runs from 5 to the end
                }
                if (from < lo || to > hi || from > to) return null
                var v = from
                while (v <= to) { out[v] = true; v += step }
            }
            return out
        }
    }
}

/** "3h", "90m", "1d", "1h30m": a duration in ms, or null. */
object Durations {
    fun parse(s: String?): Long? {
        val t = s?.trim()?.lowercase() ?: return null
        if (!Regex("(\\d+[dhm])+").matches(t)) return null
        var ms = 0L
        for (m in Regex("(\\d+)([dhm])").findAll(t)) ms += m.groupValues[1].toLong() * when (m.groupValues[2]) { "d" -> 86_400_000L; "h" -> 3_600_000L; else -> 60_000L }
        return ms.takeIf { it > 0 }
    }
}

/** A cron schedule plus a window: claiming is allowed from each cron time for [windowMs]. */
class Schedule(val cron: Cron, val windowMs: Long) {
    /** The start of the window [now] is in, or null when it is outside every window. */
    fun windowStart(now: Long, zone: ZoneId): ZonedDateTime? {
        val t = Instant(now, zone)
        val s = cron.lastAtOrBefore(t, Duration.ofMillis(windowMs)) ?: return null
        return if (now < s.toInstant().toEpochMilli() + windowMs) s else null
    }
    fun isOpen(now: Long, zone: ZoneId) = windowStart(now, zone) != null
    /** The next time a window starts after [now]. */
    fun nextStart(now: Long, zone: ZoneId): Long? = cron.nextAfter(Instant(now, zone))?.toInstant()?.toEpochMilli()

    private fun Instant(ms: Long, zone: ZoneId) = java.time.Instant.ofEpochMilli(ms).atZone(zone)

    companion object {
        /** The default window when only a schedule is set. */
        const val DEFAULT_WINDOW_MS = 3_600_000L
        fun of(cron: String?, window: String?): Schedule? {
            val c = Cron.parse(cron) ?: return null
            return Schedule(c, Durations.parse(window) ?: DEFAULT_WINDOW_MS)
        }
    }
}
