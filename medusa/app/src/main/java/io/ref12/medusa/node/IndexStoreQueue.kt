package io.ref12.medusa.node

import org.json.JSONArray
import org.json.JSONObject

/** One library download waiting to be stored: the item, the mode, and the names already uploaded (so an interrupted upload resumes). */
class StoreEntry(val mediaId: String, val mode: StoreMode, val done: MutableSet<String> = LinkedHashSet(), var attempts: Int = 0, var lastError: String? = null) {
    fun toJson(): JSONObject = JSONObject().put("mediaId", mediaId).put("mode", mode.wire).put("done", JSONArray(done.toList())).put("attempts", attempts).put("lastError", lastError ?: JSONObject.NULL)
    companion object {
        fun parse(o: JSONObject) = StoreEntry(o.getString("mediaId"), StoreMode.parse(o.optString("mode")),
            o.optJSONArray("done")?.let { a -> (0 until a.length()).mapTo(LinkedHashSet()) { a.getString(it) } } ?: LinkedHashSet(), o.optInt("attempts"), o.optString("lastError").ifEmpty { null })
    }
}

/** What the owner chose for a download that is still running (job id -> mode), and the stores still to do. Both survive the process being killed. */
class IndexStoreQueue(private val store: TextStore) {
    private fun root(): JSONObject = try { JSONObject(store.load() ?: "{}") } catch (e: Exception) { JSONObject() }
    private fun save(o: JSONObject) = store.save(o.toString())

    @Synchronized fun intend(jobId: String, mode: StoreMode) { val o = root(); val i = o.optJSONObject("intents") ?: JSONObject(); i.put(jobId, mode.wire); o.put("intents", i); save(o) }

    /** The download [jobId] finished for [mediaId]: its intent becomes a queue entry (replacing an older entry of the same item). Returns the entry, or null when the owner chose nothing for it. */
    @Synchronized fun finished(jobId: String, mediaId: String): StoreEntry? {
        val o = root(); val i = o.optJSONObject("intents") ?: return null
        if (!i.has(jobId)) return null
        val mode = StoreMode.parse(i.optString(jobId)); i.remove(jobId); o.put("intents", i)
        val q = (o.optJSONArray("queue") ?: JSONArray()).let { a -> (0 until a.length()).map { StoreEntry.parse(a.getJSONObject(it)) } }.filter { it.mediaId != mediaId }
        val e = StoreEntry(mediaId, mode)
        o.put("queue", JSONArray((q + e).map { it.toJson() })); save(o); return e
    }

    @Synchronized fun failed(jobId: String) { val o = root(); o.optJSONObject("intents")?.let { it.remove(jobId); o.put("intents", it); save(o) } }

    @Synchronized fun entries(): List<StoreEntry> = root().optJSONArray("queue")?.let { a -> (0 until a.length()).map { StoreEntry.parse(a.getJSONObject(it)) } } ?: emptyList()

    @Synchronized fun update(e: StoreEntry) {
        val o = root(); val q = entries().map { if (it.mediaId == e.mediaId) e else it }
        o.put("queue", JSONArray(q.map { it.toJson() })); save(o)
    }

    @Synchronized fun remove(mediaId: String) { val o = root(); o.put("queue", JSONArray(entries().filter { it.mediaId != mediaId }.map { it.toJson() })); save(o) }
}
