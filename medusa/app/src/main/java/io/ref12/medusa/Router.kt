package io.ref12.medusa

import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/** What the HTTP routes need from the app. */
interface HelperApi {
    fun appVersion(): String
    fun ytDlpVersion(): String
    fun submit(req: JobSubmit): String
    fun jobInfo(id: String): JobInfo?
    /** The media item JSON of a finished job, null when there is none. */
    fun resultJson(id: String): String?
    /** Media items as a JSON array, without lines. */
    fun mediaListJson(): String
    fun mediaJson(mediaId: String): String?
    fun deleteMedia(mediaId: String): Boolean
    fun videoFile(mediaId: String): Pair<File, String>?
    fun sync(mediaId: String): List<Anchor>?
    fun putSync(mediaId: String, anchors: List<Anchor>): Boolean
    /** Called for every request, so the server knows it is still in use. */
    fun touch()
}

data class JobInfo(val id: String, val state: JobState, val progress: Double?, val code: String?, val message: String?)

/** A reply; [file] (with [fileOffset] and [fileLength]) is streamed instead of [body]. */
class HttpReply(
    val status: Int,
    val headers: Map<String, String>,
    val body: String,
    val file: File? = null,
    val fileOffset: Long = 0,
    val fileLength: Long = 0,
)

/** Framework-free request handling for the local API: routes and CORS. */
class Router(private val api: HelperApi, private val allowedOrigins: () -> List<String>) {

    fun handle(method: String, path: String, origin: String?, requestHeaders: Map<String, String>, body: String?): HttpReply {
        api.touch()
        val cors = HashMap<String, String>()
        if (origin != null) {
            if (!originAllowed(origin)) return json(403, JSONObject().put("error", "origin not allowed"), emptyMap())
            cors["Access-Control-Allow-Origin"] = origin
            cors["Vary"] = "Origin"
            cors["Access-Control-Expose-Headers"] = "Content-Range, Content-Length, Accept-Ranges"
        }
        val m = method.uppercase().let { if (it == "HEAD") "GET" else it }
        if (m == "OPTIONS") {
            if (origin == null) return HttpReply(204, mapOf("Allow" to ALLOWED_METHODS), "")
            cors["Access-Control-Allow-Methods"] = ALLOWED_METHODS
            cors["Access-Control-Allow-Headers"] = requestHeaders["access-control-request-headers"] ?: "Content-Type"
            cors["Access-Control-Allow-Private-Network"] = "true"
            cors["Access-Control-Max-Age"] = "600"
            return HttpReply(204, cors, "")
        }
        val p = path.substringBefore('?').trimEnd('/')
        val s = p.split('/').filter { it.isNotEmpty() }
        if (s.size < 2 || s[0] != "v1") return err(404, "not found", cors)
        return when {
            s.size == 2 && s[1] == "status" -> only(m, "GET", cors) { json(200, JSONObject()
                .put("app", "medusa").put("version", api.appVersion()).put("ytDlp", api.ytDlpVersion()), cors) }
            s.size == 2 && s[1] == "jobs" -> only(m, "POST", cors) { createJob(body, cors) }
            s.size == 3 && s[1] == "jobs" -> only(m, "GET", cors) { jobStatus(s[2], cors) }
            s.size == 4 && s[1] == "jobs" && s[3] == "result" -> only(m, "GET", cors) { jobResult(s[2], cors) }
            s.size == 2 && s[1] == "media" -> only(m, "GET", cors) { raw(200, api.mediaListJson(), cors) }
            s.size == 3 && s[1] == "media" -> when (m) {
                "GET" -> api.mediaJson(s[2])?.let { raw(200, it, cors) } ?: err(404, "unknown media", cors)
                "DELETE" -> if (api.deleteMedia(s[2])) json(200, JSONObject().put("deleted", true), cors) else err(404, "unknown media", cors)
                else -> err(405, "method not allowed", cors)
            }
            s.size == 4 && s[1] == "media" && s[3] == "video" -> only(m, "GET", cors) { video(s[2], requestHeaders["range"], cors) }
            s.size == 4 && s[1] == "media" && s[3] == "sync" -> when (m) {
                "GET" -> api.sync(s[2])?.let { json(200, JSONObject().put("sync", MediaJson.anchorsJson(it)), cors) } ?: err(404, "unknown media", cors)
                "PUT" -> putSync(s[2], body, cors)
                else -> err(405, "method not allowed", cors)
            }
            else -> err(404, "not found", cors)
        }
    }

    private fun only(m: String, allowed: String, cors: Map<String, String>, f: () -> HttpReply) =
        if (m == allowed) f() else err(405, "method not allowed", cors)

    private fun originAllowed(origin: String): Boolean {
        val o = origin.trim().trimEnd('/')
        return allowedOrigins().any { it.trim().trimEnd('/').equals(o, ignoreCase = true) }
    }

    private fun createJob(body: String?, cors: Map<String, String>): HttpReply {
        val j = try { JSONObject(body ?: "") } catch (e: Exception) { return err(400, "body must be a JSON object", cors) }
        val url = j.optString("url", "").trim()
        if (!DeepLink.isHttp(url)) return err(400, "url must be an http(s) URL", cors)
        val id = if (j.has("id") && !j.isNull("id")) j.optString("id") else null
        if (id != null && !Regex("[A-Za-z0-9_-]{1,64}").matches(id)) return err(400, "invalid id", cors)
        var langs: List<String>? = null
        if (j.has("languages") && !j.isNull("languages")) {
            val arr = j.optJSONArray("languages") ?: return err(400, "languages must be an array of strings", cors)
            langs = (0 until arr.length()).map { arr.optString(it, "").trim() }.filter { it.isNotEmpty() }
        }
        var want = Want.DEFAULT
        if (j.has("want") && !j.isNull("want")) {
            val arr = j.optJSONArray("want") ?: return err(400, "want must be an array of \"subtitles\" and/or \"video\"", cors)
            want = Want.parse((0 until arr.length()).map { arr.optString(it, "") }) ?: return err(400, "want must name \"subtitles\" and/or \"video\"", cors)
        }
        var quality = Quality.P720
        if (j.has("quality") && !j.isNull("quality")) quality = Quality.parse(j.optString("quality")) ?: return err(400, "quality must be 720p, 480p or 360p", cors)
        val newId = try { api.submit(JobSubmit(id, url, langs, want, quality)) } catch (e: Exception) { return err(500, e.message ?: "failed", cors) }
        return json(200, JSONObject().put("id", newId), cors)
    }

    private fun jobJson(info: JobInfo): JSONObject {
        val o = JSONObject().put("id", info.id).put("state", info.state.wire)
        if (info.progress != null) o.put("progress", SyncMath.round3(info.progress))
        if (info.code != null) o.put("code", info.code)
        if (info.message != null) o.put("message", info.message)
        return o
    }

    private fun jobStatus(id: String, cors: Map<String, String>): HttpReply {
        val info = api.jobInfo(id) ?: return err(404, "unknown job", cors)
        return json(200, jobJson(info), cors)
    }

    private fun jobResult(id: String, cors: Map<String, String>): HttpReply {
        val info = api.jobInfo(id) ?: return err(404, "unknown job", cors)
        if (info.state != JobState.DONE) return json(409, jobJson(info).put("error", "job is not done"), cors)
        val r = api.resultJson(id) ?: return err(404, "media was deleted", cors)
        return raw(200, r, cors)
    }

    private fun video(mediaId: String, rangeHeader: String?, cors: Map<String, String>): HttpReply {
        val (file, type) = api.videoFile(mediaId) ?: return err(404, "no video", cors)
        val size = file.length()
        val h = HashMap(cors)
        h["Content-Type"] = type
        h["Accept-Ranges"] = "bytes"
        return when (val r = RangeSupport.parse(rangeHeader, size)) {
            is RangeResult.Full -> HttpReply(200, h, "", file, 0, size)
            is RangeResult.Partial -> {
                h["Content-Range"] = "bytes ${r.start}-${r.end}/$size"
                HttpReply(206, h, "", file, r.start, r.length)
            }
            RangeResult.Unsatisfiable -> {
                h["Content-Range"] = "bytes */$size"
                HttpReply(416, h, "")
            }
        }
    }

    private fun putSync(mediaId: String, body: String?, cors: Map<String, String>): HttpReply {
        val arr = try { JSONObject(body ?: "").optJSONArray("sync") } catch (e: Exception) { null }
        val anchors = MediaJson.parseAnchors(arr) ?: return err(400, "body must be {\"sync\":[{\"at\":s,\"offset\":s},...]}", cors)
        if (!api.putSync(mediaId, anchors)) return err(404, "unknown media", cors)
        return json(200, JSONObject().put("sync", MediaJson.anchorsJson(anchors)), cors)
    }

    private fun err(status: Int, msg: String, cors: Map<String, String>) = json(status, JSONObject().put("error", msg), cors)

    private fun json(status: Int, o: JSONObject, cors: Map<String, String>) = raw(status, o.toString(), cors)

    private fun raw(status: Int, body: String, cors: Map<String, String>) =
        HttpReply(status, cors + ("Content-Type" to "application/json; charset=utf-8"), body)

    companion object { const val ALLOWED_METHODS = "GET, HEAD, POST, PUT, DELETE, OPTIONS" }
}
