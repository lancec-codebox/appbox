package io.ref12.medusa.node

import io.ref12.medusa.VideoRequest
import org.json.JSONArray
import org.json.JSONObject

/** One way of asking YouTube for the video: what rides on the video request, whether WebM/Opus is tried first, and the height limit. */
data class Variant(val webmFirst: Boolean, val infoWithVideo: Boolean, val thumbWithVideo: Boolean, val height: Int) {
    /** What differs from [base], in words ("thumbnail with video", "WebM first off", "height 720"); "current settings" when nothing does. */
    fun describe(base: Variant): String {
        val l = ArrayList<String>()
        if (thumbWithVideo != base.thumbWithVideo) l.add("thumbnail with video " + (if (thumbWithVideo) "on" else "off"))
        if (infoWithVideo != base.infoWithVideo) l.add("info JSON with video " + (if (infoWithVideo) "on" else "off"))
        if (webmFirst != base.webmFirst) l.add("WebM first " + (if (webmFirst) "on" else "off"))
        if (height != base.height) l.add("height $height")
        return if (l.isEmpty()) "current settings" else l.joinToString(", ")
    }
}

/** One video attempt: WebM/Opus first (the job's own selector) or the library's MP4 selector, at a height. */
class Attempt(val webm: Boolean, val height: Int)

/** The order of the video attempts of one download, and the 403 policy. */
object NodeAttempts {
    /** Optional WebM first, then the library's MP4 selector at the limit, then (after a 403) once at the next height the library offers above it. */
    fun plan(webmFirst: Boolean, height: Int): List<Attempt> = buildList {
        if (webmFirst) add(Attempt(true, height))
        add(Attempt(false, height))
        VideoRequest.nextHeightAbove(height)?.let { add(Attempt(false, it)) }
    }

    /** Runs the attempts in order while the answer is a 403 on the video; any other failure ends it at once. Returns the attempt that worked and its index (0 = the first). */
    fun run(webmFirst: Boolean, height: Int, progress: (String) -> Unit, attempt: (Attempt) -> Unit): Pair<Attempt, Int> {
        val plan = plan(webmFirst, height)
        var last: NodeFetchException? = null
        for ((i, a) in plan.withIndex()) {
            if (i > 0) progress(if (a.height != height) "403 on video, retrying at " + a.height + "p" else Mp4Fallback.RETRY_TEXT)
            try { attempt(a); return a to i } catch (e: NodeFetchException) {
                if (e.kind != FailureKind.FORBIDDEN) throw e
                last = e
            }
        }
        val e = last!!
        throw NodeFetchException(FailureKind.FORBIDDEN, (if (plan.size > 1) Mp4Fallback.TWICE_TEXT + ": " else "") + e.message, e.trace)
    }
}

enum class TryOutcome { PASS, FORBIDDEN, BOT, ERROR }

/** What one diagnosis try ended in: the first 5 MB arrived (PASS), a 403, a bot block or 429, or something else. */
class TryResult(val outcome: TryOutcome, val formats: List<String> = emptyList(), val client: String? = null, val bytes: Long = 0, val error: String? = null)

class TryRecord(val variant: String, val result: String, val formats: List<String>, val client: String?) {
    fun toJson(): JSONObject = JSONObject().put("variant", variant).put("result", result).put("formats", JSONArray(formats)).put("client", client ?: JSONObject.NULL)
    companion object {
        fun parse(o: JSONObject) = TryRecord(o.optString("variant"), o.optString("result"), o.optJSONArray("formats")?.let { a -> (0 until a.length()).map { a.getString(it) } } ?: emptyList(), o.optString("client").ifEmpty { null })
    }
}

/** What a diagnosis found. [found] false: nothing passed (the tries are still recorded). It is the node's download profile when found. */
class DownloadProfile(val found: Boolean, val webmFirst: Boolean, val infoWithVideo: Boolean, val thumbWithVideo: Boolean, val maxHeight: Int?, val ytdlp: String, val foundAt: String, val tries: List<TryRecord>) {
    fun variant(height: Int) = Variant(webmFirst, infoWithVideo, thumbWithVideo, maxOf(height, maxHeight ?: 0))
    fun toJson(): JSONObject = JSONObject().put("found", found).also { o ->
        if (found) o.put("webmFirst", webmFirst).put("infoWithVideo", infoWithVideo).put("thumbWithVideo", thumbWithVideo).put("maxHeight", maxHeight ?: JSONObject.NULL)
        o.put("ytdlp", ytdlp).put("foundAt", foundAt).put("tries", JSONArray(tries.map { it.toJson() }))
    }
    companion object {
        fun parse(text: String?): DownloadProfile? = try {
            val o = JSONObject(text ?: return null)
            DownloadProfile(o.optBoolean("found", true), o.optBoolean("webmFirst", true), o.optBoolean("infoWithVideo", false), o.optBoolean("thumbWithVideo", false),
                if (o.isNull("maxHeight") || !o.has("maxHeight")) null else o.optInt("maxHeight"), o.optString("ytdlp"), o.optString("foundAt"),
                o.optJSONArray("tries")?.let { a -> (0 until a.length()).map { TryRecord.parse(a.getJSONObject(it)) } } ?: emptyList())
        } catch (e: Exception) { null }
    }
}

/** The automatic 403 diagnosis: variants of the video request tried one at a time, paced like every YouTube request, until one passes. */
object Diagnosis {
    const val MAX_TRIES = 8
    /** A try passes once this much video data has arrived (the try is then cancelled: no full download). */
    const val PASS_BYTES = 5L * 1024 * 1024

    /**
     * The variants to try, from the current settings [base], one thing changed per try: thumbnail with the video, info JSON with the video, WebM first, the height 720.
     * Then, if those all fail, the changes added together, so a pair of options that only works together is still found. At most [MAX_TRIES].
     */
    fun variants(base: Variant): List<Variant> {
        val one = listOf(
            base.copy(thumbWithVideo = !base.thumbWithVideo),
            base.copy(infoWithVideo = !base.infoWithVideo),
            base.copy(webmFirst = !base.webmFirst),
            base.copy(height = 720),
        )
        val more = listOf(
            base.copy(webmFirst = !base.webmFirst, height = 720),
            base.copy(infoWithVideo = !base.infoWithVideo, thumbWithVideo = !base.thumbWithVideo),
            base.copy(webmFirst = !base.webmFirst, infoWithVideo = !base.infoWithVideo, thumbWithVideo = !base.thumbWithVideo),
            base.copy(webmFirst = !base.webmFirst, infoWithVideo = !base.infoWithVideo, thumbWithVideo = !base.thumbWithVideo, height = 720),
        )
        return (one + more).filter { it != base }.distinct().take(MAX_TRIES)
    }

    /** Diagnose after a 403 only when there is no profile yet, yt-dlp was updated since, or the profile itself got a 403. A "nothing passed" result stands until yt-dlp is updated. */
    fun shouldRun(profile: DownloadProfile?, ytdlp: String?, usedProfile: Boolean): Boolean = when {
        profile == null -> true
        profile.ytdlp != (ytdlp ?: "unknown") -> true
        else -> profile.found && usedProfile
    }

    class Result(val profile: DownloadProfile, val botBlocked: Boolean, val botError: String?)

    /** [tryOne] runs one variant (pure from here: tests give a fake). [sleep] and [gapMs] keep the usual 20-40 s between YouTube requests; it waits before every try. */
    fun run(base: Variant, ytdlp: String, foundAt: String, sleep: (Long) -> Unit, gapMs: () -> Long, cancelled: () -> Boolean = { false },
            progress: (String) -> Unit = {}, tryOne: (Variant) -> TryResult): Result {
        val tries = ArrayList<TryRecord>()
        val list = variants(base)
        for ((i, v) in list.withIndex()) {
            if (cancelled()) break
            sleep(gapMs())
            if (cancelled()) break
            progress("403: trying " + v.describe(base) + " (" + (i + 1) + "/" + list.size + ")")
            val r = tryOne(v)
            tries.add(TryRecord(v.describe(base), when (r.outcome) { TryOutcome.PASS -> "pass"; TryOutcome.FORBIDDEN -> "403"; TryOutcome.BOT -> "bot block"; TryOutcome.ERROR -> "error" }, r.formats, r.client))
            when (r.outcome) {
                TryOutcome.PASS -> return Result(DownloadProfile(true, v.webmFirst, v.infoWithVideo, v.thumbWithVideo, v.height.takeIf { it != base.height }, ytdlp, foundAt, tries), false, null)
                TryOutcome.BOT -> return Result(DownloadProfile(false, base.webmFirst, base.infoWithVideo, base.thumbWithVideo, null, ytdlp, foundAt, tries), true, r.error)
                else -> {}
            }
        }
        return Result(DownloadProfile(false, base.webmFirst, base.infoWithVideo, base.thumbWithVideo, null, ytdlp, foundAt, tries), false, null)
    }
}

/** What the node needs of yt-dlp for the video request: a full download, a probe that stops after 5 MB, and the version. The Android class wraps youtubedl-android; tests give a fake. */
interface VideoIo {
    /** Downloads the video with [a] and [v]'s options into [dir]. Throws NodeFetchException (with a trace) on failure. [onPct] gets 0..100. */
    fun video(job: DownloadJob, dir: java.io.File, v: Variant, a: Attempt, onPct: (Int) -> Unit)
    /** One diagnosis try: the first attempt of [v], cancelled once 5 MB of video data arrived. */
    fun probe(job: DownloadJob, dir: java.io.File, v: Variant, a: Attempt): TryResult
    fun ytdlpVersion(): String
}

/** The node's own settings and profile, kept locally (NodeSettings; a fake in tests). */
interface DownloadPrefs {
    /** "Try WebM first": default on (the behaviour before 1.8.6). */
    var webmFirst: Boolean
    var profile: DownloadProfile?
}

class VideoResult(val attemptIndex: Int, val attempt: Attempt, val variant: Variant, val usedProfile: Boolean, val diagnosed: DownloadProfile?) {
    /** What downloaded.json records as formatFallback: null when the first attempt worked. */
    val formatFallback: String? get() = if (attemptIndex == 0) null else if (attempt.height != variant.height) "mp4@" + attempt.height else "mp4"
}

/** The video part of a node download: the plan of attempts, and on a 403 the diagnosis. Pure: all yt-dlp work goes through [io]. */
class NodeDownloader(private val io: VideoIo, private val prefs: DownloadPrefs, private val sleep: (Long) -> Unit, private val gapMs: () -> Long, private val nowIso: () -> String) {
    /** The variant a normal download uses: the profile when one was found (never below the job's height), else the settings. */
    fun variantFor(job: DownloadJob): Pair<Variant, Boolean> {
        val p = prefs.profile
        return if (p != null && p.found) p.variant(job.maxHeight) to true
        else Variant(prefs.webmFirst, infoWithVideo = false, thumbWithVideo = false, height = job.maxHeight) to false
    }

    fun download(job: DownloadJob, dir: java.io.File, progress: (String) -> Unit, cancelled: () -> Boolean = { false }): VideoResult {
        val (v, usedProfile) = variantFor(job)
        try {
            val (a, i) = NodeAttempts.run(v.webmFirst, v.height, progress) { att -> io.video(job, dir, v, att) { pct -> progress("download " + pct.coerceAtLeast(0) + "%") } }
            return VideoResult(i, a, v, usedProfile, null)
        } catch (e: NodeFetchException) {
            // only a 403 on the video is diagnosed; a bot block, a 429 or any other failure stays as it was
            if (e.kind != FailureKind.FORBIDDEN || cancelled() || !Diagnosis.shouldRun(prefs.profile, io.ytdlpVersion(), usedProfile)) throw e
            val base = v.copy(height = job.maxHeight)
            val res = Diagnosis.run(base, io.ytdlpVersion(), nowIso(), sleep, gapMs, cancelled, progress) { variant -> io.probe(job, dir, variant, NodeAttempts.plan(variant.webmFirst, variant.height).first()) }
            prefs.profile = res.profile
            if (res.botBlocked) throw NodeFetchException(FailureKind.BOT, "bot block during the 403 diagnosis: " + (res.botError ?: ""), e.trace)
            if (!res.profile.found) throw NodeFetchException(FailureKind.FORBIDDEN, "no working download option found (" + res.profile.tries.size + " tries): " + e.message, e.trace)
            progress("403: using " + res.profile.tries.last().variant)
            val v2 = res.profile.variant(job.maxHeight)
            val (a, i) = NodeAttempts.run(v2.webmFirst, v2.height, progress) { att -> io.video(job, dir, v2, att) { pct -> progress("download " + pct.coerceAtLeast(0) + "%") } }
            return VideoResult(i, a, v2, true, res.profile)
        }
    }
}
