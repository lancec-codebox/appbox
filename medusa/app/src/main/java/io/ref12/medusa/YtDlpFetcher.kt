package io.ref12.medusa

import android.content.Context
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLException
import com.yausername.youtubedl_android.YoutubeDLRequest
import org.json.JSONObject
import java.io.File
import java.util.UUID

/** Captures subtitles and video with yt-dlp (youtubedl-android) and ffmpeg. */
class YtDlpFetcher(private val ctx: Context, private val settings: Settings) : MediaFetcher {
    private val ytdlp get() = YoutubeDL.getInstance()
    private var inited = false
    @Volatile var updating = false
        private set

    @Synchronized
    fun ensureInit() {
        if (inited) return
        try {
            ytdlp.init(ctx)
            try { com.yausername.ffmpeg.FFmpeg.getInstance().init(ctx) } catch (e: Exception) { /* video merge may fail later with a clear message */ }
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

    override fun fetch(req: CaptureRequest, workDir: File, progress: (JobState, String?, Double?) -> Unit): CaptureOutcome {
        ensureInit()
        if (System.currentTimeMillis() - settings.lastUpdateCheck > DAY_MS) {
            progress(JobState.FETCHING, "Checking for a yt-dlp update", null)
            try { update() } catch (e: Exception) { /* offline: carry on */ }
        }
        return try {
            attempt(req, workDir, progress)
        } catch (e: YtDlpFailure) {
            progress(JobState.FETCHING, "yt-dlp failed, updating it and retrying", null)
            try { update() } catch (u: Exception) { /* retry anyway */ }
            try { attempt(req, workDir, progress) } catch (e2: YtDlpFailure) { throw explain(e2.message ?: "") }
        }
    }

    private fun attempt(req: CaptureRequest, workDir: File, progress: (JobState, String?, Double?) -> Unit): CaptureOutcome {
        progress(JobState.FETCHING, "Reading video information", 0.0)
        val listing = run(YoutubeDLRequest(req.url).apply {
            addOption("--dump-single-json"); addOption("--skip-download"); addOption("--no-playlist"); addOption("--ignore-no-formats-error")
        })
        val info = JSONObject(jsonOf(listing.out))
        val noTracks = (info.optJSONObject("subtitles")?.length() ?: 0) + (info.optJSONObject("automatic_captions")?.length() ?: 0) == 0
        if (listing.err.contains("Sign in to confirm", true) || listing.err.contains("not a bot", true))
            throw FetchException("YouTube asks for a sign-in (bot check) on this network. Try another network, such as mobile data, or try again later.", Codes.BOT_CHECK)
        if (noTracks && info.optString("title", "").isEmpty()) throw YtDlpFailure(listing.err.ifBlank { "yt-dlp returned no video information" })
        val title = info.optString("title", "")
        val duration = if (info.has("duration") && !info.isNull("duration")) info.optDouble("duration").toInt() else null
        val extractor = info.optString("extractor", info.optString("extractor_key", "")).lowercase()
        val id = info.optString("id", "")
        val localized = if (req.titleLanguage != null && Titles.supportsLocalized(extractor)) fetchTitle(req.url, req.titleLanguage) else null

        val tracks = TrackSelector.list(info)
        val trackInfos = tracks.map { it.info() }
        var subs: SubtitlesData? = null
        var subsErr: FetchException? = null
        if (req.wantSubtitles) {
            try { subs = fetchSubs(req, info, tracks, workDir, progress) } catch (e: FetchException) { subsErr = e }
        }
        var video: File? = null
        var videoErr: FetchException? = null
        if (req.wantVideo && !req.videoAlreadyStored(extractor, id)) {
            try { video = fetchVideo(req, workDir, progress) } catch (e: FetchException) { videoErr = e }
        }
        return CaptureOutcome(title, duration, extractor, id, subs, video, subsErr, videoErr, trackInfos, localized)
    }

    /** Metadata only (no download): the title YouTube serves for [language]. Null when it fails: the original title stays. */
    override fun fetchTitle(url: String, language: String): String? = try {
        ensureInit()
        val r = run(YoutubeDLRequest(url).apply {
            addOption("--dump-single-json"); addOption("--skip-download"); addOption("--no-playlist"); addOption("--ignore-no-formats-error")
            addOption("--extractor-args"); addOption("youtube:lang=$language")
        })
        JSONObject(jsonOf(r.out)).optString("title", "").ifEmpty { null }
    } catch (e: Exception) { null }

    private fun fetchSubs(req: CaptureRequest, info: JSONObject, tracks: List<Track>, workDir: File, progress: (JobState, String?, Double?) -> Unit): SubtitlesData {
        val track = TrackSelector.select(info, req.languages)
            ?: throw FetchException(TrackSelector.noCaptionsMessage(req.languages, tracks.map { it.info() }), Codes.NO_CAPTIONS)
        progress(JobState.FETCHING, "Downloading " + track.language + " " + (if (track.source == "captions") "captions" else "auto-captions"), null)
        return download(req.url, track, workDir)
    }

    private fun download(url: String, track: Track, workDir: File): SubtitlesData {
        val dir = File(workDir, "subs-" + track.key).also { it.mkdirs() }
        run(YoutubeDLRequest(url).apply {
            addOption("--skip-download"); addOption("--no-playlist"); addOption("--no-warnings"); addOption("--ignore-no-formats-error")
            addOption(if (track.source == "captions") "--write-subs" else "--write-auto-subs")
            addOption("--sub-langs", track.key); addOption("--sub-format", track.ext)
            addOption("-o", File(dir, "%(id)s.%(ext)s").absolutePath)
        })
        val file = dir.listFiles()?.firstOrNull { it.name.endsWith("." + track.ext) }
            ?: throw FetchException("yt-dlp did not produce the caption file", Codes.NO_CAPTIONS)
        val lines = SubtitleConverter.parse(track.ext, file.readText())
        if (lines.isEmpty()) throw FetchException("The caption track is empty", Codes.NO_CAPTIONS)
        return SubtitlesData(track.source, track.language, lines)
    }

    override fun fetchTrack(url: String, language: String, workDir: File): SubtitlesData {
        ensureInit()
        val listing = run(YoutubeDLRequest(url).apply {
            addOption("--dump-single-json"); addOption("--skip-download"); addOption("--no-playlist"); addOption("--ignore-no-formats-error")
        })
        val info = JSONObject(jsonOf(listing.out))
        val track = TrackSelector.find(info, language)
            ?: throw FetchException(TrackSelector.noCaptionsMessage(listOf(language), TrackSelector.list(info).map { it.info() }), Codes.NO_CAPTIONS)
        return download(url, track, workDir)
    }

    /** A quick listing for the capture screen: what subtitle tracks the video offers. */
    fun listTracks(url: String): List<TrackInfo> {
        ensureInit()
        val listing = run(YoutubeDLRequest(url).apply {
            addOption("--dump-single-json"); addOption("--skip-download"); addOption("--no-playlist"); addOption("--ignore-no-formats-error")
        })
        return TrackSelector.list(JSONObject(jsonOf(listing.out))).map { it.info() }
    }

    private fun fetchVideo(req: CaptureRequest, workDir: File, progress: (JobState, String?, Double?) -> Unit): File {
        progress(JobState.DOWNLOADING, "Downloading video (" + req.quality.wire + ")", 0.0)
        val dir = File(workDir, "video").also { it.mkdirs() }
        val h = req.quality.height
        val meter = ProgressMeter(2)
        run(YoutubeDLRequest(req.url).apply {
            addOption("--no-playlist"); addOption("--no-warnings")
            // MP4 (H.264 + AAC) so every Android player can play it; fall back to the best single MP4 file
            addOption("-f", "bv*[height<=$h][ext=mp4]+ba[ext=m4a]/b[height<=$h][ext=mp4]/b[height<=$h]")
            addOption("--merge-output-format", "mp4")
            addOption("-o", File(dir, "video.%(ext)s").absolutePath)
        }) { pct, _, _ -> progress(JobState.DOWNLOADING, null, meter.update(pct.toDouble())) }
        val f = dir.listFiles()?.filter { it.isFile && !it.name.endsWith(".part") && !it.name.endsWith(".ytdl") }?.maxByOrNull { it.length() }
            ?: throw FetchException("yt-dlp did not produce a video file", Codes.YTDLP)
        return f
    }

    private fun run(req: YoutubeDLRequest, cb: ((Float, Long, String) -> Unit)? = null) = try {
        ytdlp.execute(req, null, cb)
    } catch (e: YoutubeDLException) {
        val msg = e.message ?: ""
        if (msg.contains("Unsupported URL", true)) throw explain(msg)
        throw YtDlpFailure(msg)
    } catch (e: InterruptedException) {
        throw FetchException("Cancelled", Codes.YTDLP)
    }

    private fun jsonOf(out: String): String {
        val i = out.indexOf('{')
        if (i < 0) throw YtDlpFailure("yt-dlp returned no data")
        return out.substring(i)
    }

    private fun explain(raw: String): FetchException {
        val last = raw.lines().lastOrNull { it.contains("ERROR", true) } ?: raw.lines().lastOrNull { it.isNotBlank() } ?: "unknown error"
        val msg = last.substringAfter("ERROR:", last).trim()
        val code = when {
            raw.contains("Unsupported URL", true) -> Codes.UNSUPPORTED
            raw.contains("Sign in to confirm", true) -> Codes.BOT_CHECK
            raw.contains("Unable to download", true) || raw.contains("Temporary failure", true) ||
                raw.contains("timed out", true) || raw.contains("Network is unreachable", true) -> Codes.NETWORK
            else -> Codes.YTDLP
        }
        val text = when (code) {
            Codes.UNSUPPORTED -> "This site or link is not supported by yt-dlp"
            Codes.BOT_CHECK -> "YouTube asks for a sign-in (bot check) on this network. Try another network, or try again later."
            Codes.NETWORK -> "Network problem: " + msg
            else -> "yt-dlp failed (it may be out of date): " + msg
        }
        return FetchException(text.take(400), code)
    }

    companion object { const val DAY_MS = 24L * 3600 * 1000 }
}
