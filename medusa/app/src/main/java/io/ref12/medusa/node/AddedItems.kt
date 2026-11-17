package io.ref12.medusa.node

import org.json.JSONArray
import org.json.JSONObject

/** One thing this phone added to the index: a video, playlist or channel. */
class AddedItem(val kind: String, val jobId: String, val url: String, val name: String, val addedAt: Long, val priority: Int)

/** Where the list is kept (a SharedPreferences string on the phone, a variable in tests). */
interface TextStore { fun load(): String?; fun save(text: String) }

/** The local "Added from this phone" list. Only what this phone added; nothing is read from the index to fill it. */
class AddedItems(private val store: TextStore) {
    fun all(): List<AddedItem> {
        val a = try { JSONArray(store.load() ?: "[]") } catch (e: Exception) { JSONArray() }
        return (0 until a.length()).mapNotNull { try { a.getJSONObject(it).let { o ->
            AddedItem(o.getString("kind"), o.getString("jobId"), o.optString("url"), o.optString("name").ifEmpty { o.getString("jobId") }, o.optLong("at"), o.optInt("priority", 0)) } } catch (e: Exception) { null } }
            .sortedByDescending { it.addedAt }
    }

    private fun write(items: List<AddedItem>) = store.save(JSONArray().also { a -> items.forEach {
        a.put(JSONObject().put("kind", it.kind).put("jobId", it.jobId).put("url", it.url).put("name", it.name).put("at", it.addedAt).put("priority", it.priority)) } }.toString())

    /** Adds or replaces (same jobId) an entry. */
    @Synchronized fun add(item: AddedItem) { write(all().filter { it.jobId != item.jobId } + item) }
    @Synchronized fun remove(jobId: String) { write(all().filter { it.jobId != jobId }) }
    @Synchronized fun setPriority(jobId: String, priority: Int) {
        write(all().map { if (it.jobId == jobId) AddedItem(it.kind, it.jobId, it.url, it.name, it.addedAt, priority) else it })
    }
    fun find(jobId: String) = all().firstOrNull { it.jobId == jobId }
}

/** The store-side state of an entry, as one line. */
object AddedState {
    /** Download jobs of a source (by "source"/"sources" in their body): counts per idx_state. */
    fun sourceCounts(api: BlobApi, sourceId: String): Map<String, Int> {
        val c = LinkedHashMap<String, Int>()
        for (r in api.findAll("download")) {
            val g = api.get(r.name); if (g.status != 200) continue
            val o = JSONObject(g.text)
            val a = o.optJSONArray("sources")
            val srcs = if (a != null) (0 until a.length()).map { a.getString(it) } else listOf(o.optString("source"))
            if (sourceId in srcs) { val st = r.tags["idx_state"] ?: "?"; c[st] = (c[st] ?: 0) + 1 }
        }
        return c
    }

    fun describe(item: AddedItem, api: BlobApi): String = try {
        if (item.kind == "video") {
            val st = api.getTags(IndexVideo.jobPath(item.jobId.removePrefix("v-")))["idx_state"]
            when {
                st != null -> "download " + st
                api.listNames("index/videos/" + item.jobId.removePrefix("v-") + "/", 1).isNotEmpty() -> "downloaded"
                else -> "no job found"
            }
        } else {
            val w = api.get(WatchJobs.path(item.jobId))
            if (w.status != 200) "source not found (removed?)" else {
                val c = sourceCounts(api, item.jobId)
                "pending " + (c["pending"] ?: 0) + " \u00b7 done " + (c["done"] ?: 0) + " \u00b7 failed " + (c["failed"] ?: 0)
            }
        }
    } catch (e: Exception) { "state unknown: " + Redact.sas(e.message) }

    /** Removes the entry's job from the index: a source's watch job, or a still-pending video job. Downloaded videos stay. */
    fun removeFromIndex(api: BlobApi, item: AddedItem): String =
        if (item.kind == "video") {
            val p = IndexVideo.jobPath(item.jobId.removePrefix("v-"))
            if (api.getTags(p)["idx_state"] == "pending") { api.delete(p); "Removed the pending download" } else "Entry removed (the download is not pending, so it was left)"
        } else { WatchJobs.remove(api, item.jobId); "Source removed (its downloads stay)" }

    /** Changes the priority of an entry in the index, with the re-tagging rules of "sources priority". */
    fun changePriority(api: BlobApi, item: AddedItem, priority: Int, listed: Collection<String>?): SourceResult =
        Priorities.setSource(api, if (item.kind == "video") "v-" + item.jobId.removePrefix("v-") else item.jobId, priority, listed)
}
