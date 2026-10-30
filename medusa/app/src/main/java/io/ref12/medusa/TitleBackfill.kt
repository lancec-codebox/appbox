package io.ref12.medusa

/**
 * Looks up the device-language title once for existing items, one at a time and unhurried, so nobody has to tap
 * "Refresh details" after an update. [step] does one lookup attempt and says whether it is worth continuing.
 */
class TitleBackfill(
    private val ids: () -> List<String>,
    private val needs: (String) -> Boolean,
    private val busy: () -> Boolean,
    private val lookup: (String) -> Unit,
    private val pause: (Long) -> Unit = { Thread.sleep(it) },
    private val gapMs: Long = 3000,
) {
    private val tried = HashSet<String>()

    /** Runs until every needed item was tried once in this run (an item that fails is not retried until the next start). */
    fun run() {
        while (true) {
            if (busy()) { pause(gapMs * 5); if (busy()) return }
            val next = ids().firstOrNull { it !in tried && needs(it) } ?: return
            tried.add(next)
            try { lookup(next) } catch (e: Exception) { /* leave the original title */ }
            pause(gapMs)
        }
    }
}
