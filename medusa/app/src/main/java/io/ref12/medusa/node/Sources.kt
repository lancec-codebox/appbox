package io.ref12.medusa.node

import org.json.JSONObject
import java.net.URI

/** A followed source: a YouTube playlist or channel. [id] is the playlist id or the channel id (UC...). */
class SourceRef(val kind: String, val id: String) {
    val jobId: String get() = (if (kind == "playlist") "pl-" else "ch-") + id
    val path: String get() = WatchJobs.path(jobId)
}

object SourceUrl {
    private val HOSTS = setOf("youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be")
    private val LIST_OK = Regex("[A-Za-z0-9_-]{10,64}")
    private val CHANNEL_ID = Regex("UC[A-Za-z0-9_-]{22}")
    private val URL_IN_TEXT = Regex("https?://[^\\s<>\"]+", RegexOption.IGNORE_CASE)

    /** The first http(s) URL in shared text. */
    fun firstUrl(text: String?): String? = text?.let { URL_IN_TEXT.find(it)?.value?.trimEnd('.', ',', ')', ']') }

    private class Parts(val host: String, val segs: List<String>, val query: Map<String, String>)

    private fun parts(raw: String): Parts? = try {
        val u = URI(raw.trim().let { if (it.startsWith("http", true)) it else "https://$it" })
        val host = u.host?.lowercase() ?: return null
        if (host !in HOSTS) null else {
            val q = HashMap<String, String>()
            (u.rawQuery ?: "").split('&').filter { it.isNotEmpty() }.forEach { p -> val i = p.indexOf('='); val k = p.substringBefore('=');
                if (k !in q) q[k] = if (i < 0) "" else java.net.URLDecoder.decode(p.substring(i + 1), "UTF-8") }
            Parts(host, (u.rawPath ?: "").split('/').filter { it.isNotEmpty() }.map { java.net.URLDecoder.decode(it, "UTF-8") }, q)
        }
    } catch (e: Exception) { null }

    private val VIDEO_ID = Regex("[A-Za-z0-9_-]{11}")

    /** The 11-character id of a single-video URL (watch?v=, youtu.be/, /shorts/, /live/, /embed/), even with a list= beside it; null otherwise. */
    fun videoId(raw: String?): String? {
        val p = raw?.let { parts(it) } ?: return null
        val s = p.segs
        val id = when {
            p.host == "youtu.be" -> s.getOrNull(0)
            s.getOrNull(0) == "watch" -> p.query["v"]
            s.getOrNull(0) in setOf("shorts", "live", "embed") -> s.getOrNull(1)
            else -> null
        }
        return id?.takeIf { VIDEO_ID.matches(it) }
    }

    /** The playlist id of a "list=" URL; Watch Later, Liked videos and auto-made mixes are never sources. */
    private fun playlistId(p: Parts): String? = p.query["list"]?.takeIf { LIST_OK.matches(it) && it != "WL" && it != "LL" && !it.startsWith("RD") }

    /** True for a playlist or channel URL, with no network. A single video is false. */
    fun isSource(raw: String?): Boolean { val p = raw?.let { parts(it) } ?: return false; return playlistId(p) != null || channelUrl(p) != null }

    /** The channel URL to resolve (or that already carries the id), or null. */
    private fun channelUrl(p: Parts): String? {
        if (p.host == "youtu.be") return null
        val s = p.segs
        if (s.isEmpty()) return null
        return when {
            s[0] == "channel" && s.size >= 2 -> "https://www.youtube.com/channel/" + s[1]
            s[0].startsWith("@") && s[0].length > 1 -> "https://www.youtube.com/" + s[0]
            (s[0] == "c" || s[0] == "user") && s.size >= 2 -> "https://www.youtube.com/" + s[0] + "/" + s[1]
            else -> null
        }
    }

    /** A playlist (list=) or channel (UC id, @handle, /c/, /user/) to follow, or null (a single video, not YouTube). [resolve] turns a channel URL into its UC id (yt-dlp), null when it can't. */
    fun parse(raw: String?, resolve: (String) -> String? = { null }): SourceRef? {
        val p = raw?.let { parts(it) } ?: return null
        val isChannelPath = channelUrl(p) != null
        // a /watch or /playlist URL with list= is a playlist; a channel page is a channel
        if (!isChannelPath) return playlistId(p)?.let { SourceRef("playlist", it) }
        val cu = channelUrl(p)!!
        val direct = p.segs.getOrNull(1)?.takeIf { p.segs[0] == "channel" && CHANNEL_ID.matches(it) }
        if (p.segs[0] == "channel" && direct == null) return null
        val id = direct ?: resolve(cu)?.lineSequence()?.map { it.trim() }?.firstOrNull { it.isNotEmpty() }?.takeIf { CHANNEL_ID.matches(it) } ?: return null
        return SourceRef("channel", id)
    }

    /** What the typed name of the source should be: the handle for a channel URL, else the id. */
    fun nameHint(raw: String?, ref: SourceRef): String {
        val p = raw?.let { parts(it) }
        val h = p?.segs?.firstOrNull()?.takeIf { it.startsWith("@") }
        return h ?: ref.id
    }
}

class FollowOptions(val every: String = "1h", val backfill: Int = 0, val followChannels: Boolean = false, val name: String? = null, val maxHeight: Int = 480)

class Source(val jobId: String, val name: String, val kind: String, val every: String, val lastPolled: String?)

object WatchJobs {
    const val STAGE = "watch"
    val EVERY = listOf("1h", "6h", "1d")
    fun path(jobId: String) = "index/jobs/watch/$jobId.json"

    fun descriptor(ref: SourceRef, o: FollowOptions, addedAt: String): JSONObject = JSONObject()
        .put("v", 1).put("stage", STAGE).put("jobId", ref.jobId).put("kind", ref.kind).put("id", ref.id).put("name", o.name ?: ref.id)
        .put("every", o.every).put("backfill", o.backfill).also { if (ref.kind == "channel") it.put("tabs", org.json.JSONArray(listOf("videos", "shorts"))) }
        .put("followChannels", o.followChannels && ref.kind == "playlist").put("maxHeight", o.maxHeight)
        .put("addedAt", addedAt).put("lastPolled", JSONObject.NULL).put("attempts", 0)

    fun tags(now: String) = mapOf("idx_stage" to STAGE, "idx_state" to "pending", "idx_added" to now, "idx_due" to now)

    /** Writes the watch job unless it exists (If-None-Match: *). True when it was created, false when it was already there; an existing job is never touched. */
    fun follow(api: BlobApi, ref: SourceRef, o: FollowOptions, nowMs: Long): Boolean {
        val now = NodeRunner.iso(nowMs)
        return api.put(ref.path, descriptor(ref, o, now).toString().toByteArray(), "application/json", tags(now), null, true)
    }

    /** Find Blobs by Tags idx_stage='watch', then a read of each. */
    fun list(api: BlobApi): List<Source> = api.findAll(STAGE).mapNotNull { r ->
        try {
            val g = api.get(r.name); if (g.status != 200) return@mapNotNull null
            val o = JSONObject(g.text)
            Source(r.id, o.optString("name").ifEmpty { r.id }, o.optString("kind", if (r.id.startsWith("pl-")) "playlist" else "channel"), o.optString("every", "1h"),
                if (o.isNull("lastPolled")) null else o.optString("lastPolled").ifEmpty { null })
        } catch (e: Exception) { null }
    }.sortedBy { it.name.lowercase() }

    /** Deletes the watch job; its downloaded videos stay. */
    fun remove(api: BlobApi, jobId: String) = api.delete(path(jobId))
}

/** The outcome of "Follow in index". */
sealed class FollowResult {
    class Created(val ref: SourceRef) : FollowResult()
    class Exists(val ref: SourceRef) : FollowResult()
    object NotASource : FollowResult()
    class Failed(val message: String) : FollowResult()
}

object SourceFollow {
    /** Parses [raw] (resolving a handle through [resolve]) and creates its watch job unless it exists. Blocking: call it off the main thread. */
    fun run(api: BlobApi, raw: String, opts: FollowOptions, nowMs: Long, resolve: (String) -> String?): FollowResult {
        val url = SourceUrl.firstUrl(raw) ?: raw.trim()
        val ref = try { SourceUrl.parse(url, resolve) } catch (e: Exception) { null }
        if (ref == null) return if (SourceUrl.isSource(url)) FollowResult.Failed("Could not find the channel id of this URL") else FollowResult.NotASource
        return try {
            val named = FollowOptions(opts.every, opts.backfill, opts.followChannels, opts.name ?: SourceUrl.nameHint(url, ref), opts.maxHeight)
            if (WatchJobs.follow(api, ref, named, nowMs)) FollowResult.Created(ref) else FollowResult.Exists(ref)
        } catch (e: Exception) { FollowResult.Failed(Redact.sas(e.message ?: e.javaClass.simpleName)) }
    }
}

sealed class AddResult {
    object Added : AddResult()
    object Already : AddResult()
    class Failed(val message: String) : AddResult()
}

/** "Add to index" for one shared video: a download job, unless the job or its videos folder exists. */
object IndexVideo {
    fun jobPath(id: String) = "index/jobs/download/$id.json"

    fun descriptor(id: String, addedAt: String): JSONObject = JSONObject()
        .put("v", 1).put("stage", "download").put("jobId", id).put("url", "https://www.youtube.com/watch?v=$id")
        .put("maxHeight", 480).put("format", JSONObject.NULL).put("subs", true).put("source", "medusa-share")
        .put("addedAt", addedAt).put("attempts", 0)

    fun tags(now: String) = mapOf("idx_stage" to "download", "idx_state" to "pending", "idx_added" to now, "idx_due" to now)

    fun add(api: BlobApi, id: String, nowMs: Long): AddResult = try {
        when {
            api.get(jobPath(id)).status == 200 || api.listNames("index/videos/$id/", 1).isNotEmpty() -> AddResult.Already
            else -> { val now = NodeRunner.iso(nowMs)
                if (api.put(jobPath(id), descriptor(id, now).toString().toByteArray(), "application/json", tags(now), null, true)) AddResult.Added else AddResult.Already }
        }
    } catch (e: Exception) { AddResult.Failed(Redact.sas(e.message ?: e.javaClass.simpleName)) }

    /** Adds the video; with [alsoChannel], also follows its channel, found by [channelOf] (yt-dlp --print channel_id on the video URL). */
    fun share(api: BlobApi, raw: String, alsoChannel: Boolean, nowMs: Long, channelOf: (String) -> String?): Pair<AddResult, FollowResult?> {
        val url = SourceUrl.firstUrl(raw) ?: raw.trim()
        val id = SourceUrl.videoId(url) ?: return AddResult.Failed("That is not a YouTube video URL") to null
        val added = IndexVideo.add(api, id, nowMs)
        if (!alsoChannel) return added to null
        val ch = try { channelOf("https://www.youtube.com/watch?v=$id")?.lineSequence()?.map { it.trim() }?.firstOrNull { Regex("UC[A-Za-z0-9_-]{22}").matches(it) } } catch (e: Exception) { null }
            ?: return added to FollowResult.Failed("Could not find the channel of this video")
        val ref = SourceRef("channel", ch)
        return added to try { if (WatchJobs.follow(api, ref, FollowOptions(), nowMs)) FollowResult.Created(ref) else FollowResult.Exists(ref) }
            catch (e: Exception) { FollowResult.Failed(Redact.sas(e.message ?: e.javaClass.simpleName)) }
    }
}
