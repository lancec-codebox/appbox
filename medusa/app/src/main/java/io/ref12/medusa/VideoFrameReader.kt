package io.ref12.medusa

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaExtractor
import android.media.MediaFormat
import java.io.Closeable
import java.io.File

/**
 * Streams decoded frames of a video file with MediaExtractor + MediaCodec (not one MediaMetadataRetriever call per frame).
 * Only the luma plane of the wanted rows is copied out, so memory stays at one band per sample.
 *
 * One thread uses a reader from open to [close] (all methods are synchronized, and nothing else touches the codec). A codec that
 * fails mid-stream (IllegalStateException / CodecException, e.g. a software decoder that gives up) is dropped and the next
 * decoder from MediaCodecList takes over from the last sampled time; only when every decoder has failed does [run] throw
 * a [FetchException] with a plain message.
 */
class VideoFrameReader(file: File) : Closeable {
    private val extractor = MediaExtractor()
    private val format: MediaFormat
    private val mime: String
    private val track: Int
    val width: Int
    val height: Int
    val durationUs: Long
    private var codec: MediaCodec? = null
    /** Decoders to try, in order; [decoderIdx] is the one in use. */
    private val decoders: List<String>
    private var decoderIdx = 0
    private var dirty = false // the codec has seen end-of-stream or an abort: rebuild it before reuse
    /** Frames the decoder produced (all of them, sampled or not). */
    var decoded = 0L
        private set
    /** How many times a decoder was replaced after an error (for the report). */
    var recoveries = 0
        private set

    init {
        extractor.setDataSource(file.absolutePath)
        track = (0 until extractor.trackCount).firstOrNull { extractor.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("video/") == true }
            ?: throw FetchException("This file has no video track", Codes.UNSUPPORTED)
        extractor.selectTrack(track)
        format = extractor.getTrackFormat(track)
        mime = format.getString(MediaFormat.KEY_MIME)!!
        width = format.getInteger(MediaFormat.KEY_WIDTH)
        height = format.getInteger(MediaFormat.KEY_HEIGHT)
        durationUs = if (format.containsKey(MediaFormat.KEY_DURATION)) format.getLong(MediaFormat.KEY_DURATION) else 0L
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible)
        val names = ArrayList<String>()
        try { MediaCodecList(MediaCodecList.REGULAR_CODECS).findDecoderForFormat(format)?.let { names.add(it) } } catch (e: Exception) {}
        try {
            val all = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { !it.isEncoder && it.supportedTypes.any { t -> t.equals(mime, true) } }
            // software decoders last-but-reliable: the platform one (c2.android.*) goes right after the default choice
            names.addAll(all.map { it.name }.sortedBy { if (it.startsWith("c2.android.")) 0 else if (it.startsWith("OMX.google.")) 1 else 2 })
        } catch (e: Exception) {}
        decoders = names.distinct().ifEmpty { listOf("") }
        dirty = true
    }

    private fun newCodec(): MediaCodec {
        codec?.let { try { it.stop() } catch (e: Exception) {}; try { it.release() } catch (e: Exception) {} }
        codec = null
        var lastErr: Exception? = null
        while (decoderIdx < decoders.size) {
            val name = decoders[decoderIdx]
            try {
                val c = if (name.isEmpty()) MediaCodec.createDecoderByType(mime) else MediaCodec.createByCodecName(name)
                c.configure(format, null, null, 0); c.start()
                codec = c; dirty = false
                return c
            } catch (e: Exception) { lastErr = e; decoderIdx++ }
        }
        throw FetchException("This phone cannot decode this video (" + (lastErr?.message ?: "no decoder") + ")", Codes.UNSUPPORTED)
    }

    private fun usable(): MediaCodec = codec.takeIf { !dirty } ?: newCodec()

    /**
     * Decodes from [startUs] and calls [onFrame] with the frame time and the luma rows [band] of the first frame at or after each
     * sample time (every [stepUs]), until [endUs], [maxSamples], or [cancelled]. [band] gets the frame height. A failing decoder is
     * replaced and decoding goes on after the last sampled frame.
     */
    @Synchronized
    fun run(startUs: Long, endUs: Long, stepUs: Long, band: (Int) -> IntRange, cancelled: () -> Boolean = { false },
            maxSamples: Int = Int.MAX_VALUE, onFrame: (Long, Luma) -> Unit) {
        var from = startUs
        var samples = 0
        var failures = 0
        while (true) {
            try {
                samples += decodeFrom(from, endUs, stepUs, band, cancelled, maxSamples - samples) { t, l -> from = t + stepUs; onFrame(t, l) }
                return
            } catch (e: FetchException) { throw e
            } catch (e: Exception) {
                if (e !is IllegalStateException && e !is MediaCodec.CodecException) throw e
                dirty = true
                recoveries++
                // a recoverable or transient error: same decoder, fresh instance, twice; then the next decoder
                if (++failures % 3 == 0) decoderIdx++
                if (decoderIdx >= decoders.size) throw FetchException("The video decoder failed (" + (e.message ?: e.javaClass.simpleName) + "). Try again later.", Codes.UNSUPPORTED)
            }
        }
    }

    private fun decodeFrom(startUs: Long, endUs: Long, stepUs: Long, band: (Int) -> IntRange, cancelled: () -> Boolean, maxSamples: Int,
                           onFrame: (Long, Luma) -> Unit): Int {
        val codec = usable()
        extractor.seekTo(startUs, MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
        codec.flush()
        val info = MediaCodec.BufferInfo()
        var inputDone = false
        var nextUs = startUs
        var samples = 0
        var idle = 0
        try {
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
                if (o < 0) { if (++idle > 2000) throw IllegalStateException("decoder gave no frame"); continue }
                idle = 0
                val eos = info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0
                if (info.size > 0) {
                    decoded++
                    val t = info.presentationTimeUs
                    if (t >= endUs) { codec.releaseOutputBuffer(o, false); break }
                    if (t >= nextUs) {
                        val img = codec.getOutputImage(o)
                        if (img != null) { try { onFrame(t, readBand(img, band)) } finally { img.close() } }
                        samples++
                        while (nextUs <= t) nextUs += stepUs
                    }
                }
                codec.releaseOutputBuffer(o, false)
                if (eos) { dirty = true; break }
            }
        } finally {
            // a run that stopped early leaves queued input/output behind: the next run flushes, but after end-of-stream rebuild instead
            if (inputDone) dirty = true
        }
        return samples
    }

    /** One full-height luma frame at about [timeUs] (for band detection and the preview). */
    @Synchronized
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

    @Synchronized
    override fun close() {
        codec?.let { try { it.stop() } catch (e: Exception) {}; try { it.release() } catch (e: Exception) {} }
        codec = null
        extractor.release()
    }
}
