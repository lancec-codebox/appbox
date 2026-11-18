package io.ref12.medusa.node

import org.json.JSONArray
import org.json.JSONObject

/** Job priority exactly as IndexNode 0.8.6 (Protocol.cs Tags, PriorityCommand.cs). */
object Prio {
    const val MIN = -1000
    const val MAX = 1000
    const val TAG = "idx_priority"

    fun check(p: Int): Int = if (p in MIN..MAX) p else throw IllegalArgumentException("priority $p is outside $MIN..$MAX")

    /** idx_priority of a job blob; missing, unparsable or out of range counts as 0. */
    fun of(tags: Map<String, String>): Int = tags[TAG]?.trim()?.toIntOrNull()?.takeIf { it in MIN..MAX } ?: 0

    /** Claim order, as NodeEngine.TryClaimAsync: priority descending, then idx_added, then name. */
    fun claimOrder(jobs: List<JobRef>): List<JobRef> =
        jobs.sortedWith(compareByDescending<JobRef> { of(it.tags) }.thenBy { it.added }.thenBy { it.name })

    /** The tags of a new job; 0 is the default and writes no idx_priority tag. */
    fun jobTags(stage: String, state: String, added: String, priority: Int, due: String? = added): Map<String, String> {
        val t = LinkedHashMap<String, String>()
        t["idx_stage"] = stage; t["idx_state"] = state; t["idx_added"] = added; t["idx_due"] = due ?: added
        if (priority != 0) t[TAG] = check(priority).toString()
        return t
    }
}

/** The jobs a finished download creates, as IndexNode's DownloadStage.EnsureFollowUpJobs: comments plus one job per ingest step. Never the legacy "process" job. */
object FollowUps {
    val STEP_STAGES = listOf("frames", "sscd", "dino", "thumbs", "clip", "transcribe", "ocrsubs", "cast", "sync", "describe", "translate")
    /** Protocol.IngestStepStages: every step except the opt-in dino and clip. */
    val INGEST_STEPS = STEP_STAGES.filter { it != "dino" && it != "clip" }
    const val ASR = "sensevoice"
    const val SSCD = "sscd_disc_mixup"

    fun needsOf(step: String): List<String> = when (step) {
        "sscd", "dino", "clip", "thumbs" -> listOf("frames/manifest.json")
        "cast" -> listOf("frames/manifest.json", "sscd/manifest.json")
        "sync" -> listOf("asr/$ASR.zh.json", "ocr/lines.json")
        "describe" -> listOf("asr/$ASR.zh.json", "ocr/lines.json", "cast/clusters.json")
        "translate" -> listOf("describe.json")
        else -> emptyList()
    }

    fun stepJob(step: String, jobId: String, priority: Int, at: String): JSONObject = JSONObject()
        .put("v", 1).put("stage", step).put("jobId", jobId).put("steps", JSONArray(listOf(step)))
        .also { o -> needsOf(step).takeIf { it.isNotEmpty() }?.let { o.put("needs", JSONArray(it)) } }
        .put("priority", priority).put("addedAt", at).put("attempts", 0)

    fun commentsJob(jobId: String, url: String, priority: Int, at: String): JSONObject = JSONObject()
        .put("v", 1).put("stage", "comments").put("jobId", jobId).put("url", url)
        .put("maxComments", 1000).put("sort", "top").put("round", 0).put("priority", priority).put("addedAt", at).put("attempts", 0)

    /** Writes each job unless it exists (If-None-Match: *). Returns the stages created. */
    fun create(api: BlobApi, jobId: String, url: String, priority: Int, at: String): List<String> {
        val made = ArrayList<String>()
        fun put(stage: String, o: JSONObject) {
            if (api.put("index/jobs/$stage/$jobId.json", o.toString().toByteArray(), "application/json", Prio.jobTags(stage, "pending", at, priority), null, true)) made.add(stage)
        }
        put("comments", commentsJob(jobId, url, priority, at))
        for (s in INGEST_STEPS) put(s, stepJob(s, jobId, priority, at))
        return made
    }
}

class SetResult(val changed: Int, val skipped: Map<String, Int>)
class SourceResult(val videos: Int, val changed: Int, val skipped: Map<String, Int>, val matched: Int)

/** "sources priority" / "priority set": re-tags pending jobs only, under the lease, keeping every other tag. */
object Priorities {
    private fun stateName(s: String?) = when (s) { "done" -> "done"; "failed" -> "failed"; else -> "state $s" }

    fun set(api: BlobApi, priority: Int, targets: List<Pair<String, String>>, explicit: Boolean = true): SetResult {
        Prio.check(priority)
        var changed = 0
        val skipped = LinkedHashMap<String, Int>()
        fun skip(why: String) { skipped[why] = (skipped[why] ?: 0) + 1 }
        for ((stage, id) in targets.distinct()) {
            val name = "index/jobs/$stage/$id.json"
            var tags = api.getTags(name)
            if (tags.isEmpty()) { skip("no such job"); continue }
            if (tags["idx_state"] != "pending") { skip(stateName(tags["idx_state"])); continue }
            val lease = api.acquireLease(name, 60)
            if (lease == null) { skip("leased"); continue }
            try {
                tags = api.getTags(name) // confirm under the lease
                val body = api.get(name)
                if (tags.isEmpty() || body.status != 200 || tags["idx_state"] != "pending") { skip("no longer pending"); continue }
                val job = JSONObject(body.text).put("priority", priority)
                if (explicit) job.put("explicit", true) else job.remove("explicit")
                val next = LinkedHashMap(tags) // every tag stays
                if (stage == RefreshJobs.STAGE && Prio.of(tags) == RefreshJobs.NOW_PRIORITY) { /* a lookup that is due now keeps its boost; the body's priority is what its videos inherit */ }
                else if (priority == 0) next.remove(Prio.TAG) else next[Prio.TAG] = priority.toString()
                api.put(name, job.toString().toByteArray(), "application/json", next, lease, false)
                changed++
            } finally { try { api.releaseLease(name, lease) } catch (e: Exception) { /* it expires */ } }
        }
        return SetResult(changed, skipped)
    }

    private fun sourcesOf(o: JSONObject): List<String> {
        val a = o.optJSONArray("sources")
        if (a != null) return (0 until a.length()).map { a.getString(it) }
        return o.optString("source").takeIf { it.isNotEmpty() }?.let { listOf(it) } ?: emptyList()
    }

    /** The highest of the video's own priority and its sources' priorities; 0 when it has none. */
    fun effective(dl: JSONObject, sourcePriority: Map<String, Int>): Int {
        val all = ArrayList<Int>()
        if (dl.has("ownPriority") && !dl.isNull("ownPriority")) all.add(dl.optInt("ownPriority"))
        for (s in sourcesOf(dl)) sourcePriority[s]?.let { all.add(it) }
        return all.maxOrNull() ?: 0
    }

    /**
     * Sets a source's priority and re-tags the still-pending jobs (download, comments, steps) of every video from it, as "sources priority".
     * [sourceId] is a refresh job id (ch-/pl-) or v-<videoId> for a direct video. [listedIds]: video ids from a yt-dlp listing of the source,
     * to match videos queued before their source was recorded.
     */
    fun setSource(api: BlobApi, sourceId: String, priority: Int, listedIds: Collection<String>?): SourceResult {
        Prio.check(priority)
        val direct = sourceId.startsWith("v-")
        val prios = HashMap<String, Int>()
        try { RefreshJobs.migrateLegacy(api) } catch (e: Exception) { /* read the old ones below */ }
        for (r in api.findAll("refresh")) { val g = api.get(r.name); if (g.status == 200) { val w = JSONObject(g.text); prios[w.optString("jobId", r.id)] = w.optInt("priority", 0) } }
        val skipped = LinkedHashMap<String, Int>(); var changed = 0; var matched = 0
        if (!direct) {
            if (!prios.containsKey(sourceId)) throw IllegalArgumentException("no source $sourceId")
            val r = set(api, priority, listOf("refresh" to sourceId), explicit = false)
            if (r.changed == 0) throw IllegalStateException("source $sourceId could not be updated (" + r.skipped.keys.joinToString(", ") + "); try again")
            prios[sourceId] = priority
        }
        val listed = listedIds?.toSet() ?: emptySet()
        val videos = ArrayList<Pair<String, JSONObject>>()
        for (b in api.findAll("download")) {
            val id = b.id
            if (direct && id != sourceId.substring(2)) continue
            val g = api.get(b.name); if (g.status != 200) continue
            val dl = JSONObject(g.text)
            val recorded = sourcesOf(dl).contains(sourceId)
            if (!direct && !recorded && id !in listed) continue
            if (direct) dl.put("ownPriority", priority)
            else if (!recorded) { dl.put("sources", JSONArray((sourcesOf(dl) + sourceId).distinct())); matched++ }
            if (direct || !recorded) {
                val lease = api.acquireLease(b.name, 60)
                if (lease != null) { try { api.put(b.name, dl.toString().toByteArray(), "application/json", LinkedHashMap(b.tags), lease, false) } finally { try { api.releaseLease(b.name, lease) } catch (e: Exception) {} } }
                else skipped["leased (source not recorded)"] = (skipped["leased (source not recorded)"] ?: 0) + 1
            }
            videos.add(id to dl)
        }
        for ((id, dl) in videos) {
            val p = effective(dl, prios)
            val r = set(api, p, (listOf("download", "comments") + FollowUps.STEP_STAGES).map { it to id }, explicit = false)
            changed += r.changed
            for ((k, v) in r.skipped) if (k != "no such job") skipped[k] = (skipped[k] ?: 0) + v
        }
        return SourceResult(videos.size, changed, skipped, matched)
    }
}
