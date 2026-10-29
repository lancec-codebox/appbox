package io.ref12.mediahelper

import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executor

fun interface TranscriptFetcher {
    /** Blocks until done; reports progress as short messages. Throws FetchException with a user-facing message. */
    fun fetch(url: String, languages: List<String>, progress: (String) -> Unit): Transcript
}

class JobRecord(
    val id: String,
    val url: String,
    val languages: List<String>,
    val returnUrl: String?,
) {
    @Volatile var state = JobState.QUEUED
    @Volatile var message: String? = null
    @Volatile var title: String? = null
    @Volatile var updatedAt = 0L
}

class JobManager(
    private val storage: Storage,
    private val fetcher: TranscriptFetcher,
    private val executor: Executor,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val jobs = ConcurrentHashMap<String, JobRecord>()

    /** Time of the last job change; the server idles out relative to this. */
    @Volatile var lastActivity: Long = clock()
        private set

    fun activeCount() = jobs.values.count { !it.state.finished }

    /** Starts a job, or returns the id of a cached or already running one for the same URL and languages. */
    @Synchronized
    fun submit(id: String?, url: String, languages: List<String>?, returnUrl: String? = null): String {
        val langs = languages?.takeIf { it.isNotEmpty() } ?: DEFAULT_LANGUAGES
        val jobId = id ?: UUID.randomUUID().toString()
        record(jobId)?.let { return it.id }
        val key = Storage.key(url, langs)
        jobs.values.firstOrNull { !it.state.finished && Storage.key(it.url, it.languages) == key }?.let { return it.id }
        val rec = JobRecord(jobId, url, langs, returnUrl)
        jobs[jobId] = rec
        val cached = storage.loadResult(key)
        if (cached != null) {
            rec.title = org.json.JSONObject(cached).optString("title").takeIf { it.isNotEmpty() }
            set(rec, JobState.DONE, null)
            return jobId
        }
        set(rec, JobState.QUEUED, null)
        executor.execute { run(rec, key) }
        return jobId
    }

    fun info(id: String): JobInfo? = record(id)?.let { JobInfo(it.id, it.state, it.message) }

    fun record(id: String): JobRecord? {
        jobs[id]?.let { return it }
        val m = storage.loadJob(id) ?: return null
        val r = JobRecord(m.id, m.url, m.languages, m.returnUrl)
        r.state = if (m.state.finished) m.state else JobState.FAILED
        r.message = if (m.state.finished) m.message else "Interrupted"
        r.title = m.title
        r.updatedAt = m.updatedAt
        return jobs.putIfAbsent(id, r) ?: r
    }

    fun resultJson(id: String): String? {
        val r = record(id) ?: return null
        if (r.state != JobState.DONE) return null
        return storage.loadResult(Storage.key(r.url, r.languages))
    }

    fun recent(): List<JobMeta> = storage.recentJobs()

    private fun run(rec: JobRecord, key: String) {
        set(rec, JobState.FETCHING, "Starting")
        try {
            val t = fetcher.fetch(rec.url, rec.languages) { msg -> set(rec, JobState.FETCHING, msg) }
            storage.saveResult(key, t.toJson().toString())
            rec.title = t.title
            set(rec, JobState.DONE, null)
        } catch (e: FetchException) {
            set(rec, JobState.FAILED, e.message)
        } catch (e: Throwable) {
            set(rec, JobState.FAILED, e.message ?: e.javaClass.simpleName)
        }
    }

    private fun set(rec: JobRecord, state: JobState, message: String?) {
        rec.state = state
        rec.message = message
        rec.updatedAt = clock()
        lastActivity = rec.updatedAt
        storage.saveJob(JobMeta(rec.id, rec.url, rec.languages, state, message, rec.title, rec.returnUrl, rec.updatedAt))
    }
}
