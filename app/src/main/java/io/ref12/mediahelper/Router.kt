package io.ref12.mediahelper

import org.json.JSONArray
import org.json.JSONObject

/** What the HTTP routes need from the app. */
interface HelperApi {
    fun appVersion(): String
    fun ytDlpVersion(): String
    fun submit(id: String?, url: String, languages: List<String>?): String
    fun jobInfo(id: String): JobInfo?
    /** The result JSON of a finished job, null when there is none. */
    fun resultJson(id: String): String?
    /** Called for every request, so the server knows it is still in use. */
    fun touch()
}

data class JobInfo(val id: String, val state: JobState, val message: String?)

class HttpReply(val status: Int, val headers: Map<String, String>, val body: String)

/** Framework-free request handling for the local API: routes and CORS. */
class Router(private val api: HelperApi, private val allowedOrigins: () -> List<String>) {

    fun handle(method: String, path: String, origin: String?, requestHeaders: Map<String, String>, body: String?): HttpReply {
        api.touch()
        val cors = HashMap<String, String>()
        if (origin != null) {
            if (!originAllowed(origin)) return json(403, JSONObject().put("error", "origin not allowed"), emptyMap())
            cors["Access-Control-Allow-Origin"] = origin
            cors["Vary"] = "Origin"
        }
        val m = method.uppercase()
        if (m == "OPTIONS") {
            if (origin == null) return HttpReply(204, mapOf("Allow" to "GET, POST, OPTIONS"), "")
            cors["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"
            cors["Access-Control-Allow-Headers"] = requestHeaders["access-control-request-headers"] ?: "Content-Type"
            cors["Access-Control-Allow-Private-Network"] = "true"
            cors["Access-Control-Max-Age"] = "600"
            return HttpReply(204, cors, "")
        }
        val p = path.substringBefore('?').trimEnd('/')
        val segs = p.split('/').filter { it.isNotEmpty() }
        if (segs.size < 2 || segs[0] != "v1") return err(404, "not found", cors)
        return when {
            segs.size == 2 && segs[1] == "status" -> if (m == "GET") json(200, JSONObject()
                .put("app", "media-helper").put("version", api.appVersion()).put("ytDlp", api.ytDlpVersion()), cors) else err(405, "method not allowed", cors)
            segs.size == 2 && segs[1] == "jobs" -> if (m == "POST") createJob(body, cors) else err(405, "method not allowed", cors)
            segs.size == 3 && segs[1] == "jobs" -> if (m == "GET") jobStatus(segs[2], cors) else err(405, "method not allowed", cors)
            segs.size == 4 && segs[1] == "jobs" && segs[3] == "result" -> if (m == "GET") jobResult(segs[2], cors) else err(405, "method not allowed", cors)
            else -> err(404, "not found", cors)
        }
    }

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
            val arr: JSONArray = j.optJSONArray("languages") ?: return err(400, "languages must be an array of strings", cors)
            langs = (0 until arr.length()).map { arr.optString(it, "").trim() }.filter { it.isNotEmpty() }
        }
        val newId = try { api.submit(id, url, langs) } catch (e: Exception) { return err(500, e.message ?: "failed", cors) }
        return json(200, JSONObject().put("id", newId), cors)
    }

    private fun jobStatus(id: String, cors: Map<String, String>): HttpReply {
        val info = api.jobInfo(id) ?: return err(404, "unknown job", cors)
        val o = JSONObject().put("id", info.id).put("state", info.state.wire)
        if (info.message != null) o.put("message", info.message)
        return json(200, o, cors)
    }

    private fun jobResult(id: String, cors: Map<String, String>): HttpReply {
        val info = api.jobInfo(id) ?: return err(404, "unknown job", cors)
        if (info.state != JobState.DONE) {
            val o = JSONObject().put("error", "job is not done").put("state", info.state.wire)
            if (info.message != null) o.put("message", info.message)
            return json(409, o, cors)
        }
        val r = api.resultJson(id) ?: return err(404, "result expired", cors)
        return HttpReply(200, cors + ("Content-Type" to "application/json; charset=utf-8"), r)
    }

    private fun err(status: Int, msg: String, cors: Map<String, String>) = json(status, JSONObject().put("error", msg), cors)

    private fun json(status: Int, o: JSONObject, cors: Map<String, String>) =
        HttpReply(status, cors + ("Content-Type" to "application/json; charset=utf-8"), o.toString())
}
