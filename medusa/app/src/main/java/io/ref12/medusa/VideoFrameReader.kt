package io.ref12.medusa

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaExtractor
import android.media.MediaFormat
import java.io.Closeable
import java.io.File

/**
 * Streams decoded frames of a video file with MediaExtractor + MediaCodec (not one MediaMetadataRetriever call per frame).
 * Only the luma plane of the wanted rows is copied out, so memory stays at one band per sample.
 */
class VideoFrameReader(file: File) : Closeable {
    private val extractor = MediaExtractor()
    private val codec: MediaCodec
    val width: Int
    val height: Int
    val durationUs: Long
    /** Frames the decoder produced (all of them, sampled or not). */
    var decoded = 0L
        private set

    init {
        extractor.setDataSource(file.absolutePath)
        val track = (0 until extractor.trackCount).firstOrNull { extractor.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("video/") == true }
            ?: throw FetchException("This file has no video track", Codes.UNSUPPORTED)
        extractor.selectTrack(track)
        val f = extractor.getTrackFormat(track)
        width = f.getInteger(MediaFormat.KEY_WIDTH)
        height = f.getInteger(MediaFormat.KEY_HEIGHT)
        durationUs = if (f.containsKey(MediaFormat.KEY_DURATION)) f.getLong(MediaFormat.KEY_DURATION) else 0L
        f.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible)
        codec = MediaCodec.createDecoderByType(f.getString(MediaFormat.KEY_MIME)!!)
        codec.configure(f, null, null, 0)
        codec.start()
    }

    /**
     * Decodes from [startUs] and calls [onFrame] with the frame time and the luma rows [band] of the first frame at or after each
     * sample time (every [stepUs]), until [endUs], [maxSamples], or [cancelled]. [band] gets the frame height.
     */
    fun run(startUs: Long, endUs: Long, stepUs: Long, band: (Int) -> IntRange, cancelled: () -> Boolean = { false },
            maxSamples: Int = Int.MAX_VALUE, onFrame: (Long, Luma) -> Unit) {
        extractor.seekTo(startUs, MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
        codec.flush()
        val info = MediaCodec.BufferInfo()
        var inputDone = false
        var nextUs = startUs
        var samples = 0
        while (!cancelled() && samples < maxSamples) {
            if (!inputDone) {
                val i = codec.dequeueInputBuffer(5_000)
                if (i >= 0) {
                    val buf = codec.getInputBuffer(i)!!
                    val n = extractor.readSampleData(buf, 0)
                    if (n < 0) { codec.queueInputBuffer(i, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM); inputDone = true }
                    else { codec.queueInputBuffer(i, 0, n, extractor.sampleTime, 0); extractor.advance() }
                }
            }
            val o = codec.dequeueOutputBuffer(info, 5_000)
            if (o < 0) continue
            val eos = info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0
            if (info.size > 0) {
                decoded++
                val t = info.presentationTimeUs
                if (t >= endUs) { codec.releaseOutputBuffer(o, false); break }
                if (t >= nextUs) {
                    codec.getOutputImage(o)?.use { img -> onFrame(t, readBand(img, band)) }
                    samples++
                    while (nextUs <= t) nextUs += stepUs
                }
            }
            codec.releaseOutputBuffer(o, false)
            if (eos) break
        }
    }

    /** One full-height luma frame at about [timeUs] (for band detection and the preview). */
    fun frameAt(timeUs: Long): Luma? {
        var out: Luma? = null
        run(timeUs, Long.MAX_VALUE, 1_000_000L, { 0 until it }, maxSamples = 1) { _, l -> out = l }
        return out
    }

    private fun readBand(img: android.media.Image, band: (Int) -> IntRange): Luma {
        val crop = img.cropRect
        val w = crop.width(); val h = crop.height()
        val rows = band(h)
        val plane = img.planes[0]
        val buf = plane.buffer
        val rs = plane.rowStride; val ps = plane.pixelStride
        val out = ByteArray(w * rows.count())
        var o = 0
        for (y in rows) {
            val base = (y + crop.top) * rs + crop.left * ps
            if (ps == 1) { buf.position(base); buf.get(out, o, w) }
            else for (x in 0 until w) out[o + x] = buf.get(base + x * ps)
            o += w
        }
        return Luma(w, rows.count(), out)
    }

    override fun close() {
        try { codec.stop() } catch (e: Exception) {}
        try { codec.release() } catch (e: Exception) {}
        extractor.release()
    }

    private inline fun <T : AutoCloseable, R> T.use(f: (T) -> R): R = try { f(this) } finally { close() }
}
