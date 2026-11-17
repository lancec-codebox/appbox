package io.ref12.medusa.node

/** What the node is waiting for, why it is not downloading. */
enum class Wait { NONE, NOT_CHARGING, NOT_UNMETERED, BATTERY_LOW, STORAGE_LOW, WINDOW }

/** The state the headline (screen and notification) is worked out from: every field the device and the node know, so [NodeStatus.text] alone decides what wins. */
class StatusInput(
    val enabled: Boolean = true,
    val downloading: Boolean = false, val doneToday: Int = 0, val cap: Int = 20, val title: String? = null, val progress: String? = null,
    val wait: Wait = Wait.NONE, val capReached: Boolean = false, val budgetSpent: Boolean = false, val stoppedByOwner: Boolean = false,
    val drain: Boolean = false, val nextCheckMin: Int? = null, val message: String? = null,
    /** Back-off: the local time it ends (already formatted) and why; set only while it is in force. */
    val backoffUntil: String? = null, val backoffReason: String? = null,
    val noWork: Boolean = false,
    /** The per-IP cap holds the node back: how many downloads are already running from this IP (null = not capped). */
    val ipCapRunning: Int? = null,
)

/** What the worker is doing right now, shared with the screen (same process). */
object NodeLive {
    @Volatile var input: StatusInput? = null
    /** Progress of storing a library download in the index (1.8.6), shown in the status notification while it runs. */
    @Volatile var store: String? = null
}

object NodeStatus {
    /** Precedence: off > downloading > backing off > waiting for the per-IP cap > waiting > stopped by the owner > cap / budget > draining > no work > idle. */
    fun text(s: StatusInput): String = when {
        !s.enabled -> "Index node is off"
        !s.downloading && NodeLive.store != null -> NodeLive.store!!
        s.downloading -> "Downloading: " + (s.title?.take(50) ?: "starting") + (s.progress?.let { " \u2014 " + friendlyProgress(it) } ?: "")
        s.backoffUntil != null -> "Backing off until " + s.backoffUntil + (s.backoffReason?.let { " \u2014 $it" } ?: "")
        s.ipCapRunning != null -> NetCap.waitText(s.ipCapRunning)
        s.wait != Wait.NONE -> "Waiting: " + when (s.wait) {
            Wait.NOT_CHARGING -> "not charging"; Wait.NOT_UNMETERED -> "not on Wi-Fi"; Wait.BATTERY_LOW -> "battery low"; Wait.STORAGE_LOW -> "storage low"; Wait.WINDOW -> "outside the schedule window"; else -> ""
        }
        s.stoppedByOwner -> "Downloads stopped by the owner" + (s.message?.let { " - $it" } ?: "")
        s.capReached -> "Daily cap reached (" + s.doneToday + "/" + s.cap + ")"
        s.budgetSpent -> "Budget spent"
        s.drain -> "Downloads are draining: no new jobs"
        s.noWork -> "No downloads queued"
        else -> "Idle: next check in " + (s.nextCheckMin ?: 15) + " min"
    }

    /** The fetcher's progress words as the headline says them. */
    fun friendlyProgress(p: String): String = when {
        p.startsWith("download ") -> "video " + p.removePrefix("download ")
        p.startsWith("upload ") -> "uploading " + p.removePrefix("upload ")
        else -> p
    }

    /** The status after a run, from the runner's end reason. */
    fun afterRun(endReason: String, doneToday: Int, cap: Int, nextCheckMin: Int = 15, message: String? = null): StatusInput = when {
        endReason.startsWith("daily cap") -> StatusInput(doneToday = doneToday, cap = cap, capReached = true)
        endReason.startsWith("budget spent") -> StatusInput(budgetSpent = true)
        endReason == "control: stop" -> StatusInput(stoppedByOwner = true, message = message)
        endReason == "control: drain" -> StatusInput(drain = true)
        endReason == "waiting for window" -> StatusInput(wait = Wait.WINDOW)
        endReason.startsWith(NodeRunner.IP_CAP_REASON) -> StatusInput(ipCapRunning = endReason.removePrefix(NodeRunner.IP_CAP_REASON).toIntOrNull() ?: 0)
        endReason == "no work" -> StatusInput(noWork = true, nextCheckMin = nextCheckMin)
        else -> StatusInput(nextCheckMin = nextCheckMin)
    }

    /** "downloaded 2, failed 1, backing off": what a run did, in words. */
    fun runSummary(done: Int, failed: Int, released: Int, endReason: String): String {
        val end = when {
            endReason.startsWith("bot block") -> "backing off"
            endReason == "backing off" -> "still backing off"
            endReason == "no work" -> "no downloads queued"
            endReason.startsWith("daily cap") -> "daily cap reached"
            endReason.startsWith("budget spent") -> "budget spent"
            endReason == "control: stop" -> "stopped by the owner"
            endReason == "control: drain" -> "draining"
            endReason == "waiting for window" -> "outside the schedule window"
            endReason.startsWith(NodeRunner.IP_CAP_REASON) -> NetCap.waitText(endReason.removePrefix(NodeRunner.IP_CAP_REASON).toIntOrNull() ?: 0).removePrefix("Waiting: ")
            endReason == "all jobs are held by other nodes" -> "all jobs held by other nodes"
            else -> endReason
        }
        return "downloaded $done, failed $failed" + (if (released > 0) ", released $released" else "") + (if (end.isNotEmpty()) ", $end" else "")
    }

    /** One recent-jobs line: local time, job, result, and the Medusa version that ran it (older entries are marked). */
    fun historyLine(e: JobLogEntry, fmt: (Long) -> String): String =
        fmt(e.at) + " \u00b7 " + e.jobId + " " + (e.title?.take(30) ?: "") + " - " + e.result + (e.message?.let { m -> " (" + m.take(220) + ")" } ?: "") +
            " \u00b7 " + (e.version?.let { "Medusa $it" } ?: "older version")

    /** The last-run line: when, which version, what it did. */
    fun lastRunLine(at: Long, version: String?, summary: String, fmt: (Long) -> String): String =
        if (at == 0L) "Last run: never" else "Last run: " + fmt(at) + " \u00b7 " + (version?.let { "Medusa $it" } ?: "older version") + " - " + summary

    /** Which condition is not met, in the order the owner would fix them; null when all are. */
    fun waitReason(needsCharging: Boolean, charging: Boolean, unmetered: Boolean, batteryLow: Boolean, storageLow: Boolean): Wait = when {
        !unmetered -> Wait.NOT_UNMETERED
        needsCharging && !charging -> Wait.NOT_CHARGING
        batteryLow -> Wait.BATTERY_LOW
        storageLow -> Wait.STORAGE_LOW
        else -> Wait.NONE
    }

    /** Progress text changes at most every [minMs]; anything else (a new state) goes through at once. */
    class Throttle(private val minMs: Long = 4000, private val clock: () -> Long = System::currentTimeMillis) {
        private var lastText: String? = null; private var lastAt: Long? = null
        fun allow(text: String, progressOnly: Boolean): Boolean {
            val now = clock()
            if (text == lastText) return false
            if (progressOnly && lastAt != null && now - lastAt!! < minMs) return false
            lastText = text; lastAt = now; return true
        }
    }
}
