package io.ref12.medusa

/** What an incoming intent asks the shell to do: show the Downloads page (and for a link, start the job). */
data class Route(
    val show: Boolean,
    val request: JobRequest? = null,
    val jobId: String? = null,
    val invalidLink: Boolean = false,
    /** True when Back must finish the activity and land in the app the link came from, unchanged. */
    val leaveOnBack: Boolean = false,
)

object LinkRouting {
    /** [referrerPackage] is the package of the app that started us (null when unknown or launched from the launcher or a notification). */
    fun resolve(data: String?, jobExtra: String?, openDownloads: Boolean, referrerPackage: String?, ownPackage: String, fromMedusa: Boolean = false, newId: () -> String = { java.util.UUID.randomUUID().toString() }): Route {
        if (data != null) {
            val req = DeepLink.parse(data, newId) ?: return Route(show = true, invalidLink = true, leaveOnBack = BackPolicy.hasPreviousApp(true, referrerPackage, ownPackage, fromMedusa))
            return Route(show = true, request = req, jobId = req.id, leaveOnBack = BackPolicy.hasPreviousApp(true, referrerPackage, ownPackage, fromMedusa))
        }
        if (jobExtra != null || openDownloads) return Route(show = true, jobId = jobExtra)
        return Route(show = false)
    }
}

/** Pure helpers of the Downloads page and the Library's active list. */
object DownloadsLogic {
    data class Groups(val active: List<JobMeta>, val today: List<JobMeta>, val week: List<JobMeta>)

    /** Active first (oldest first, the order they run in), then finished ones of today and of the last 7 days (newest first); older ones are left out. */
    fun group(all: List<JobMeta>, now: Long, startOfToday: Long): Groups {
        val active = all.filter { !it.state.finished }.sortedBy { it.updatedAt }
        val done = all.filter { it.state.finished }.sortedByDescending { it.updatedAt }
        val weekStart = startOfToday - 6L * 24 * 3600 * 1000
        return Groups(active, done.filter { it.updatedAt >= startOfToday }, done.filter { it.updatedAt in weekStart until startOfToday })
    }

    /** True when the bar should spin rather than show a number: nothing is known yet (0% is not information). */
    fun indeterminate(state: JobState, progress: Double?) = !state.finished && (progress == null || progress <= 0.0)

    fun stageText(state: JobState, message: String?, progress: Double?): String = when (state) {
        JobState.QUEUED -> "Waiting"
        JobState.FETCHING, JobState.DOWNLOADING ->
            (message ?: "Working") + (if (progress != null && progress > 0.0) " " + (progress * 100).toInt() + "%" else "")
        JobState.DONE -> "Done" + (message?.let { " \u00b7 $it" } ?: "")
        JobState.FAILED -> if (message == "Cancelled") "Cancelled" else "Failed: " + (message ?: "unknown error")
    }

    fun displayTitle(j: JobMeta) = j.title?.takeIf { it.isNotBlank() } ?: j.url
}
