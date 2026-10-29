package io.ref12.mediahelper

import android.content.Context
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLException
import com.yausername.youtubedl_android.YoutubeDLRequest
import org.json.JSONObject
import java.io.File
import java.util.UUID

/** Fetches subtitles with yt-dlp (youtubedl-android). No video is downloaded. */
class YtDlpFetcher(private val ctx: Context, private val settings: Settings) : TranscriptFetcher {
    private val ytdlp get() = YoutubeDL.getInstance()
    private var inited = false
    @Volatile var updating = false
        private set

    @Synchronized
    fun ensureInit() {
        if (inited) return
        try {
            ytdlp.init(ctx)
        } catch (e: YoutubeDLException) {
            throw FetchException("Could not start yt-dlp: " + (e.message ?: "unknown error"))
        }
        inited = true
    }

    fun version(): String = try {
        ensureInit()
        ytdlp.version(ctx) ?: ytdlp.versionName(ctx) ?: "unknown"
    } catch (e: Throwable) { "unknown" }

    /** Updates yt-dlp through the library; returns a short human result. */
    fun update(): String {
        ensureInit()
        updating = true
        try {
            val st = ytdlp.updateYoutubeDL(ctx, YoutubeDL.UpdateChannel.STABLE)
            settings.lastUpdateCheck = System.currentTimeMillis()
            return if (st == YoutubeDL.UpdateStatus.DONE) "Updated to " + version() else "Already up to date (" + version() + ")"
        } finally { updating = false }
    }

    override fun fetch(url: String, languages: List<String>, progress: (String) -> Unit): Transcript {
        ensureInit()
        if (System.currentTimeMillis() - settings.lastUpdateCheck > DAY_MS) {
            progress("Checking for a yt-dlp update")
            try { update() } catch (e: Exception) { /* offline or rate-limited: carry on with what we have */ }
        }
        return try {
            attempt(url, languages, progress)
        } catch (e: YtDlpFailure) {
            progress("yt-dlp failed, updating it and retrying")
            try { update() } catch (u: Exception) { /* retry anyway */ }
            try {
                attempt(url, languages, progress)
            } catch (e2: YtDlpFailure) {
                throw FetchException(explain(e2.message ?: ""))
            }
        }
    }

    private fun attempt(url: String, languages: List<String>, progress: (String) -> Unit): Transcript {
        progress("Listing captions")
        val info = JSONObject(jsonOf(run(YoutubeDLRequest(url).apply {
            addOption("--dump-single-json")
            addOption("--skip-download")
            addOption("--no-playlist")
            addOption("--no-warnings")
            addOption("--ignore-no-formats-error")
        })))
        val track = TrackSelector.select(info, languages)
            ?: throw FetchException("No Chinese captions found for this video (looked for " + languages.joinToString(", ") + ")")
        progress("Downloading " + track.language + " " + (if (track.source == "captions") "captions" else "auto-captions"))
        val dir = File(ctx.cacheDir, "subs-" + UUID.randomUUID()).also { it.mkdirs() }
        try {
            run(YoutubeDLRequest(url).apply {
                addOption("--skip-download")
                addOption("--no-playlist")
                addOption("--no-warnings")
                addOption("--ignore-no-formats-error")
                addOption(if (track.source == "captions") "--write-subs" else "--write-auto-subs")
                addOption("--sub-langs", track.key)
                addOption("--sub-format", track.ext)
                addOption("-o", File(dir, "%(id)s.%(ext)s").absolutePath)
            })
            val file = dir.listFiles()?.firstOrNull { it.name.endsWith("." + track.ext) }
                ?: throw FetchException("yt-dlp did not produce the caption file")
            progress("Converting")
            val lines = SubtitleConverter.parse(track.ext, file.readText())
            if (lines.isEmpty()) throw FetchException("The caption track is empty")
            return Transcript(
                url = url,
                title = info.optString("title", ""),
                durationSec = if (info.has("duration") && !info.isNull("duration")) info.optDouble("duration").toInt() else null,
                extractor = info.optString("extractor", info.optString("extractor_key", "")).lowercase(),
                videoId = info.optString("id", ""),
                source = track.source,
                language = track.language,
                lines = lines,
            )
        } finally { dir.deleteRecursively() }
    }

    private fun run(req: YoutubeDLRequest): String = try {
        ytdlp.execute(req, null, null).out
    } catch (e: YoutubeDLException) {
        val msg = e.message ?: ""
        if (msg.contains("Unsupported URL", true)) throw FetchException(explain(msg))
        throw YtDlpFailure(msg)
    } catch (e: InterruptedException) {
        throw FetchException("Cancelled")
    }

    private fun jsonOf(out: String): String {
        val i = out.indexOf('{')
        if (i < 0) throw YtDlpFailure("yt-dlp returned no data")
        return out.substring(i)
    }

    private fun explain(raw: String): String {
        val last = raw.lines().lastOrNull { it.contains("ERROR", true) } ?: raw.lines().lastOrNull { it.isNotBlank() } ?: "unknown error"
        val msg = last.substringAfter("ERROR:", last).trim()
        return when {
            raw.contains("Unsupported URL", true) -> "This site or link is not supported by yt-dlp"
            raw.contains("Unable to download", true) || raw.contains("Temporary failure", true) ||
                raw.contains("timed out", true) || raw.contains("Network is unreachable", true) -> "Network problem: " + msg
            else -> "yt-dlp failed (it may be out of date): " + msg
        }.take(400)
    }

    companion object { const val DAY_MS = 24L * 3600 * 1000 }
}
