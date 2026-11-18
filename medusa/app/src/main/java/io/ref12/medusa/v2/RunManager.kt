package io.ref12.medusa.v2

import io.ref12.medusa.WorkGate
import java.io.File
import java.util.UUID
import java.util.concurrent.CancellationException
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

enum class RunState(val wire: String, val finished: Boolean) {
    QUEUED("queued", false), RUNNING("running", false), SUCCEEDED("succeeded", true),
    FAILED("failed", true), CANCELLED("cancelled", true), TIMED_OUT("timed_out", true)
}

/** What a tool sees of its run: a place to send output lines, and a way to be told to stop. */
class RunControl(private val sink: (String) -> Unit) {
    @Volatile var stopRequested = false
        private set
    private var killer: (() -> Unit)? = null
    fun emit(line: String) = sink(line)
    /** The tool says how to kill itself; runs at once when a stop was already asked for. */
    fun setKiller(k: (() -> Unit)?) {
        synchronized(this) { killer = k }
        if (stopRequested && k != null) k()
    }
    fun stop() {
        val k = synchronized(this) { stopRequested = true; killer }
        k?.invoke()
    }
}

/** Runs one tool to the end and returns its exit code; blocks. The app's version starts real processes, tests use a fake. */
interface ToolRunner {
    fun run(runId: String, tool: String, args: List<String>, workDir: File?, ctl: RunControl): Int
}

class Run(val id: String, val tool: String, val args: List<String>, val workspace: String?, val timeoutSec: Int, val queuedAt: Long,
          val weight: WorkGate.Weight = WorkGate.Weight.HEAVY, val priority: WorkGate.Priority = WorkGate.Priority.INTERACTIVE) {
    internal val ticket = WorkGate.Ticket("v2 run " + id + " (" + tool + ")", weight, priority)
    /** Where this run stands in its queue while it is queued: null otherwise. */
    @Volatile var waiting: WorkGate.Wait? = null
        internal set
    private val lock = ReentrantLock()
    private val changed = lock.newCondition()
    private var st = RunState.QUEUED
    private var code: Int? = null
    private var pct: Double? = null
    private var started = 0L
    private var ended = 0L
    private val lines = ArrayDeque<String>()
    private var base = 0
    private var total = 0
    @Volatile internal var ctl: RunControl? = null
    @Volatile internal var timedOut = false
    @Volatile internal var cancelRequested = false

    val state get() = lock.withLock { st }
    val exitCode get() = lock.withLock { code }
    /** yt-dlp's download percent (0-100), null when none was seen. */
    val progress get() = lock.withLock { pct }
    val startedAt get() = lock.withLock { started }
    val finishedAt get() = lock.withLock { ended }
    val lineCount get() = lock.withLock { total }

    internal fun addLine(line: String) = lock.withLock {
        lines.addLast(if (line.length > MAX_LINE) line.substring(0, MAX_LINE) else line)
        total++
        while (lines.size > MAX_LINES) { lines.removeFirst(); base++ }
        if (tool == "yt-dlp") PROGRESS.find(line)?.let { pct = it.groupValues[1].toDouble().coerceIn(0.0, 100.0) }
        changed.signalAll()
    }

    internal fun markRunning(now: Long) = lock.withLock { waiting = null; st = RunState.RUNNING; started = now; changed.signalAll() }

    internal fun finish(s: RunState, exit: Int?, now: Long) = lock.withLock {
        if (st.finished) return@withLock
        waiting = null
        st = s; code = exit; ended = now
        if (s == RunState.SUCCEEDED) pct = pct?.let { 100.0 } ?: pct
        changed.signalAll()
    }

    /** The lines from index [from] (older ones may have been dropped): the index of the first returned line and the lines. */
    fun linesFrom(from: Int): Pair<Int, List<String>> = lock.withLock {
        val start = maxOf(from, base)
        start to if (start - base >= lines.size) emptyList() else lines.toList().subList(start - base, lines.size)
    }

    fun tail(n: Int): List<String> = lock.withLock { lines.toList().takeLast(n) }

    /** Waits up to [timeoutMs] for [until] (checked under the run's lock); true when it held. */
    fun await(timeoutMs: Long, until: () -> Boolean): Boolean {
        var left = TimeUnit.MILLISECONDS.toNanos(timeoutMs)
        lock.withLock {
            while (!until()) {
                if (left <= 0) return false
                left = changed.awaitNanos(left)
            }
            return true
        }
    }

    companion object {
        const val MAX_LINES = 20000
        const val MAX_LINE = 16 * 1024 * 1024
        private val PROGRESS = Regex("""\[download]\s+(\d{1,3}(?:\.\d+)?)%""")
    }
}

/**
 * Tool runs: each waits its turn at the [gate] (shared with /v1 downloads and the index node), so a queued run says what it waits for.
 * [maxQueued] bounds how many runs may be waiting at once.
 */
class RunManager(
    private val runner: ToolRunner,
    val gate: WorkGate,
    val maxQueued: Int = 20,
    private val workDir: (String) -> File? = { null },
    private val onFinished: (Run) -> Unit = {},
    private val clock: () -> Long = System::currentTimeMillis,
    private val secondMs: Long = 1000,
    private val executor: ExecutorService = Executors.newCachedThreadPool { r -> Thread(r, "v2-run").apply { isDaemon = true } },
    private val timer: ScheduledExecutorService = Executors.newSingleThreadScheduledExecutor { r -> Thread(r, "v2-timer").apply { isDaemon = true } },
) {
    private val runs = LinkedHashMap<String, Run>()

    /** Starts a run (it may have to wait for a turn first); null when [maxQueued] runs are already waiting. */
    @Synchronized fun submit(tool: String, args: List<String>, workspace: String?, timeoutSec: Int,
                             priority: WorkGate.Priority = WorkGate.Priority.INTERACTIVE, weight: WorkGate.Weight = RunClass.weightOf(tool, args)): Run? {
        purge()
        if (runs.values.count { it.state == RunState.QUEUED } >= maxQueued) return null
        val r = Run(UUID.randomUUID().toString().replace("-", "").take(16), tool, args, workspace, timeoutSec, clock(), weight, priority)
        runs[r.id] = r
        r.waiting = WorkGate.Wait(1, emptyList()) // placeholder until the gate reports; replaced at once
        executor.execute { execute(r) }
        return r
    }

    @Synchronized fun get(id: String): Run? = runs[id]

    @Synchronized fun activeCount() = runs.values.count { !it.state.finished }

    @Synchronized fun isBusy(workspace: String) = runs.values.any { !it.state.finished && it.workspace == workspace }

    /** The running or queued run of [tool], if there is one. */
    @Synchronized fun active(tool: String) = runs.values.firstOrNull { it.tool == tool && !it.state.finished }

    /** What a queued run still waits for, refreshed from the gate. */
    fun waitOf(r: Run): WorkGate.Wait? = if (r.state == RunState.QUEUED) gate.waitOf(r.ticket) ?: r.waiting else null

    /** Cancels a queued run at once, or tells a running one to stop; null for an unknown id. */
    fun cancel(id: String): Run? {
        val r = synchronized(this) { runs[id] } ?: return null
        if (r.state.finished) return r
        r.cancelRequested = true
        if (r.state == RunState.QUEUED) {
            gate.cancel(r.ticket)
            r.finish(RunState.CANCELLED, null, clock())
        } else r.ctl?.stop()
        return r
    }

    private fun execute(r: Run) {
        try {
            gate.enter(r.ticket, { r.cancelRequested }) { w -> r.waiting = w }
        } catch (e: CancellationException) {
            r.finish(RunState.CANCELLED, null, clock())
            try { onFinished(r) } catch (x: Throwable) { }
            return
        }
        try {
            if (r.cancelRequested) { r.finish(RunState.CANCELLED, null, clock()); return }
            r.markRunning(clock())
            val ctl = RunControl { r.addLine(it) }
            r.ctl = ctl
            val t = timer.schedule({ r.timedOut = true; ctl.stop() }, r.timeoutSec * secondMs, TimeUnit.MILLISECONDS)
            var code = -1
            try {
                code = runner.run(r.id, r.tool, r.args, r.workspace?.let(workDir), ctl)
            } catch (e: Throwable) {
                r.addLine("error: " + (e.message ?: e.javaClass.simpleName))
            }
            t.cancel(false)
            val s = when {
                r.timedOut -> RunState.TIMED_OUT
                r.cancelRequested -> RunState.CANCELLED
                code == 0 -> RunState.SUCCEEDED
                else -> RunState.FAILED
            }
            r.finish(s, code, clock())
            try { onFinished(r) } catch (e: Throwable) { /* housekeeping only */ }
        } finally { gate.leave(r.ticket) }
    }

    private fun purge() {
        val now = clock()
        val done = runs.values.filter { it.state.finished }
        for (r in done) if (now - r.finishedAt > KEEP_MS) runs.remove(r.id)
        val left = runs.values.filter { it.state.finished }
        for (r in left.take(maxOf(0, left.size - KEEP_FINISHED))) runs.remove(r.id)
    }

    companion object {
        const val KEEP_MS = 6L * 3600 * 1000
        const val KEEP_FINISHED = 200
    }
}
