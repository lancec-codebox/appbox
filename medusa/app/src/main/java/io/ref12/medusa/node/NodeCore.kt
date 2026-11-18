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

/** FORBIDDEN: HTTP 403 on the video data itself (YouTube withholds some formats): retried once as MP4, never counted as an attempt. */
enum class FailureKind { PERMANENT, TRANSIENT, BOT, FORBIDDEN }

class NodeFetchException(val kind: FailureKind, message: String, val trace: FailureTrace? = null) : Exception(Redact.sas(message))

/** How a yt-dlp error is handled, exactly as the protocol says. */
object Failures {
    /** yt-dlp's "Unable to download video subtitles for 'en-ar': HTTP Error 429..." and similar. */
    fun isSubtitleError(lower: String) = lower.contains("video subtitles") || lower.contains("subtitles for '") || lower.contains("unable to download subtitle")

    /** The error line to keep: the last "ERROR:" line, whole (HTTP status and reason included), else the last non-blank line. */
    fun errorLine(raw: String): String {
        val lines = raw.lines().filter { it.isNotBlank() }
        return (lines.lastOrNull { it.trimStart().startsWith("ERROR") } ?: lines.lastOrNull() ?: "yt-dlp failed").trim()
    }

    fun classify(raw: String): FailureKind {
        val t = raw.lowercase()
        // an age gate also says "sign in to confirm", so it is checked before the bot block
        val permanent = listOf("confirm your age", "age-restricted", "age restricted", "video unavailable", "private video", "members-only", "members only",
            "join this channel", "has been removed", "been terminated", "available in your country", "blocked it in your country", "blocked in your country",
            "unsupported url", "video is not available", "this video is not available", "does not exist", "copyright", "is a private", "no video formats found")
        if (permanent.any { t.contains(it) }) return FailureKind.PERMANENT
        // a subtitle-track error (any HTTP status) is never a bot block, whatever it says
        if (isSubtitleError(t)) return FailureKind.TRANSIENT
        if (t.contains("not a bot") || t.contains("http error 429") || t.contains("too many requests") || t.contains("sign in to confirm")) return FailureKind.BOT
        if (t.contains("http error 403")) return FailureKind.FORBIDDEN
        return FailureKind.TRANSIENT
    }
}

/** The 403 policy: one retry as MP4, then release. [attempt] runs one yt-dlp download with the given selector (null: the job's own). */
object Mp4Fallback {
    const val RETRY_TEXT = "403 on video, retrying as MP4"
    const val TWICE_TEXT = "403 twice"
    /** The MP4 selector of Medusa's own downloader; it carries the height limit itself, so no sort arguments go with it. */
    fun selector(maxHeight: Int) = "bv*[height<=$maxHeight][ext=mp4]+ba[ext=m4a]/b[height<=$maxHeight][ext=mp4]/b[height<=$maxHeight]"
    /** Retry only a 403 on the video, and only once. A bot block (429, sign in), a subtitle error or anything else is not retried. */
    fun shouldRetry(e: NodeFetchException, alreadyMp4: Boolean) = e.kind == FailureKind.FORBIDDEN && !alreadyMp4
    /** Returns "mp4" when the retry was needed (recorded as formatFallback), else null. */
    fun run(job: DownloadJob, progress: (String?, String?) -> Unit, attempt: (selector: String?) -> Unit): String? {
        try { attempt(null); return null } catch (e: NodeFetchException) {
            if (!shouldRetry(e, false)) throw e
            progress(RETRY_TEXT, null)
            try { attempt(selector(job.maxHeight)); return "mp4" } catch (e2: NodeFetchException) {
                if (e2.kind == FailureKind.FORBIDDEN) throw NodeFetchException(FailureKind.FORBIDDEN, TWICE_TEXT + ": " + e2.message)
                throw e2
            }
        }
    }
}

/** run < drain < stop; the stricter of mode and the stage's own setting wins. */
enum class Level { RUN, DRAIN, STOP;
    companion object { fun of(s: String?) = when (s?.lowercase()) { "drain" -> DRAIN; "stop" -> STOP; else -> RUN } }
}

/** nodes.<nodeId> in control.json: per-node run limits that win over the node's local settings. */
class NodeOverride(val budget: Int?, val schedule: String?, val window: String?)

class Control(val mode: Level, val stages: Map<String, Level>, val message: String?, val changedAt: String?, val changedBy: String?, val nodes: Map<String, NodeOverride> = emptyMap(),
              /** downloads.maxPerIp (1.8.5): null or 0 = no cap, the default. */ val maxPerIp: Int? = null) {
    /** The stricter of the mode and the stage's own setting; the pre-1.9.2 "watch" key is the refresh stage (the new key wins). */
    fun effective(stage: String): Level = maxOf(mode, stages[stage] ?: (if (stage == "refresh") stages["watch"] else null) ?: Level.RUN)
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
            return Control(Level.of(o.optString("mode", "run")), st, o.optString("message").ifEmpty { null }, o.optString("changedAt").ifEmpty { null }, o.optString("changedBy").ifEmpty { null }, nodes,
                o.optJSONObject("downloads")?.let { d -> if (d.has("maxPerIp") && !d.isNull("maxPerIp")) d.optInt("maxPerIp", 0).takeIf { it > 0 } else null })
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
    /** Jobs this node leaves alone until the time (a video that answered 403 twice). */
    fun skipUntil(jobId: String): Long = 0
    fun skipJob(jobId: String, until: Long) {}
    fun record(entry: JobLogEntry)
    /** A line for the node log the owner can share (Settings > Index node). */
    fun log(line: String) {}
    fun saveControl(text: String)
    /** The local settings of the run limits. */
    fun localLimits(): LocalLimits = LocalLimits(null, null, null)
    /** Which budget value (local or remote, with changedAt) the countdown belongs to, and what is left of it. */
    fun budgetKey(): String? = null
    fun budgetLeft(): Int = 0
    fun setBudget(key: String?, left: Int) {}
    /** A one-line description of the limits in force, for the settings screen. */
    fun saveLimits(text: String) {}
    /** The download profile found by the 403 diagnosis (1.8.6), null when none; written to the node record. */
    fun downloadProfile(): DownloadProfile? = null
}

class JobLogEntry(val jobId: String, val title: String?, val result: String, val at: Long, val message: String?, val version: String? = null)

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
    /** Fetches a Cloudflare trace URL (null when it fails); tests replace it. */
    val netFetch: (String) -> String? = NetworkProbe::httpFetch,
    /** The phone's transport and VPN flag; tests replace it. */
    val deviceNet: () -> DeviceNet = { DeviceNet(null, null) },
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
    /** The public network as last looked up (null before the first lookup). */
    @Volatile var network: NetInfo? = null
        private set
    @Volatile private var netDirty = false
    /** The network the running attempt started on, written to the job record and downloaded.json. */
    @Volatile private var attemptNet: JSONObject? = null
    /** Downloads running on other nodes from this IP when the cap last held this node back. */
    @Volatile var capHeldAt: Int? = null
        private set

    /** The phone's network changed (Android callback): look it up again before the next claim. */
    fun onNetworkChanged() { netDirty = true }

    /** Looks the public IPv4/IPv6 up (5 s per family) and keeps "since" while the address pair stays. */
    @Synchronized fun refreshNetwork() {
        netDirty = false
        try { network = NetworkProbe.detect(env.netFetch, iso(env.clock()), env.deviceNet(), network) } catch (e: Exception) { /* keep the last */ }
    }

    private fun ensureFreshNetwork() {
        val n = network
        val at = n?.checkedAt?.let { try { java.time.Instant.parse(it).toEpochMilli() } catch (e: Exception) { null } }
        if (n == null || at == null || netDirty || env.clock() - at >= NetworkProbe.CLAIM_STALE_MS) refreshNetwork()
    }

    /** The cap check before a claim: true = may claim. Off (the default) reads nothing. A failed read of the other nodes' records does not impose a cap. */
    private fun underIpCap(): Boolean {
        capHeldAt = null
        val cap = control.current.maxPerIp
        if (cap == null || cap <= 0) return true
        return try {
            val own = if (running != null) 1 else 0
            val peers = api.listNames("index/nodes/").filter { it != "index/nodes/${cfg.nodeId}.json" && it.endsWith(".json") }.mapNotNull { n ->
                val g = api.get(n); if (g.status == 200) PeerNode.parse(g.text) else null }
            val others = NetCap.countOthers(network, cfg.nodeId, peers, env.clock())
            if (NetCap.mayClaim(cap, others, own)) true else { capHeldAt = others + own; false }
        } catch (e: Exception) { true }
    }

    /** The system is stopping the work (time limit, constraints): let go of the lease and end. */
    fun abort() { aborted.set(true); fetcher.cancel() }

    fun run(): Summary {
        val s = Summary()
        regState = "idle"
        // the previous record's network keeps "since" across runs; then look the address up (at every start)
        try { api.get("index/nodes/${cfg.nodeId}.json").let { if (it.status == 200) network = NetInfo.parse(it.text) } } catch (e: Exception) {}
        refreshNetwork()
        writeRegistry(true)
        try {
            while (true) {
                if (aborted.get()) { s.endReason = "stopped by the system"; break }
                val c = control.refresh(); state.saveControl(describe(c))
                val lvl = minOf(c.effective(STAGE), c.effective(REFRESH)) // the node stops or drains only when both of its stages do
                if (lvl == Level.STOP) { s.endReason = "control: stop"; s.finalState = "stopped"; break }
                if (lvl == Level.DRAIN) { s.endReason = "control: drain"; regState = "draining"; break }
                val dlOpen = c.effective(STAGE) == Level.RUN; val rfOpen = c.effective(REFRESH) == Level.RUN
                if (state.backoffUntil() > env.clock()) { s.endReason = "backing off"; regState = "backoff"; break }
                val eff = RunLimits.resolve(state.localLimits(), c, cfg.nodeId)
                val left = RunLimits.remaining(eff, state)
                val gate = RunLimits.gate(eff, left, env.clock(), env.zone)
                limitView = limitView(eff, left)
                if (gate == Gate.BUDGET_SPENT) { s.endReason = "budget spent"; regState = "budget spent"; break }
                if (gate == Gate.WAITING) { s.endReason = "waiting for window"; regState = "waiting for window"; break }
                // the daily cap and the per-IP cap are about downloads: when one holds, sources can still be listed (a listing is one small request)
                var blocked: String? = null
                if (state.doneToday(day(env.clock())) >= cap) blocked = "daily cap reached"
                ensureFreshNetwork()
                if (blocked == null && dlOpen && !underIpCap()) blocked = IP_CAP_REASON + capHeldAt // waiting, not failing: no attempt counted, no back-off
                val downloads = if (dlOpen && blocked == null) api.findJobs(STAGE) else emptyList()
                val nowIso = iso(env.clock())
                val refreshes = if (rfOpen) { try { RefreshJobs.migrateLegacy(api) } catch (e: Exception) { /* the next claim retries */ }
                    api.findJobs(REFRESH).filter { (it.tags["idx_due"] ?: "") <= nowIso } } else emptyList()
                // one claim order for both stages: priority, then idx_added, then name (a lookup or manual run is at 500)
                val jobs = Prio.claimOrder(downloads + refreshes).filter { state.skipUntil(it.id) <= env.clock() }
                if (jobs.isEmpty()) { s.endReason = blocked ?: "no work"; break }
                listener.onWork(jobs.size)
                var outcome: Outcome? = null
                for (j in jobs) {
                    if (control.refresh().effective(stageOf(j)) != Level.RUN) continue // that stage was stopped or drained meanwhile: the next job may be of the other stage
                    val lease = api.acquireLease(j.name, 60) ?: continue
                    outcome = runJob(j, lease, jobs.size, s)
                    break
                }
                if (outcome == null) { val open = control.current.effective(STAGE) == Level.RUN || control.current.effective(REFRESH) == Level.RUN; s.endReason = if (!open) "control changed" else "all jobs are held by other nodes"; if (open) break else continue }
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

    private fun stageOf(ref: JobRef) = if (ref.name.startsWith("index/jobs/$REFRESH/")) REFRESH else STAGE

    private fun runJob(ref: JobRef, leaseId: String, left: Int, s: Summary): Outcome {
        val t0 = env.clock()
        val stage = stageOf(ref)
        attemptNet = network?.forJob() // the network this attempt starts on
        val rj = RunningJob(ref.id, stage, if (stage == REFRESH) "refresh" else "download", null); running = rj; regState = "working"; writeRegistry(true)
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
                        if (control.refresh().effective(stage) == Level.STOP) { stopReq.set(true); fetcher.cancel() }
                        try { api.setMetadata(ref.name, leaseId, mapOf("node" to cfg.nodeId, "progress" to (rj.progress ?: rj.step), "heartbeat" to iso(env.clock()))) } catch (e: Exception) {}
                        val nAt = network?.checkedAt?.let { try { java.time.Instant.parse(it).toEpochMilli() } catch (e: Exception) { null } }
                        if (netDirty || nAt == null || env.clock() - nAt >= NetworkProbe.REFRESH_MS) refreshNetwork()
                        writeRegistry(false)
                    }
                }
                try {
                    val g = api.get(ref.name)
                    if (g.status != 200) throw NodeFetchException(FailureKind.PERMANENT, "the job blob is gone")
                    jobText = g.text
                    // someone may have finished it between the search and our claim
                    if (api.getTags(ref.name)["idx_state"] != "pending") { release(ref.name, leaseId); outcome = Outcome.SKIPPED; return@runBlocking }
                    if (stage == REFRESH) {
                        // a source: a lookup (full listing) or a refresh (incremental); no video, no upload, and nothing off the budget or the daily cap
                        val kind = RefreshWork.kindOf(JSONObject(jobText)); rj.step = kind
                        title = JSONObject(jobText).optString("name").ifEmpty { null }
                        listener.onJob(ref.id, title, left, kind)
                        val r = RefreshWork.run(api, fetcher, JSONObject(jobText), env.clock())
                        if (cancelled()) throw BlobCancelled()
                        val body = r.job.put("finishedAt", iso(env.clock())).put("nodeId", cfg.nodeId)
                        // pending again at the next scheduled time (once and off: done until triggered by hand); back at the source's own priority
                        val tg = Prio.jobTags(REFRESH, if (r.nextDueMs == null) "done" else "pending", ref.tags["idx_added"] ?: iso(env.clock()), body.optInt("priority", 0), iso(r.nextDueMs ?: env.clock()))
                        api.put(ref.name, body.toString().toByteArray(), "application/json", tg, leaseId)
                        release(ref.name, leaseId)
                        state.record(JobLogEntry(ref.id, title, r.kind + " done", env.clock(), r.listed.toString() + " listed, " + r.made + " queued"))
                        outcome = Outcome.DONE
                        return@runBlocking
                    }
                    val job = DownloadJob.parse(jobText)
                    rj.step = "download"
                    val out = fetcher.download(job, workDir) { text, t ->
                        if (t != null) title = t
                        rj.progress = text; listener.onJob(ref.id, title, left, text)
                    }
                    if (cancelled()) throw BlobCancelled()
                    rj.step = "upload"; rj.progress = null
                    val base = "index/videos/${ref.id}/"
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
                        .also { m -> out.formatFallback?.let { m.put("formatFallback", it) } }.put("subs", JSONArray(out.subs)).put("subsFailed", JSONArray(out.subsFailed)).put("embeddable", out.embeddable ?: JSONObject.NULL)
                        .put("audioLanguage", out.audioLanguage ?: JSONObject.NULL).put("audioNote", out.audioNote ?: JSONObject.NULL)
                        .put("audioTracks", JSONArray().also { a -> out.audioTracks.forEach { a.put(JSONObject().put("language", it.first).put("note", it.second ?: JSONObject.NULL)) } }).put("nodeId", cfg.nodeId).put("at", at)
                        .also { m -> attemptNet?.let { m.put("network", it) } }
                    // 1.9.0: download.zip (info.json, description.txt, thumb.jpg, subs/ + the former downloaded.json as its idx_summary) goes up last, after video.mp4: its existence is the completion signal
                    StageZip.upload(api, base, out.extras, marker, cfg.nodeId, at, File(out.video.parentFile ?: workDir, ".zipwork"), false, ::cancelled)
                    // as IndexNode's DownloadStage: comments plus one job per ingest step (with needs), all at the download's priority; no legacy process job
                    FollowUps.create(api, ref.id, job.url, job.priority ?: Prio.of(ref.tags), at)
                    val done = JSONObject(jobText).put("finishedAt", at).put("nodeId", cfg.nodeId).put("elapsedSeconds", (env.clock() - t0) / 1000)
                        .also { d -> attemptNet?.let { d.put("network", it) } }
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
        val msg = Redact.sas(e.message ?: e.javaClass.simpleName).take(500)
        if (wasCancelled || e is BlobCancelled) { // stop, system stop or a lost lease: let go without counting an attempt
            release(ref.name, leaseId); s.released++
            state.record(JobLogEntry(ref.id, title, "released", env.clock(), if (stopped) "stopped" else "lease lost"))
            return Outcome.STOPPED
        }
        val kind = if (e is NodeFetchException) e.kind else FailureKind.TRANSIENT
        val trace = (e as? NodeFetchException)?.trace
        // every failed attempt leaves its trace: network (rewrite adds it), yt-dlp version, format ids, client and the last 20 lines of output, next to lastError; the same goes into the node log
        val withTrace: (JSONObject) -> JSONObject = { o -> trace?.putInto(o); o }
        trace?.let { state.log("trace " + ref.id + ": " + it.text()) }
        when (kind) {
            FailureKind.FORBIDDEN -> {
                // 403 on the video, also as MP4: not the job's fault. Back to pending without an attempt; this node leaves it for 6 hours, other nodes may take it.
                rewrite(ref, leaseId, jobText, "pending") { withTrace(it.put("lastError", msg)) }
                state.skipJob(ref.id, env.clock() + SKIP_MS)
                state.record(JobLogEntry(ref.id, title, "released: " + Mp4Fallback.TWICE_TEXT, env.clock(), msg))
                s.released++
                release(ref.name, leaseId)
                return Outcome.FAILED
            }
            FailureKind.BOT -> {
                // no attempt counted; the error and the network go into the job so a block can be matched to home, VPN or mobile data
                rewrite(ref, leaseId, jobText, "pending") { withTrace(it.put("lastError", msg)) }
                release(ref.name, leaseId); s.released++
                state.setBackoffUntil(env.clock() + BACKOFF_MS)
                state.record(JobLogEntry(ref.id, title, "bot block", env.clock(), msg))
                return Outcome.BOT
            }
            FailureKind.PERMANENT -> {
                rewrite(ref, leaseId, jobText, "failed") { withTrace(it.put("lastError", msg).put("permanent", true)) }
                state.record(JobLogEntry(ref.id, title, "failed (permanent)", env.clock(), msg))
            }
            FailureKind.TRANSIENT -> {
                var attempts = 0
                rewrite(ref, leaseId, jobText, null) { attempts = it.optInt("attempts", 0) + 1; withTrace(it.put("attempts", attempts).put("lastError", msg)) }
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
            val o = edit(JSONObject(jobText.ifEmpty { "{}" })).also { j -> attemptNet?.let { j.put("network", it) } }
            val st = state ?: if (o.optInt("attempts", 0) >= MAX_ATTEMPTS) "failed" else "pending"
            api.put(ref.name, o.toString().toByteArray(), "application/json", tagsOf(ref, st), leaseId)
        } catch (e: Exception) { /* the lease is released next either way */ }
    }

    private fun tagsOf(ref: JobRef, st: String): Map<String, String> {
        val t = LinkedHashMap(ref.tags.filterKeys { it.startsWith("idx_") })
        t["idx_stage"] = stageOf(ref); t["idx_state"] = st; if (!t.containsKey("idx_added")) t["idx_added"] = iso(env.clock())
        return t
    }

    private fun release(path: String, leaseId: String) { try { api.releaseLease(path, leaseId) } catch (e: Exception) { /* it expires in 60 s */ } }

    /** The registry blob; throttled to env.registryMs unless [force]. Failures are ignored. */
    private fun writeRegistry(force: Boolean) {
        val now = env.clock()
        if (!force && now - lastRegistry < env.registryMs) return
        lastRegistry = now
        try {
            val json = registryJson(cfg, regState, control.current.effective(STAGE).name.lowercase(), running, iso(now), startedAt, limitView, network, state.downloadProfile())
            api.put("index/nodes/${cfg.nodeId}.json", json.toString().toByteArray(), "application/json", mapOf("idx_kind" to "node"))
        } catch (e: Exception) { /* the registry is best effort */ }
    }

    companion object {
        const val STAGE = "download"
        /** 1.9.2: the source stage (formerly "watch"); claimed with the downloads, by the same single worker. */
        const val REFRESH = "refresh"
        const val MAX_ATTEMPTS = 3
        const val BACKOFF_MS = 3_600_000L
        const val SKIP_MS = 6 * 3_600_000L
        /** The end reason when the per-IP cap holds the node back; the number of downloads already running from this IP follows. */
        const val IP_CAP_REASON = "waiting for IP cap: "

        fun iso(ms: Long): String = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }.format(Date(ms))
        fun day(ms: Long): String = SimpleDateFormat("yyyy-MM-dd", Locale.US).format(Date(ms))
        fun describe(c: Control): String = c.effective(STAGE).name.lowercase() + (c.message?.let { " - $it" } ?: "")

        fun registryJson(cfg: NodeConfig, state: String, control: String, running: RunningJob?, lastSeen: String, startedAt: String, limits: LimitView? = null, network: NetInfo? = null, profile: DownloadProfile? = null): JSONObject = JSONObject()
            .put("v", 1).put("nodeId", cfg.nodeId).put("name", cfg.name).put("kind", "medusa").put("version", cfg.version).put("platform", cfg.platform)
            .put("stages", JSONArray(listOf("download", "refresh"))).put("jobs", JSONObject().put("download", 1).put("refresh", 1)).put("threads", cfg.threads)
            .put("conditions", cfg.conditions).put("state", state).put("control", control)
            .put("running", JSONArray().also { a -> running?.let { r -> a.put(JSONObject().put("jobId", r.jobId).put("stage", r.stage).put("step", r.step).put("progress", r.progress ?: JSONObject.NULL)) } })
            .put("lastSeen", lastSeen).put("startedAt", startedAt)
            .also { o -> network?.let { o.put("network", it.toJson()) } }
            .also { o -> profile?.let { o.put("downloadProfile", it.toJson()) } }
            .also { o -> limits?.let { l ->
                l.budget?.let { o.put("budget", it) }
                l.schedule?.let { o.put("schedule", it) }; l.window?.let { o.put("window", it) }
                l.nextWindow?.let { o.put("nextWindow", it) }
            } }
    }
}

class DownloadJob(val jobId: String, val url: String, val maxHeight: Int, val format: String?, val subs: Boolean, val priority: Int? = null) {
    /** The -f value: a raw format wins, else any video plus compact Opus audio, falling back to a single file. Resolution comes from [sortArgs]. */
    fun selector(): String = format ?: "bv*+ba[acodec=opus][abr<=80]/bv*+ba/b"

    /** Extra sort arguments: resolution first (-S res:H) for the default request; none when the job brings its own format. */
    fun sortArgs(): List<String> = if (format != null) emptyList() else listOf("-S", "res:$maxHeight")

    /** All yt-dlp format arguments, in order. */
    fun formatArgs(): List<String> = sortArgs() + listOf("-f", selector(), "--merge-output-format", "mp4")

    companion object {
        fun parse(text: String): DownloadJob {
            val o = JSONObject(text)
            return DownloadJob(o.getString("jobId"), o.getString("url"), if (o.isNull("maxHeight")) 480 else o.optInt("maxHeight", 480),
                if (o.isNull("format")) null else o.optString("format").ifBlank { null }, o.optBoolean("subs", true),
                if (o.has("priority") && !o.isNull("priority")) o.optInt("priority", 0).takeIf { it in Prio.MIN..Prio.MAX } else null)
        }
    }
}

class DownloadedFiles(
    val video: File, val extras: List<Triple<String, File, String>>, val height: Int?, val durationSeconds: Double?,
    val vcodec: String? = null, val acodec: String? = null, val formatId: String? = null,
    val subs: List<String> = emptyList(), val embeddable: Boolean? = null,
    val audioLanguage: String? = null, val audioNote: String? = null, val audioTracks: List<Pair<String, String?>> = emptyList(),
    /** Subtitle tracks that were wanted but could not be fetched ("lang" or "lang.auto"); they never fail the job. */
    val subsFailed: List<String> = emptyList(),
    /** "mp4" when the default selector got a 403 and the MP4 selector was used instead. */
    val formatFallback: String? = null,
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
    /** Lists a playlist or channel tab with yt-dlp --flat-playlist (newest first); [end] 0 = the whole listing. Returns the raw lines ([RefreshWork.LIST_PRINT]). Throws NodeFetchException. */
    fun list(url: String, end: Int): List<String> = throw NodeFetchException(FailureKind.PERMANENT, "this fetcher cannot list sources")
    /** Kills the running download. */
    fun cancel()
    fun cleanup(dir: File) {}
}
