package io.ref12.medusa.node

import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files

class CrashLogTest {
    @Test fun writesTypeMessageStackVersionAndTimeAndRedactsTheSas() {
        val dir = Files.createTempDirectory("crash").toFile()
        assertNull(CrashLog.read(dir))
        val e = IllegalArgumentException("Expedited jobs cannot be battery not low constrained https://a.blob.core.windows.net/c?sp=r&sig=SECRET123")
        CrashLog.write(dir, RuntimeException("outer", e), "1.8.2", 1_800_000_000_000L)
        val raw = java.io.File(dir, CrashLog.FILE).readText()
        assertFalse(raw, raw.contains("SECRET123"))
        val r = CrashLog.read(dir)!!
        assertEquals("1.8.2", r.version); assertTrue(r.time.endsWith("Z")); assertTrue(r.line().startsWith("Last crash: " + r.time + " java.lang.RuntimeException: outer"))
        assertTrue(r.stack.contains("CrashLogTest")); assertTrue(r.text().contains("1.8.2"))
        assertTrue(r.stack.contains("sig=REDACTED"))
        CrashLog.clear(dir); assertNull(CrashLog.read(dir))
    }

    @Test fun theHandlerWritesTheRecordThenPassesTheCrashOn() {
        val dir = Files.createTempDirectory("crash").toFile()
        val old = Thread.getDefaultUncaughtExceptionHandler(); var passed: Throwable? = null
        try {
            Thread.setDefaultUncaughtExceptionHandler { _, e -> passed = e }
            CrashRedaction.install(dir, "1.8.2")
            Thread.getDefaultUncaughtExceptionHandler()!!.uncaughtException(Thread.currentThread(), IllegalStateException("boom"))
        } finally { Thread.setDefaultUncaughtExceptionHandler(old) }
        assertNotNull(passed); assertTrue(CrashLog.read(dir)!!.summary.contains("IllegalStateException: boom"))
    }

    @Test fun aClickThatThrowsShowsTheReasonInsteadOfCrashing() {
        val shown = ArrayList<String>()
        SafeClick.run("Run now", { shown.add(it) }) { throw IllegalArgumentException("Expedited jobs cannot be battery not low constrained") }
        assertEquals(1, shown.size); assertTrue(shown[0], shown[0].startsWith("Run now failed: IllegalArgumentException: Expedited"))
        SafeClick.run("Run now", { shown.add(it) }) { "Started" }
        SafeClick.run("Run now", { shown.add(it) }) { null }
        assertEquals(listOf(shown[0], "Started"), shown)
    }
}
