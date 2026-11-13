package io.ref12.medusa.node

import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLException
import com.yausername.youtubedl_android.YoutubeDLRequest
import io.ref12.medusa.YtDlpFetcher
import org.json.JSONObject
import java.io.File

/** Downloads through the same yt-dlp (and QuickJS, ffmpeg) that the rest of Medusa uses, under its own process id. */
class YtDlpNodeFetcher(private val yt: YtDlpFetcher) : NodeFetcher {
    @Volatile private var cancelled = false

    override fun cancel() { cancelled = true; try { YoutubeDL.getInstance().destroyProcessById(PID) } catch (e: Throwable) {} }

    override fun download(job: DownloadJob, dir: File, progress: (String?, String?) -> Unit): DownloadedFiles {
        cancelled = false
        dir.deleteRecursively(); dir.mkdirs()
        try { yt.ensureInit() } catch (e: Exception) { throw NodeFetchException(FailureKind.TRANSIENT, e.message ?: "yt-dlp did not start") }
        progress("starting", null)
        val req = YoutubeDLRequest(job.url).apply {
            addOption("--no-playlist"); addOption("--no-warnings")
            addOption("-f", job.selector()); addOption("--merge-output-format", "mp4")
            addOption("--write-info-json"); addOption("--write-thumbnail"); addOption("--convert-thumbnails", "jpg")
            if (job.subs) {
                addOption("--write-subs"); addOption("--write-auto-subs")
                // Chinese first; only these are fetched
                addOption("--sub-langs", "zh-Hans,zh-Hant,zh-CN,zh-TW,zh,en")
                addOption("--sub-format", "vtt"); addOption("--convert-subs", "vtt")
            }
            addOption("-o", File(dir, "video.%(ext)s").absolutePath)
        }
        var title: String? = null
        try {
            YoutubeDL.getInstance().execute(req, PID) { pct, _, line ->
                if (title == null) title = titleFrom(dir)
                progress("download " + pct.toInt().coerceAtLeast(0) + "%", title)
            }
        } catch (e: YoutubeDLException) {
            if (cancelled) throw NodeFetchException(FailureKind.TRANSIENT, "cancelled")
            throw NodeFetchException(Failures.classify(e.message ?: ""), (e.message ?: "yt-dlp failed").lines().lastOrNull { it.isNotBlank() } ?: "yt-dlp failed")
        } catch (e: InterruptedException) {
            throw NodeFetchException(FailureKind.TRANSIENT, "cancelled")
        }
        val video = File(dir, "video.mp4").takeIf { it.isFile && it.length() > 0 }
            ?: throw NodeFetchException(FailureKind.TRANSIENT, "yt-dlp did not produce video.mp4")
        val extras = ArrayList<Triple<String, File, String>>()
        val info = File(dir, "video.info.json")
        var height: Int? = null; var dur: Double? = null
        if (info.isFile) {
            extras.add(Triple("info.json", info, "application/json"))
            try { val o = JSONObject(info.readText()); height = o.optInt("height", 0).takeIf { it > 0 }; dur = if (o.isNull("duration")) null else o.optDouble("duration") } catch (e: Exception) {}
        }
        File(dir, "video.jpg").takeIf { it.isFile }?.let { extras.add(Triple("thumb.jpg", it, "image/jpeg")) }
        val subs = (dir.listFiles() ?: emptyArray()).filter { it.name.startsWith("video.") && it.name.endsWith(".vtt") }
        val rank = { l: String -> if (l.startsWith("zh")) 0 else 1 }
        for (f in subs.sortedBy { rank(it.name.removePrefix("video.").removeSuffix(".vtt")) }) {
            extras.add(Triple("subs/" + f.name.removePrefix("video.").removeSuffix(".vtt") + ".vtt", f, "text/vtt"))
        }
        return DownloadedFiles(video, extras, height, dur)
    }

    private fun titleFrom(dir: File): String? = try { File(dir, "video.info.json").takeIf { it.isFile }?.let { JSONObject(it.readText()).optString("title").ifEmpty { null } } } catch (e: Exception) { null }

    override fun cleanup(dir: File) { dir.deleteRecursively() }

    companion object { const val PID = "medusa-index-node" }
}
