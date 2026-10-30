package io.ref12.medusa

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/** A medusa://open link lands on the Downloads page inside the normal shell, with its job card highlighted. */
@RunWith(AndroidJUnit4::class)
class DeepLinkRouteUiTest {
    private val ctx: Context = ApplicationProvider.getApplicationContext()

    @Test fun deep_link_opens_downloads_with_the_job_highlighted() {
        val url = Uri.encode("https://example.com/video?utm_source=x#frag")
        val intent = Intent(Intent.ACTION_VIEW, Uri.parse("medusa://open?job=dlroute1&url=$url"), ctx, MainActivity::class.java)
        ActivityScenario.launch<MainActivity>(intent).use { sc ->
            sc.onActivity { a ->
                assertEquals(Dest.DOWNLOADS, a.nav.current.dest)
                assertEquals("dlroute1", a.nav.current.arg)
                assertTrue(a.currentPage is DownloadsPage)
                assertNotNull(a.app.jobs.record("dlroute1"))
                assertEquals("https://example.com/video", a.app.jobs.record("dlroute1")!!.url)
            }
            Thread.sleep(500)
            sc.onActivity { a -> assertNotNull(a.window.decorView.findViewWithTag<android.view.View>("job:dlroute1")) }
        }
    }
}
