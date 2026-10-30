package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.assertFalse
import org.junit.Test

class OcrLogicTest {
    /** A grey picture with a noisy "moving" background and optional white text blocks (rectangles) at the given columns. */
    private fun band(w: Int = 320, h: Int = 40, seed: Int = 0, blocks: List<IntRange> = emptyList()): Luma {
        val d = ByteArray(w * h)
        val rnd = java.util.Random(seed.toLong())
        for (i in d.indices) d[i] = (60 + rnd.nextInt(40)).toByte()
        for (b in blocks) for (y in 10 until 30) for (x in b) d[y * w + x] = 250.toByte()
        return Luma(w, h, d)
    }

    // --- change detection

    @Test fun sameTextOverMovingBackgroundIsUnchanged() {
        val c = ChangeDetector()
        assertTrue(c.changed(band(seed = 1, blocks = listOf(40..80, 100..140))))
        for (s in 2..8) assertFalse("background noise only (seed $s)", c.changed(band(seed = s, blocks = listOf(40..80, 100..140))))
    }

    @Test fun newTextIsAChange() {
        val c = ChangeDetector()
        c.changed(band(seed = 1, blocks = listOf(40..80)))
        assertTrue(c.changed(band(seed = 2, blocks = listOf(40..80, 150..200))))
        assertFalse(c.changed(band(seed = 3, blocks = listOf(40..80, 150..200))))
        assertTrue("text gone", c.changed(band(seed = 4)))
    }

    @Test fun emptyBandsStayUnchanged() {
        val c = ChangeDetector()
        assertTrue(c.changed(band(seed = 1)))
        assertFalse(c.changed(band(seed = 2)))
    }

    // --- cue building

    private fun feed(b: CueBuilder, from: Double, to: Double, text: String, step: Double = 0.25) {
        var t = from; while (t < to - 1e-9) { b.add(t, text); t += step }
    }

    @Test fun identicalSamplesMergeIntoOneCue() {
        val b = CueBuilder(0.25)
        feed(b, 1.0, 3.0, "你好"); feed(b, 3.0, 4.0, ""); feed(b, 4.0, 6.0, "再见")
        val c = b.finish()
        assertEquals(listOf(Line(1.0, 3.0, "你好"), Line(4.0, 6.0, "再见")), c)
    }

    @Test fun misreadCharacterStillMerges() {
        val b = CueBuilder(0.25)
        feed(b, 0.0, 1.0, "今天天气很好我们出去玩吧"); b.add(1.0, "今天天气很好我们出去玩吗"); feed(b, 1.25, 2.0, "今天天气很好我们出去玩吧")
        val c = b.finish()
        assertEquals(1, c.size)
        assertEquals("今天天气很好我们出去玩吧", c[0].text) // the most frequent reading wins
        assertEquals(0.0, c[0].start, 1e-9); assertEquals(2.0, c[0].end, 1e-9)
    }

    @Test fun shortDropoutDoesNotSplitACue() {
        val b = CueBuilder(0.25)
        feed(b, 0.0, 1.0, "我们走吧"); b.add(1.0, ""); feed(b, 1.25, 2.0, "我们走吧")
        assertEquals(1, b.finish().size)
    }

    @Test fun differentTextsAreSeparateAndSingleSampleNoiseIsDropped() {
        val b = CueBuilder(0.25)
        feed(b, 0.0, 1.0, "第一句话"); feed(b, 1.0, 2.0, "第二句话"); b.add(2.0, "乱码"); feed(b, 2.5, 3.0, "")
        val c = b.finish()
        assertEquals(listOf("第一句话", "第二句话"), c.map { it.text })
        assertEquals(1.0, c[0].end, 1e-9)
    }

    @Test fun cuesNeverOverlap() {
        val b = CueBuilder(0.25, gapSec = 2.0)
        feed(b, 0.0, 2.0, "甲乙丙丁戊"); feed(b, 2.0, 3.0, "子丑寅卯辰")
        val c = b.finish()
        assertTrue(c[0].end <= c[1].start)
    }

    @Test fun lineBreaksStayInsideACue() {
        val b = CueBuilder(0.25)
        feed(b, 0.0, 1.0, "第一行\n第二行")
        assertEquals("第一行\n第二行", b.finish()[0].text)
    }

    // --- text clean-up

    @Test fun normalisesPunctuationAndWidth() {
        assertEquals("你好，世界！", OcrText.normalizeLine("你好, 世界!"))
        assertEquals("ABC 123", OcrText.normalizeLine("ＡＢＣ １２３"))
        assertEquals("等一下\u2026", OcrText.normalizeLine("等一下..."))
        assertEquals("你好", OcrText.normalizeLine("| 你 好 _"))
    }

    @Test fun joinDropsLowConfidenceAndNoise() {
        val lines = listOf(OcrLine("第二行", 0.9f, 0, 50, 10, 60), OcrLine("第一行", 0.8f, 0, 10, 10, 20),
            OcrLine("噪", 0.2f, 0, 70, 10, 80), OcrLine("--", 0.95f, 0, 90, 10, 95))
        assertEquals("第一行\n第二行", OcrText.join(lines))
        assertEquals("", OcrText.join(listOf(OcrLine("--", 0.9f, 0, 0, 1, 1))))
    }

    @Test fun editDistance() {
        assertEquals(0, OcrText.distance("abc", "abc")); assertEquals(1, OcrText.distance("abc", "abd")); assertEquals(3, OcrText.distance("", "abc"))
        assertTrue(OcrText.similar("今天天气很好", "今天天气很奸")); assertFalse(OcrText.similar("今天天气很好", "明天去爬山吧"))
    }

    // --- script

    @Test fun simplifiedVersusTraditional() {
        assertEquals("zh-Hans", OcrText.language("这是我们的国家，你说对吗"))
        assertEquals("zh-Hant", OcrText.language("這是我們的國家，你說對嗎"))
        assertEquals("zh-Hans", OcrText.language("你好"))   // no evidence: Simplified
        assertEquals("en", OcrText.language("Hello there"))
        assertEquals("Chinese (from picture)", OcrText.label("zh-Hant"))
    }

    // --- band detection

    @Test fun boxesClusterAtTheBottom() {
        val frames = (0 until 20).map { i ->
            listOf(BandDetector.Box("LOGO", 20, 50), BandDetector.Box("字幕$i", 600 + (i % 3) * 2, 650 + (i % 3) * 2)) +
                if (i % 5 == 0) listOf(BandDetector.Box("路牌", 300, 340)) else emptyList()
        }
        val b = BandDetector.fromBoxes(frames, 720)!!
        assertTrue("top ${b.top}", b.top in 0.78f..0.85f)
        assertTrue("bottom ${b.bottom}", b.bottom in 0.91f..0.99f)
    }

    @Test fun watermarkAtTheBottomIsIgnored() {
        val frames = (0 until 20).map { i -> listOf(BandDetector.Box("WATERMARK", 680, 710), BandDetector.Box("台词$i", 560, 610)) }
        val b = BandDetector.fromBoxes(frames, 720)!!
        assertTrue(b.bottom < 0.9f)
    }

    @Test fun noBoxesNoBand() {
        assertNull(BandDetector.fromBoxes(List(10) { emptyList() }, 720))
    }

    @Test fun lumaBandFromSyntheticFrames() {
        // 30 frames, 640x360; "text" = alternating light/dark vertical strokes in rows 290..320
        val frames = (0 until 6).map { f ->
            val w = 640; val h = 360
            val d = ByteArray(w * h) { 90 }
            for (y in 290..320) for (x in 100 until 540) if ((x / 4 + f) % 2 == 0) d[y * w + x] = 250.toByte()
            Luma(w, h, d)
        }
        val b = BandDetector.fromLuma(frames)
        assertNotNull(b)
        assertTrue("top ${b!!.top}", b.top * 360 in 270f..292f)
        assertTrue("bottom ${b.bottom}", b.bottom * 360 in 320f..345f)
        assertNull(BandDetector.fromLuma(listOf(Luma(64, 64, ByteArray(64 * 64) { 90 }))))
    }

    // --- band rows

    @Test fun bandRows() {
        assertEquals(504 until 720, BandFrac(0.7f, 1f).rows(720))
        assertTrue(BandFrac(0.999f, 1f).rows(100).count() >= 2)
    }

    // --- pipeline: OCR only when the band changes

    @Test fun pipelineSkipsUnchangedFrames() {
        var calls = 0
        val pipe = OcrPipeline(0.25, { b -> calls++; if (b.data.any { (it.toInt() and 0xFF) >= 240 }) listOf(OcrLine("你好世界", 0.9f, 0, 10, 100, 30)) else emptyList() })
        // 40 s at 4 fps = 160 samples; one subtitle visible from 10 s to 30 s over moving background
        var t = 0.0; var i = 0
        while (t < 40.0) {
            val visible = t in 10.0..30.0
            pipe.push(t, band(seed = i++, blocks = if (visible) listOf(40..80, 100..140) else emptyList()))
            t += 0.25
        }
        val cues = pipe.finish()
        assertEquals(1, cues.size)
        assertEquals(10.0, cues[0].start, 0.3); assertEquals(30.25, cues[0].end, 0.3)
        assertEquals(160, pipe.stats.samples)
        assertTrue("OCR calls ${pipe.stats.ocrCalls} of 160", pipe.stats.ocrCalls <= 20)
    }

    @Test fun checkpointRoundTrip() {
        val c = OcrCheckpoint(BandFrac(0.7f, 0.95f), 42.5, listOf(Line(1.0, 2.0, "你好")), 3600.0)
        assertEquals(c, OcrCheckpoints.parse(OcrCheckpoints.toJson(c)))
    }

    @Test fun ocrTrackSourceIsAccepted() {
        val t = TrackRules.parse(org.json.JSONObject("""{"language":"zh-Hans","source":"ocr","origin":"ocr","lines":[{"start":1,"end":2,"text":"你好"}]}"""))
        assertEquals("ocr", t.source)
        assertTrue(TrackInfo("zh-Hans", "ocr").added && TrackInfo("zh-Hans", "ocr").ocr)
    }
}
