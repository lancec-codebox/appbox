package io.ref12.medusa.node

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.concurrent.atomic.AtomicBoolean

enum class FailureKind { PERMANENT, TRANSIENT, BOT }

class NodeFetchException(val kind: FailureKind, message: String) : Exception(Redact.sas(message))

/** How a yt-dlp error is handled, exactly as the protocol says. */
object Failures {
    fun classify(raw: String): FailureKind {
        val t = raw.lowercase()
        // an age gate also says "sign in to confirm", so it is checked before the bot block
        val permanent = listOf("confirm your age", "age-restricted", "age restricted", "video unavailable", "private video", "members-only", "members only",
            "join this channel", "has been removed", "been terminated", "available in your country", "blocked it in your country", "blocked in your country",
            "unsupported url", "video is not available", "this video is not available", "does not exist", "copyright", "is a private", "no video formats found")
        if (permanent.any { t.contains(it) }) return FailureKind.PERMANENT
        if (t.contains("not a bot") || t.contains("http error 429") || t.contains("too many requests") || t.contains("sign in to confirm")) return FailureKind.BOT
        return FailureKind.TRANSIENT
    }
}

/** run < drain < stop; the stricter of mode and the stage's own setting wins. */
enum class Level { RUN, DRAIN, STOP;
    companion object { fun of(s: String?) = when (s?.lowercase()) { "drain" -> DRAIN; "stop" -> STOP; else -> RUN } }
}

/** nodes.<nodeId> in control.json: per-node run limits that win over the node's local settings. */
class NodeOverride(val budget: Int?, val schedule: String?, val window: String?)

class Control(val mode: Level, val stages: Map<String, Level>, val message: String?, val changedAt: String?, val changedBy: String?, val nodes: Map<String, NodeOverride> = emptyMap()) {
    fun effective(stage: String): Level = maxOf(mode, stages[stage] ?: Level.RUN)
    companion object {
        val RUN = Control(Level.RUN, emptyMap(), null, null, null)
        fun parse(text: String): Control {
            val o = JSONObject(text)
            val st = HashMap<String, Level>(); o.optJSONObject("stages")?.let { s -> s.keys().forEach { st[it] = Level.of(s.optString(it)) } }
            val nodes = HashMap<String, NodeOverride>()
            o.optJSONObject("nodes")?.let { n -> n.keys().forEach { id -> n.optJSONObject(id)?.let { x ->
                nodes[id] = NodeOverride(if (x.has("budget") && !x.isNull("budget")) x.optInt("budget", -1).takeIf { it >= 0 } else null,
                    x.optString("schedule").ifEmpty { null }, x.optString("window").ifEmpty { null })
            } } }
            return Control(Level.of(o.optString("mode", "run")), st, o.optString("message").ifEmpty { null }, o.optString("changedAt").ifEmpty { null }, o.optString("changedBy").ifEmpty { null }, nodes)
        }
    }
}

/** Reads index/control.json with a conditional GET. A missing file is run; a failed read keeps the last value. */
class ControlWatcher(private val api: BlobApi) {
    private var etag: String? = null
    @Volatile var current: Control = Control.RUN
        private set
    @Synchronized fun refresh(): Control {
        try {
            val r = api.get(CONTROL_PATH, etag)
            when (r.status) {
                200 -> { current = try { Control.parse(r.text) } catch (e: Exception) { Control.RUN }; etag = r.etag }
                404 -> { current = Control.RUN; etag = null }
            }
        } catch (e: java.io.IOException) { /* keep the last known setting */ }
        return current
    }
    companion object { const val CONTROL_PATH = "index/control.json" }
}

/** The run limits as the registry and the settings screen show them: budget left, schedule, window, and when the next window opens (null inside one). */
class LimitView(val budget: Int?, val schedule: String?, val window: String?, val nextWindow: String?)

class RunningJob(val jobId: String, val stage: String, @Volatile var step: String, @Volatile var progress: String?)

class NodeConfig(val nodeId: String, val name: String, val conditions: String, val version: String, val platform: String, val threads: Int, val workRoot: File)

/** Counters and logs the node keeps between runs. */
interface NodeState {
    fun doneToday(day: String): Int
    fun addDone(day: String)
    fun backoffUntil(): Long
    fun setBackoffUntil(t: Long)
    fun record(entry: JobLogEntry)
    fun saveControl(text: String)
    /** The local settings of the run limits. */
    fun localLimits(): LocalLimits = LocalLimits(null, null, null)
    /** Which budget value (local or remote, with changedAt) the countdown belongs to, and what is left of it. */
    fun budgetKey(): String? = null
    fun budgetLeft(): Int = 0
    fun setBudget(key: String?, left: Int) {}
    /** A one-line description of the limits in force, for the settings screen. */
    fun saveLimits(text: String) {}
}

class JobLogEntry(val jobId: String, val title: String?, val result: String, val at: Long, val message: String?)

interface NodeListener {
    fun onWork(left: Int) {}
    fun onJob(jobId: String, title: String?, left: Int, text: String?) {}
}

class NodeEnv(
    val clock: () -> Long = System::currentTimeMillis,
    val sleep: (Long) -> Unit = { Thread.sleep(it) },
    val gapMs: () -> Long = { 20_000L + (Math.random() * 20_000L).toLong() },
    val renewMs: Long = 30_000L,
    val registryMs: Long = 60_000L,
    val zone: java.time.ZoneId = java.time.ZoneId.systemDefault(),
)

class Summary { var done = 0; var failed = 0; var released = 0; var endReason = ""; var finalState = "idle" }

/** The download-stage state machine: claim, renew, download, upload, finish or let go. */
class NodeRunner(
    private val api: BlobApi, private val fetcher: NodeFetcher, private val cfg: NodeConfig, private val state: NodeState,
    private val env: NodeEnv = NodeEnv(), private val listener: NodeListener = object : NodeListener {}, private val cap: Int = 20,
) {
    private val control = ControlWatcher(api)
    private val aborted = AtomicBoolean(false)
    @Volatile private var running: RunningJob? = null
    private var regState = "idle"
    private var limitView: LimitView? = null
    private var lastRegistry = 0L
    private val startedAt = iso(env.clock())

    /** The system is stopping the work (time limit, constraints): let go of the lease and end. */
    fun abort() { aborted.set(true); fetcher.cancel() }

    fun run(): Summary {
        val s = Summary()
        regState = "idle"; writeRegistry(true)
        try {
            while (true) {
                if (aborted.get()) { s.endReason = "stopped by the system"; break }
                val c = control.refresh(); state.saveControl(describe(c))
                val lvl = c.effective(STAGE)
                if (lvl == Level.STOP) { s.endReason = "control: stop"; s.finalState = "stopped"; break }
                if (lvl == Level.DRAIN) { s.endReason = "control: drain"; regState = "draining"; break }
                if (state.backoffUntil() > env.clock()) { s.endReason = "backing off"; regState = "backoff"; break }
                val eff = RunLimits.resolve(state.localLimits(), c, cfg.nodeId)
                val left = RunLimits.remaining(eff, state)
                val gate = RunLimits.gate(eff, left, env.clock(), env.zone)
                limitView = limitView(eff, left)
                if (gate == Gate.BUDGET_SPENT) { s.endReason = "budget spent"; regState = "budget spent"; break }
                if (gate == Gate.WAITING) { s.endReason = "waiting for window"; regState = "waiting for window"; break }
                if (state.doneToday(day(env.clock())) >= cap) { s.endReason = "daily cap reached"; break }
                val jobs = api.findJobs(STAGE)
                if (jobs.isEmpty()) { s.endReason = "no work"; break }
                listener.onWork(jobs.size)
                var outcome: Outcome? = null
                for (j in jobs) {
                    if (control.refresh().effective(STAGE) != Level.RUN) break
                    val lease = api.acquireLease(j.name, 60) ?: continue
                    outcome = runJob(j, lease, jobs.size, s)
                    break
                }
                if (outcome == null) { s.endReason = if (control.current.effective(STAGE) != Level.RUN) "control changed" else "all jobs are held by other nodes"; if (control.current.effective(STAGE) == Level.RUN) break else continue }
                if (outcome == Outcome.BOT) { s.endReason = "bot block: backing off for an hour"; regState = "backoff"; break }
                if (outcome == Outcome.STOPPED) { s.endReason = if (aborted.get()) "stopped by the system" else "control: stop"; if (!aborted.get()) s.finalState = "stopped"; break }
                if (jobs.size > 1) env.sleep(env.gapMs())
            }
        } finally {
            running = null
            // while the worker is not running the node is idle, stopped, or says why it claims nothing
            regState = if (s.finalState == "stopped") "stopped" else if (regState == "budget spent" || regState == "waiting for window") regState else "idle"
            writeRegistry(true)
        }
        return s
    }

    private enum class Outcome { DONE, FAILED, BOT, STOPPED, SKIPPED }

    private fun runJob(ref: JobRef, leaseId: String, left: Int, s: Summary): Outcome {
        val t0 = env.clock()
        val rj = RunningJob(ref.id, STAGE, "download", null); running = rj; regState = "working"; writeRegistry(true)
        val lost = AtomicBoolean(false); val stopReq = AtomicBoolean(false)
        var title: String? = null
        val workDir = File(cfg.workRoot, ref.id)
        fun cancelled() = lost.get() || stopReq.get() || aborted.get()
        listener.onJob(ref.id, null, left, null)
        var jobText = ""
        var outcome = Outcome.SKIPPED
        try {
            runBlocking {
                val renew = launch(Dispatchers.IO) {
                    while (isActive) {
                        delay(env.renewMs)
                        try { api.renewLease(ref.name, leaseId) } catch (e: BlobException) {
                            if (e.status == 409 || e.status == 404 || e.status == 412) { lost.set(true); fetcher.cancel(); return@launch }
                        }
                        if (control.refresh().effective(STAGE) == Level.STOP) { stopReq.set(true); fetcher.cancel() }
                        try { api.setMetadata(ref.name, leaseId, mapOf("node" to cfg.nodeId, "progress" to (rj.progress ?: rj.step), "heartbeat" to iso(env.clock()))) } catch (e: Exception) {}
                        writeRegistry(false)
                    }
                }
                try {
                    val g = api.get(ref.name)
                    if (g.status != 200) throw NodeFetchException(FailureKind.PERMANENT, "the job blob is gone")
                    jobText = g.text
                    // someone may have finished it between the search and our claim
                    if (api.getTags(ref.name)["idx_state"] != "pending") { release(ref.name, leaseId); outcome = Outcome.SKIPPED; return@runBlocking }
                    val job = DownloadJob.parse(jobText)
                    rj.step = "download"
                    val out = fetcher.download(job, workDir) { text, t ->
                        if (t != null) title = t
                        rj.progress = text; listener.onJob(ref.id, title, left, text)
                    }
                    if (cancelled()) throw BlobCancelled()
                    rj.step = "upload"; rj.progress = null
                    val base = "index/videos/${ref.id}/"
                    for ((rel, f, ct) in out.extras) api.put(base + rel, f.readBytes(), ct)
                    var lastPct = -1
                    api.putBlocks(base + "video.mp4", out.video, "video/mp4", emptyMap(), ::cancelled) { sent, total ->
                        val pct = (sent * 100 / total).toInt()
                        if (pct != lastPct) { lastPct = pct; rj.progress = "upload $pct%"; listener.onJob(ref.id, title, left, rj.progress) }
                    }
                    if (cancelled()) throw BlobCancelled()
                    val at = iso(env.clock())
                    val marker = JSONObject().put("v", 1).put("bytes", out.video.length()).put("height", out.height ?: JSONObject.NULL)
                        .put("durationSeconds", out.durationSeconds ?: JSONObject.NULL).put("vcodec", out.vcodec ?: JSONObject.NULL)
                        .put("acodec", out.acodec ?: JSONObject.NULL).put("formatId", out.formatId ?: JSONObject.NULL)
                        .put("subs", JSONArray(out.subs)).put("embeddable", out.embeddable ?: JSONObject.NULL)
                        .put("audioLanguage", out.audioLanguage ?: JSONObject.NULL).put("audioNote", out.audioNote ?: JSONObject.NULL)
                        .put("audioTracks", JSONArray().also { a -> out.audioTracks.forEach { a.put(JSONObject().put("language", it.first).put("note", it.second ?: JSONObject.NULL)) } }).put("nodeId", cfg.nodeId).put("at", at)
                    api.put(base + "downloaded.json", marker.toString().toByteArray(), "application/json")
                    val pj = JSONObject().put("v", 1).put("stage", "process").put("jobId", ref.id)
                        .put("steps", JSONArray(listOf("frames", "sscd", "clip", "transcribe", "ocrsubs")))
                        .put("addedAt", at).put("attempts", 0)
                    api.put("index/jobs/process/${ref.id}.json", pj.toString().toByteArray(), "application/json",
                        mapOf("idx_stage" to "process", "idx_state" to "pending", "idx_added" to at), null, true)
                    val cj = JSONObject().put("v", 1).put("stage", "comments").put("jobId", ref.id).put("url", job.url)
                        .put("maxComments", 1000).put("sort", "top").put("round", 0).put("addedAt", at).put("attempts", 0)
                    api.put("index/jobs/comments/${ref.id}.json", cj.toString().toByteArray(), "application/json",
                        mapOf("idx_stage" to "comments", "idx_state" to "pending", "idx_added" to at, "idx_due" to at), null, true)
                    val done = JSONObject(jobText).put("finishedAt", at).put("nodeId", cfg.nodeId).put("elapsedSeconds", (env.clock() - t0) / 1000)
                    api.put(ref.name, done.toString().toByteArray(), "application/json", tagsOf(ref, "done"), leaseId)
                    release(ref.name, leaseId)
                    state.addDone(day(env.clock())); s.done++
                    countDown()
                    state.record(JobLogEntry(ref.id, title, "done", env.clock(), null))
                    outcome = Outcome.DONE
                } catch (e: Exception) {
                    outcome = handleFailure(e, ref, leaseId, jobText, cancelled(), stopReq.get() || aborted.get(), title, s)
                } finally { renew.cancel() }
            }
        } finally {
            running = null
            fetcher.cleanup(workDir)
            workDir.deleteRecursively()
        }
        return outcome
    }

    /** One finished download off the budget, when there is one. */
    private fun countDown() {
        val eff = RunLimits.resolve(state.localLimits(), control.current, cfg.nodeId)
        val left = RunLimits.remaining(eff, state) ?: return
        state.setBudget(eff.budgetKey, maxOf(0, left - 1))
    }

    private fun limitView(eff: EffectiveLimits, left: Int?): LimitView {
        val next = eff.schedule?.let { sc -> if (sc.isOpen(env.clock(), env.zone)) null else sc.nextStart(env.clock(), env.zone) }
        state.saveLimits(describeLimits(left, eff, next))
        return LimitView(left, eff.scheduleText, if (eff.scheduleText != null) eff.windowText ?: "1h" else null, next?.let { iso(it) })
    }

    private fun describeLimits(left: Int?, eff: EffectiveLimits, next: Long?): String =
        (if (left != null) "budget left $left" else "no budget") + "; " +
            (if (eff.scheduleText == null) "any time" else "schedule ${eff.scheduleText} for ${eff.windowText ?: "1h"}" +
                (if (next != null) ", next window " + SimpleDateFormat("EEE d MMM HH:mm", Locale.US).apply { timeZone = TimeZone.getTimeZone(env.zone) }.format(Date(next)) else ", in a window now"))

    private fun handleFailure(e: Exception, ref: JobRef, leaseId: String, jobText: String, wasCancelled: Boolean, stopped: Boolean, title: String?, s: Summary): Outcome {
        val msg = Redact.sas(e.message ?: e.javaClass.simpleName).take(300)
        if (wasCancelled || e is BlobCancelled) { // stop, system stop or a lost lease: let go without counting an attempt
            release(ref.name, leaseId); s.released++
            state.record(JobLogEntry(ref.id, title, "released", env.clock(), if (stopped) "stopped" else "lease lost"))
            return Outcome.STOPPED
        }
        val kind = if (e is NodeFetchException) e.kind else FailureKind.TRANSIENT
        when (kind) {
            FailureKind.BOT -> {
                release(ref.name, leaseId); s.released++
                state.setBackoffUntil(env.clock() + BACKOFF_MS)
                state.record(JobLogEntry(ref.id, title, "bot block", env.clock(), msg))
                return Outcome.BOT
            }
            FailureKind.PERMANENT -> {
                rewrite(ref, leaseId, jobText, "failed") { it.put("lastError", msg).put("permanent", true) }
                state.record(JobLogEntry(ref.id, title, "failed (permanent)", env.clock(), msg))
            }
            FailureKind.TRANSIENT -> {
                var attempts = 0
                rewrite(ref, leaseId, jobText, null) { attempts = it.optInt("attempts", 0) + 1; it.put("attempts", attempts).put("lastError", msg) }
                state.record(JobLogEntry(ref.id, title, if (attempts >= MAX_ATTEMPTS) "failed (3 attempts)" else "will retry", env.clock(), msg))
            }
        }
        s.failed++
        release(ref.name, leaseId)
        return Outcome.FAILED
    }

    /** Rewrites the job blob with the lease id and the full idx_* tag set. [state] null: failed at 3 attempts, else pending. */
    private fun rewrite(ref: JobRef, leaseId: String, jobText: String, state: String?, edit: (JSONObject) -> JSONObject) {
        try {
            val o = edit(JSONObject(jobText.ifEmpty { "{}" }))
            val st = state ?: if (o.optInt("attempts", 0) >= MAX_ATTEMPTS) "failed" else "pending"
            api.put(ref.name, o.toString().toByteArray(), "application/json", tagsOf(ref, st), leaseId)
        } catch (e: Exception) { /* the lease is released next either way */ }
    }

    private fun tagsOf(ref: JobRef, st: String): Map<String, String> {
        val t = LinkedHashMap(ref.tags.filterKeys { it.startsWith("idx_") })
        t["idx_stage"] = STAGE; t["idx_state"] = st; if (!t.containsKey("idx_added")) t["idx_added"] = iso(env.clock())
        return t
    }

    private fun release(path: String, leaseId: String) { try { api.releaseLease(path, leaseId) } catch (e: Exception) { /* it expires in 60 s */ } }

    /** The registry blob; throttled to env.registryMs unless [force]. Failures are ignored. */
    private fun writeRegistry(force: Boolean) {
        val now = env.clock()
        if (!force && now - lastRegistry < env.registryMs) return
        lastRegistry = now
        try {
            val json = registryJson(cfg, regState, control.current.effective(STAGE).name.lowercase(), running, iso(now), startedAt, limitView)
            api.put("index/nodes/${cfg.nodeId}.json", json.toString().toByteArray(), "application/json", mapOf("idx_kind" to "node"))
        } catch (e: Exception) { /* the registry is best effort */ }
    }

    companion object {
        const val STAGE = "download"
        const val MAX_ATTEMPTS = 3
        const val BACKOFF_MS = 3_600_000L

        fun iso(ms: Long): String = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }.format(Date(ms))
        fun day(ms: Long): String = SimpleDateFormat("yyyy-MM-dd", Locale.US).format(Date(ms))
        fun describe(c: Control): String = c.effective(STAGE).name.lowercase() + (c.message?.let { " - $it" } ?: "")

        fun registryJson(cfg: NodeConfig, state: String, control: String, running: RunningJob?, lastSeen: String, startedAt: String, limits: LimitView? = null): JSONObject = JSONObject()
            .put("v", 1).put("nodeId", cfg.nodeId).put("name", cfg.name).put("kind", "medusa").put("version", cfg.version).put("platform", cfg.platform)
            .put("stages", JSONArray(listOf("download"))).put("jobs", JSONObject().put("download", 1)).put("threads", cfg.threads)
            .put("conditions", cfg.conditions).put("state", state).put("control", control)
            .put("running", JSONArray().also { a -> running?.let { r -> a.put(JSONObject().put("jobId", r.jobId).put("stage", r.stage).put("step", r.step).put("progress", r.progress ?: JSONObject.NULL)) } })
            .put("lastSeen", lastSeen).put("startedAt", startedAt)
            .also { o -> limits?.let { l ->
                l.budget?.let { o.put("budget", it) }
                l.schedule?.let { o.put("schedule", it) }; l.window?.let { o.put("window", it) }
                l.nextWindow?.let { o.put("nextWindow", it) }
            } }
    }
}

class DownloadJob(val jobId: String, val url: String, val maxHeight: Int, val format: String?, val subs: Boolean) {
    /** The protocol's selector: a raw format wins, else the default capped at the height. */
    fun selector(): String = format ?: ("bv*[height<=$maxHeight][vcodec^=av01]+ba[acodec=opus][abr<=80]/bv*[height<=$maxHeight][vcodec^=vp9]+ba[acodec=opus][abr<=80]/" +
          "bv*[height<=$maxHeight]+ba/b[height<=$maxHeight]/b")
    companion object {
        fun parse(text: String): DownloadJob {
            val o = JSONObject(text)
            return DownloadJob(o.getString("jobId"), o.getString("url"), if (o.isNull("maxHeight")) 480 else o.optInt("maxHeight", 480),
                if (o.isNull("format")) null else o.optString("format").ifBlank { null }, o.optBoolean("subs", true))
        }
    }
}

class DownloadedFiles(
    val video: File, val extras: List<Triple<String, File, String>>, val height: Int?, val durationSeconds: Double?,
    val vcodec: String? = null, val acodec: String? = null, val formatId: String? = null,
    val subs: List<String> = emptyList(), val embeddable: Boolean? = null,
    val audioLanguage: String? = null, val audioNote: String? = null, val audioTracks: List<Pair<String, String?>> = emptyList(),
)

/** What the node reads from yt-dlp's info.json. */
object NodeInfo {
    class Parsed(val vcodec: String?, val acodec: String?, val formatId: String?, val embeddable: Boolean?, val description: String,
                 val manual: Set<String>, val auto: Set<String>,
                 val audioLanguage: String?, val audioNote: String?, val audioTracks: List<Pair<String, String?>>)

    fun parse(text: String): Parsed {
        val o = JSONObject(text)
        fun s(k: String) = if (o.isNull(k)) null else o.optString(k).ifEmpty { null }
        fun keys(k: String): Set<String> = o.optJSONObject(k)?.keys()?.asSequence()?.toSet() ?: emptySet()
        fun str(x: JSONObject, k: String) = if (x.isNull(k)) null else x.optString(k).ifEmpty { null }
        fun isAudio(x: JSONObject) = str(x, "acodec").let { it != null && it != "none" } && (str(x, "vcodec") ?: "none") == "none"
        val chosen = o.optJSONArray("requested_formats")?.let { a -> (0 until a.length()).map { a.getJSONObject(it) }.firstOrNull { isAudio(it) } }
        val tracks = LinkedHashMap<String, String?>()
        o.optJSONArray("formats")?.let { a -> for (i in 0 until a.length()) { val f = a.getJSONObject(i)
            if (isAudio(f)) str(f, "language")?.let { if (it !in tracks) tracks[it] = str(f, "format_note") } } }
        return Parsed(s("vcodec"), s("acodec"), s("format_id"), if (o.isNull("playable_in_embed")) null else o.optBoolean("playable_in_embed"),
            s("description") ?: "", keys("subtitles"), keys("automatic_captions"),
            chosen?.let { str(it, "language") }, chosen?.let { str(it, "format_note") }, tracks.map { it.key to it.value })
    }

    /** A language under subtitles is uploaded; one only under automatic_captions is automatic ("<lang>.auto"). */
    fun subName(lang: String, p: Parsed) = if (lang !in p.manual && lang in p.auto) "$lang.auto" else lang

    /** Uploaded before automatic, Chinese before the rest. */
    fun order(names: Collection<String>) = names.sortedWith(compareBy({ it.endsWith(".auto") }, { !it.startsWith("zh") }, { it }))
}

interface NodeFetcher {
    /** Downloads the job into [dir]; [progress] gets (text, title). Throws NodeFetchException. */
    fun download(job: DownloadJob, dir: File, progress: (String?, String?) -> Unit): DownloadedFiles
    /** Kills the running download. */
    fun cancel()
    fun cleanup(dir: File) {}
}
