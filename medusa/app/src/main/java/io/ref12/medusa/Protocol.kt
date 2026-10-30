package io.ref12.medusa

import java.io.File

val DEFAULT_ORIGINS = listOf(
    "https://apps--hexad-project.ref12.dev",
    "https://ref12.github.io",
    "http://localhost:5173",
)

const val SERVER_PORT = 38917

enum class JobState(val wire: String) {
    QUEUED("queued"), FETCHING("fetching"), DOWNLOADING("downloading"), DONE("done"), FAILED("failed");

    val finished get() = this == DONE || this == FAILED
}

/** Failure codes of the protocol. */
object Codes {
    const val NO_CAPTIONS = "no_captions"
    const val BOT_CHECK = "bot_check"
    const val UNSUPPORTED = "unsupported"
    const val NETWORK = "network"
    const val STORAGE = "storage"
    const val YTDLP = "ytdlp"
    const val INTERRUPTED = "interrupted"
    const val CANCELLED = "cancelled"
    const val UNKNOWN_JOB = "unknown_job"
}

enum class Quality(val wire: String, val height: Int) {
    P720("720p", 720), P480("480p", 480), P360("360p", 360);

    companion object {
        fun parse(s: String?): Quality? = entries.firstOrNull { it.wire == s }
    }
}

data class Want(val subtitles: Boolean, val video: Boolean) {
    companion object {
        val DEFAULT = Want(subtitles = true, video = true)

        /** "subtitles,video" style list; null when it names nothing known. */
        fun parse(items: List<String>?): Want? {
            if (items == null) return null
            val l = items.map { it.trim().lowercase() }.filter { it.isNotEmpty() }
            if (l.any { it != "subtitles" && it != "video" }) return null
            val w = Want("subtitles" in l, "video" in l)
            return if (w.subtitles || w.video) w else null
        }
    }
}

data class Line(val start: Double, val end: Double, val text: String)

/** One sync anchor: from [at] (video seconds, in the line-start domain) subtitles are shifted by [offset] seconds. */
data class Anchor(val at: Double, val offset: Double)

/** A subtitle track the video offers (without its lines). */
data class TrackInfo(val language: String, val source: String, val name: String? = null, val trackId: String? = null, val origin: String? = null) {
    /** What the pickers and saved choices use: the trackId of an added track, else the language. */
    val key: String get() = trackId ?: language
    /** A track whose lines are stored in the item folder (producer-supplied or extracted from the picture), as opposed to one yt-dlp downloads. */
    val added: Boolean get() = source == SOURCE_ADDED || source == SOURCE_OCR
    val ocr: Boolean get() = source == SOURCE_OCR
    companion object { const val SOURCE_ADDED = "added"; const val SOURCE_OCR = "ocr" }
}

data class SubtitlesData(val source: String, val language: String, val lines: List<Line>)

data class VideoData(val file: String, val contentType: String, val bytes: Long)

data class MediaMeta(
    val mediaId: String,
    val url: String,
    val title: String,
    val durationSec: Int?,
    val extractor: String,
    val videoId: String,
    val subtitles: SubtitlesData?,
    val video: VideoData?,
    val createdAt: Long,
    val tracks: List<TrackInfo> = emptyList(),
    /** The title in the user's language when the site offers one that differs from [title] (v1.3.1); null otherwise. */
    val localizedTitle: String? = null,
    val localizedLanguage: String? = null,
    /** Video pixel size from yt-dlp when known (v1.5.3); lets fullscreen pick its orientation before playback. */
    val width: Int? = null,
    val height: Int? = null,
)

/** Thrown for failures with a protocol code and a message that can be shown to the user as is. */
class FetchException(message: String, val code: String = Codes.YTDLP) : Exception(message)

/** yt-dlp itself failed (as opposed to "no captions"); worth an update and one retry. */
class YtDlpFailure(message: String) : Exception(message)

data class JobSubmit(
    val id: String?,
    val url: String,
    val languages: List<String>?,
    val want: Want = Want.DEFAULT,
    val quality: Quality = Quality.P720,
    val returnUrl: String? = null,
)

class CaptureRequest(
    val url: String,
    /** Preferred subtitle languages in order; empty means the video's original language. */
    val languages: List<String>,
    val wantSubtitles: Boolean,
    val wantVideo: Boolean,
    val quality: Quality,
    /** Asked once yt-dlp has named the video (extractor, id): true when that video is already stored, so it is not downloaded again. */
    /** Language (e.g. "en") to also ask the site for the title in; null = do not ask. */
    val titleLanguage: String? = null,
    val videoAlreadyStored: (String, String) -> Boolean = { _, _ -> false },
)

class CaptureOutcome(
    val title: String,
    val durationSec: Int?,
    val extractor: String,
    val videoId: String,
    val subtitles: SubtitlesData?,
    val videoFile: File?,
    val subtitlesError: FetchException?,
    val videoError: FetchException?,
    val tracks: List<TrackInfo> = emptyList(),
    /** The title as the site serves it in [CaptureRequest.titleLanguage]; null when not asked, unsupported or failed. */
    val localizedTitle: String? = null,
    val width: Int? = null,
    val height: Int? = null,
)

fun interface MediaFetcher {
    /**
     * Blocks until done. Puts any downloaded video into [workDir]. Reports (state, message, fraction) as it goes.
     * Throws FetchException when nothing could be captured; per-part failures come back in the outcome.
     */
    fun fetch(req: CaptureRequest, workDir: File, progress: (JobState, String?, Double?) -> Unit): CaptureOutcome

    /** Stops the capture that is running now (its process); the blocked [fetch] then throws. */
    fun cancel() {}

    /** Downloads one more subtitle track of a video, by language. Throws FetchException (no_captions when it does not exist). */
    fun fetchTrack(url: String, language: String, workDir: File): SubtitlesData =
        throw FetchException("Not supported", Codes.YTDLP)

    /** Metadata only: the title the site serves for [language]. Null when the extractor has no translated titles or it failed. */
    fun fetchTitle(url: String, language: String): String? = null
}

/** Maps yt-dlp's per-stream percentages (video, then audio) to one fraction that never goes backwards. */
class ProgressMeter(private val streams: Int) {
    private var stream = 0
    private var last = 0.0
    var value = 0.0
        private set

    fun update(percent: Double): Double {
        val p = percent.coerceIn(0.0, 100.0)
        if (p + 30 < last && stream < streams - 1) stream++
        last = p
        val v = ((stream + p / 100.0) / streams).coerceAtMost(0.99)
        if (v > value) value = v
        return value
    }
}
