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
            job.sortArgs().chunked(2).forEach { addOption(it[0], it[1]) }
            addOption("-f", job.selector()); addOption("--merge-output-format", "mp4")
            addOption("--write-info-json"); addOption("--write-thumbnail"); addOption("--convert-thumbnails", "jpg")
            // the video only: subtitles follow in their own calls, from the exact tracks in the info JSON, so a subtitle failure cannot fail the video
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
            throw NodeFetchException(Failures.classify(e.message ?: ""), Failures.errorLine(e.message ?: "yt-dlp failed"))
        } catch (e: InterruptedException) {
            throw NodeFetchException(FailureKind.TRANSIENT, "cancelled")
        }
        val video = File(dir, "video.mp4").takeIf { it.isFile && it.length() > 0 }
            ?: throw NodeFetchException(FailureKind.TRANSIENT, "yt-dlp did not produce video.mp4")
          val extras = ArrayList<Triple<String, File, String>>()
          val info = File(dir, "video.info.json")
          var height: Int? = null; var dur: Double? = null
          var parsed: NodeInfo.Parsed? = null
          if (info.isFile) {
              extras.add(Triple("info.json", info, "application/json"))
              try {
                  val text = info.readText(); val o = JSONObject(text)
                  height = o.optInt("height", 0).takeIf { it > 0 }; dur = if (o.isNull("duration")) null else o.optDouble("duration")
                  parsed = NodeInfo.parse(text)
              } catch (e: Exception) {}
          }
          parsed?.description?.takeIf { it.isNotBlank() }?.let {
              val f = File(dir, "description.txt"); f.writeText(it, Charsets.UTF_8); extras.add(Triple("description.txt", f, "text/plain; charset=utf-8"))
          }
          File(dir, "video.jpg").takeIf { it.isFile }?.let { extras.add(Triple("thumb.jpg", it, "image/jpeg")) }
          // subtitles: exact tracks from the info JSON, uploaded in one call and original-language automatic ones in another; a failure is recorded, not thrown
          val subFiles = LinkedHashMap<String, File>() // upload name -> file
          var subsFailed: List<String> = emptyList()
          if (job.subs && !cancelled) {
              val plan = SubLangs.plan(if (info.isFile) info.readText() else null)
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
              parsed?.audioLanguage, parsed?.audioNote, parsed?.audioTracks ?: emptyList(), subsFailed)
    }

    private fun titleFrom(dir: File): String? = try { File(dir, "video.info.json").takeIf { it.isFile }?.let { JSONObject(it.readText()).optString("title").ifEmpty { null } } } catch (e: Exception) { null }

    override fun cleanup(dir: File) { dir.deleteRecursively() }

    companion object {
        const val PID = "medusa-index-node"
    }
}
