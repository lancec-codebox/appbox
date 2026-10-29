package io.ref12.medusa

import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executor

class JobRecord(
    val id: String,
    val url: String,
    val languages: List<String>,
    val want: Want,
    val quality: Quality,
    val returnUrl: String?,
    val mediaId: String,
) {
    @Volatile var state = JobState.QUEUED
    @Volatile var progress: Double? = null
    @Volatile var code: String? = null
    @Volatile var message: String? = null
    @Volatile var title: String? = null
    @Volatile var updatedAt = 0L
}

class JobManager(
    private val storage: Storage,
    val media: MediaStore,
    private val fetcher: MediaFetcher,
    private val executor: Executor,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val jobs = ConcurrentHashMap<String, JobRecord>()

    /** Time of the last job change; the server idles out relative to this. */
    @Volatile var lastActivity: Long = clock()
        private set

    fun activeCount() = jobs.values.count { !it.state.finished }

    /** Starts a job, or returns the id of a running one for the same URL, or completes at once when already captured. */
    @Synchronized
    fun submit(req: JobSubmit): String {
        val langs = req.languages?.takeIf { it.isNotEmpty() } ?: DEFAULT_LANGUAGES
        val jobId = req.id ?: UUID.randomUUID().toString()
        record(jobId)?.let { return it.id }
        jobs.values.firstOrNull { !it.state.finished && it.url == req.url && it.languages == langs }?.let { return it.id }
        val mediaId = media.idFor(req.url)
        val rec = JobRecord(jobId, req.url, langs, req.want, req.quality, req.returnUrl, mediaId)
        jobs[jobId] = rec
        val existing = media.get(mediaId)
        if (existing != null && (!req.want.subtitles || existing.subtitles != null) && (!req.want.video || existing.video != null)) {
            rec.title = existing.title
            set(rec, JobState.DONE, null, null, 1.0)
            return jobId
        }
        set(rec, JobState.QUEUED, null, null, null)
        executor.execute { run(rec) }
        return jobId
    }

    fun info(id: String): JobInfo? = record(id)?.let { JobInfo(it.id, it.state, it.progress, it.code, it.message) }

    fun record(id: String): JobRecord? {
        jobs[id]?.let { return it }
        val m = storage.loadJob(id) ?: return null
        val r = JobRecord(m.id, m.url, m.languages, m.want, m.quality, m.returnUrl, m.mediaId ?: media.idFor(m.url))
        r.state = if (m.state.finished) m.state else JobState.FAILED
        r.code = if (m.state.finished) m.code else Codes.YTDLP
        r.message = if (m.state.finished) m.message else "Interrupted"
        r.progress = m.progress
        r.title = m.title
        r.updatedAt = m.updatedAt
        return jobs.putIfAbsent(id, r) ?: r
    }

    /** The media item JSON of a finished job. */
    fun resultJson(id: String): String? {
        val r = record(id) ?: return null
        if (r.state != JobState.DONE) return null
        return itemJson(r.mediaId)
    }

    fun itemJson(mediaId: String, withLines: Boolean = true): String? =
        media.get(mediaId)?.let { MediaJson.item(it, media.sync(mediaId), withLines).toString() }

    fun recent(): List<JobMeta> = storage.recentJobs()

    private fun run(rec: JobRecord) {
        set(rec, JobState.FETCHING, null, null, null, "Starting")
        val work = media.workDir(rec.mediaId)
        try {
            val out = fetcher.fetch(CaptureRequest(rec.url, rec.languages, rec.want.subtitles, rec.want.video, rec.quality), work) { st, msg, frac ->
                set(rec, st, null, msg, frac)
            }
            val old = media.get(rec.mediaId)
            val subtitles = out.subtitles ?: old?.subtitles
            var video = old?.video
            if (out.videoFile != null) {
                video = try {
                    media.adoptVideo(rec.mediaId, out.videoFile, contentTypeOf(out.videoFile.extension))
                } catch (e: java.io.IOException) {
                    throw FetchException("Could not store the video: " + e.message, Codes.STORAGE)
                }
            }
            val wantedGot = (rec.want.subtitles && out.subtitles != null) || (rec.want.video && out.videoFile != null)
            if (!wantedGot && !(old != null && ((rec.want.subtitles && old.subtitles != null) || (rec.want.video && old.video != null)))) {
                throw (if (rec.want.video && !rec.want.subtitles) out.videoError else if (rec.want.subtitles && !rec.want.video) out.subtitlesError else out.videoError ?: out.subtitlesError)
                    ?: FetchException("Nothing could be captured")
            }
            media.save(MediaMeta(rec.mediaId, rec.url, out.title.ifEmpty { old?.title ?: rec.url }, out.durationSec ?: old?.durationSec,
                out.extractor, out.videoId, subtitles, video, old?.createdAt ?: clock()))
            rec.title = out.title
            val note = listOfNotNull(
                if (rec.want.subtitles && out.subtitles == null) out.subtitlesError?.message else null,
                if (rec.want.video && out.videoFile == null) out.videoError?.message else null,
            ).joinToString(" ").ifEmpty { null }
            set(rec, JobState.DONE, null, note, 1.0)
        } catch (e: FetchException) {
            set(rec, JobState.FAILED, e.code, e.message, null)
        } catch (e: Throwable) {
            set(rec, JobState.FAILED, Codes.YTDLP, e.message ?: e.javaClass.simpleName, null)
        } finally {
            work.deleteRecursively()
        }
    }

    private fun set(rec: JobRecord, state: JobState, code: String?, message: String?, progress: Double?, keepMessage: String? = null) {
        rec.state = state
        rec.code = code
        rec.message = message ?: keepMessage
        if (progress != null) rec.progress = progress
        rec.updatedAt = clock()
        lastActivity = rec.updatedAt
        storage.saveJob(JobMeta(rec.id, rec.url, rec.languages, rec.want, rec.quality, state, rec.progress, code, rec.message, rec.title, rec.mediaId, rec.returnUrl, rec.updatedAt))
    }

    companion object {
        fun contentTypeOf(ext: String) = when (ext.lowercase()) {
            "mp4", "m4v" -> "video/mp4"
            "webm" -> "video/webm"
            "mkv" -> "video/x-matroska"
            else -> "application/octet-stream"
        }
    }
}
