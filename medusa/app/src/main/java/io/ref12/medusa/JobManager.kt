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
    mediaId: String,
) {
    /** The library item this job fills; may change to an existing item once the video's identity is known. */
    @Volatile var mediaId: String = mediaId
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
        val langs = req.languages?.filter { it.isNotBlank() } ?: emptyList()
        val requested = req.id
        if (requested != null) record(requested)?.let { return requested }
        val mediaId = media.lookupId(req.url)
        // the same video is already being captured: the requested id becomes an alias of that job
        jobs.values.firstOrNull { !it.state.finished && it.mediaId == mediaId && it.languages == langs }?.let { running ->
            if (requested == null) return running.id
            aliases[requested] = running.id
            storage.saveAlias(requested, running.id)
            return requested
        }
        val jobId = requested ?: UUID.randomUUID().toString()
        val rec = JobRecord(jobId, req.url, langs, req.want, req.quality, req.returnUrl, mediaId)
        jobs[jobId] = rec
        val existing = media.get(mediaId)
        if (existing != null && (!req.want.subtitles || (existing.subtitles != null && (langs.isEmpty() || langs.any { TrackSelector.languageMatches(existing.subtitles.language, it) }))) && (!req.want.video || existing.video != null)) {
            rec.title = existing.title
            set(rec, JobState.DONE, null, null, 1.0)
            return jobId
        }
        set(rec, JobState.QUEUED, null, null, null)
        executor.execute { run(rec) }
        return jobId
    }

    /** Status of a job under the id the client knows (its own id, or an alias of a job that was already running). */
    fun info(id: String): JobInfo? = record(id)?.let { JobInfo(id, it.state, it.progress, it.code, it.message) }

    private val aliases = ConcurrentHashMap<String, String>()

    /** A job by id or alias, from memory or from disk (jobs survive the process being killed). */
    fun record(id: String): JobRecord? {
        jobs[id]?.let { return it }
        val target = aliases[id] ?: storage.loadAlias(id)?.also { aliases[id] = it }
        if (target != null && target != id) return record(target)
        val m = storage.loadJob(id) ?: return null
        val r = JobRecord(m.id, m.url, m.languages, m.want, m.quality, m.returnUrl, m.mediaId ?: media.lookupId(m.url))
        r.title = m.title
        r.updatedAt = m.updatedAt
        r.progress = m.progress
        if (m.state.finished) {
            r.state = m.state; r.code = m.code; r.message = m.message
        } else {
            // it was running when the process died: say so, and keep that answer on disk
            r.state = JobState.FAILED; r.code = Codes.INTERRUPTED
            r.message = "Medusa was closed before this capture finished. Capture it again."
            r.updatedAt = clock()
            storage.saveJob(JobMeta(r.id, r.url, r.languages, r.want, r.quality, r.state, r.progress, r.code, r.message, r.title, r.mediaId, r.returnUrl, r.updatedAt))
        }
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

    /**
     * Lines of one subtitle track of a media item. The main track is answered from the item; another one is
     * downloaded on demand (once) and kept. Returns null for an unknown media item.
     */
    fun subtitlesFor(mediaId: String, language: String, trackId: String? = null): SubtitlesData? {
        val meta = media.get(mediaId) ?: return null
        if (trackId != null) {
            val t = meta.tracks.firstOrNull { it.trackId == trackId && it.added }
                ?: throw FetchException("This item has no added track $trackId", Codes.NO_CAPTIONS)
            return media.loadAdded(mediaId, t.trackId!!) ?: throw FetchException("The added track's lines are missing", Codes.NO_CAPTIONS)
        }
        meta.subtitles?.takeIf { it.language.equals(language, true) }?.let { return it }
        val captured = meta.tracks.filter { !it.added }
        val known = captured.firstOrNull { it.language.equals(language, true) }
            ?: captured.firstOrNull { TrackSelector.languageMatches(it.language, language) }
            ?: throw FetchException(TrackSelector.noCaptionsMessage(listOf(language), captured), Codes.NO_CAPTIONS)
        media.loadTrack(mediaId, known.language)?.let { return it }
        val work = media.workDir(mediaId)
        try {
            val s = fetcher.fetchTrack(meta.url, known.language, work)
            media.saveTrack(mediaId, s)
            return s
        } finally { work.deleteRecursively() }
    }

    /** Makes [language] the item's main subtitle track. Null for an unknown media item. */
    fun setMainTrack(mediaId: String, language: String): MediaMeta? {
        val meta = media.get(mediaId) ?: return null
        val s = subtitlesFor(mediaId, language) ?: return null
        val updated = meta.copy(subtitles = s)
        media.save(updated)
        media.saveTrack(mediaId, s)
        return updated
    }

    fun recent(): List<JobMeta> = storage.recentJobs()

    private fun run(rec: JobRecord) {
        set(rec, JobState.FETCHING, null, null, null, "Starting")
        val work = media.workDir(rec.mediaId)
        try {
            val existing = media.get(rec.mediaId)
            // only what is missing: the video is never downloaded twice, a subtitle track only when it is not stored
            val subsStored = existing?.subtitles != null && (rec.languages.isEmpty() || rec.languages.any { TrackSelector.languageMatches(existing.subtitles.language, it) })
            val wantSubs = rec.want.subtitles && !subsStored
            val wantVideo = rec.want.video && existing?.video == null
            val req = CaptureRequest(rec.url, rec.languages, wantSubs, wantVideo, rec.quality) { extractor, id ->
                media.findByIdentity(extractor, id)?.video != null
            }
            val out = fetcher.fetch(req, work) { st, msg, frac -> set(rec, st, null, msg, frac) }
            // the same video under another URL spelling lands in its existing item
            val target = media.findByIdentity(out.extractor, out.videoId)?.mediaId ?: rec.mediaId
            rec.mediaId = target
            val old = media.get(target)
            var subtitles = old?.subtitles
            if (out.subtitles != null) {
                old?.subtitles?.let { if (!it.language.equals(out.subtitles.language, true)) media.saveTrack(target, it) } // keep the previous main track
                subtitles = out.subtitles
                media.saveTrack(target, out.subtitles)
            }
            var video = old?.video
            if (out.videoFile != null) {
                video = try {
                    media.adoptVideo(target, out.videoFile, contentTypeOf(out.videoFile.extension))
                } catch (e: java.io.IOException) {
                    throw FetchException("Could not store the video: " + e.message, Codes.STORAGE)
                }
            }
            val gotSubs = out.subtitles != null || (rec.want.subtitles && subtitles != null && !wantSubs)
            val gotVideo = out.videoFile != null || (rec.want.video && video != null)
            if (!(rec.want.subtitles && gotSubs) && !(rec.want.video && gotVideo)) {
                throw (if (rec.want.video && !rec.want.subtitles) out.videoError else if (rec.want.subtitles && !rec.want.video) out.subtitlesError else out.videoError ?: out.subtitlesError)
                    ?: FetchException("Nothing could be captured")
            }
            media.save(MediaMeta(target, if (old != null) old.url else rec.url, out.title.ifEmpty { old?.title ?: rec.url }, out.durationSec ?: old?.durationSec,
                out.extractor.ifEmpty { old?.extractor ?: "" }, out.videoId.ifEmpty { old?.videoId ?: "" }, subtitles, video, old?.createdAt ?: clock(),
                out.tracks.ifEmpty { old?.tracks?.filter { !it.added } ?: emptyList() } + (old?.tracks?.filter { it.added } ?: emptyList())))
            rec.title = out.title
            val note = listOfNotNull(
                if (wantSubs && out.subtitles == null) out.subtitlesError?.message else null,
                if (wantVideo && out.videoFile == null) out.videoError?.message else null,
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
