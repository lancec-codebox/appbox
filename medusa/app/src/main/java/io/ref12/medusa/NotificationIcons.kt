package io.ref12.medusa

/** Which small icon a Medusa notification gets: the animated system download icon only while a library download the owner started runs. */
object NotificationIcons {
    enum class Kind { STATIC, DOWNLOADING }

    /** [activeLibraryJobs] is the library's running jobs; the server, node and OCR notifications never use the animated icon. */
    fun forServer(activeLibraryJobs: Int, ocrActive: Boolean): Kind =
        if (!ocrActive && activeLibraryJobs > 0) Kind.DOWNLOADING else Kind.STATIC

    fun forNode(): Kind = Kind.STATIC

    fun res(kind: Kind): Int = if (kind == Kind.DOWNLOADING) android.R.drawable.stat_sys_download else R.drawable.ic_stat_medusa
}
