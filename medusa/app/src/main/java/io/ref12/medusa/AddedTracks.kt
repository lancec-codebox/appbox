package io.ref12.medusa

import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest

/** A body or file that is not an acceptable subtitle track; the message is shown to the caller as is. */
class TrackRejected(message: String) : Exception(message)

/** A validated producer-supplied track. */
data class AddedTrack(val language: String, val label: String?, val origin: String, val lines: List<Line>, val source: String = TrackInfo.SOURCE_ADDED) {
    /** Same for the same language + origin, so adding again replaces the track and keeps its id. */
    val trackId: String get() = idFor(language, origin)

    companion object {
        fun idFor(language: String, origin: String): String =
            "t-" + MessageDigest.getInstance("SHA-256").digest((language.lowercase() + "|" + origin).toByteArray()).take(5).joinToString("") { "%02x".format(it) }
    }
}

/** What an import file holds: the track plus whatever it says about its media and language. */
data class ImportedFile(val lines: List<Line>, val language: String?, val label: String?, val origin: String?, val mediaId: String?, val url: String?)

object TrackRules {
    const val MAX_BYTES = 2_000_000
    const val MAX_LINES = 20_000
    const val MAX_TEXT = 2_000
    private val LANGUAGE = Regex("[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*")
    private val ORIGIN = Regex("[A-Za-z0-9._-]{1,40}")
    private val MARKUP = Regex("<[A-Za-z/!][^>]*>")
    private val CONTROL = Regex("[\\u0000-\\u0008\\u000B\\u000C\\u000E-\\u001F\\u007F]")

    /** The body of POST /v1/media/{id}/tracks (and of a *.medusa-track.json file, which may add mediaId or url). */
    fun parse(j: JSONObject): AddedTrack {
        val language = j.optString("language", "").trim()
        if (!LANGUAGE.matches(language)) throw TrackRejected("language must be a language tag such as \"en\" or \"pt-BR\"")
        if (j.has("source") && !j.isNull("source") && j.optString("source") !in listOf("added", "ocr")) throw TrackRejected("source must be \"added\" or \"ocr\"")
        val label = if (j.has("label") && !j.isNull("label")) j.optString("label").trim().also {
            if (it.length > 60 || CONTROL.containsMatchIn(it) || MARKUP.containsMatchIn(it)) throw TrackRejected("label must be plain text of at most 60 characters")
        }.ifEmpty { null } else null
        val origin = if (j.has("origin") && !j.isNull("origin")) j.optString("origin").trim().also {
            if (!ORIGIN.matches(it)) throw TrackRejected("origin must be 1-40 characters of letters, digits, . _ -")
        } else ""
        val arr = j.optJSONArray("lines") ?: throw TrackRejected("lines must be an array of {start, end, text}")
        return AddedTrack(language, label, origin, lines(arr), if (j.optString("source") == "ocr") TrackInfo.SOURCE_OCR else TrackInfo.SOURCE_ADDED)
    }

    fun lines(arr: JSONArray): List<Line> {
        if (arr.length() == 0) throw TrackRejected("lines is empty")
        if (arr.length() > MAX_LINES) throw TrackRejected("too many lines (at most $MAX_LINES)")
        val out = ArrayList<Line>(arr.length())
        var prev = 0.0
        for (i in 0 until arr.length()) {
            val o = arr.optJSONObject(i) ?: throw TrackRejected("line $i is not an object")
            val s = o.optDouble("start", Double.NaN)
            val e = o.optDouble("end", Double.NaN)
            val t = if (o.has("text") && o.get("text") is String) o.getString("text") else throw TrackRejected("line $i: text must be a string")
            if (s.isNaN() || e.isNaN() || s.isInfinite() || e.isInfinite()) throw TrackRejected("line $i: start and end must be numbers")
            if (s < 0) throw TrackRejected("line $i: start must not be negative")
            if (e <= s) throw TrackRejected("line $i: end must be greater than start")
            if (s < prev) throw TrackRejected("line $i: lines must be sorted by start")
            if (t.isBlank()) throw TrackRejected("line $i: text is empty")
            if (t.length > MAX_TEXT) throw TrackRejected("line $i: text is longer than $MAX_TEXT characters")
            if (CONTROL.containsMatchIn(t)) throw TrackRejected("line $i: text has control characters")
            if (MARKUP.containsMatchIn(t)) throw TrackRejected("line $i: text must be plain text, without markup")
            prev = s
            out.add(Line(SyncMath.round3(s), SyncMath.round3(e), t))
        }
        return out
    }

    fun clean(text: String): String = text.replace(MARKUP, "").replace(Regex("\\{\\\\an?\\d\\}"), "")
        .replace("&nbsp;", " ").replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&amp;", "&")
        .replace(CONTROL, "").lines().joinToString("\n") { it.trim() }.trim()
}

/** Reads .srt, .vtt and *.medusa-track.json files. */
object SubtitleFiles {
    private const val TS = "(?:(\\d+):)?(\\d{1,2}):(\\d{2})[.,](\\d{1,3})"
    private val CUE = Regex("^\\s*$TS\\s*-->\\s*$TS")

    private fun secs(h: String, m: String, s: String, ms: String) =
        (h.ifEmpty { "0" }.toInt() * 3600 + m.toInt() * 60 + s.toInt()) + ms.padEnd(3, '0').toInt() / 1000.0

    /** SRT and WebVTT share the cue syntax; WebVTT adds a header, NOTE/STYLE/REGION blocks and cue settings. */
    fun parseCues(raw: String): List<Line> {
        val text = raw.removePrefix("\uFEFF").replace("\r\n", "\n").replace('\r', '\n')
        val out = ArrayList<Line>()
        for (block in text.split(Regex("\n[ \t]*\n"))) {
            val ls = block.lines()
            val i = ls.indexOfFirst { it.contains("-->") }
            if (i < 0 || ls[0].startsWith("NOTE") || ls[0].startsWith("STYLE") || ls[0].startsWith("REGION")) continue
            val m = CUE.find(ls[i]) ?: continue
            val g = m.groupValues
            val start = secs(g[1], g[2], g[3], g[4])
            val end = secs(g[5], g[6], g[7], g[8])
            val t = TrackRules.clean(ls.drop(i + 1).joinToString("\n"))
            if (t.isEmpty() || end <= start) continue
            out.add(Line(SyncMath.round3(start), SyncMath.round3(end), t))
        }
        if (out.isEmpty()) throw TrackRejected("No subtitle lines found in this file")
        return out.sortedBy { it.start }.take(TrackRules.MAX_LINES)
    }

    /** "movie.en.srt" -> "en" (null when the name has no language tag). */
    fun languageFromName(name: String): String? {
        val parts = name.substringBeforeLast('.').split('.')
        return parts.drop(1).lastOrNull()?.takeIf { Regex("[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*").matches(it) }
    }

    fun parse(name: String, content: String): ImportedFile {
        if (content.length > TrackRules.MAX_BYTES) throw TrackRejected("This file is larger than 2 MB")
        val lower = name.lowercase()
        val body = content.removePrefix("\uFEFF").trimStart()
        if (lower.endsWith(".json") || body.startsWith("{")) {
            val j = try { JSONObject(body) } catch (e: Exception) { throw TrackRejected("This is not a valid Medusa track file") }
            val t = TrackRules.parse(j)
            return ImportedFile(t.lines, t.language, t.label, t.origin.ifEmpty { null },
                j.optString("mediaId", "").ifEmpty { null }, j.optString("url", "").ifEmpty { null })
        }
        return ImportedFile(parseCues(content), languageFromName(name), null, null, null, null)
    }
}

/** Which library item an import file belongs to. */
object ImportMatch {
    /** By mediaId, else by video URL (any spelling of it). Null when the file says nothing that matches. */
    fun find(items: List<MediaMeta>, mediaId: String?, url: String?): MediaMeta? {
        if (!mediaId.isNullOrEmpty()) items.firstOrNull { it.mediaId == mediaId }?.let { return it }
        if (!url.isNullOrEmpty()) {
            val c = UrlNormalizer.canonical(url)
            return items.firstOrNull { UrlNormalizer.canonical(it.url) == c }
        }
        return null
    }
}

enum class ExportFormat(val ext: String, val mime: String) {
    SRT("srt", "application/x-subrip"), VTT("vtt", "text/vtt"), JSON("medusa-track.json", "application/json");
}

/** Writes a track as a file other apps can read; SRT and JSON come back through [SubtitleFiles] unchanged. */
object TrackExport {
    private fun ts(sec: Double, comma: Boolean): String {
        val ms = Math.round(sec * 1000)
        return "%02d:%02d:%02d%s%03d".format(ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, if (comma) "," else ".", ms % 1000)
    }

    fun srt(lines: List<Line>) = lines.mapIndexed { i, l -> "${i + 1}\n${ts(l.start, true)} --> ${ts(l.end, true)}\n${l.text}\n" }.joinToString("\n")

    fun vtt(lines: List<Line>) = "WEBVTT\n\n" + lines.joinToString("\n") { "${ts(it.start, false)} --> ${ts(it.end, false)}\n${it.text}\n" }

    fun json(m: MediaMeta, language: String, label: String?, origin: String?, lines: List<Line>, source: String = "added"): String = JSONObject()
        .put("mediaId", m.mediaId).put("url", m.url).put("title", m.title).put("language", language)
        .also { o -> label?.let { o.put("label", it) }; if (!origin.isNullOrEmpty()) o.put("origin", origin) }
        .put("source", source).put("lines", JSONArray().also { a -> for (l in lines) a.put(JSONObject().put("start", l.start).put("end", l.end).put("text", l.text)) })
        .toString(2)

    fun content(f: ExportFormat, m: MediaMeta, t: TrackInfo, lines: List<Line>) = when (f) {
        ExportFormat.SRT -> srt(lines)
        ExportFormat.VTT -> vtt(lines)
        ExportFormat.JSON -> json(m, t.language, t.name, t.origin, lines, if (t.ocr) "ocr" else "added")
    }

    /** "<title> [<language>].<ext>" with characters files cannot have removed. */
    fun fileName(title: String, language: String, f: ExportFormat): String {
        fun safe(s: String) = s.replace(Regex("[\\\\/:*?\"<>|\\u0000-\\u001F]"), " ").replace(Regex("\\s+"), " ").trim().trim('.').take(80)
        val t = safe(title).ifEmpty { "subtitles" }
        return "$t [${safe(language).ifEmpty { "und" }}].${f.ext}"
    }
}
