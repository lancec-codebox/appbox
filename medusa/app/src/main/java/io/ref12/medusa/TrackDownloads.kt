package io.ref12.medusa

import java.util.concurrent.Executor

/** Where a subtitle track's lines are, as the item page shows it. */
enum class TrackState { ON_DEVICE, NOT_DOWNLOADED, DOWNLOADING, FAILED }

/** Which tracks have their lines on the device, and which a "Download all" still has to fetch. */
object TrackDevice {
    /** Added, imported and OCR tracks always are; a captured one is when it is the item's main track or its lines were saved. */
    fun onDevice(m: MediaMeta, t: TrackInfo, hasSaved: (String) -> Boolean): Boolean =
        t.added || m.subtitles?.language.equals(t.language, true) || hasSaved(t.language)

    /** The captured tracks still to fetch, in the order they are listed. */
    fun missing(m: MediaMeta, tracks: List<TrackInfo>, hasSaved: (String) -> Boolean): List<TrackInfo> =
        tracks.filter { !it.added && !onDevice(m, it, hasSaved) }
}

/** The reason a subtitle download failed, in words for a dialog. */
object DownloadErrors {
    private const val MAX = 160

    fun message(e: Throwable, online: Boolean): String {
        if (!online) return "No network connection. Connect to the internet and try again."
        val raw = (e.message ?: "").trim()
        val code = (e as? FetchException)?.code
        val low = raw.lowercase()
        return when {
            code == Codes.NO_CAPTIONS -> "This subtitle track is no longer available from the source."
            code == Codes.BOT_CHECK || low.contains("sign in") || low.contains("log in") || low.contains("login") || low.contains("confirm you")
                -> "The source needs you to sign in, so the subtitles can't be downloaded."
            low.contains("http error 404") || low.contains("http error 410") || low.contains("video unavailable") || low.contains("has been removed") ||
                low.contains("private video") || low.contains("does not exist")
                -> "The source video is gone or is no longer available."
            code == Codes.STORAGE -> "Not enough storage to save the subtitles."
            raw.isEmpty() -> "The download failed."
            else -> shorten(raw)
        }
    }

    /** One line: the last "ERROR:" message of yt-dlp's output, without the "[site] id:" prefix, cut to a readable length. */
    fun shorten(raw: String): String {
        val line = raw.lines().map { it.trim() }.filter { it.isNotEmpty() }.let { ls -> ls.lastOrNull { it.contains("ERROR:") } ?: ls.firstOrNull() ?: "" }
        var s = line.substringAfter("ERROR:").trim().replace(Regex("^\\[[^\\]]+\\]\\s*[^:\\s]*:\\s*"), "").replace(Regex("\\s+"), " ")
        if (s.isEmpty()) s = "The download failed."
        return if (s.length > MAX) s.take(MAX - 1).trimEnd() + "\u2026" else s
    }
}

/**
 * Downloads captured subtitle tracks one at a time off the main thread and remembers which are running or failed.
 * [load] fetches and stores one track (throws on failure); [post] runs a callback on the main thread.
 */
class TrackDownloads(
    private val onDevice: (String, TrackInfo) -> Boolean,
    private val load: (String, TrackInfo) -> SubtitlesData,
    private val online: () -> Boolean,
    private val executor: Executor,
    private val post: (() -> Unit) -> Unit,
) {
    private val lock = Any()
    private val running = HashSet<String>()
    private val failed = HashMap<String, String>()
    private fun k(mediaId: String, t: TrackInfo) = mediaId + "\u0000" + t.key

    fun isRunning(mediaId: String, t: TrackInfo) = synchronized(lock) { k(mediaId, t) in running }
    fun anyRunning(mediaId: String) = synchronized(lock) { running.any { it.startsWith(mediaId + "\u0000") } }
    fun failure(mediaId: String, t: TrackInfo): String? = synchronized(lock) { failed[k(mediaId, t)] }

    fun state(mediaId: String, t: TrackInfo): TrackState = when {
        isRunning(mediaId, t) -> TrackState.DOWNLOADING
        onDevice(mediaId, t) -> TrackState.ON_DEVICE
        failure(mediaId, t) != null -> TrackState.FAILED
        else -> TrackState.NOT_DOWNLOADED
    }

    /** Marks [t] as running unless it already is; false when it was. */
    private fun claim(mediaId: String, t: TrackInfo): Boolean = synchronized(lock) {
        if (!running.add(k(mediaId, t))) false else { failed.remove(k(mediaId, t)); true }
    }

    /** Runs one claimed download on the calling (worker) thread; the error message, or null on success. */
    private fun work(mediaId: String, t: TrackInfo): Pair<SubtitlesData?, String?> {
        var data: SubtitlesData? = null
        var err: String? = null
        try { data = load(mediaId, t) } catch (e: Throwable) { err = DownloadErrors.message(e, online()) }
        synchronized(lock) { running.remove(k(mediaId, t)); if (err != null) failed[k(mediaId, t)] = err else failed.remove(k(mediaId, t)) }
        return data to err
    }

    /** Downloads (or loads from the device) one track. [done] runs on the main thread with its lines or the reason. */
    fun download(mediaId: String, t: TrackInfo, started: () -> Unit = {}, done: (SubtitlesData?, String?) -> Unit) {
        if (!claim(mediaId, t)) return
        started()
        executor.execute { val (d, e) = work(mediaId, t); post { done(d, e) } }
    }

    /** Fetches every captured track of [tracks] not yet on the device, one after another. [changed] runs on the main thread after each. */
    fun downloadAll(mediaId: String, tracks: List<TrackInfo>, hasMissing: (TrackInfo) -> Boolean, changed: (TrackInfo, String?) -> Unit) {
        val todo = tracks.filter { !it.added && hasMissing(it) && claim(mediaId, it) }
        if (todo.isEmpty()) return
        executor.execute {
            for (t in todo) { val (_, e) = work(mediaId, t); post { changed(t, e) } }
        }
    }
}
