package io.ref12.mediahelper

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest

data class JobMeta(
    val id: String,
    val url: String,
    val languages: List<String>,
    val state: JobState,
    val message: String?,
    val title: String?,
    val returnUrl: String?,
    val updatedAt: Long,
) {
    fun toJson() = JSONObject().put("id", id).put("url", url).put("languages", JSONArray(languages))
        .put("state", state.wire).put("message", message).put("title", title)
        .put("returnUrl", returnUrl).put("updatedAt", updatedAt)

    companion object {
        fun fromJson(j: JSONObject) = JobMeta(
            j.getString("id"), j.getString("url"),
            j.getJSONArray("languages").let { a -> (0 until a.length()).map { a.getString(it) } },
            JobState.entries.first { it.wire == j.getString("state") },
            j.optString("message").takeIf { it.isNotEmpty() && !j.isNull("message") },
            j.optString("title").takeIf { it.isNotEmpty() && !j.isNull("title") },
            j.optString("returnUrl").takeIf { it.isNotEmpty() && !j.isNull("returnUrl") },
            j.optLong("updatedAt"),
        )
    }
}

/** Result cache (keyed by URL + languages) and job records, as plain files in app storage. */
class Storage(private val root: File, private val clock: () -> Long = System::currentTimeMillis) {
    private val results = File(root, "results").also { it.mkdirs() }
    private val jobs = File(root, "jobs").also { it.mkdirs() }

    init { purge() }

    fun saveResult(key: String, json: String) = File(results, "$key.json").writeText(json)

    fun loadResult(key: String): String? {
        val f = File(results, "$key.json")
        if (!f.isFile) return null
        if (clock() - f.lastModified() > MAX_AGE_MS) { f.delete(); return null }
        return f.readText()
    }

    fun saveJob(m: JobMeta) = File(jobs, "${m.id}.json").writeText(m.toJson().toString())

    fun loadJob(id: String): JobMeta? {
        if (!Regex("[A-Za-z0-9_-]{1,64}").matches(id)) return null
        val f = File(jobs, "$id.json")
        return try { if (f.isFile) JobMeta.fromJson(JSONObject(f.readText())) else null } catch (e: Exception) { null }
    }

    fun recentJobs(limit: Int = 20): List<JobMeta> =
        (jobs.listFiles() ?: emptyArray()).mapNotNull { f ->
            try { JobMeta.fromJson(JSONObject(f.readText())) } catch (e: Exception) { null }
        }.sortedByDescending { it.updatedAt }.take(limit)

    private fun purge() {
        for (dir in listOf(results, jobs)) for (f in dir.listFiles() ?: emptyArray()) {
            if (clock() - f.lastModified() > MAX_AGE_MS) f.delete()
        }
    }

    companion object {
        const val MAX_AGE_MS = 30L * 24 * 3600 * 1000

        fun key(url: String, languages: List<String>): String {
            val d = MessageDigest.getInstance("SHA-256").digest((url.trim() + "\n" + languages.joinToString(",")).toByteArray())
            return d.joinToString("") { "%02x".format(it) }
        }
    }
}
