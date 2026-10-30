package io.ref12.medusa

import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

/*
 * The platform-free half of the burned-in subtitle extractor (1.6.0): change detection, subtitle band detection,
 * text clean-up, cue building and script detection. Everything here runs in plain unit tests.
 */

/** A grey-scale picture: [w] x [h] bytes, one per pixel (0 = black), rows top to bottom. */
class Luma(val w: Int, val h: Int, val data: ByteArray) {
    fun at(x: Int, y: Int) = data[y * w + x].toInt() and 0xFF
}

/** One recognised text line with its box (pixels of the picture it was found in) and the recogniser's confidence 0..1. */
data class OcrLine(val text: String, val confidence: Float, val left: Int, val top: Int, val right: Int, val bottom: Int)

/** The subtitle band as fractions of the picture height (0 = top, 1 = bottom). */
data class BandFrac(val top: Float, val bottom: Float) {
    fun rows(height: Int): IntRange {
        val t = (top.coerceIn(0f, 1f) * height).toInt().coerceIn(0, height - 2)
        val b = (bottom.coerceIn(0f, 1f) * height).toInt().coerceIn(t + 2, height)
        return t until b
    }
    companion object { val DEFAULT = BandFrac(0.72f, 1f) }
}

/**
 * Tells whether a subtitle band changed between two samples, from a coarse "bright pixel" map of the band.
 * Subtitle text is nearly always the brightest thing in its band, so a moving background changes the map far less than
 * a new line of text does; that is what lets almost all samples skip OCR.
 */
class ChangeDetector(private val cols: Int = 96, private val rows: Int = 16, private val brightness: Int = 190,
                     private val minCellsChanged: Int = 5, private val cellDelta: Int = 24) {
    private var last: IntArray? = null

    /** The map: per cell, how many percent of its pixels are bright (0..100). */
    fun signature(band: Luma): IntArray {
        val sig = IntArray(cols * rows)
        val counts = IntArray(cols * rows)
        for (y in 0 until band.h) {
            val cy = y * rows / band.h
            val rowStart = y * band.w
            for (x in 0 until band.w) {
                val cell = cy * cols + x * cols / band.w
                counts[cell]++
                if ((band.data[rowStart + x].toInt() and 0xFF) >= brightness) sig[cell]++
            }
        }
        for (i in sig.indices) sig[i] = if (counts[i] == 0) 0 else sig[i] * 100 / counts[i]
        return sig
    }

    /** True when [band] differs from the last one this returned true (or null was seen) for. The first band always counts as changed. */
    fun changed(band: Luma): Boolean {
        val s = signature(band)
        val prev = last
        val c = prev == null || differs(prev, s)
        if (c) last = s
        return c
    }

    private fun differs(a: IntArray, b: IntArray): Boolean {
        var n = 0
        for (i in a.indices) if (abs(a[i] - b[i]) >= cellDelta && ++n >= minCellsChanged) return true
        return false
    }

    fun reset() { last = null }
}

/** Finds the band of the picture where subtitles are. */
object BandDetector {
    /** A text box found by OCR in one sample frame (pixels of the frame). */
    data class Box(val text: String, val top: Int, val bottom: Int, val left: Int = 0, val right: Int = 0)

    /**
     * From the OCR boxes of several sample frames: the horizontal strip where text keeps turning up, ignoring text that stays
     * the same in most frames (logos, watermarks). Null when nothing clusters.
     */
    fun fromBoxes(frames: List<List<Box>>, frameH: Int): BandFrac? {
        if (frames.isEmpty() || frameH <= 0) return null
        val total = frames.size
        // text that is the same in nearly every frame is a watermark, not a subtitle
        val perText = HashMap<String, Int>()
        for (f in frames) f.map { it.text.trim() }.toSet().forEach { perText[it] = (perText[it] ?: 0) + 1 }
        val statics = if (total >= 5) perText.filter { it.value * 10 >= total * 8 }.keys else emptySet()
        val bins = 50 // 2% of the height each
        val hits = IntArray(bins)
        val usable = frames.map { f -> f.filter { it.text.trim() !in statics && it.bottom > it.top && ((it.top + it.bottom) / 2) * 100 / frameH >= 35 } }
        for (f in usable) {
            val seen = BooleanArray(bins)
            for (b in f) seen[(((b.top + b.bottom) / 2) * bins / frameH).coerceIn(0, bins - 1)] = true
            for (i in 0 until bins) if (seen[i]) hits[i]++
        }
        var peak = 0
        for (i in 1 until bins) if (hits[i] > hits[peak]) peak = i
        if (hits[peak] < max(2, total / 8)) return null
        var lo = peak; var hi = peak
        while (lo > 0 && hits[lo - 1] * 4 >= hits[peak]) lo--
        while (hi < bins - 1 && hits[hi + 1] * 4 >= hits[peak]) hi++
        val inside = usable.flatten().filter { val b = ((it.top + it.bottom) / 2) * bins / frameH; b in lo..hi }
        if (inside.isEmpty()) return null
        val tops = inside.map { it.top }.sorted(); val bottoms = inside.map { it.bottom }.sorted()
        val t = tops[tops.size * 5 / 100]; val b = bottoms[min(bottoms.size - 1, bottoms.size * 95 / 100)]
        val pad = max(4, (b - t) / 5)
        return BandFrac(((t - pad).coerceAtLeast(0)).toFloat() / frameH, ((b + pad).coerceAtMost(frameH)).toFloat() / frameH)
    }

    /**
     * A fallback that needs no OCR: where, in the lower half of several frames, there are many sharp light/dark edges
     * (text strokes). Null when no strip stands out.
     */
    fun fromLuma(frames: List<Luma>): BandFrac? {
        if (frames.isEmpty()) return null
        val h = frames[0].h; val w = frames[0].w
        val energy = DoubleArray(h)
        for (f in frames) for (y in h / 2 until h) {
            var e = 0
            val o = y * w
            for (x in 0 until w - 1) if (abs((f.data[o + x].toInt() and 0xFF) - (f.data[o + x + 1].toInt() and 0xFF)) > 60) e++
            energy[y] += e.toDouble() / frames.size / w
        }
        val peak = energy.max()
        if (peak < 0.01) return null
        val on = BooleanArray(h) { energy[it] >= peak * 0.25 && energy[it] >= 0.004 }
        var bestS = -1; var bestE = -1; var y = h / 2
        while (y < h) {
            if (!on[y]) { y++; continue }
            val s = y; var e = y; var gap = 0
            while (y < h && gap <= 3) { if (on[y]) { e = y; gap = 0 } else gap++; y++ }
            if (bestS < 0 || energy.slice(s..e).sum() > energy.slice(bestS..bestE).sum()) { bestS = s; bestE = e }
        }
        if (bestS < 0) return null
        val pad = max(3, (bestE - bestS) / 5)
        return BandFrac((bestS - pad).coerceAtLeast(0).toFloat() / h, (bestE + 1 + pad).coerceAtMost(h).toFloat() / h)
    }
}

/** Script and punctuation handling for recognised text. */
object OcrText {
    // characters whose Simplified and Traditional forms differ (one string per script, no character in both)
    private const val SIMPLIFIED = "这们个来时国说为对会学没还过关见观电话问发开样点动无现业东车书买卖马鸟鱼龙爱乐汉语谁让从处头长门间听别吗吧将进应该觉经实总样带边认听员体联难题运选两众优传务产亲儿兴写军击创刘办击岁办爷妈爸"
    private const val TRADITIONAL = "這們個來時國說為對會學沒還過關見觀電話問發開樣點動無現業東車書買賣馬鳥魚龍愛樂漢語誰讓從處頭長門間聽別嗎吧將進應該覺經實總樣帶邊認聽員體聯難題運選兩眾優傳務產親兒興寫軍擊創劉辦擊歲辦爺媽爸"
    private val simp = SIMPLIFIED.toSet() - TRADITIONAL.toSet()
    private val trad = TRADITIONAL.toSet() - SIMPLIFIED.toSet()

    fun isCjk(c: Char) = c in '\u4E00'..'\u9FFF' || c in '\u3400'..'\u4DBF'

    /** "zh-Hans" or "zh-Hant" by which script's distinguishing characters there are more of (Simplified on a tie or no evidence); "en" for text with no CJK at all. */
    fun language(text: String): String {
        var s = 0; var t = 0; var cjk = 0; var latin = 0
        for (c in text) { if (c in simp) s++; if (c in trad) t++; if (isCjk(c)) cjk++ else if (c in 'a'..'z' || c in 'A'..'Z') latin++ }
        if (cjk == 0 && latin > 0) return "en"
        return if (t > s) "zh-Hant" else "zh-Hans"
    }

    fun label(language: String) = (if (language.startsWith("zh")) "Chinese" else LanguageTools.displayName(language)) + " (from picture)"

    private fun fullToHalfAlnum(c: Char) = if (c in '\uFF10'..'\uFF19' || c in '\uFF21'..'\uFF3A' || c in '\uFF41'..'\uFF5A') (c - 0xFEE0) else c

    /** One line: width forms, spaces between CJK characters, ASCII punctuation next to CJK made full-width, ellipses and quotes unified, edge noise trimmed. */
    fun normalizeLine(raw: String): String {
        var s = raw.map { fullToHalfAlnum(it) }.joinToString("").replace('\u00A0', ' ').replace(Regex("\\s+"), " ").trim()
        s = s.replace(Regex("(?<=[\\u4E00-\\u9FFF\\u3400-\\u4DBF\\u3000-\\u303F\\uFF00-\\uFFEF]) +| +(?=[\\u4E00-\\u9FFF\\u3400-\\u4DBF\\u3000-\\u303F\\uFF00-\\uFFEF])"), "")
        s = s.replace(Regex("\\.{3,}|\u3002{2,}|\u00B7{3,}"), "\u2026").replace("\u2026\u2026", "\u2026")
        val sb = StringBuilder()
        for ((i, c) in s.withIndex()) {
            val cjkNear = (i > 0 && isCjk(s[i - 1])) || (i + 1 < s.length && isCjk(s[i + 1]))
            sb.append(if (cjkNear) when (c) { ',' -> '\uFF0C'; '!' -> '\uFF01'; '?' -> '\uFF1F'; ':' -> '\uFF1A'; ';' -> '\uFF1B'; '(' -> '\uFF08'; ')' -> '\uFF09'; else -> c } else c)
        }
        s = sb.toString().replace('\u201C', '\u300C').replace('\u201D', '\u300D').replace("\"", "")
        return s.trim { !it.isLetterOrDigit() && it !in "\u2026\u300C\u300D\uFF08\uFF09\u3010\u3011\uFF1F\uFF01\u266A" }.let { t ->
            // keep a trailing sentence mark the trim above did not remove but drop leading junk like "|" or "_"
            t
        }
    }

    /**
     * What one sample's recognition says: lines top to bottom, confident ones only, those with at least a CJK character or two
     * letters/digits, joined with line breaks. Empty string when nothing usable.
     */
    fun join(lines: List<OcrLine>, minConfidence: Float = 0.5f): String =
        lines.filter { it.confidence >= minConfidence }.sortedBy { it.top }
            .map { normalizeLine(it.text) }
            .filter { l -> l.any { isCjk(it) } || l.count { it.isLetterOrDigit() } >= 2 }
            .joinToString("\n")

    /** Levenshtein distance (single row; texts are short). */
    fun distance(a: String, b: String): Int {
        if (a == b) return 0
        var prev = IntArray(b.length + 1) { it }
        for (i in 1..a.length) {
            val cur = IntArray(b.length + 1); cur[0] = i
            for (j in 1..b.length) cur[j] = min(min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + if (a[i - 1] == b[j - 1]) 0 else 1)
            prev = cur
        }
        return prev[b.length]
    }

    /** Same subtitle despite a misread character or two: distance at most 1 per 6 characters (short texts must match exactly: "第一句" and "第二句" are different lines). */
    fun similar(a: String, b: String): Boolean {
        val x = a.replace("\n", ""); val y = b.replace("\n", "")
        if (x == y) return true
        val allowed = max(x.length, y.length) / 6
        if (allowed == 0 || abs(x.length - y.length) > allowed) return false
        return distance(x, y) <= allowed
    }
}

/**
 * Turns per-sample recognised text into cues. Feed samples in time order with [add] (empty text = no subtitle at that moment),
 * then call [finish].
 */
class CueBuilder(private val stepSec: Double, private val minSamples: Int = 2, private val gapSec: Double = 0.6) {
    private class Open(var start: Double, var last: Double) {
        val votes = LinkedHashMap<String, Int>()
        var samples = 0
        fun text() = votes.maxByOrNull { it.value }!!.key
    }

    private val done = ArrayList<Line>()
    private var cur: Open? = null

    val cues: List<Line> get() = done

    /** Start of the cue being built (so a resume can restart there), or null. */
    val openStart: Double? get() = cur?.start

    fun add(timeSec: Double, text: String) {
        val c = cur
        if (text.isEmpty()) {
            if (c != null && timeSec - c.last > gapSec) close()
            return
        }
        if (c != null && OcrText.similar(c.text(), text) && timeSec - c.last <= gapSec + stepSec) {
            c.last = timeSec; c.samples++; c.votes[text] = (c.votes[text] ?: 0) + 1
            return
        }
        close()
        cur = Open(timeSec, timeSec).also { it.samples = 1; it.votes[text] = 1 }
    }

    private fun close() {
        val c = cur ?: return
        cur = null
        if (c.samples < minSamples) return
        val end = c.last + stepSec
        done.add(Line(SyncMath.round3(c.start), SyncMath.round3(end), c.text()))
    }

    /** Closes the open cue and returns all cues, sorted, with no overlap. */
    fun finish(): List<Line> {
        close()
        return done.mapIndexed { i, l ->
            val next = done.getOrNull(i + 1)
            if (next != null && l.end > next.start) Line(l.start, SyncMath.round3(next.start), l.text) else l
        }.filter { it.end > it.start }
    }
}

/** What a pipeline run did, for progress and the performance report. */
class OcrStats {
    @Volatile var samples = 0
    @Volatile var ocrCalls = 0
    @Volatile var decodedFrames = 0
}

/** Samples in, cues out: skips unchanged bands, recognises changed ones, builds cues. */
class OcrPipeline(private val stepSec: Double, private val recognize: (Luma) -> List<OcrLine>, val stats: OcrStats = OcrStats(),
                  private val maxSkipSec: Double = 3.0, private val minConfidence: Float = 0.5f) {
    private val change = ChangeDetector()
    val builder = CueBuilder(stepSec)
    private var lastText = ""
    private var lastOcrAt = -1e9

    fun push(timeSec: Double, band: Luma) {
        stats.samples++
        val changed = change.changed(band)
        if (changed || timeSec - lastOcrAt >= maxSkipSec) {
            stats.ocrCalls++
            lastOcrAt = timeSec
            lastText = OcrText.join(recognize(band), minConfidence)
        }
        builder.add(timeSec, lastText)
    }

    fun finish(): List<Line> = builder.finish()
}
