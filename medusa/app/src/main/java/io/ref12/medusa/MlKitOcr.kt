package io.ref12.medusa

import android.graphics.Bitmap
import com.google.android.gms.tasks.Tasks
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.text.TextRecognition
import com.google.mlkit.vision.text.chinese.ChineseTextRecognizerOptions
import java.io.Closeable

/** ML Kit Text Recognition v2 with the Chinese model bundled in the APK (no Play Services model download). It also reads Latin text. */
class MlKitOcr : Closeable {
    private val recognizer = TextRecognition.getClient(ChineseTextRecognizerOptions.Builder().build())
    private var bmp: Bitmap? = null

    /** Recognises a grey-scale picture. One bitmap is kept and reused while the size stays the same. */
    fun recognize(l: Luma): List<OcrLine> {
        var b = bmp
        if (b == null || b.width != l.w || b.height != l.h) { b?.recycle(); b = Bitmap.createBitmap(l.w, l.h, Bitmap.Config.ARGB_8888); bmp = b }
        val px = IntArray(l.w)
        for (y in 0 until l.h) {
            for (x in 0 until l.w) { val v = l.data[y * l.w + x].toInt() and 0xFF; px[x] = (0xFF shl 24) or (v shl 16) or (v shl 8) or v }
            b.setPixels(px, 0, l.w, 0, y, l.w, 1)
        }
        val text = Tasks.await(recognizer.process(InputImage.fromBitmap(b, 0)))
        val out = ArrayList<OcrLine>()
        for (block in text.textBlocks) for (line in block.lines) {
            val r = line.boundingBox ?: continue
            out.add(OcrLine(line.text, line.confidence, r.left, r.top, r.right, r.bottom))
        }
        return out
    }

    override fun close() { recognizer.close(); bmp?.recycle(); bmp = null }
}
