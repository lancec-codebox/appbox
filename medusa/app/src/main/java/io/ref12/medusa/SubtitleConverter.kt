package io.ref12.medusa

import org.json.JSONObject

/** Converts json3 and WebVTT subtitle files to protocol lines. */
object SubtitleConverter {

    fun parse(ext: String, content: String): List<Line> = when (ext.lowercase()) {
        "json3" -> parseJson3(content)
        "vtt" -> parseVtt(content)
        else -> throw FetchException("Unsupported subtitle format: $ext")
    }

    fun parseJson3(content: String): List<Line> {
        val events = JSONObject(content).optJSONArray("events") ?: return emptyList()
        val out = ArrayList<Line>()
        for (i in 0 until events.length()) {
            val e = events.getJSONObject(i)
            val segs = e.optJSONArray("segs") ?: continue
            if (!e.has("tStartMs")) continue
            val sb = StringBuilder()
            for (j in 0 until segs.length()) sb.append(segs.getJSONObject(j).optString("utf8", ""))
            val text = joinLines(sb.toString().split('\n'))
            if (text.isEmpty()) continue
            val start = e.getLong("tStartMs") / 1000.0
            val end = start + e.optLong("dDurationMs", 0L) / 1000.0
            out.add(Line(start, end, text))
        }
        return normalize(out)
    }

    private val timing = Regex("""((?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*((?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})""")
    private val tag = Regex("<[^>]*>")

    fun parseVtt(content: String): List<Line> {
        val blocks = content.replace("\r\n", "\n").replace('\r', '\n').trim('\uFEFF', '\n').split(Regex("\n{2,}"))
        val out = ArrayList<Line>()
        var prevIdx: Map<String, Int> = emptyMap() // text of the previous cue's lines -> index in out
        for (block in blocks) {
            val rows = block.split('\n')
            val ti = rows.indexOfFirst { it.contains("-->") }
            if (ti < 0) continue
            val m = timing.find(rows[ti]) ?: continue
            val start = ts(m.groupValues[1])
            val end = ts(m.groupValues[2])
            val cueLines = rows.drop(ti + 1).map { decode(tag.replace(it, "")).trim() }.filter { it.isNotEmpty() }
            val cur = LinkedHashMap<String, Int>()
            for (text in cueLines) {
                val old = prevIdx[text]
                if (old != null) {
                    // a rolling caption repeats the line it scrolled up: extend it, do not duplicate it
                    val l = out[old]
                    out[old] = l.copy(end = maxOf(l.end, end))
                    cur[text] = old
                } else {
                    out.add(Line(start, end, text))
                    cur[text] = out.size - 1
                }
            }
            prevIdx = cur
        }
        return normalize(out)
    }

    /** Sorts out empties, back-to-back duplicates and overlaps. */
    fun normalize(lines: List<Line>): List<Line> {
        val sorted = lines.map { it.copy(text = it.text.trim()) }.filter { it.text.isNotEmpty() }.sortedBy { it.start }
        val merged = ArrayList<Line>()
        for (l in sorted) {
            val last = merged.lastOrNull()
            if (last != null && last.text == l.text && l.start <= last.end + 0.05) {
                merged[merged.size - 1] = last.copy(end = maxOf(last.end, l.end))
            } else merged.add(l)
        }
        for (i in 0 until merged.size - 1) {
            val l = merged[i]
            val next = merged[i + 1]
            if (l.end > next.start && next.start > l.start) merged[i] = l.copy(end = next.start)
            else if (l.end < l.start) merged[i] = l.copy(end = l.start)
        }
        return merged
    }

    private fun ts(s: String): Double {
        val parts = s.replace(',', '.').split(':')
        var sec = 0.0
        for (p in parts) sec = sec * 60 + p.toDouble()
        return sec
    }

    private fun decode(s: String) = s.replace("&nbsp;", " ").replace("&lt;", "<").replace("&gt;", ">")
        .replace("&quot;", "\"").replace("&#39;", "'").replace("&amp;", "&")

    /** Joins the rows of a multi-line caption: a space only between two Latin words. */
    private fun joinLines(rows: List<String>): String {
        val parts = rows.map { it.trim() }.filter { it.isNotEmpty() }
        val sb = StringBuilder()
        for (p in parts) {
            if (sb.isNotEmpty() && sb.last().code < 128 && p.first().code < 128) sb.append(' ')
            sb.append(p)
        }
        return sb.toString()
    }
}
