package io.ref12.medusa.v2

import io.ref12.medusa.HttpReply
import io.ref12.medusa.RangeResult
import io.ref12.medusa.RangeSupport
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.InputStream

/** One tool Medusa can run for a caller. */
class ToolInfo(val name: String, val available: Boolean, val version: String?, val note: String? = null)

/** What the /v2 routes need from the app (the real one asks the device; tests use a fake). */
interface V2Host {
    fun tools(): List<ToolInfo>
    fun device(): JSONObject
    /** A pairing request is waiting: show "Allow <appName> to use Medusa's tools?". */
    fun promptPairing(req: PairingStore.Request)
    fun touch() {}
}

class V2Request(
    val method: String,
    val path: String,
    val query: String?,
    val headers: Map<String, String>,
    val body: String?,
    /** The raw request body (uploads); the router reads at most [contentLength] bytes of it. */
    val stream: InputStream? = null,
    val contentLength: Long = -1,
)

class V2Limits(
    private val concurrency: () -> io.ref12.medusa.GateLimits, val maxQueued: Int, val maxTimeoutSec: Int = 6 * 3600, val defaultTimeoutSec: Int = 600,
    val maxUploadBytes: Long = 2L * 1024 * 1024 * 1024, val maxArgs: Int = 500, val workspaceTtlHours: Int = 24, val maxWorkspaces: Int = 20,
) {
    constructor(maxConcurrent: Int, maxQueued: Int, maxTimeoutSec: Int = 6 * 3600, defaultTimeoutSec: Int = 600, maxUploadBytes: Long = 2L * 1024 * 1024 * 1024)
        : this({ io.ref12.medusa.GateLimits(maxConcurrent, maxConcurrent) }, maxQueued, maxTimeoutSec, defaultTimeoutSec, maxUploadBytes)
    val heavy get() = concurrency().heavy
    val light get() = concurrency().light
}

/** The /v2 capability API: framework-free routes. Everything except starting and polling a pairing needs a paired app's token. */
class V2Router(
    private val host: V2Host,
    private val pairing: PairingStore,
    private val runs: RunManager,
    private val workspaces: WorkspaceStore,
    private val limits: V2Limits,
    private val appVersion: String,
) {
    fun handle(req: V2Request): HttpReply {
        host.touch()
        if (req.headers["origin"] != null) return err(403, "browsers may not use /v2", "origin_not_allowed")
        val m = req.method.uppercase().let { if (it == "HEAD") "GET" else it }
        val s = req.path.trimEnd('/').split('/').filter { it.isNotEmpty() }
        if (s.size < 2 || s[0] != "v2") return err(404, "not found")
        // pairing starts without a token: that is how an app gets one
        if (s[1] == "pair") return when {
            s.size == 2 -> only(m, "POST") { startPairing(req.body) }
            s.size == 3 -> only(m, "GET") { pollPairing(s[2]) }
            else -> err(404, "not found")
        }
        val token = req.headers["x-medusa-token"]?.trim()
        val app = token?.takeIf { it.isNotEmpty() }?.let { pairing.authenticate(it) }
            ?: return err(401, "pair this app first (POST /v2/pair) and send its token in X-Medusa-Token", "unauthorized")
        return when {
            s.size == 2 && s[1] == "capabilities" -> only(m, "GET") { capabilities() }
            s.size == 2 && s[1] == "device" -> only(m, "GET") { json(200, host.device()) }
            s.size == 2 && s[1] == "runs" -> only(m, "POST") { startRun(req.body) }
            s.size == 3 && s[1] == "runs" -> only(m, "GET") { runInfo(s[2]) }
            s.size == 4 && s[1] == "runs" && s[3] == "log" -> only(m, "GET") { runLog(s[2], req.query) }
            s.size == 4 && s[1] == "runs" && s[3] == "cancel" -> only(m, "POST") { cancel(s[2]) }
            s.size == 4 && s[1] == "tools" && s[2] == "yt-dlp" && s[3] == "update" -> only(m, "POST") { updateYtDlp() }
            s.size == 2 && s[1] == "workspaces" -> only(m, "POST") { createWorkspace() }
            s.size == 3 && s[1] == "workspaces" -> only(m, "DELETE") { deleteWorkspace(s[2]) }
            s.size == 4 && s[1] == "workspaces" && s[3] == "files" -> only(m, "GET") { listFiles(s[2]) }
            s.size >= 5 && s[1] == "workspaces" && s[3] == "files" -> {
                val rel = s.drop(4).joinToString("/")
                when (m) {
                    "GET" -> download(s[2], rel, req.headers["range"])
                    "PUT" -> upload(s[2], rel, req)
                    else -> err(405, "method not allowed")
                }
            }
            else -> err(404, "not found")
        }
    }

    // ---- pairing

    private fun startPairing(body: String?): HttpReply {
        val j = try { JSONObject(body ?: "") } catch (e: Exception) { return err(400, "body must be {\"appName\": \"...\"}") }
        val name = PairingStore.cleanName(j.optString("appName", "")) ?: return err(400, "appName must be 1 to 60 characters")
        val r = pairing.request(name, null) ?: return err(429, "too many pairing requests are waiting; answer them on the phone first", "too_many_requests")
        host.promptPairing(r)
        return json(200, JSONObject().put("requestId", r.id).put("state", r.state.wire).put("expiresInSec", PairingStore.REQUEST_TTL_MS / 1000))
    }

    private fun pollPairing(id: String): HttpReply {
        val (state, token) = pairing.poll(id) ?: return err(404, "unknown or expired pairing request", "unknown_request")
        val o = JSONObject().put("state", state.wire)
        if (token != null) o.put("token", token)
        return json(200, o)
    }

    // ---- tools and runs

    private fun capabilities(): HttpReply {
        val tools = JSONArray()
        for (t in host.tools()) tools.put(JSONObject().put("name", t.name).put("available", t.available)
            .put("version", t.version ?: JSONObject.NULL).apply { if (t.note != null) put("note", t.note) })
        val lim = JSONObject().put("priorities", JSONArray().put("interactive").put("background")).put("maxConcurrentHeavyRuns", limits.heavy).put("maxConcurrentLightRuns", limits.light).put("maxQueuedRuns", limits.maxQueued)
            .put("maxTimeoutSec", limits.maxTimeoutSec).put("defaultTimeoutSec", limits.defaultTimeoutSec)
            .put("maxUploadBytes", limits.maxUploadBytes).put("maxArgs", limits.maxArgs)
            .put("workspaceTtlHours", limits.workspaceTtlHours).put("maxWorkspaces", limits.maxWorkspaces)
        return json(200, JSONObject().put("api", 2).put("medusa", appVersion).put("tools", tools).put("limits", lim))
    }

    private fun startRun(body: String?): HttpReply {
        val j = try { JSONObject(body ?: "") } catch (e: Exception) { return err(400, "body must be a JSON object") }
        val tool = j.optString("tool", "")
        val info = host.tools().firstOrNull { it.name == tool } ?: return err(400, "unknown tool; see /v2/capabilities", "unknown_tool")
        if (!info.available) return err(409, "$tool is not available on this device", "tool_unavailable")
        val arr = j.optJSONArray("args") ?: return err(400, "args must be an array of strings")
        if (arr.length() > limits.maxArgs) return err(400, "too many args (at most ${limits.maxArgs})")
        val args = ArrayList<String>()
        for (i in 0 until arr.length()) { args.add(arr.opt(i) as? String ?: return err(400, "args must be an array of strings")) }
        if (args.any { it.length > 32768 || it.contains('\u0000') }) return err(400, "an argument is too long or has a NUL character")
        val ws = j.optString("workspace", "")
        if (workspaces.dir(ws) == null) return err(404, "unknown workspace", "unknown_workspace")
        var timeout = limits.defaultTimeoutSec
        if (j.has("timeoutSec") && !j.isNull("timeoutSec")) {
            timeout = (j.opt("timeoutSec") as? Number)?.toInt() ?: return err(400, "timeoutSec must be a number")
            if (timeout < 1 || timeout > limits.maxTimeoutSec) return err(400, "timeoutSec must be 1 to ${limits.maxTimeoutSec}")
        }
        var priority = io.ref12.medusa.WorkGate.Priority.INTERACTIVE
        if (j.has("priority") && !j.isNull("priority")) {
            priority = io.ref12.medusa.WorkGate.Priority.values().firstOrNull { it.wire == j.opt("priority") }
                ?: return err(400, "priority must be \"interactive\" or \"background\"")
        }
        workspaces.touch(ws)
        val r = runs.submit(tool, args, ws, timeout, priority) ?: return err(429, "the run queue is full; try again later", "queue_full")
        return json(200, JSONObject().put("runId", r.id).put("state", r.state.wire))
    }

    private fun updateYtDlp(): HttpReply {
        runs.active(UPDATE_TOOL)?.let { return json(200, JSONObject().put("runId", it.id).put("state", it.state.wire)) }
        val r = runs.submit(UPDATE_TOOL, emptyList(), null, 300) ?: return err(429, "the run queue is full; try again later", "queue_full")
        return json(200, JSONObject().put("runId", r.id).put("state", r.state.wire))
    }

    private fun runJson(r: Run): JSONObject {
        val o = JSONObject().put("runId", r.id).put("tool", r.tool).put("state", r.state.wire)
            .put("exitCode", r.exitCode ?: JSONObject.NULL).put("progress", r.progress ?: JSONObject.NULL)
            .put("lineCount", r.lineCount).put("queuedAt", r.queuedAt)
            .put("startedAt", r.startedAt.takeIf { it > 0 } ?: JSONObject.NULL).put("finishedAt", r.finishedAt.takeIf { it > 0 } ?: JSONObject.NULL)
            .put("workspace", r.workspace ?: JSONObject.NULL).put("weight", r.weight.wire).put("priority", r.priority.wire)
        if (r.state == RunState.QUEUED) {
            val w = runs.waitOf(r)
            o.put("queue", JSONObject().put("position", w?.position ?: 1).put("waitingFor", JSONArray().apply { for (l in w?.waitingBehind ?: emptyList()) put(l) })
                .put("message", if (w != null) io.ref12.medusa.WorkGate.describe(w) else "Waiting for a free slot"))
        }
        val tail = JSONArray(); for (l in r.tail(TAIL_LINES)) tail.put(l)
        return o.put("tail", tail)
    }

    private fun runInfo(id: String): HttpReply = runs.get(id)?.let { json(200, runJson(it)) } ?: err(404, "unknown run", "unknown_run")

    /** Output lines as text, one per line, from line [from]; with wait=N it holds the request up to N seconds for new lines. Headers say where to continue. */
    private fun runLog(id: String, query: String?): HttpReply {
        val r = runs.get(id) ?: return err(404, "unknown run", "unknown_run")
        val from = (query("from", query)?.toIntOrNull() ?: 0).coerceAtLeast(0)
        val wait = (query("wait", query)?.toIntOrNull() ?: 0).coerceIn(0, MAX_WAIT_SEC)
        if (wait > 0) r.await(wait * 1000L) { r.lineCount > from || r.state.finished }
        val (first, lines) = r.linesFrom(from)
        val state = r.state
        val sb = StringBuilder(); for (l in lines) sb.append(l).append('\n')
        return HttpReply(200, mapOf("Content-Type" to "text/plain; charset=utf-8", "X-Run-State" to state.wire,
            "X-Log-From" to first.toString(), "X-Log-Next" to (first + lines.size).toString(), "Cache-Control" to "no-store"), sb.toString())
    }

    private fun cancel(id: String): HttpReply {
        val r = runs.cancel(id) ?: return err(404, "unknown run", "unknown_run")
        return json(200, JSONObject().put("runId", r.id).put("state", r.state.wire).put("cancelRequested", !r.state.finished || r.state == RunState.CANCELLED))
    }

    // ---- workspaces

    private fun createWorkspace(): HttpReply {
        if (workspaces.count() >= limits.maxWorkspaces) return err(429, "too many workspaces; delete one first", "too_many_workspaces")
        return json(200, JSONObject().put("workspace", workspaces.create()))
    }

    private fun deleteWorkspace(id: String): HttpReply {
        if (workspaces.dir(id) == null) return err(404, "unknown workspace", "unknown_workspace")
        if (runs.isBusy(id)) return err(409, "a run is still using this workspace", "workspace_busy")
        workspaces.delete(id)
        return json(200, JSONObject().put("deleted", true))
    }

    private fun listFiles(id: String): HttpReply {
        val l = workspaces.list(id) ?: return err(404, "unknown workspace", "unknown_workspace")
        workspaces.touch(id)
        val a = JSONArray()
        for (e in l) a.put(JSONObject().put("path", e.path).put("size", e.size).put("modified", e.modified))
        return json(200, JSONObject().put("files", a))
    }

    private fun download(id: String, rel: String, rangeHeader: String?): HttpReply {
        if (workspaces.dir(id) == null) return err(404, "unknown workspace", "unknown_workspace")
        val f = workspaces.resolve(id, rel) ?: return err(400, "path not allowed", "bad_path")
        if (!f.isFile) return err(404, "no such file")
        workspaces.touch(id)
        val size = f.length()
        val h = HashMap<String, String>()
        h["Content-Type"] = contentType(f.name)
        h["Accept-Ranges"] = "bytes"
        return when (val r = RangeSupport.parse(rangeHeader, size)) {
            is RangeResult.Full -> HttpReply(200, h, "", f, 0, size)
            is RangeResult.Partial -> { h["Content-Range"] = "bytes ${r.start}-${r.end}/$size"; HttpReply(206, h, "", f, r.start, r.length) }
            RangeResult.Unsatisfiable -> { h["Content-Range"] = "bytes */$size"; HttpReply(416, h, "") }
        }
    }

    private fun upload(id: String, rel: String, req: V2Request): HttpReply {
        if (workspaces.dir(id) == null) return err(404, "unknown workspace", "unknown_workspace")
        val f = workspaces.resolve(id, rel) ?: return err(400, "path not allowed", "bad_path")
        val len = req.contentLength
        val ins = req.stream
        if (len < 0 || ins == null) return err(411, "send a Content-Length", "length_required")
        if (len > limits.maxUploadBytes) return err(413, "file is larger than ${limits.maxUploadBytes} bytes", "too_large")
        if (f.isDirectory) return err(409, "a folder is in the way", "is_directory")
        f.parentFile?.mkdirs()
        val tmp = File(f.parentFile, ".medusa-upload-" + System.nanoTime())
        try {
            var left = len
            val buf = ByteArray(64 * 1024)
            tmp.outputStream().use { out ->
                while (left > 0) {
                    val n = ins.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
                    if (n < 0) throw java.io.EOFException()
                    out.write(buf, 0, n); left -= n
                }
            }
            if (!tmp.renameTo(f)) { f.delete(); if (!tmp.renameTo(f)) throw java.io.IOException("could not store the file") }
        } catch (e: java.io.IOException) {
            tmp.delete()
            return err(400, "upload ended early or could not be stored", "upload_failed")
        }
        workspaces.touch(id)
        return json(200, JSONObject().put("path", rel).put("size", len))
    }

    // ---- helpers

    private fun only(m: String, allowed: String, f: () -> HttpReply) = if (m == allowed) f() else err(405, "method not allowed")

    private fun err(status: Int, msg: String, code: String? = null) =
        json(status, JSONObject().put("error", msg).apply { if (code != null) put("code", code) })

    private fun json(status: Int, o: JSONObject) =
        HttpReply(status, mapOf("Content-Type" to "application/json; charset=utf-8", "Cache-Control" to "no-store"), o.toString())

    companion object {
        const val UPDATE_TOOL = "yt-dlp-update"
        const val TAIL_LINES = 20
        const val MAX_WAIT_SEC = 30

        fun query(name: String, q: String?): String? {
            for (pair in (q ?: return null).split('&')) {
                if (pair.substringBefore('=') == name) return java.net.URLDecoder.decode(pair.substringAfter('=', ""), "UTF-8")
            }
            return null
        }

        fun contentType(name: String) = when (name.substringAfterLast('.', "").lowercase()) {
            "json" -> "application/json"; "txt", "log", "srt", "description" -> "text/plain; charset=utf-8"; "vtt" -> "text/vtt"
            "jpg", "jpeg" -> "image/jpeg"; "png" -> "image/png"; "webp" -> "image/webp"
            "mp4", "m4v" -> "video/mp4"; "webm" -> "video/webm"; "mkv" -> "video/x-matroska"
            "m4a" -> "audio/mp4"; "mp3" -> "audio/mpeg"; "opus", "ogg" -> "audio/ogg"; "wav" -> "audio/wav"
            else -> "application/octet-stream"
        }
    }
}
