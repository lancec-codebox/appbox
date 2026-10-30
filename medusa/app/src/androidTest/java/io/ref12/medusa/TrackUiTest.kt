package io.ref12.medusa

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.view.View
import android.view.ViewGroup
import android.widget.RadioButton
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import androidx.test.ext.junit.runners.AndroidJUnit4
import java.io.File

/** Drives the real screens on an emulator: capture list, CC menu, track switching and fullscreen; saves screenshots. */
@RunWith(AndroidJUnit4::class)
class TrackUiTest {
    private val inst = InstrumentationRegistry.getInstrumentation()
    private val ctx: Context = ApplicationProvider.getApplicationContext()
    private val id = "uitest01"
    private val shots = File("/sdcard/Download/medusa-shots").also { it.mkdirs() }

    @Before fun seed() {
        try { inst.uiAutomation.grantRuntimePermission(ctx.packageName, "android.permission.POST_NOTIFICATIONS") } catch (e: Exception) {}
        val media = ctx.app.media
        media.delete(id)
        val work = media.workDir(id)
        val mp4 = File(work, "video.mp4")
        InstrumentationRegistry.getInstrumentation().context.assets.open("test.mp4").use { i -> mp4.outputStream().use { i.copyTo(it) } }
        val video = media.adoptVideo(id, mp4, "video/mp4")
        work.deleteRecursively()
        fun lines(t: String) = (0 until 20).map { Line(it.toDouble(), it + 1.0, "$t line $it") }
        val en = SubtitlesData("captions", "en", lines("English"))
        val fr = SubtitlesData("auto-captions", "fr", lines("Francais"))
        media.saveTrack(id, en); media.saveTrack(id, fr)
        media.save(MediaMeta(id, "https://example.com/v", "Test video", 20, "test", "t1", en, video, 1L,
            listOf(TrackInfo("en", "captions", "English"), TrackInfo("fr", "auto-captions", "French"))))
        ctx.app.settings.setTrackChoice(id, "main", null); ctx.app.settings.setTrackChoice(id, "second", null)
        ctx.getSharedPreferences("media_helper", Context.MODE_PRIVATE).edit().remove("track.$id.main").remove("track.$id.second").apply()
    }

    private fun shot(name: String) {
        Thread.sleep(700)
        val bmp = inst.uiAutomation.takeScreenshot()
        File(shots, "$name.png").outputStream().use { bmp.compress(Bitmap.CompressFormat.PNG, 100, it) }
    }

    private fun find(root: View, tag: String): View? {
        if (tag == root.tag) return root
        if (root is ViewGroup) for (i in 0 until root.childCount) find(root.getChildAt(i), tag)?.let { return it }
        return null
    }

    /** Views in dialogs live in other windows: look in the activity and in the dialog's root. */
    private fun clickTag(a: android.app.Activity, tag: String) {
        inst.runOnMainSync {
            val v = find(a.window.decorView, tag) ?: dialogRoots().firstNotNullOfOrNull { find(it, tag) } ?: error("no view tagged $tag")
            v.performClick()
        }
    }

    @Suppress("UNCHECKED_CAST")
    private fun dialogRoots(): List<View> {
        val g = Class.forName("android.view.WindowManagerGlobal").getMethod("getInstance").invoke(null)
        val f = g.javaClass.getDeclaredField("mViews").also { it.isAccessible = true }
        return (f.get(g) as List<View>)
    }

    @Test fun capture_screen_lists_tracks_as_radio_buttons() {
        ActivityScenario.launch(MainActivity::class.java).use { sc ->
            sc.onActivity { it.go(Dest.CAPTURE) }
            sc.onActivity { it.setChoices(listOf(TrackInfo("en", "captions", "English"), TrackInfo("fr", "auto-captions", "French"), TrackInfo("zh-Hans", "captions", "Chinese (Simplified)"))) }
            sc.onActivity { a ->
                val scroll = a.window.decorView.findViewWithTag<View>("capture:en")
                assertTrue(scroll is RadioButton)
                assertTrue(find(a.window.decorView, "capture:original") is RadioButton)
                assertTrue(find(a.window.decorView, "capture:none") is RadioButton)
                scroll.parent.requestChildFocus(scroll, scroll)
                scroll.requestRectangleOnScreen(android.graphics.Rect(0, 0, scroll.width, scroll.height + 400), true)
            }
            shot("capture-screen-languages")
        }
    }

    @Test fun player_cc_menu_switches_main_and_second_line() {
        val i = Intent(ctx, PlayerActivity::class.java).putExtra(PlayerActivity.EXTRA_MEDIA, id).putExtra(PlayerActivity.EXTRA_PAUSED, true)
        ActivityScenario.launch<PlayerActivity>(i).use { sc ->
            Thread.sleep(1500)
            var a: PlayerActivity? = null
            sc.onActivity { a = it }
            assertEquals("en", a!!.chosenMain)
            assertTrue(a!!.subtitleText.startsWith("English line"))
            shot("player-portrait")
            clickTag(a!!, "Subtitles menu")
            Thread.sleep(600)
            shot("player-cc-menu")
            clickTag(a!!, "second:fr"); Thread.sleep(800)
            assertEquals("fr", a!!.chosenSecond)
            assertTrue(a!!.secondText.startsWith("Francais line"))
            clickTag(a!!, "main:fr"); Thread.sleep(800)
            assertEquals("fr", a!!.chosenMain)
            assertTrue(a!!.subtitleText.startsWith("Francais line"))
            shot("player-cc-menu-switched")
            assertEquals("fr", ctx.app.settings.trackChoice(id, "main")) // remembered per item
        }
        // reopened: the choice is remembered
        ActivityScenario.launch<PlayerActivity>(Intent(ctx, PlayerActivity::class.java).putExtra(PlayerActivity.EXTRA_MEDIA, id).putExtra(PlayerActivity.EXTRA_PAUSED, true)).use { sc ->
            Thread.sleep(1200)
            sc.onActivity { assertEquals("fr", it.chosenMain); assertEquals("fr", it.chosenSecond) }
            shot("player-portrait-two-lines")
        }
    }

    @Test fun fullscreen_is_landscape_with_subtitles_and_tools() {
        val i = Intent(ctx, PlayerActivity::class.java).putExtra(PlayerActivity.EXTRA_MEDIA, id).putExtra(PlayerActivity.EXTRA_PAUSED, true)
            .putExtra(PlayerActivity.EXTRA_FULLSCREEN, true)
        ActivityScenario.launch<PlayerActivity>(i).use { sc ->
            ctx.app.settings.setTrackChoice(id, "second", "fr")
            Thread.sleep(2500)
            sc.onActivity { assertTrue(it.isFullscreen); assertEquals(android.content.res.Configuration.ORIENTATION_LANDSCAPE, it.resources.configuration.orientation) }
            shot("player-fullscreen-landscape")
        }
    }
}
