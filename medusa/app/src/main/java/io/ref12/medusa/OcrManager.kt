package io.ref12.medusa

import android.content.Context
import android.os.PowerManager
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Executors

/** What the foreground OCR run is doing, for the item screen and the notification. */
data class OcrState(
    val mediaId: String,
    val phase: Phase,
    val positionSec: Double = 0.0,
    val durationSec: Double = 0.0,
    val samples: Int = 0,
    val ocrCalls: Int = 0,
    val cues: Int = 0,
    val etaSec: Double? = null,
    val message: String? = null,
) {
    enum class Phase { DETECTING, RUNNING, DONE, FAILED, CANCELLED }
    val active get() = phase == Phase.DETECTING || phase == Phase.RUNNING
    val percent get() = if (durationSec > 0) (positionSec / durationSec * 100).toInt().coerceIn(0, 100) else 0
}

/** A saved, unfinished run: the band, the cues so far and where to carry on. */
data class OcrCheckpoint(val band: BandFrac, val resumeSec: Double, val cues: List<Line>, val durationSec: Double)

object OcrCheckpoints {
    fun toJson(c: OcrCheckpoint) = JSONObject().put("top", c.band.top.toDouble()).put("bottom", c.band.bottom.toDouble())
        .put("resumeSec", c.resumeSec).put("durationSec", c.durationSec)
        .put("cues", JSONArray().also { a -> for (l in c.cues) a.put(JSONObject().put("start", l.start).put("end", l.end).put("text", l.text)) }).toString()

    fun parse(s: String): OcrCheckpoint? = try {
        val j = JSONObject(s); val a = j.getJSONArray("cues")
        OcrCheckpoint(BandFrac(j.getDouble("top").toFloat(), j.getDouble("bottom").toFloat()), j.getDouble("resumeSec"),
            (0 until a.length()).map { i -> a.getJSONObject(i).let { Line(it.getDouble("start"), it.getDouble("end"), it.getString("text")) } }, j.optDouble("durationSec", 0.0))
    } catch (e: Exception) { null }
}

/**
 * Runs the extraction on one background thread and keeps its state. The foreground service ([HelperService]) shows it and
 * stays up while [activeCount] > 0. One run at a time (the phone is small).
 */
class OcrManager(private val ctx: Context, private val media: MediaStore, private val clock: () -> Long = System::currentTimeMillis) {
    private val exec = Executors.newSingleThreadExecutor { r -> Thread(r, "ocr").apply { isDaemon = true } }
    @Volatile var state: OcrState? = null
        private set
    @Volatile private var cancelFlag = false
    @Volatile var lastStats: OcrStats? = null
        private set

    fun activeCount() = if (state?.active == true) 1 else 0

    fun cancel() { cancelFlag = true }

    private fun checkpointFile(id: String) = media.fileIn(id, "ocr-checkpoint.json")

    fun checkpoint(id: String): OcrCheckpoint? = checkpointFile(id).takeIf { it.isFile }?.let { OcrCheckpoints.parse(it.readText()) }
    fun discardCheckpoint(id: String) { checkpointFile(id).delete() }

    /** Looks for the subtitle band in a sample of frames (blocking: call off the main thread). */
    fun detectBand(mediaId: String, onProgress: (Int, Int) -> Unit = { _, _ -> }): BandFrac {
        val file = media.videoFile(mediaId) ?: throw FetchException("This item has no video", Codes.UNSUPPORTED)
        VideoFrameReader(file).use { r ->
            MlKitOcr().use { ocr ->
                val n = 24
                val dur = r.durationUs.takeIf { it > 0 } ?: 600_000_000L
                val boxes = ArrayList<List<BandDetector.Box>>()
                val lumas = ArrayList<Luma>()
                var h = r.height
                for (i in 0 until n) {
                    onProgress(i, n)
                    val t = (dur * (0.04 + 0.92 * i / n)).toLong()
                    val f = r.frameAt(t) ?: continue
                    h = f.h
                    boxes.add(ocr.recognize(f).filter { it.confidence >= 0.4f }.map { BandDetector.Box(it.text, it.top, it.bottom, it.left, it.right) })
                    if (i % 3 == 0) lumas.add(f)
                }
                return BandDetector.fromBoxes(boxes, h) ?: BandDetector.fromLuma(lumas) ?: BandFrac.DEFAULT
            }
        }
    }

    /** One preview frame (grey) at 40% of the video, for adjusting the band. Blocking. */
    fun previewFrame(mediaId: String): Luma? {
        val file = media.videoFile(mediaId) ?: return null
        VideoFrameReader(file).use { r -> return r.frameAt((r.durationUs * 0.4).toLong()) }
    }

    /** Starts extraction of [mediaId]'s picture subtitles in [band]; resumes from the saved checkpoint when [resume]. */
    fun start(mediaId: String, band: BandFrac, resume: Boolean) {
        if (state?.active == true) return
        cancelFlag = false
        state = OcrState(mediaId, OcrState.Phase.RUNNING)
        try { HelperService.start(ctx) } catch (e: Exception) {}
        exec.execute { runJob(mediaId, band, resume) }
    }

    private fun runJob(mediaId: String, bandArg: BandFrac, resume: Boolean) {
        val wl = (ctx.getSystemService(Context.POWER_SERVICE) as PowerManager).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "medusa:ocr")
        try {
            wl.acquire(6 * 3600_000L)
            val file = media.videoFile(mediaId) ?: throw FetchException("This item has no video", Codes.UNSUPPORTED)
            val saved = if (resume) checkpoint(mediaId) else null
            val band = saved?.band ?: bandArg
            VideoFrameReader(file).use { reader ->
                MlKitOcr().use { ocr ->
                    val stats = OcrStats(); lastStats = stats
                    val step = 0.25
                    val dur = (reader.durationUs / 1e6).takeIf { it > 0 } ?: (media.get(mediaId)?.durationSec?.toDouble() ?: 0.0)
                    val pipe = OcrPipeline(step, { ocr.recognize(it) }, stats)
                    val kept = ArrayList<Line>(saved?.cues ?: emptyList())
                    val from = saved?.resumeSec ?: 0.0
                    val startedAt = clock()
                    var lastSave = clock()
                    state = OcrState(mediaId, OcrState.Phase.RUNNING, from, dur, cues = kept.size)
                    reader.run((from * 1e6).toLong(), Long.MAX_VALUE, (step * 1e6).toLong(), { band.rows(it) }, { cancelFlag }) { tUs, luma ->
                        val t = tUs / 1e6
                        pipe.push(t, luma)
                        stats.decodedFrames = reader.decoded.toInt()
                        val elapsed = (clock() - startedAt) / 1000.0
                        val done = t - from
                        val eta = if (elapsed > 5 && done > 1 && dur > 0) elapsed / done * (dur - t) else null
                        state = OcrState(mediaId, OcrState.Phase.RUNNING, t, dur, stats.samples, stats.ocrCalls, kept.size + pipe.builder.cues.size, eta)
                        if (clock() - lastSave > 30_000) {
                            lastSave = clock()
                            val resumeAt = pipe.builder.openStart ?: t
                            saveCheckpoint(mediaId, OcrCheckpoint(band, resumeAt, kept + pipe.builder.cues.filter { it.end <= resumeAt }, dur))
                        }
                    }
                    if (cancelFlag) {
                        val resumeAt = pipe.builder.openStart ?: ((state?.positionSec) ?: from)
                        saveCheckpoint(mediaId, OcrCheckpoint(band, resumeAt, kept + pipe.builder.cues.filter { it.end <= resumeAt }, dur))
                        state = OcrState(mediaId, OcrState.Phase.CANCELLED, resumeAt, dur, stats.samples, stats.ocrCalls, kept.size, message = "Stopped. You can resume it later.")
                        return
                    }
                    val lines = (kept + pipe.finish()).sortedBy { it.start }
                    if (lines.isEmpty()) throw FetchException("No subtitle text was found in the picture. Try adjusting the subtitle band.", Codes.NO_CAPTIONS)
                    val language = OcrText.language(lines.joinToString("") { it.text })
                    media.addTrack(mediaId, AddedTrack(language, OcrText.label(language), "ocr", lines.take(TrackRules.MAX_LINES), TrackInfo.SOURCE_OCR))
                    discardCheckpoint(mediaId)
                    state = OcrState(mediaId, OcrState.Phase.DONE, dur, dur, stats.samples, stats.ocrCalls, lines.size, message = "Added ${OcrText.label(language)}: ${lines.size} lines")
                }
            }
        } catch (e: Throwable) {
            state = OcrState(mediaId, OcrState.Phase.FAILED, message = e.message ?: e.javaClass.simpleName)
        } finally {
            if (wl.isHeld) wl.release()
        }
    }

    private fun saveCheckpoint(id: String, c: OcrCheckpoint) { try { checkpointFile(id).writeText(OcrCheckpoints.toJson(c)) } catch (e: Exception) {} }

}
