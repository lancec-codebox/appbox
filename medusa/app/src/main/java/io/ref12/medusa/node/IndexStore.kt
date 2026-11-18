package io.ref12.medusa.node

import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/** What the library download is stored as: only the video (no step jobs), or the video plus the same step jobs a node download creates. */
enum class StoreMode(val wire: String, val label: String) {
    STORE_ONLY("store", "Store only"), STORE_AND_PROCESS("process", "Store and process");
    companion object { fun parse(s: String?) = entries.firstOrNull { it.wire == s } ?: STORE_ONLY }
}

/** Everything of one library download that goes to index/videos/<videoId>/. [extras] are (name under the video folder, file, content type): info.json, description.txt, thumb.jpg, subs/<lang>.vtt. */
class StoreFiles(
    val videoId: String, val url: String, val video: File, val extras: List<Triple<String, File, String>>, val subs: List<String>,
    val height: Int?, val durationSeconds: Double?, val info: NodeInfo.Parsed? = null, val formatId: String? = null,
)

enum class StoreResult { STORED, ALREADY }

/**
 * Library downloads into the index (1.9.0 layout), write-only as storage. video.mp4 goes first (8 MiB blocks), then download.zip LAST and create-only (If-None-Match: *): the zip is the
 * completion signal (it replaces downloaded.json), so a drama with a video and no zip is half-written and never taken for complete, and a rerun just continues. If download.zip already exists
 * (checked before anything is uploaded, and again by the create-only write: 409/412) the video is already in the index and nothing is replaced. Then the download job as done and, for
 * "store and process", the same step jobs a node download creates (comments plus one job per ingest step, with needs).
 * [done] lists what an interrupted run already uploaded (the caller keeps it), so a resumed run continues instead of starting over.
 */
object IndexStore {
    const val ORIGIN = "medusa-library"
    private const val MARKER = StageZip.NAME

    fun marker(f: StoreFiles, nodeId: String, at: String): JSONObject = JSONObject().put("v", 1).put("bytes", f.video.length()).put("height", f.height ?: JSONObject.NULL)
        .put("durationSeconds", f.durationSeconds ?: JSONObject.NULL).put("vcodec", f.info?.vcodec ?: JSONObject.NULL).put("acodec", f.info?.acodec ?: JSONObject.NULL)
        .put("formatId", f.formatId ?: f.info?.formatId ?: JSONObject.NULL).put("subs", JSONArray(f.subs)).put("subsFailed", JSONArray())
        .put("embeddable", f.info?.embeddable ?: JSONObject.NULL).put("audioLanguage", f.info?.audioLanguage ?: JSONObject.NULL).put("audioNote", f.info?.audioNote ?: JSONObject.NULL)
        .put("audioTracks", JSONArray().also { a -> (f.info?.audioTracks ?: emptyList()).forEach { a.put(JSONObject().put("language", it.first).put("note", it.second ?: JSONObject.NULL)) } })
        .put("nodeId", nodeId).put("at", at).put("origin", ORIGIN)

    fun store(api: BlobApi, f: StoreFiles, mode: StoreMode, nodeId: String, priority: Int, nowIso: String, done: MutableSet<String>,
              cancelled: () -> Boolean = { false }, onProgress: (Long, Long) -> Unit = { _, _ -> }, persist: () -> Unit = {}): StoreResult {
        val base = "index/videos/${f.videoId}/"
        if ("video.mp4" !in done) {
            if (api.exists(base + MARKER)) return StoreResult.ALREADY   // the video is in the index (a node's or an earlier store): nothing is uploaded or replaced
            api.putBlocks(base + "video.mp4", f.video, "video/mp4", emptyMap(), cancelled, onProgress); done.add("video.mp4"); persist()
        }
        if (cancelled()) throw BlobCancelled()
        if (MARKER !in done) {
            // last, create-only: the zip appearing is the moment the drama counts as downloaded
            if (!StageZip.upload(api, base, f.extras, marker(f, nodeId, nowIso), nodeId, nowIso, File(f.video.parentFile ?: File("."), ".zipwork"), true, cancelled)) return StoreResult.ALREADY
            done.add(MARKER); persist()
        }
        if (cancelled()) throw BlobCancelled()
        finishJobs(api, f, mode, nodeId, priority, nowIso)
        return StoreResult.STORED
    }

    /** The download job: created as done, or set to done when it is pending (under its lease); one that is already done or failed is left alone. So no node downloads this video again. */
    private fun finishJobs(api: BlobApi, f: StoreFiles, mode: StoreMode, nodeId: String, priority: Int, now: String) {
        val path = IndexVideo.jobPath(f.videoId)
        fun body(o: JSONObject) = o.put("finishedAt", now).put("nodeId", nodeId).put("origin", ORIGIN).put("elapsedSeconds", 0).toString().toByteArray()
        val tags = Prio.jobTags("download", "done", now, priority)
        if (!api.put(path, body(IndexVideo.descriptor(f.videoId, now, priority)), "application/json", tags, null, true)) {
            val cur = api.getTags(path)
            if (cur["idx_state"] == "pending") {
                val lease = api.acquireLease(path, 60)
                if (lease != null) try {
                    val old = JSONObject(api.get(path).text.ifEmpty { "{}" })
                    api.put(path, body(old), "application/json", LinkedHashMap(cur.filterKeys { it.startsWith("idx_") }).also { it["idx_state"] = "done" }, lease)
                } finally { try { api.releaseLease(path, lease) } catch (e: Exception) {} }
            }
        }
        // the step jobs exactly as a node download creates them, at priority 0 unless the owner set one
        if (mode == StoreMode.STORE_AND_PROCESS) FollowUps.create(api, f.videoId, f.url, priority, now)
    }
}
