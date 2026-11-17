package io.ref12.medusa.node

import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLException
import com.yausername.youtubedl_android.YoutubeDLRequest
import io.ref12.medusa.VideoRequest
import io.ref12.medusa.YtDlpFetcher
import org.json.JSONObject
import java.io.File

/** yt-dlp for the node's video request: the library's request builder and MP4 selector (VideoRequest), the node's own process id, and output kept for the failure trace. */
class YtDlpVideoIo(private val yt: YtDlpFetcher, private val cancelledFlag: () -> Boolean) : VideoIo {
    override fun ytdlpVersion(): String = yt.version()

    /** The video request: the library's options, plus (only when a variant asks for them, a diagnosis or the Test download) the options that used to ride on it. */
    private fun request(job: DownloadJob, dir: File, v: Variant, a: Attempt): YoutubeDLRequest {
        val out = File(dir, "video.%(ext)s").absolutePath
        val r = YoutubeDLRequest(job.url)
        if (a.webm) {
            // the first attempt: any video plus compact Opus audio, resolution from -S, as before 1.8.6
            r.addOption("--no-playlist"); r.addOption("--no-warnings")
            job.sortArgs().chunked(2).forEach { r.addOption(it[0], it[1]) }
            r.addOption("-f", job.selector()); r.addOption("--merge-output-format", "mp4"); r.addOption("-o", out)
        } else VideoRequest.apply(r, VideoRequest.options(out, a.height))
        if (v.infoWithVideo) r.addOption("--write-info-json")
        if (v.thumbWithVideo) { r.addOption("--write-thumbnail"); r.addOption("--convert-thumbnails", "jpg") }
        return r
    }

    private fun fail(e: YoutubeDLException, lines: List<String>): NodeFetchException {
        val raw = (e.message ?: "") + "\n" + lines.joinToString("\n")
        val kind = Failures.classify(e.message ?: raw)
        return NodeFetchException(kind, Failures.errorLine(e.message ?: raw), FailureTrace.of(lines.joinToString("\n") + "\n" + (e.message ?: ""), yt.version()))
    }

    override fun video(job: DownloadJob, dir: File, v: Variant, a: Attempt, onPct: (Int) -> Unit) {
        val lines = java.util.Collections.synchronizedList(ArrayList<String>())
        try {
            YoutubeDL.getInstance().execute(request(job, dir, v, a), PID) { pct, _, line -> if (line.isNotBlank()) lines.add(line); onPct(pct.toInt()) }
        } catch (e: YoutubeDLException) {
            if (cancelledFlag()) throw NodeFetchException(FailureKind.TRANSIENT, "cancelled")
            throw fail(e, lines.toList())
        } catch (e: InterruptedException) { throw NodeFetchException(FailureKind.TRANSIENT, "cancelled") }
    }

    override fun probe(job: DownloadJob, dir: File, v: Variant, a: Attempt): TryResult {
        val lines = java.util.Collections.synchronizedList(ArrayList<String>())
        val passedFlag = java.util.concurrent.atomic.AtomicBoolean(false)
        fun bytes() = (dir.listFiles() ?: emptyArray()).filter { it.isFile && it.name.startsWith("video.") }.sumOf { it.length() }
        try {
            YoutubeDL.getInstance().execute(request(job, dir, v, a), PID) { _, _, line ->
                if (line.isNotBlank()) lines.add(line)
                if (!passedFlag.get() && bytes() >= Diagnosis.PASS_BYTES) { passedFlag.set(true); try { YoutubeDL.getInstance().destroyProcessById(PID) } catch (e: Throwable) {} }
            }
            return TryResult(TryOutcome.PASS, FailureTrace.formatsOf(lines.joinToString("\n")), FailureTrace.clientOf(lines.joinToString("\n")), bytes())
        } catch (e: Exception) {
            val text = lines.joinToString("\n") + "\n" + (e.message ?: "")
            val formats = FailureTrace.formatsOf(text); val client = FailureTrace.clientOf(text)
            if (passedFlag.get() || bytes() >= Diagnosis.PASS_BYTES) return TryResult(TryOutcome.PASS, formats, client, bytes())
            if (cancelledFlag()) return TryResult(TryOutcome.ERROR, formats, client, bytes(), "cancelled")
            return TryResult(when (Failures.classify(text)) { FailureKind.FORBIDDEN -> TryOutcome.FORBIDDEN; FailureKind.BOT -> TryOutcome.BOT; else -> TryOutcome.ERROR }, formats, client, bytes(), Failures.errorLine(text))
        } finally { dir.listFiles()?.filter { it.name.startsWith("video.") }?.forEach { it.delete() } }
    }

    companion object { const val PID = "medusa-index-node" }
}

/** Downloads through the same yt-dlp (and QuickJS, ffmpeg) that the rest of Medusa uses, under its own process id. The video goes through [NodeDownloader]; the info JSON, thumbnail, description and subtitles follow in separate calls. */
class YtDlpNodeFetcher(private val yt: YtDlpFetcher, private val prefs: DownloadPrefs, private val env: NodeEnv = NodeEnv()) : NodeFetcher {
    @Volatile private var cancelled = false
    private val io = YtDlpVideoIo(yt) { cancelled }
    private val downloader = NodeDownloader(io, prefs, env.sleep, env.gapMs) { NodeRunner.iso(env.clock()) }
    /** What the last failed download left: the trace for the job record. Set by [download] on failure (the exception carries it too). */
    @Volatile var lastProfile: DownloadProfile? = null; private set

    /** The Test download starts yt-dlp the same way a node download does. */
    fun ensureTestReady() { try { yt.ensureInit() } catch (e: Exception) { throw NodeFetchException(FailureKind.TRANSIENT, e.message ?: "yt-dlp did not start") } }

    override fun cancel() { cancelled = true; try { YoutubeDL.getInstance().destroyProcessById(PID) } catch (e: Throwable) {} }

    override fun download(job: DownloadJob, dir: File, progress: (String?, String?) -> Unit): DownloadedFiles {
        cancelled = false
        dir.deleteRecursively(); dir.mkdirs()
        try { yt.ensureInit() } catch (e: Exception) { throw NodeFetchException(FailureKind.TRANSIENT, e.message ?: "yt-dlp did not start") }
        progress("starting", null)
        var title: String? = null
        val r = downloader.download(job, dir, { t -> progress(t, title) }, { cancelled })
        lastProfile = prefs.profile
        val video = File(dir, "video.mp4").takeIf { it.isFile && it.length() > 0 }
            ?: throw NodeFetchException(FailureKind.TRANSIENT, "yt-dlp did not produce video.mp4")
        val extras = ArrayList<Triple<String, File, String>>()
        var height: Int? = null; var dur: Double? = null
        var parsed: NodeInfo.Parsed? = null
        // the info JSON, thumbnail and description: separate calls after the video, so nothing extra rides on the video request (unless a variant asks for it)
        var info = File(dir, "video.info.json").takeIf { it.isFile }
        if (!cancelled && info == null) { progress("info", null); info = fetchInfo(job, dir) }
        if (info != null) {
            extras.add(Triple("info.json", info, "application/json"))
            try {
                val text = info.readText(); val o = JSONObject(text)
                title = o.optString("title").ifEmpty { null }
                height = o.optInt("height", 0).takeIf { it > 0 }; dur = if (o.isNull("duration")) null else o.optDouble("duration")
                parsed = NodeInfo.parse(text)
            } catch (e: Exception) {}
        }
        parsed?.description?.takeIf { it.isNotBlank() }?.let {
            val f = File(dir, "description.txt"); f.writeText(it, Charsets.UTF_8); extras.add(Triple("description.txt", f, "text/plain; charset=utf-8"))
        }
        var thumb = File(dir, "video.jpg").takeIf { it.isFile }
        if (!cancelled && thumb == null) { progress("thumbnail", title); thumb = fetchThumb(job, dir) }
        thumb?.let { extras.add(Triple("thumb.jpg", it, "image/jpeg")) }
        // subtitles: exact tracks from the info JSON, uploaded in one call and original-language automatic ones in another; a failure is recorded, not thrown
        val subFiles = LinkedHashMap<String, File>()
        var subsFailed: List<String> = emptyList()
        if (job.subs && !cancelled) {
            progress("subtitles", title)
            val plan = SubLangs.plan(info?.takeIf { it.isFile }?.readText())
            subsFailed = SubLangs.fetch(plan) { auto, langs ->
                if (cancelled) throw InterruptedException()
                val sd = File(dir, if (auto) "auto" else "manual").apply { mkdirs() }
                YoutubeDL.getInstance().execute(YoutubeDLRequest(job.url).apply {
                    addOption("--no-playlist"); addOption("--no-warnings"); addOption("--skip-download")
                    addOption(if (auto) "--write-auto-subs" else "--write-subs"); addOption("--sub-langs", langs)
                    addOption("--sub-format", "vtt"); addOption("--convert-subs", "vtt")
                    addOption("-o", File(sd, "video.%(ext)s").absolutePath)
                }, PID) { _, _, _ -> }
            }
            for (auto in listOf(false, true)) for (f in (File(dir, if (auto) "auto" else "manual").listFiles() ?: emptyArray()).filter { it.name.startsWith("video.") && it.name.endsWith(".vtt") })
                subFiles[f.name.removePrefix("video.").removeSuffix(".vtt") + if (auto) ".auto" else ""] = f
        }
        val names = NodeInfo.order(subFiles.keys)
        for (n in names) extras.add(Triple("subs/$n.vtt", subFiles[n]!!, "text/vtt"))
        return DownloadedFiles(video, extras, height, dur, parsed?.vcodec, parsed?.acodec, parsed?.formatId, names, parsed?.embeddable,
            parsed?.audioLanguage, parsed?.audioNote, parsed?.audioTracks ?: emptyList(), subsFailed, r.formatFallback)
    }

    /** The info JSON in its own call (no video, no thumbnail). A failure leaves the job without it: the video is already downloaded. */
    fun fetchInfo(job: DownloadJob, dir: File): File? = try {
        YoutubeDL.getInstance().execute(YoutubeDLRequest(job.url).apply {
            addOption("--no-playlist"); addOption("--no-warnings"); addOption("--skip-download"); addOption("--write-info-json"); addOption("--ignore-no-formats-error")
            addOption("-o", File(dir, "video.%(ext)s").absolutePath)
        }, PID) { _, _, _ -> }
        File(dir, "video.info.json").takeIf { it.isFile }
    } catch (e: Exception) { null }

    /** The thumbnail in its own call, converted to JPEG. */
    fun fetchThumb(job: DownloadJob, dir: File): File? = try {
        YoutubeDL.getInstance().execute(YoutubeDLRequest(job.url).apply {
            addOption("--no-playlist"); addOption("--no-warnings"); addOption("--skip-download"); addOption("--write-thumbnail"); addOption("--convert-thumbnails", "jpg"); addOption("--ignore-no-formats-error")
            addOption("-o", File(dir, "video.%(ext)s").absolutePath)
        }, PID) { _, _, _ -> }
        File(dir, "video.jpg").takeIf { it.isFile }
    } catch (e: Exception) { null }

    override fun cleanup(dir: File) { dir.deleteRecursively() }

    companion object { const val PID = YtDlpVideoIo.PID }
}
