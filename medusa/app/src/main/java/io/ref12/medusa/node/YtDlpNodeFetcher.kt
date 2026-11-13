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
                addOption("--sub-langs", SUB_LANGS)
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
          // subtitles: a language under "subtitles" is uploaded, one only under "automatic_captions" is automatic.
          // yt-dlp prefers the uploaded file when both exist, so the automatic one of such a language needs a second call.
          val subFiles = LinkedHashMap<String, File>() // upload name -> file
          fun langOf(f: File) = f.name.removePrefix("video.").removeSuffix(".vtt")
          for (f in (dir.listFiles() ?: emptyArray()).filter { it.name.startsWith("video.") && it.name.endsWith(".vtt") }) {
              val l = langOf(f); subFiles[if (parsed != null) NodeInfo.subName(l, parsed) else l] = f
          }
          if (job.subs && parsed != null && !cancelled) {
              val both = subFiles.keys.filter { !it.endsWith(".auto") && it in parsed.auto }
              if (both.isNotEmpty()) {
                  val ad = File(dir, "auto").apply { mkdirs() }
                  try {
                      YoutubeDL.getInstance().execute(YoutubeDLRequest(job.url).apply {
                          addOption("--no-playlist"); addOption("--no-warnings"); addOption("--skip-download"); addOption("--write-auto-subs")
                          addOption("--sub-langs", both.joinToString(",")); addOption("--sub-format", "vtt"); addOption("--convert-subs", "vtt")
                          addOption("-o", File(ad, "video.%(ext)s").absolutePath)
                      }, PID) { _, _, _ -> }
                  } catch (e: Exception) { /* missing automatic captions are not an error */ }
                  for (f in (ad.listFiles() ?: emptyArray()).filter { it.name.endsWith(".vtt") }) subFiles[langOf(f) + ".auto"] = f
              }
          }
          val names = NodeInfo.order(subFiles.keys)
          for (n in names) extras.add(Triple("subs/$n.vtt", subFiles[n]!!, "text/vtt"))
          return DownloadedFiles(video, extras, height, dur, parsed?.vcodec, parsed?.acodec, parsed?.formatId, names, parsed?.embeddable,
              parsed?.audioLanguage, parsed?.audioNote, parsed?.audioTracks ?: emptyList())
    }

    private fun titleFrom(dir: File): String? = try { File(dir, "video.info.json").takeIf { it.isFile }?.let { JSONObject(it.readText()).optString("title").ifEmpty { null } } } catch (e: Exception) { null }

    override fun cleanup(dir: File) { dir.deleteRecursively() }

    companion object {
        const val PID = "medusa-index-node"
        const val SUB_LANGS = "zh,zh-Hans,zh-Hant,zh-CN,zh-TW,zh-HK,en.*"
    }
}
