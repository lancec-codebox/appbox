package io.ref12.medusa

/** How long the local server stays up after the user leaves Medusa. */
enum class KeepAliveMode(val minutes: Int, val label: String) {
    MIN10(10, "10 minutes"), MIN30(30, "30 minutes"), HOUR2(120, "2 hours"), ALWAYS(0, "Always");

    val millis: Long get() = minutes * 60_000L

    companion object {
        val DEFAULT = MIN30
        fun fromMinutes(m: Int): KeepAliveMode = values().firstOrNull { it.minutes == m } ?: DEFAULT
    }
}

object KeepAlive {
    /**
     * True when the server should stop: no Medusa screen is open, no job is active, and the last request, job change
     * or screen close is older than the chosen time. "Always" never stops. Requests and jobs reset the timer.
     */
    fun shouldStop(mode: KeepAliveMode, now: Long, lastRequest: Long, lastJobActivity: Long, lastUiLeft: Long, activeJobs: Int, uiOpen: Boolean): Boolean {
        if (mode == KeepAliveMode.ALWAYS || uiOpen || activeJobs > 0) return false
        val idleSince = maxOf(lastRequest, lastJobActivity, lastUiLeft)
        return now - idleSince >= mode.millis
    }
}

/** What "Back" means on the job screen, and when the web app's return link is offered instead. */
object BackPolicy {
    /** True when finishing the activity lands in another app or Medusa screen the user came from. */
    fun hasPreviousApp(launchedByLink: Boolean, referrerPackage: String?, ownPackage: String, fromMedusa: Boolean): Boolean =
        fromMedusa || (launchedByLink && referrerPackage != null && referrerPackage != ownPackage)

    /** The return URL is only a fallback link, for when there is nothing to go back to. */
    fun showReturnLink(returnUrl: String?, hasPrevious: Boolean) = returnUrl != null && !hasPrevious

    fun returnLabel(returnUrl: String): String =
        "Open " + (Regex("^https?://([^/?#:]+)", RegexOption.IGNORE_CASE).find(returnUrl)?.groupValues?.get(1) ?: "app")
}

/** The yt-dlp version without blocking the caller: the status endpoint must answer at once. */
class VersionCache(private val compute: () -> String, private val executor: java.util.concurrent.Executor) {
    @Volatile private var value: String? = null
    private val started = java.util.concurrent.atomic.AtomicBoolean(false)

    fun get(): String {
        if (started.compareAndSet(false, true)) executor.execute { value = try { compute() } catch (e: Throwable) { "unknown" } }
        return value ?: "..."
    }
}
