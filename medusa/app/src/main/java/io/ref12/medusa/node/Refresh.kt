package io.ref12.medusa.node

import org.json.JSONArray
import org.json.JSONObject
import java.util.Calendar
import java.util.TimeZone

/** When a source is refreshed after its lookup (IndexNode's Schedule): once | off | 6h | 1d | daily@03:00 (UTC). A pre-1.9.2 job has only "every", which stays its schedule. */
class RefreshSchedule(val kind: String, val intervalMs: Long, val atMinutes: Int, val text: String) {
    fun describe() = when (kind) { "every" -> "every $text"; "daily" -> "daily " + text.removePrefix("daily@") + " UTC"; else -> text }
    /** The next time after [nowMs]; null for once and off. */
    fun next(nowMs: Long): Long? = when (kind) {
        "every" -> nowMs + intervalMs
        "daily" -> { val c = Calendar.getInstance(TimeZone.getTimeZone("UTC")).apply { timeInMillis = nowMs; set(Calendar.HOUR_OF_DAY, atMinutes / 60); set(Calendar.MINUTE, atMinutes % 60); set(Calendar.SECOND, 0); set(Calendar.MILLISECOND, 0) }
            if (c.timeInMillis > nowMs) c.timeInMillis else c.timeInMillis + 86_400_000L }
        else -> null
    }
    companion object {
        val ONCE = RefreshSchedule("once", 0, 0, "once"); val OFF = RefreshSchedule("off", 0, 0, "off")
        private val EVERY = Regex("^(\\d+)\\s*([smhd])$", RegexOption.IGNORE_CASE)
        private val DAILY = Regex("^daily@(\\d{1,2}):(\\d{2})$", RegexOption.IGNORE_CASE)
        fun parse(s: String?): RefreshSchedule {
            val t = (s ?: "").trim().removePrefix("every ").trim()
            if (t.isEmpty() || t.equals("once", true)) return ONCE
            if (t.equals("off", true)) return OFF
            DAILY.matchEntire(t)?.let { m -> val h = m.groupValues[1].toInt(); val mi = m.groupValues[2].toInt()
                if (h > 23 || mi > 59) throw IllegalArgumentException("bad schedule '$s' (daily@03:00 needs a time of day, UTC)")
                return RefreshSchedule("daily", 0, h * 60 + mi, "daily@%02d:%02d".format(h, mi)) }
            val m = EVERY.matchEntire(t)?.takeIf { it.groupValues[1].toLong() > 0 } ?: throw IllegalArgumentException("bad schedule '$s' (once, off, 6h, 1d or daily@03:00)")
            val n = m.groupValues[1].toLong()
            return RefreshSchedule("every", n * when (m.groupValues[2].lowercase()) { "s" -> 1_000L; "m" -> 60_000L; "h" -> 3_600_000L; else -> 86_400_000L }, 0, t)
        }
        /** The schedule of a source job: its "schedule", else the "every" it was added with before 1.9.2, else once. */
        fun of(job: JSONObject): RefreshSchedule = parse(job.optString("schedule").ifEmpty { job.optString("every") })
    }
}

/** One video of a source listing (yt-dlp --flat-playlist --print [RefreshWork.LIST_PRINT]). */
class ListEntry(val id: String, val channelId: String?, val channelName: String?, val duration: Double?, val availability: String?, val title: String?)

/**
 * A source job's run (IndexNode's RefreshStage): a lookup (full listing: every member recorded, every unknown video queued, subject to the backfill limit) or a refresh
 * (incremental: a channel's newest uploads until known ones are reached, a playlist in full; only new videos queued). Lists with yt-dlp through [NodeFetcher.list].
 */
object RefreshWork {
    const val LIST_PRINT = "%(id)s\t%(channel_id)s\t%(channel)s\t%(duration)s\t%(availability)s\t%(title)s"
    const val LIST_SIZE = 30
    const val CHANNEL_FIRST = 100
    const val SEEN_CAP = 500
    private val ID = Regex("[A-Za-z0-9_-]{11}")

    fun parseLine(line: String): ListEntry? {
        val p = line.trimEnd('\r').split('\t')
        val id = p[0].trim(); if (!ID.matches(id)) return null
        fun f(i: Int) = p.getOrNull(i)?.trim()?.takeIf { it.isNotEmpty() && it != "NA" }
        return ListEntry(id, f(1), f(2), f(3)?.toDoubleOrNull(), f(4), if (p.size > 5) p.drop(5).joinToString(" ").trim().takeIf { it.isNotEmpty() && it != "NA" } else null)
    }

    /** What the next claim does: the job's own "run", else a job written before 1.9.2 that never ran is a lookup and any other a refresh. */
    fun kindOf(job: JSONObject): String = job.optString("run").takeIf { it == "lookup" || it == "refresh" } ?: if (job.isNull("seen")) "lookup" else "refresh"

    fun urls(job: JSONObject): List<String> {
        val id = job.getString("id")
        if (job.optString("kind") == "playlist") return listOf("https://www.youtube.com/playlist?list=$id")
        val tabs = job.optJSONArray("tabs")?.let { a -> (0 until a.length()).map { a.getString(it) } }?.takeIf { it.isNotEmpty() } ?: listOf("videos", "shorts")
        return tabs.map { "https://www.youtube.com/channel/$id/$it" }
    }

    private fun list(fetcher: NodeFetcher, job: JSONObject, end: Int): List<ListEntry> {
        val all = ArrayList<ListEntry>(); var failed = 0; var last: NodeFetchException? = null
        val urls = urls(job)
        for (u in urls) {
            try { for (l in fetcher.list(u, end)) parseLine(l)?.let { e -> if (all.none { it.id == e.id }) all.add(e) } }
            catch (e: NodeFetchException) { failed++; last = e }
        }
        if (failed == urls.size) throw last!!
        return all
    }

    class Result(val job: JSONObject, val nextDueMs: Long?, val kind: String, val listed: Int, val made: Int)

    fun run(api: BlobApi, fetcher: NodeFetcher, job: JSONObject, nowMs: Long): Result {
        val at = NodeRunner.iso(nowMs)
        val kind = kindOf(job); val lookup = kind == "lookup"
        val isPlaylist = job.optString("kind") == "playlist"
        val sourceId = job.getString("jobId")
        val backfill = if (job.has("backfill") && !job.isNull("backfill")) job.optInt("backfill", 0).coerceAtLeast(0) else null
        var end = if (lookup) (if (isPlaylist) 0 else maxOf(CHANNEL_FIRST, backfill ?: 0)) else if (isPlaylist) 0 else LIST_SIZE
        val seen = HashSet<String>(); job.optJSONArray("seen")?.let { a -> for (i in 0 until a.length()) seen.add(a.getString(i)) }
        var listed = list(fetcher, job, end)
        while (!lookup && !isPlaylist && end < 500 && listed.size >= end && listed.none { it.id in seen }) { end *= 4; listed = list(fetcher, job, end) }
        val fresh = if (lookup) listed else listed.filter { it.id !in seen }
        val priority = job.optInt("priority", 0)
        val limit = if (lookup && isPlaylist && backfill != null) backfill else Int.MAX_VALUE
        var made = 0
        for (e in fresh) {
            val known = api.getTags(IndexVideo.jobPath(e.id)).isNotEmpty() || api.listNames("index/videos/${e.id}/", 1).isNotEmpty()
            if (known) continue
            if (made >= limit) break
            val d = JSONObject().put("v", 1).put("stage", "download").put("jobId", e.id).put("url", "https://www.youtube.com/watch?v=${e.id}")
                .put("maxHeight", job.optInt("maxHeight", 480)).put("format", JSONObject.NULL).put("subs", true).put("source", sourceId).put("sources", JSONArray(listOf(sourceId)))
                .put("addedAt", at).put("attempts", 0).also { if (priority != 0) it.put("priority", Prio.check(priority)) }
                .also { o -> e.title?.let { o.put("title", it) }; e.duration?.let { o.put("duration", it) }; e.availability?.let { o.put("availability", it) }
                    (e.channelId ?: if (!isPlaylist) job.getString("id") else null)?.let { o.put("channelId", it) }
                    (e.channelName ?: if (!isPlaylist) job.optString("name") else null)?.let { o.put("channelName", it) } }
            if (api.put(IndexVideo.jobPath(e.id), d.toString().toByteArray(), "application/json", Prio.jobTags("download", "unclassified", at, priority), null, true)) made++
        }
        if (made > 0) ensureClassifier(api, at)
        if (job.optBoolean("followChannels", false)) {
            val done = HashSet<String>()
            for (e in fresh) { val c = e.channelId ?: continue; if (!done.add(c)) continue
                SourceFollow.followRef(api, SourceRef("channel", c), FollowOptions("once", 0, false, e.channelName ?: c, job.optInt("maxHeight", 480), priority), nowMs) }
        }
        val members = writeMembers(api, job, listed, lookup, at)
        val nextSeen = (fresh.map { it.id } + seen.toList()).distinct().take(SEEN_CAP)
        val next = RefreshSchedule.of(job).next(nowMs)
        job.put("seen", JSONArray(nextSeen)).put("found", job.optInt("found", 0) + made).put("lastPolled", at).put("lastRun", kind).put(if (lookup) "lastLookup" else "lastRefresh", at)
            .put("run", "refresh").put("attempts", 0).put("members", members).also { it.remove("lastError"); it.remove("permanent") }
            .put("nextRefresh", next?.let { NodeRunner.iso(it) } ?: JSONObject.NULL)
        return Result(job, next, kind, listed.size, made)
    }

    /** index/sources/<id>/members.json: the member list of the last look (a channel's listing is only its newest uploads, so it is merged into what is there). */
    private fun writeMembers(api: BlobApi, job: JSONObject, listed: List<ListEntry>, lookup: Boolean, at: String): Int {
        val path = "index/sources/" + job.getString("jobId") + "/members.json"
        val rows = ArrayList<JSONObject>(); val ids = HashSet<String>()
        for (e in listed) { ids.add(e.id); rows.add(JSONObject().put("id", e.id).put("title", e.title ?: JSONObject.NULL).put("duration", e.duration ?: JSONObject.NULL)
            .put("channelId", e.channelId ?: JSONObject.NULL).put("channelName", e.channelName ?: JSONObject.NULL).put("availability", e.availability ?: JSONObject.NULL)) }
        var looked = at
        try { val g = api.get(path); if (g.status == 200) { val o = JSONObject(g.text)
            if (job.optString("kind") == "channel") o.optJSONArray("members")?.let { a -> for (i in 0 until a.length()) { val m = a.getJSONObject(i); if (ids.add(m.getString("id"))) rows.add(m) } }
            if (!lookup) looked = o.optString("lookedUpAt").ifEmpty { at } } } catch (e: Exception) { /* a broken old file is rewritten */ }
        val doc = JSONObject().put("v", 1).put("sourceId", job.getString("jobId")).put("kind", job.optString("kind")).put("id", job.getString("id")).put("name", job.optString("name"))
            .put("updatedAt", at).put("lookedUpAt", looked).put("members", JSONArray(rows))
        api.put(path, doc.toString().toByteArray(), "application/json")
        return rows.size
    }

    /** The singleton classify job (index/jobs/classify/all.json) that decides the unclassified downloads this run made. */
    private fun ensureClassifier(api: BlobApi, at: String) {
        val name = "index/jobs/classify/all.json"
        val body = JSONObject().put("v", 1).put("stage", "classify").put("jobId", "all").put("name", "classify all").put("addedAt", at).put("attempts", 0).toString().toByteArray()
        try { if (!api.put(name, body, "application/json", Prio.jobTags("classify", "pending", at, 0), null, true)) {
            val tags = api.getTags(name); val st = tags["idx_state"]
            if (st == "done" || st == "failed") api.put(name, body, "application/json", Prio.jobTags("classify", "pending", tags["idx_added"] ?: at, 0, at), null, false)
        } } catch (e: BlobException) { /* leased by a node: it is running */ }
    }
}

