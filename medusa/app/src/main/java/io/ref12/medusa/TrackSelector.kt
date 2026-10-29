package io.ref12.medusa

import org.json.JSONArray
import org.json.JSONObject

data class Track(
    /** The key yt-dlp lists it under (what --sub-langs takes). */
    val key: String,
    /** The language to report: the key without yt-dlp's "-orig" marker. */
    val language: String,
    val source: String, // "captions" | "auto-captions"
    val ext: String, // "json3" | "vtt"
    val name: String? = null,
) {
    fun info() = TrackInfo(language, source, name)
}

object TrackSelector {
    private val PREFERRED_EXT = listOf("json3", "vtt")

    /**
     * Every subtitle track the video offers: manual captions, then auto-captions. Auto-captions are only the
     * spoken language (YouTube lists machine translations under every language code, which are left out).
     */
    fun list(info: JSONObject): List<Track> {
        val manual = candidates(info.optJSONObject("subtitles"), "captions", false, null)
        val auto = info.optJSONObject("automatic_captions")
        val hasOrig = auto != null && auto.keys().asSequence().any { it.endsWith("-orig") }
        val autoTracks = candidates(auto, "auto-captions", hasOrig, if (hasOrig) null else info.optString("language", "").ifEmpty { null })
        // an auto track in a language that has real captions adds nothing
        return manual + autoTracks.filter { a -> manual.none { it.language.equals(a.language, true) } }
    }

    /** The video's spoken language: the "-orig" auto-caption track, else the language yt-dlp reports. */
    fun spokenLanguage(info: JSONObject): String? {
        val auto = info.optJSONObject("automatic_captions")
        auto?.keys()?.asSequence()?.filter { it.endsWith("-orig") }?.sorted()?.firstOrNull()?.let { return it.removeSuffix("-orig") }
        return info.optString("language", "").ifEmpty { null }
    }

    /** The original spoken language: manual captions first, then that language's auto-captions. */
    fun original(info: JSONObject): Track? {
        val all = list(info)
        val spoken = spokenLanguage(info)
        if (spoken == null) return all.firstOrNull { it.source == "captions" } ?: all.firstOrNull()
        return all.firstOrNull { it.source == "captions" && languageMatches(it.language, spoken) }
            ?: all.firstOrNull { it.source == "auto-captions" && languageMatches(it.language, spoken) }
    }

    /** No languages: the original language. Otherwise the first requested language with a track, manual captions before auto. */
    fun select(info: JSONObject, languages: List<String>): Track? {
        if (languages.isEmpty()) return original(info)
        val all = list(info)
        for (source in listOf("captions", "auto-captions")) {
            val of = all.filter { it.source == source }
            for (lang in languages) {
                of.firstOrNull { it.language.equals(lang, true) }?.let { return it }
                of.firstOrNull { compatible(it.language, lang) }?.let { return it }
            }
        }
        return null
    }

    /** One specific track by language (manual captions preferred). */
    fun find(info: JSONObject, language: String): Track? = select(info, listOf(language))

    fun languageMatches(a: String, b: String) = a.equals(b, true) || compatible(a, b)

    fun noCaptionsMessage(requested: List<String>, tracks: List<TrackInfo>): String {
        val have = tracks.map { it.language }.distinct()
        val wanted = if (requested.isEmpty()) "the video's original language" else requested.joinToString(", ")
        return if (have.isEmpty()) "No captions found for this video"
        else "No captions in $wanted (available: " + have.joinToString(", ") + ")"
    }

    private fun candidates(tracks: JSONObject?, source: String, onlyOrig: Boolean, onlyLanguage: String?): List<Track> {
        if (tracks == null) return emptyList()
        val out = ArrayList<Track>()
        for (key in tracks.keys().asSequence().sorted()) {
            if (key == "live_chat") continue
            if (onlyOrig && !key.endsWith("-orig")) continue
            val lang = key.removeSuffix("-orig")
            if (onlyLanguage != null && !languageMatches(lang, onlyLanguage)) continue
            val formats = tracks.optJSONArray(key)
            val ext = usableExt(formats) ?: continue
            out.add(Track(key, lang, source, ext, nameOf(formats)))
        }
        return out
    }

    /** "zh" and "zh-Hans" are the same language family; "zh-Hans" and "zh-Hant" are not. */
    private fun compatible(a: String, b: String): Boolean {
        val x = a.lowercase()
        val y = b.lowercase()
        return x.startsWith("$y-") || y.startsWith("$x-")
    }

    private fun nameOf(formats: JSONArray?): String? {
        if (formats == null) return null
        for (i in 0 until formats.length()) formats.optJSONObject(i)?.optString("name", "")?.takeIf { it.isNotEmpty() }?.let { return it }
        return null
    }

    private fun usableExt(formats: JSONArray?): String? {
        if (formats == null) return null
        val exts = (0 until formats.length()).mapNotNull { formats.optJSONObject(it)?.optString("ext") }
        return PREFERRED_EXT.firstOrNull { it in exts }
    }
}
