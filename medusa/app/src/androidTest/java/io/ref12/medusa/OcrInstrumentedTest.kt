package io.ref12.medusa

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.MediaMuxer
import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/**
 * Generates a short video with Chinese subtitles drawn into the picture, then extracts them with the real decoder and
 * ML Kit's bundled Chinese model, and checks the cues and timings. Logs the performance numbers (tag OcrPerf).
 */
@RunWith(AndroidJUnit4::class)
class OcrInstrumentedTest {
    private val ctx: Context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val W = 640; private val H = 360; private val FPS = 10; private val SECONDS = 14

    private data class Expect(val start: Double, val end: Double, val text: String)
    private val expected = listOf(
        Expect(1.0, 3.5, "你好，世界"),
        Expect(4.0, 7.0, "今天天气很好"),
        Expect(8.5, 10.5, "我们一起去吃饭吧"),
        Expect(11.0, 13.5, "再见，明天见"),
    )

    /** Draws frame [i]: a moving gradient background, a static logo-like corner mark and the subtitle that is on at that time. */
    private fun frame(i: Int, bmp: Bitmap, paint: Paint, stroke: Paint) {
        val c = Canvas(bmp)
        val t = i / FPS.toDouble()
        val shift = (i * 6) % 255
        c.drawColor(Color.rgb(30 + shift / 4, 60, 90 + shift / 5))
        paint.style = Paint.Style.FILL; paint.color = Color.rgb(200, 120, 40)
        c.drawCircle((50 + i * 4 % 500).toFloat(), 120f, 40f, paint) // something moving behind the text
        c.drawRect(((i * 5) % 600).toFloat(), 250f, ((i * 5) % 600 + 60).toFloat(), 300f, paint)
        paint.color = Color.WHITE; paint.textSize = 40f
        val cue = expected.firstOrNull { t >= it.start && t < it.end }
        if (cue != null) {
            val w = paint.measureText(cue.text)
            val x = (W - w) / 2
            c.drawText(cue.text, x, 330f, stroke)
            c.drawText(cue.text, x, 330f, paint)
        }
    }

    private fun makeVideo(out: File) {
        val fmt = MediaFormat.createVideoFormat("video/avc", W, H).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible)
            setInteger(MediaFormat.KEY_BIT_RATE, 2_500_000); setInteger(MediaFormat.KEY_FRAME_RATE, FPS); setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
        }
        val enc = MediaCodec.createEncoderByType("video/avc")
        enc.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE); enc.start()
        val mux = MediaMuxer(out.absolutePath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
        var track = -1; var started = false
        val bmp = Bitmap.createBitmap(W, H, Bitmap.Config.ARGB_8888)
        val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { typeface = Typeface.DEFAULT }
        val stroke = Paint(paint).apply { style = Paint.Style.STROKE; strokeWidth = 5f; color = Color.BLACK }
        val px = IntArray(W * H)
        val info = MediaCodec.BufferInfo()
        val total = FPS * SECONDS
        var sent = 0; var eosSent = false; var eos = false
        while (!eos) {
            if (!eosSent) {
                val i = enc.dequeueInputBuffer(10_000)
                if (i >= 0) {
                    if (sent == total) { enc.queueInputBuffer(i, 0, 0, sent * 1_000_000L / FPS, MediaCodec.BUFFER_FLAG_END_OF_STREAM); eosSent = true }
                    else {
                        frame(sent, bmp, paint, stroke); bmp.getPixels(px, 0, W, 0, 0, W, H)
                        val img = enc.getInputImage(i)!!
                        val yp = img.planes[0]; val up = img.planes[1]; val vp = img.planes[2]
                        for (y in 0 until H) for (x in 0 until W) {
                            val p = px[y * W + x]; val r = (p shr 16) and 255; val g = (p shr 8) and 255; val b = p and 255
                            yp.buffer.put(y * yp.rowStride + x * yp.pixelStride, (((66 * r + 129 * g + 25 * b + 128) shr 8) + 16).toByte())
                            if (y % 2 == 0 && x % 2 == 0) {
                                up.buffer.put((y / 2) * up.rowStride + (x / 2) * up.pixelStride, (((-38 * r - 74 * g + 112 * b + 128) shr 8) + 128).toByte())
                                vp.buffer.put((y / 2) * vp.rowStride + (x / 2) * vp.pixelStride, (((112 * r - 94 * g - 18 * b + 128) shr 8) + 128).toByte())
                            }
                        }
                        enc.queueInputBuffer(i, 0, W * H * 3 / 2, sent * 1_000_000L / FPS, 0)
                        sent++
                    }
                }
            }
            val o = enc.dequeueOutputBuffer(info, 10_000)
            when {
                o == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> { track = mux.addTrack(enc.outputFormat); mux.start(); started = true }
                o >= 0 -> {
                    if (started && info.size > 0 && info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0) mux.writeSampleData(track, enc.getOutputBuffer(o)!!, info)
                    eos = info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0
                    enc.releaseOutputBuffer(o, false)
                }
            }
        }
        enc.stop(); enc.release(); mux.stop(); mux.release()
    }

    private fun overlap(a: String, b: String) = a.toSet().intersect(b.toSet()).size.toDouble() / maxOf(a.toSet().size, b.toSet().size)

    private fun check(cues: List<Line>) {
        for (e in expected) {
            val hit = cues.firstOrNull { it.start < e.end && it.end > e.start && overlap(it.text, e.text) >= 0.6 }
            assertNotNull("cue \"${e.text}\" ${e.start}-${e.end} not found in $cues", hit)
            assertEquals("start of ${e.text}", e.start, hit!!.start, 0.6)
            assertEquals("end of ${e.text}", e.end, hit.end, 0.6)
        }
        assertTrue("no more than a few spurious cues: $cues", cues.size <= expected.size + 1)
    }

    @Test fun burnedInChineseSubtitlesAreExtracted() {
        val video = File(ctx.cacheDir, "ocr-test.mp4"); video.delete()
        makeVideo(video)
        VideoFrameReader(video).use { r ->
            assertEquals(W, r.width); assertEquals(H, r.height)
            MlKitOcr().use { ocr ->
                // band detection on a sample of frames
                val t0 = System.nanoTime()
                val boxes = ArrayList<List<BandDetector.Box>>()
                for (i in 0 until 14) { val f = r.frameAt((i + 0.5).toLong() * 1_000_000L)!!; boxes.add(ocr.recognize(f).map { BandDetector.Box(it.text, it.top, it.bottom) }) }
                val band = BandDetector.fromBoxes(boxes, H) ?: BandFrac.DEFAULT
                Log.i("OcrPerf", "band ${band.top}..${band.bottom} detected in ${(System.nanoTime() - t0) / 1_000_000} ms")
                assertTrue("band should cover the subtitle rows (y 290-340): $band", band.top * H <= 295 && band.bottom * H >= 335)
                assertTrue("band should not be the whole picture: $band", band.top > 0.5f)

                val stats = OcrStats()
                val pipe = OcrPipeline(0.25, { ocr.recognize(it) }, stats)
                val t1 = System.nanoTime()
                r.run(0, Long.MAX_VALUE, 250_000L, { band.rows(it) }) { tUs, luma -> pipe.push(tUs / 1e6, luma) }
                stats.decodedFrames = r.decoded.toInt()
                val secs = (System.nanoTime() - t1) / 1e9
                val cues = pipe.finish()
                Log.i("OcrPerf", "cues=$cues")
                Log.i("OcrPerf", "video ${SECONDS}s: decoded ${stats.decodedFrames} frames, sampled ${stats.samples}, OCR calls ${stats.ocrCalls}, " +
                    "%.2f s wall = %.1f s per minute of video".format(secs, secs / SECONDS * 60))
                check(cues)
                assertTrue("OCR ran on far fewer samples than were taken: ${stats.ocrCalls} of ${stats.samples}", stats.ocrCalls * 2 < stats.samples)
            }
        }
    }

    @Test fun managerAddsAnOcrTrackToTheItem() {
        val video = File(ctx.cacheDir, "ocr-test2.mp4"); video.delete()
        makeVideo(video)
        val root = File(ctx.cacheDir, "ocr-store"); root.deleteRecursively()
        val media = MediaStore(root)
        val id = "ocrtest"
        media.save(MediaMeta(id, "https://example.com/v", "t", SECONDS, "x", "v", null, null, 1L))
        val vd = media.adoptVideo(id, video, "video/mp4")
        media.save(media.get(id)!!.copy(video = vd))
        val mgr = OcrManager(ctx, media)
        val band = mgr.detectBand(id)
        mgr.start(id, band, false)
        val deadline = System.currentTimeMillis() + 120_000
        while (mgr.state?.active != false && System.currentTimeMillis() < deadline) Thread.sleep(200)
        val s = mgr.state!!
        assertEquals(s.message, OcrState.Phase.DONE, s.phase)
        val tr = media.get(id)!!.tracks.single { it.ocr }
        assertEquals("zh-Hans", tr.language); assertEquals("Chinese (from picture)", tr.name)
        val saved = media.loadAdded(id, tr.trackId!!)!!
        assertEquals("ocr", saved.source)
        check(saved.lines)
        Log.i("OcrPerf", "manager: samples ${s.samples}, OCR calls ${s.ocrCalls}, cues ${s.cues}")
    }
}
