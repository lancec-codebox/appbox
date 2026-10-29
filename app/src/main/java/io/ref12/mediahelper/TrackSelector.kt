package io.ref12.mediahelper

import org.json.JSONArray
import org.json.JSONObject

data class Track(
    /** The key yt-dlp lists it under (what --sub-langs takes). */
    val key: String,
    /** The language to report: the key without yt-dlp's "-orig" marker. */
    val language: String,
    val source: String, // "captions" | "auto-captions"
    val ext: String, // "json3" | "vtt"
)

object TrackSelector {
    private val PREFERRED_EXT = listOf("json3", "vtt")

    /**
     * First requested language that has a usable track, manual captions before auto-captions.
     * YouTube lists machine translations of the spoken language under every language code; when
     * the info marks the spoken language ("xx-orig"), only that one counts as auto-captions.
     */
    fun select(info: JSONObject, languages: List<String>): Track? {
        pick(info.optJSONObject("subtitles"), languages, "captions", false)?.let { return it }
        val auto = info.optJSONObject("automatic_captions")
        val onlyOrig = auto != null && auto.keys().asSequence().any { it.endsWith("-orig") }
        return pick(auto, languages, "auto-captions", onlyOrig)
    }

    private fun pick(tracks: JSONObject?, languages: List<String>, source: String, onlyOrig: Boolean): Track? {
        if (tracks == null) return null
        val keys = tracks.keys().asSequence().filter { !onlyOrig || it.endsWith("-orig") }.toList()
        for (lang in languages) {
            val exact = keys.filter { it.equals(lang, ignoreCase = true) || it.removeSuffix("-orig").equals(lang, ignoreCase = true) }
            val compat = keys.filter { compatible(it.removeSuffix("-orig"), lang) }
            for (key in exact + compat) {
                val ext = usableExt(tracks.optJSONArray(key)) ?: continue
                return Track(key, key.removeSuffix("-orig"), source, ext)
            }
        }
        return null
    }

    /** "zh" and "zh-Hans" are the same language family; "zh-Hans" and "zh-Hant" are not. */
    private fun compatible(a: String, b: String): Boolean {
        val x = a.lowercase()
        val y = b.lowercase()
        return x.startsWith("$y-") || y.startsWith("$x-")
    }

    private fun usableExt(formats: JSONArray?): String? {
        if (formats == null) return null
        val exts = (0 until formats.length()).mapNotNull { formats.optJSONObject(it)?.optString("ext") }
        return PREFERRED_EXT.firstOrNull { it in exts }
    }
}
