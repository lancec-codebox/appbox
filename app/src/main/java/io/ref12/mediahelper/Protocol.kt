package io.ref12.mediahelper

import org.json.JSONArray
import org.json.JSONObject

val DEFAULT_LANGUAGES = listOf("zh-Hans", "zh-CN", "zh", "zh-Hant", "zh-TW")

val DEFAULT_ORIGINS = listOf(
    "https://apps--hexad-project.ref12.dev",
    "https://ref12.github.io",
    "http://localhost:5173",
)

const val SERVER_PORT = 38917

enum class JobState(val wire: String) {
    QUEUED("queued"), FETCHING("fetching"), DONE("done"), FAILED("failed");

    val finished get() = this == DONE || this == FAILED
}

data class Line(val start: Double, val end: Double, val text: String)

data class Transcript(
    val url: String,
    val title: String,
    val durationSec: Int?,
    val extractor: String,
    val videoId: String,
    val source: String, // "captions" | "auto-captions"
    val language: String,
    val lines: List<Line>,
) {
    fun toJson(): JSONObject = JSONObject().apply {
        put("url", url)
        put("title", title)
        if (durationSec != null) put("durationSec", durationSec)
        put("extractor", extractor)
        put("videoId", videoId)
        put("source", source)
        put("language", language)
        put("lines", JSONArray().also { arr ->
            for (l in lines) arr.put(
                JSONObject().put("start", round3(l.start)).put("end", round3(l.end)).put("text", l.text)
            )
        })
    }

    private fun round3(v: Double) = Math.round(v * 1000.0) / 1000.0
}

/** Thrown for failures with a message that can be shown to the user as is. */
class FetchException(message: String) : Exception(message)

/** The site or yt-dlp itself failed (as opposed to "no captions"); worth an update and one retry. */
class YtDlpFailure(message: String) : Exception(message)
