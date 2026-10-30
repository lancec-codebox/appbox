package io.ref12.medusa

import android.app.AlertDialog
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.view.View
import android.widget.LinearLayout
import android.widget.SeekBar
import android.widget.TextView

/** The "Extract subtitles from the picture" card of the item screen, and its band-adjust dialog. */
object OcrUi {
    fun progressText(s: OcrState): String = when (s.phase) {
        OcrState.Phase.DETECTING -> "Looking for the subtitle area\u2026"
        OcrState.Phase.RUNNING -> {
            val eta = s.etaSec?.let { "  \u00b7  about " + Ui.duration(it.toInt().coerceAtLeast(1)) + " left" } ?: ""
            "${s.percent}%  \u00b7  ${s.samples} frames, ${s.ocrCalls} read  \u00b7  ${s.cues} lines$eta"
        }
        else -> s.message ?: ""
    }

    /** The card for [m], or null when the item has no video. */
    fun section(a: MainActivity, m: MediaMeta): View? {
        if (m.video == null) return null
        val app = a.app
        val st = app.ocr.state?.takeIf { it.mediaId == m.mediaId }
        val box = Ui.card(a).apply { tag = "ocr-card" }
        val status = Ui.muted(a, "", 13f).apply { tag = "ocr-status" }
        fun row(vararg v: View) = LinearLayout(a).apply { orientation = LinearLayout.HORIZONTAL; v.forEach { addView(it, Ui.lp(-2, -2, 0f, 0, 6, 8, 0, a)) } }
        if (st != null && st.active) {
            box.addView(Ui.text(a, "Reading subtitles from the picture", 14f, bold = true))
            box.addView(status.apply { text = progressText(st) })
            box.addView(row(Ui.button(a, "Cancel", danger = true) { app.ocr.cancel() }.apply { tag = "ocr-cancel" }))
            watch(a, box, m.mediaId)
        } else {
            val cp = app.ocr.checkpoint(m.mediaId)
            val offered = app.settings.ocrOffered(m.mediaId)
            val hasOcr = m.tracks.any { it.ocr }
            box.addView(Ui.text(a, "Subtitles from the picture", 14f, bold = true))
            box.addView(Ui.muted(a, when {
                offered && !hasOcr -> "No subtitle track was found for this video. If the subtitles are part of the picture, Medusa can read them (on this phone, a few minutes per hour of video)."
                st != null && st.phase == OcrState.Phase.DONE -> st.message ?: "Done"
                st != null && st.phase == OcrState.Phase.FAILED -> "Failed: " + (st.message ?: "unknown error")
                st != null && st.phase == OcrState.Phase.CANCELLED -> st.message ?: "Stopped"
                else -> "For videos whose subtitles are burned into the picture."
            }, 13f).apply { tag = "ocr-note" })
            val btns = ArrayList<View>()
            if (cp != null) {
                btns.add(Ui.button(a, "Resume (" + (if (cp.durationSec > 0) (cp.resumeSec / cp.durationSec * 100).toInt() else 0) + "%)", primary = true) {
                    app.settings.setOcrOffered(m.mediaId, false); app.ocr.start(m.mediaId, cp.band, true); a.show()
                }.apply { tag = "ocr-resume" })
                btns.add(Ui.button(a, "Start over") { app.ocr.discardCheckpoint(m.mediaId); a.show() })
            } else {
                btns.add(Ui.button(a, "Extract subtitles from the picture", primary = offered && !hasOcr) { configure(a, m) }.apply { tag = "ocr-start" })
            }
            if (offered) btns.add(Ui.button(a, "Not now") { app.settings.setOcrOffered(m.mediaId, false); a.show() })
            box.addView(row(*btns.toTypedArray()))
        }
        return box
    }

    /** Refreshes the status line while the card is on screen; redraws the item when the run ends. */
    private fun watch(a: MainActivity, box: View, mediaId: String) {
        val tick = object : Runnable {
            override fun run() {
                if (!box.isAttachedToWindow) return
                val s = a.app.ocr.state
                if (s == null || s.mediaId != mediaId || !s.active) { a.show(); return }
                box.findViewWithTag<TextView>("ocr-status")?.text = progressText(s)
                box.postDelayed(this, 1000)
            }
        }
        box.postDelayed(tick, 1000)
    }

    /** Finds the band (progress dialog), then lets the user adjust it on a preview frame before the run starts. */
    fun configure(a: MainActivity, m: MediaMeta) {
        val app = a.app
        app.settings.setOcrOffered(m.mediaId, false)
        val wait = AlertDialog.Builder(a).setTitle("Looking for the subtitle area").setMessage("Reading a few frames\u2026").setCancelable(false).show()
        Thread {
            var band = BandFrac.DEFAULT
            var frame: Luma? = null
            var err: String? = null
            try { band = app.ocr.detectBand(m.mediaId); frame = app.ocr.previewFrame(m.mediaId) } catch (e: Throwable) { err = e.message ?: e.javaClass.simpleName }
            a.runOnUiThread {
                wait.dismiss()
                if (err != null) AlertDialog.Builder(a).setTitle("Could not read the video").setMessage(err).setPositiveButton("OK", null).show()
                else adjust(a, m, band, frame)
            }
        }.start()
    }

    private fun adjust(a: MainActivity, m: MediaMeta, detected: BandFrac, frame: Luma?) {
        val preview = BandPreview(a, frame, detected)
        val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(Ui.dp(a, 16), Ui.dp(a, 8), Ui.dp(a, 16), 0) }
        col.addView(Ui.muted(a, "The red frame is where the subtitles are. Drag the sliders if it is wrong.", 13f))
        col.addView(preview, LinearLayout.LayoutParams(-1, Ui.dp(a, 190)))
        fun slider(label: String, init: Float, onChange: (Float) -> Unit) {
            col.addView(Ui.muted(a, label, 12f))
            col.addView(SeekBar(a).apply {
                max = 100; progress = (init * 100).toInt()
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(s: SeekBar?, p: Int, fromUser: Boolean) { if (fromUser) onChange(p / 100f) }
                    override fun onStartTrackingTouch(s: SeekBar?) {}
                    override fun onStopTrackingTouch(s: SeekBar?) {}
                })
            })
        }
        slider("Top of the subtitle area", detected.top) { preview.band = BandFrac(it.coerceAtMost(preview.band.bottom - 0.03f), preview.band.bottom); preview.invalidate() }
        slider("Bottom of the subtitle area", detected.bottom) { preview.band = BandFrac(preview.band.top, it.coerceAtLeast(preview.band.top + 0.03f)); preview.invalidate() }
        AlertDialog.Builder(a).setTitle("Subtitle area").setView(col)
            .setPositiveButton("Start") { _, _ -> a.app.ocr.start(m.mediaId, preview.band, false); a.show() }
            .setNegativeButton("Cancel", null).show()
    }

    /** The preview frame with the band drawn over it. */
    private class BandPreview(c: Context, frame: Luma?, var band: BandFrac) : View(c) {
        private val bmp: Bitmap? = frame?.let {
            val px = IntArray(it.w * it.h) { i -> val v = it.data[i].toInt() and 0xFF; Color.rgb(v, v, v) }
            Bitmap.createBitmap(px, it.w, it.h, Bitmap.Config.ARGB_8888)
        }
        private val line = Paint().apply { color = Color.RED; style = Paint.Style.STROKE; strokeWidth = 4f }
        private val fill = Paint().apply { color = 0x33FF0000 }
        override fun onDraw(c: Canvas) {
            val w = width.toFloat(); val h = height.toFloat()
            var left = 0f; var top = 0f; var dw = w; var dh = h
            bmp?.let { b ->
                val s = minOf(w / b.width, h / b.height); dw = b.width * s; dh = b.height * s; left = (w - dw) / 2; top = (h - dh) / 2
                c.drawBitmap(b, null, android.graphics.RectF(left, top, left + dw, top + dh), null)
            }
            val r = android.graphics.RectF(left, top + band.top * dh, left + dw, top + band.bottom * dh)
            c.drawRect(r, fill); c.drawRect(r, line)
        }
    }
}
