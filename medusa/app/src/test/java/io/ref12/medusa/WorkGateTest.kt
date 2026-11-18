package io.ref12.medusa

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class WorkGateTest {
    private fun gate(h: Int = 1, l: Int = 2) = WorkGate { GateLimits(h, l) }
    private fun t(l: String, w: WorkGate.Weight = WorkGate.Weight.HEAVY, p: WorkGate.Priority = WorkGate.Priority.INTERACTIVE) = WorkGate.Ticket(l, w, p)
    private fun waitFor(c: () -> Boolean) { val end = System.currentTimeMillis() + 5000; while (!c() && System.currentTimeMillis() < end) Thread.sleep(10); assertTrue(c()) }

    @Test fun secondHeavyWaitsAndSaysWhatItWaitsFor() {
        val g = gate(); val a = t("index-node download x", p = WorkGate.Priority.BACKGROUND); g.enter(a)
        val b = t("download b", p = WorkGate.Priority.BACKGROUND)
        val th = Thread { g.enter(b); g.leave(b) }.apply { start() }
        waitFor { g.waitOf(b) != null }
        val w = g.waitOf(b)!!
        assertEquals(1, w.position); assertEquals(listOf("index-node download x"), w.waitingBehind)
        assertEquals("Waiting for index-node download x", WorkGate.describe(w))
        g.leave(a); th.join(5000); assertFalse(th.isAlive)
    }

    @Test fun lightRunDoesNotWaitBehindDownload() {
        val g = gate(); val a = t("dl"); g.enter(a)
        val done = CountDownLatch(1)
        Thread { g.run("list", WorkGate.Weight.LIGHT, WorkGate.Priority.INTERACTIVE) { done.countDown() } }.start()
        assertTrue(done.await(3, TimeUnit.SECONDS))
        g.leave(a)
    }

    @Test fun interactiveGoesBeforeQueuedBackgroundWhenTheSlotFrees() {
        val g = gate(); val a = t("holder"); g.enter(a)
        val order = java.util.Collections.synchronizedList(ArrayList<String>())
        val bg = t("bg", p = WorkGate.Priority.BACKGROUND); val ia = t("caller")
        val t1 = Thread { g.enter(bg); order.add("bg"); g.leave(bg) }.apply { start() }
        waitFor { g.waitOf(bg) != null }
        val t2 = Thread { g.enter(ia); order.add("caller"); g.leave(ia) }.apply { start() }
        waitFor { g.waitOf(ia) != null }
        g.leave(a); t1.join(5000); t2.join(5000)
        assertEquals(listOf("caller", "bg"), order)
    }

    @Test fun cancelledWaiterThrows() {
        val g = gate(); val a = t("holder"); g.enter(a)
        val b = t("b"); var threw = false
        val th = Thread { try { g.enter(b) } catch (e: CancellationException) { threw = true } }.apply { start() }
        waitFor { g.waitOf(b) != null }
        g.cancel(b); th.join(5000)
        assertTrue(threw); g.leave(a)
    }

    @Test fun limitIsConfigurable() {
        val g = gate(h = 2); val a = t("a"); val b = t("b"); g.enter(a); g.enter(b)
        assertEquals(2, g.running().size)
        g.leave(a); g.leave(b)
    }

    @Test fun classifiesLightRuns() {
        assertEquals(WorkGate.Weight.LIGHT, io.ref12.medusa.v2.RunClass.weightOf("yt-dlp", listOf("--flat-playlist", "u")))
        assertEquals(WorkGate.Weight.LIGHT, io.ref12.medusa.v2.RunClass.weightOf("ffprobe", listOf("x")))
        assertEquals(WorkGate.Weight.HEAVY, io.ref12.medusa.v2.RunClass.weightOf("yt-dlp", listOf("-f", "b", "u")))
        assertEquals(WorkGate.Weight.HEAVY, io.ref12.medusa.v2.RunClass.weightOf("ffmpeg", listOf("-i", "x")))
    }
}
