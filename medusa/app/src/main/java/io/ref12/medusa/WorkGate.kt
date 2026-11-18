package io.ref12.medusa

import java.util.concurrent.CancellationException
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/** How many may run at once in each weight class (heavy, light). */
class GateLimits(val heavy: Int, val light: Int)

/**
 * Where all of Medusa's tool work waits its turn: /v1 downloads, /v2 runs and the index node's own downloads.
 * Work is heavy (a download, ffmpeg) or light (a listing, ffprobe, a metadata read), and interactive (a caller is waiting)
 * or background (Medusa's own node). Each weight has one limit; a waiting interactive ticket goes ahead of queued background ones
 * (a running job is never interrupted), so a light run never waits behind a big download and node work does not hold up a caller.
 */
class WorkGate(private val limits: () -> GateLimits) {
    enum class Weight(val wire: String) { HEAVY("heavy"), LIGHT("light") }
    enum class Priority(val wire: String) { INTERACTIVE("interactive"), BACKGROUND("background") }

    class Ticket(val label: String, val weight: Weight, val priority: Priority) {
        @Volatile internal var cancelled = false
    }

    /** A waiting ticket's place: 1 = next, and the labels of what holds the pool and of the ones ahead. */
    class Wait(val position: Int, val waitingBehind: List<String>)

    private val lock = ReentrantLock()
    private val changed = lock.newCondition()
    private val waiting = ArrayList<Ticket>()
    private val holders = ArrayList<Ticket>()

    private fun samePool(a: Ticket, b: Ticket) = a.weight == b.weight
    /** The queue of a pool: interactive tickets first, then background, each in arrival order. */
    private fun queueOf(t: Ticket) = waiting.filter { samePool(it, t) }.sortedBy { it.priority.ordinal }
    private fun limitOf(t: Ticket) = (if (t.weight == Weight.HEAVY) limits().heavy else limits().light).coerceAtLeast(1)

    /** Blocks until the ticket may run; [onWait] hears about each change of place. Throws [CancellationException] when cancelled first. */
    fun enter(t: Ticket, cancelled: () -> Boolean = { false }, onWait: (Wait) -> Unit = {}) {
        lock.withLock { waiting.add(t) }
        var last: Wait? = null
        try {
            while (true) {
                var w: Wait? = null
                lock.withLock {
                    if (t.cancelled || cancelled()) { waiting.remove(t); changed.signalAll(); throw CancellationException("cancelled while waiting") }
                    val pool = queueOf(t)
                    if (pool.first() === t && holders.count { samePool(it, t) } < limitOf(t)) {
                        waiting.remove(t); holders.add(t); changed.signalAll()
                        return
                    }
                    w = waitOf(t)
                    changed.await(500, TimeUnit.MILLISECONDS)
                }
                val cur = w!!
                if (last == null || last!!.position != cur.position || last!!.waitingBehind != cur.waitingBehind) { last = cur; onWait(cur) }
            }
        } catch (e: InterruptedException) {
            lock.withLock { waiting.remove(t); changed.signalAll() }
            throw CancellationException("interrupted")
        }
    }

    fun leave(t: Ticket) = lock.withLock { holders.remove(t); waiting.remove(t); changed.signalAll() }

    /** Takes a waiting ticket out of the queue (its [enter] throws). */
    fun cancel(t: Ticket) = lock.withLock { t.cancelled = true; changed.signalAll() }

    /** Where a waiting ticket stands now; null when it is not waiting. */
    fun waitOf(t: Ticket): Wait? = lock.withLock {
        val pool = queueOf(t)
        val i = pool.indexOf(t)
        if (i < 0) return null
        Wait(i + 1, holders.filter { samePool(it, t) }.map { it.label } + pool.take(i).map { it.label })
    }

    /** What is running now. */
    fun running(): List<Ticket> = lock.withLock { holders.toList() }

    /** Waits for a turn, runs [block], gives the turn back. */
    fun <T> run(label: String, weight: Weight, priority: Priority, cancelled: () -> Boolean = { false }, onWait: (Wait) -> Unit = {}, block: () -> T): T {
        val t = Ticket(label, weight, priority)
        enter(t, cancelled, onWait)
        try { return block() } finally { leave(t) }
    }

    companion object {
        /** The words for a waiting job, e.g. "Waiting for index-node download abc (2 ahead)". */
        fun describe(w: Wait): String {
            val first = w.waitingBehind.firstOrNull() ?: return "Waiting for a free slot"
            val more = w.position - 1
            return "Waiting for " + first + if (more > 0) " (and $more more ahead)" else ""
        }
    }
}
