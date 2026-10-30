package io.ref12.medusa

import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Bitmap
import android.graphics.Color
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.MediaMuxer
import android.view.WindowInsets
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/**
 * Fullscreen orientation on a real (emulated) device: tall video -> sensorPortrait, wide -> sensorLandscape,
 * near-square keeps the current orientation, exit restores the previous one, subtitles sit above the navigation bar.
 * The videos are generated here (a few frames of H.264), so no binary assets are needed.
 */
@RunWith(AndroidJUnit4::class)
class FullscreenOrientationUiTest {
    private val inst = InstrumentationRegistry.getInstrumentation()
    private val ctx: Context = ApplicationProvider.getApplicationContext()
    private val shots = File("/sdcard/Download/medusa-shots").also { it.mkdirs() }

    /** Encodes ~2 s of a moving colour field at w x h into an mp4. */
    private fun makeVideo(file: File, w: Int, h: Int) {
        val fmt = MediaFormat.createVideoFormat("video/avc", w, h).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, 400_000); setInteger(MediaFormat.KEY_FRAME_RATE, 15)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
        }
        val codec = MediaCodec.createEncoderByType("video/avc")
        codec.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        val surface = codec.createInputSurface()
        codec.start()
        val muxer = MediaMuxer(file.path, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
        var track = -1; var started = false
        val info = MediaCodec.BufferInfo()
        fun drain(end: Boolean) {
            if (end) codec.signalEndOfInputStream()
            while (true) {
                val i = codec.dequeueOutputBuffer(info, 10_000)
                when {
                    i == MediaCodec.INFO_TRY_AGAIN_LATER -> if (!end) return
                    i == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> { track = muxer.addTrack(codec.outputFormat); muxer.start(); started = true }
                    i >= 0 -> {
                        val buf = codec.getOutputBuffer(i)!!
                        if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) info.size = 0
                        if (info.size > 0 && started) muxer.writeSampleData(track, buf, info)
                        codec.releaseOutputBuffer(i, false)
                        if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) return
                    }
                }
            }
        }
        val paint = android.graphics.Paint()
        for (n in 0 until 30) {
            val c = surface.lockCanvas(null)
            c.drawColor(Color.HSVToColor(floatArrayOf((n * 12f) % 360f, 0.6f, 0.8f)))
            paint.color = Color.WHITE; c.drawRect(n * 4f % w, 0f, n * 4f % w + 20f, h.toFloat(), paint)
            surface.unlockCanvasAndPost(c)
            drain(false)
        }
        drain(true)
        codec.stop(); codec.release(); muxer.stop(); muxer.release(); surface.release()
    }

    private fun seed(id: String, w: Int, h: Int): String {
        val media = ctx.app.media
        media.delete(id)
        val work = media.workDir(id)
        val mp4 = File(work, "video.mp4")
        makeVideo(mp4, w, h)
        val video = media.adoptVideo(id, mp4, "video/mp4")
        work.deleteRecursively()
        val en = SubtitlesData("captions", "en", (0 until 20).map { Line(it.toDouble(), it + 1.0, "Subtitle line $it") })
        media.saveTrack(id, en)
        // no stored width/height: the live Media3 video size has to decide
        media.save(MediaMeta(id, "https://example.com/$id", "Orientation $id", 2, "test", id, en, video, 1L,
            listOf(TrackInfo("en", "captions", "English"))))
        ctx.app.settings.setTrackChoice(id, "main", null); ctx.app.settings.setTrackChoice(id, "second", null)
        return id
    }

    private fun open(id: String) = ActivityScenario.launch<PlayerActivity>(
        Intent(ctx, PlayerActivity::class.java).putExtra(PlayerActivity.EXTRA_MEDIA, id).putExtra(PlayerActivity.EXTRA_PAUSED, true))

    private fun requested(sc: ActivityScenario<PlayerActivity>): Int { var r = -99; sc.onActivity { r = it.requestedOrientation }; return r }

    private fun awaitRequested(sc: ActivityScenario<PlayerActivity>, want: Int) {
        val end = System.currentTimeMillis() + 15_000
        while (System.currentTimeMillis() < end && requested(sc) != want) Thread.sleep(200)
        assertEquals(want, requested(sc))
    }

    private fun fullscreen(sc: ActivityScenario<PlayerActivity>) {
        // the player's own toggle is what the fullscreen button calls; the intent extra uses the same path
        sc.onActivity { a -> a.javaClass.getDeclaredMethod("toggleFullscreen").also { it.isAccessible = true }.invoke(a) }
    }

    private fun shot(name: String) {
        Thread.sleep(700)
        val bmp = inst.uiAutomation.takeScreenshot()
        File(shots, "$name.png").outputStream().use { bmp.compress(Bitmap.CompressFormat.PNG, 100, it) }
    }

    /** The subtitle view must end above the navigation-bar inset (gesture bar) and be on screen. */
    private fun assertSubtitlesAboveNavBar(sc: ActivityScenario<PlayerActivity>) {
        sc.onActivity { a ->
            val sub = a.subtitleView
            assertTrue("subtitle is shown", sub.visibility == android.view.View.VISIBLE && sub.text.isNotEmpty())
            val loc = IntArray(2); sub.getLocationOnScreen(loc)
            val bottom = loc[1] + sub.height
            val root = a.window.decorView
            var nav = 0
            if (android.os.Build.VERSION.SDK_INT >= 30)
                nav = root.rootWindowInsets?.getInsetsIgnoringVisibility(WindowInsets.Type.navigationBars())?.bottom ?: 0
            val screenH = a.resources.displayMetrics.heightPixels
            assertTrue("subtitle bottom $bottom must be <= ${screenH - nav} (screen $screenH, nav inset $nav)", bottom <= screenH - nav)
        }
    }

    @Test fun portrait_video_goes_sensor_portrait_and_restores_on_exit() {
        val id = seed("fsportrait", 176, 320)
        open(id).use { sc ->
            assertEquals(ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED, requested(sc))
            Thread.sleep(1500)
            fullscreen(sc)
            awaitRequested(sc, ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT)
            Thread.sleep(1000)
            assertSubtitlesAboveNavBar(sc)
            shot("fullscreen-portrait-subtitles")
            fullscreen(sc)
            assertEquals(ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED, requested(sc))
        }
    }

    @Test fun landscape_video_goes_sensor_landscape_and_restores_on_exit() {
        val id = seed("fslandscape", 320, 176)
        open(id).use { sc ->
            Thread.sleep(1500)
            fullscreen(sc)
            awaitRequested(sc, ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE)
            Thread.sleep(1500)
            assertSubtitlesAboveNavBar(sc)
            shot("fullscreen-landscape-subtitles")
            fullscreen(sc)
            assertEquals(ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED, requested(sc))
        }
    }

    @Test fun near_square_video_keeps_the_current_orientation() {
        val id = seed("fssquare", 192, 176) // aspect 1.09
        open(id).use { sc ->
            Thread.sleep(1500)
            var cur = 0
            sc.onActivity { cur = it.resources.configuration.orientation }
            val want = if (cur == android.content.res.Configuration.ORIENTATION_LANDSCAPE)
                ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE else ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
            fullscreen(sc)
            Thread.sleep(1500)
            assertEquals(want, requested(sc))
            sc.onActivity { assertEquals(cur, it.resources.configuration.orientation) }
            fullscreen(sc)
            assertEquals(ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED, requested(sc))
        }
    }
}
