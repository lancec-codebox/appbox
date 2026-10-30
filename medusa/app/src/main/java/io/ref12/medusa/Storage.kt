package io.ref12.medusa

import org.json.JSONArray
import org.json.JSONObject
import java.io.File

data class JobMeta(
    val id: String,
    val url: String,
    val languages: List<String>,
    val want: Want,
    val quality: Quality,
    val state: JobState,
    val progress: Double?,
    val code: String?,
    val message: String?,
    val title: String?,
    val mediaId: String?,
    val returnUrl: String?,
    val updatedAt: Long,
) {
    fun toJson() = JSONObject().put("id", id).put("url", url).put("languages", JSONArray(languages))
        .put("wantSubtitles", want.subtitles).put("wantVideo", want.video).put("quality", quality.wire)
        .put("state", state.wire).put("progress", progress ?: JSONObject.NULL).put("code", code ?: JSONObject.NULL)
        .put("message", message ?: JSONObject.NULL).put("title", title ?: JSONObject.NULL)
        .put("mediaId", mediaId ?: JSONObject.NULL).put("returnUrl", returnUrl ?: JSONObject.NULL).put("updatedAt", updatedAt)

    companion object {
        private fun JSONObject.str(k: String) = if (isNull(k) || !has(k)) null else optString(k).takeIf { it.isNotEmpty() }

        fun fromJson(j: JSONObject) = JobMeta(
            j.getString("id"), j.getString("url"),
            j.getJSONArray("languages").let { a -> (0 until a.length()).map { a.getString(it) } },
            Want(j.optBoolean("wantSubtitles", true), j.optBoolean("wantVideo", true)),
            Quality.parse(j.optString("quality")) ?: Quality.P720,
            JobState.entries.first { it.wire == j.getString("state") },
            if (j.isNull("progress") || !j.has("progress")) null else j.optDouble("progress"),
            j.str("code"), j.str("message"), j.str("title"), j.str("mediaId"), j.str("returnUrl"),
            j.optLong("updatedAt"),
        )
    }
}

/** Job records as plain files in app storage (captured media lives in [MediaStore]). */
class Storage(root: File, private val clock: () -> Long = System::currentTimeMillis) {
    private val jobs = File(root, "jobs").also { it.mkdirs() }

    init { purge() }

    fun saveJob(m: JobMeta) = File(jobs, "${m.id}.json").writeText(m.toJson().toString())

    fun loadJob(id: String): JobMeta? {
        if (!Regex("[A-Za-z0-9_-]{1,64}").matches(id)) return null
        val f = File(jobs, "$id.json")
        return try { if (f.isFile) JobMeta.fromJson(JSONObject(f.readText())) else null } catch (e: Exception) { null }
    }

    /** An alias: GET /v1/jobs/<alias> answers with the job <target> (a client's own id for a job that already existed). */
    fun saveAlias(alias: String, target: String) = File(jobs, "$alias.alias").writeText(target)

    fun loadAlias(alias: String): String? {
        if (!Regex("[A-Za-z0-9_-]{1,64}").matches(alias)) return null
        val f = File(jobs, "$alias.alias")
        return try { if (f.isFile) f.readText().trim().takeIf { it.isNotEmpty() } else null } catch (e: Exception) { null }
    }

    fun jobIds(): List<String> = (jobs.listFiles() ?: emptyArray()).filter { it.name.endsWith(".json") }.map { it.name.removeSuffix(".json") }

    fun deleteJob(id: String) { File(jobs, "$id.json").delete() }

    fun recentJobs(limit: Int = 20): List<JobMeta> =
        (jobs.listFiles() ?: emptyArray()).filter { it.name.endsWith(".json") }.mapNotNull { f ->
            try { JobMeta.fromJson(JSONObject(f.readText())) } catch (e: Exception) { null }
        }.sortedByDescending { it.updatedAt }.take(limit)

    private fun purge() {
        for (f in jobs.listFiles() ?: emptyArray()) if (clock() - f.lastModified() > MAX_AGE_MS) f.delete()
    }

    companion object { const val MAX_AGE_MS = 30L * 24 * 3600 * 1000 }
}
