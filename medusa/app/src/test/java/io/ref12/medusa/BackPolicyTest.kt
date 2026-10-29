package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class BackPolicyTest {
    @Test fun a_link_from_another_app_has_a_previous_app() {
        assertTrue(BackPolicy.hasPreviousApp(true, "com.android.chrome", "io.ref12.medusa", false))
        assertFalse(BackPolicy.hasPreviousApp(true, null, "io.ref12.medusa", false)) // no referrer: launched by the system
        assertFalse(BackPolicy.hasPreviousApp(true, "io.ref12.medusa", "io.ref12.medusa", false))
        assertFalse(BackPolicy.hasPreviousApp(false, null, "io.ref12.medusa", false)) // notification or launcher
        assertTrue(BackPolicy.hasPreviousApp(false, null, "io.ref12.medusa", true)) // opened from Medusa's own screens
    }

    @Test fun return_link_is_only_a_fallback() {
        assertTrue(BackPolicy.showReturnLink("https://ref12.github.io/app", false))
        assertFalse(BackPolicy.showReturnLink("https://ref12.github.io/app", true))
        assertFalse(BackPolicy.showReturnLink(null, false))
        assertEquals("Open ref12.github.io", BackPolicy.returnLabel("https://ref12.github.io/app#/x?y=1"))
        assertEquals("Open localhost", BackPolicy.returnLabel("http://localhost:5173/"))
    }

    @Test fun version_cache_never_blocks_the_caller() {
        val gate = CountDownLatch(1)
        val ex = Executors.newSingleThreadExecutor()
        val c = VersionCache({ gate.await(5, TimeUnit.SECONDS); "2025.01.01" }, ex)
        val t0 = System.nanoTime()
        assertEquals("...", c.get()) // yt-dlp is still starting: answered at once
        assertTrue((System.nanoTime() - t0) / 1_000_000 < 100)
        gate.countDown(); ex.shutdown(); ex.awaitTermination(5, TimeUnit.SECONDS)
        assertEquals("2025.01.01", c.get())
    }
}
