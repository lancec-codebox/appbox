package io.ref12.medusa.node

/** What the node is waiting for, why it is not downloading. */
enum class Wait { NONE, NOT_CHARGING, NOT_UNMETERED, BATTERY_LOW, STORAGE_LOW, WINDOW }

/** The state the status notification shows. */
class StatusInput(
    val enabled: Boolean = true,
    val downloading: Boolean = false, val doneToday: Int = 0, val cap: Int = 20, val title: String? = null, val progress: String? = null,
    val wait: Wait = Wait.NONE, val capReached: Boolean = false, val budgetSpent: Boolean = false, val stoppedByOwner: Boolean = false,
    val drain: Boolean = false, val nextCheckMin: Int? = null, val message: String? = null,
)

object NodeStatus {
    fun text(s: StatusInput): String = when {
        !s.enabled -> "Index node is off"
        s.downloading -> "Downloading " + (s.doneToday + 1) + "/" + s.cap + " today" + (s.title?.let { ": " + it.take(50) } ?: "") + (s.progress?.let { " " + it } ?: "")
        s.stoppedByOwner -> "Downloads stopped by the owner" + (s.message?.let { " - $it" } ?: "")
        s.wait != Wait.NONE -> "Waiting: " + when (s.wait) {
            Wait.NOT_CHARGING -> "not charging"; Wait.NOT_UNMETERED -> "not on Wi-Fi"; Wait.BATTERY_LOW -> "battery low"; Wait.STORAGE_LOW -> "storage low"; Wait.WINDOW -> "outside the schedule window"; else -> ""
        }
        s.capReached -> "Daily cap reached: resumes tomorrow"
        s.budgetSpent -> "Budget spent"
        s.drain -> "Downloads are draining: no new jobs"
        else -> "Idle: next check in " + (s.nextCheckMin ?: 15) + " min"
    }

    /** The status after a run, from the runner's end reason. */
    fun afterRun(endReason: String, doneToday: Int, cap: Int, nextCheckMin: Int = 15, message: String? = null): StatusInput = when {
        endReason.startsWith("daily cap") -> StatusInput(doneToday = doneToday, cap = cap, capReached = true)
        endReason.startsWith("budget spent") -> StatusInput(budgetSpent = true)
        endReason == "control: stop" -> StatusInput(stoppedByOwner = true, message = message)
        endReason == "control: drain" -> StatusInput(drain = true)
        endReason == "waiting for window" -> StatusInput(wait = Wait.WINDOW)
        else -> StatusInput(nextCheckMin = nextCheckMin)
    }

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
