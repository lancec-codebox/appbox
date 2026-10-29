package io.ref12.medusa

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.util.UUID

object MediaJson {
    /** The media item of the protocol; [forStorage] adds the private createdAt. */
    fun item(m: MediaMeta, sync: List<Anchor>, withLines: Boolean = true, forStorage: Boolean = false): JSONObject = JSONObject().apply {
        put("mediaId", m.mediaId)
        put("url", m.url)
        put("title", m.title)
        put("durationSec", m.durationSec ?: JSONObject.NULL)
        put("extractor", m.extractor)
        put("videoId", m.videoId)
        put("video", m.video?.let {
            JSONObject().put("path", "/v1/media/${m.mediaId}/video").put("contentType", it.contentType).put("bytes", it.bytes)
                .also { o -> if (forStorage) o.put("file", it.file) }
        } ?: JSONObject.NULL)
        put("subtitles", m.subtitles?.let { subtitlesJson(it, withLines) } ?: JSONObject.NULL)
        put("tracks", JSONArray().also { arr ->
            for (t in m.tracks) arr.put(JSONObject().put("language", t.language).put("source", t.source).also { o ->
                t.name?.let { n -> o.put("name", n); if (t.added) o.put("label", n) }
                t.trackId?.let { id -> o.put("trackId", id) }
                if (t.added && !t.origin.isNullOrEmpty()) o.put("origin", t.origin)
            })
        })
        put("sync", anchorsJson(sync))
        if (forStorage) put("createdAt", m.createdAt)
    }

    fun subtitlesJson(s: SubtitlesData, withLines: Boolean = true): JSONObject =
        JSONObject().put("source", s.source).put("language", s.language).also { o ->
            if (withLines) o.put("lines", JSONArray().also { arr ->
                for (l in s.lines) arr.put(JSONObject().put("start", SyncMath.round3(l.start)).put("end", SyncMath.round3(l.end)).put("text", l.text))
            })
        }

    fun anchorsJson(a: List<Anchor>) = JSONArray().also { arr ->
        for (x in a) arr.put(JSONObject().put("at", x.at).put("offset", x.offset))
    }

    /** Parses [{at, offset}]; null when it is not a valid list. */
    fun parseAnchors(arr: JSONArray?): List<Anchor>? {
        if (arr == null) return null
        val out = ArrayList<Anchor>()
        for (i in 0 until arr.length()) {
            val o = arr.optJSONObject(i) ?: return null
            if (!o.has("at") || !o.has("offset")) return null
            val at = o.optDouble("at", Double.NaN)
            val off = o.optDouble("offset", Double.NaN)
            out.add(Anchor(at, off))
        }
        return if (SyncMath.isValid(out)) SyncMath.normalize(out) else null
    }

    fun fromStorage(j: JSONObject): MediaMeta {
        val v = j.optJSONObject("video")
        val s = j.optJSONObject("subtitles")
        return MediaMeta(
            j.getString("mediaId"), j.getString("url"), j.optString("title", ""),
            if (j.isNull("durationSec")) null else j.optInt("durationSec"),
            j.optString("extractor", ""), j.optString("videoId", ""),
            s?.let {
                val arr = it.optJSONArray("lines") ?: JSONArray()
                SubtitlesData(it.getString("source"), it.getString("language"),
                    (0 until arr.length()).map { i -> arr.getJSONObject(i).let { l -> Line(l.getDouble("start"), l.getDouble("end"), l.getString("text")) } })
            },
            v?.let { VideoData(it.getString("file"), it.getString("contentType"), it.getLong("bytes")) },
            j.optLong("createdAt"),
            j.optJSONArray("tracks")?.let { a -> (0 until a.length()).map { i -> a.getJSONObject(i).let { t -> TrackInfo(t.getString("language"), t.getString("source"), t.optString("name", "").ifEmpty { null }, t.optString("trackId", "").ifEmpty { null }, t.optString("origin", "").ifEmpty { null }) } } } ?: emptyList(),
        )
    }
}

/** Captured media as plain files: media/<id>/{item.json, video.<ext>, sync.json}. */
class MediaStore(private val root: File, private val clock: () -> Long = System::currentTimeMillis) {
    private val base = File(root, "media").also { it.mkdirs() }

    private fun hashId(s: String): String =
        MessageDigest.getInstance("SHA-256").digest(s.toByteArray()).take(8).joinToString("") { "%02x".format(it) }

    /** The cache key: the same for every spelling of a video URL (see [UrlNormalizer]). */
    fun idFor(url: String): String = hashId(UrlNormalizer.canonical(url))

    /** The key of an existing item for this URL: the canonical one, else the key older versions used (hash of the raw URL). */
    fun lookupId(url: String): String {
        val id = idFor(url)
        if (get(id) != null) return id
        val legacy = hashId(url.trim())
        return if (get(legacy) != null) legacy else id
    }

    private fun valid(id: String) = Regex("[A-Za-z0-9_-]{1,64}").matches(id)
    private fun dir(id: String) = File(base, id)

    fun get(id: String): MediaMeta? {
        if (!valid(id)) return null
        val f = File(dir(id), "item.json")
        return try { if (f.isFile) MediaJson.fromStorage(JSONObject(f.readText())) else null } catch (e: Exception) { null }
    }

    /** The stored item for a video's identity (extractor + id), whatever URL it was captured from. */
    fun findByIdentity(extractor: String, videoId: String): MediaMeta? =
        if (extractor.isEmpty() || videoId.isEmpty()) null
        else list().firstOrNull { it.extractor.equals(extractor, true) && it.videoId == videoId }

    fun list(): List<MediaMeta> =
        (base.listFiles() ?: emptyArray()).filter { it.isDirectory }.mapNotNull { get(it.name) }.sortedByDescending { it.createdAt }

    /** A fresh empty folder inside the item's folder for a download in progress. */
    fun workDir(id: String): File = File(dir(id), "tmp-" + UUID.randomUUID()).also { it.mkdirs() }

    fun save(meta: MediaMeta) {
        dir(meta.mediaId).mkdirs()
        File(dir(meta.mediaId), "item.json").writeText(MediaJson.item(meta, emptyList(), forStorage = true).toString())
    }

    /** Moves a finished download in as the item's video. */
    fun adoptVideo(id: String, source: File, contentType: String): VideoData {
        dir(id).mkdirs()
        dir(id).listFiles()?.filter { it.name.startsWith("video.") }?.forEach { it.delete() }
        val target = File(dir(id), "video." + source.extension.ifEmpty { "mp4" })
        if (!source.renameTo(target)) { source.copyTo(target, overwrite = true); source.delete() }
        return VideoData(target.name, contentType, target.length())
    }

    private fun trackFile(id: String, language: String) = File(dir(id), "sub-" + language.replace(Regex("[^A-Za-z0-9_-]"), "_") + ".json")

    /** Lines of a non-main track fetched on demand (kept so it is downloaded once). */
    fun loadTrack(id: String, language: String): SubtitlesData? {
        val f = trackFile(id, language)
        return try {
            if (!f.isFile) null else JSONObject(f.readText()).let { j ->
                val arr = j.getJSONArray("lines")
                SubtitlesData(j.getString("source"), j.getString("language"),
                    (0 until arr.length()).map { i -> arr.getJSONObject(i).let { l -> Line(l.getDouble("start"), l.getDouble("end"), l.getString("text")) } })
            }
        } catch (e: Exception) { null }
    }

    fun saveTrack(id: String, s: SubtitlesData) {
        dir(id).mkdirs()
        trackFile(id, s.language).writeText(MediaJson.subtitlesJson(s).toString())
    }

    private fun addedFile(id: String, trackId: String) = File(dir(id), "added-$trackId.json")

    /** Adds (or replaces, same language + origin) a producer-supplied track; null for unknown media. */
    fun addTrack(id: String, t: AddedTrack): TrackInfo? {
        val meta = get(id) ?: return null
        val info = TrackInfo(t.language, TrackInfo.SOURCE_ADDED, t.label, t.trackId, t.origin.ifEmpty { null })
        addedFile(id, t.trackId).writeText(MediaJson.subtitlesJson(SubtitlesData(TrackInfo.SOURCE_ADDED, t.language, t.lines)).toString())
        val rest = meta.tracks.filter { it.trackId != t.trackId }
        save(meta.copy(tracks = rest + info))
        return info
    }

    fun loadAdded(id: String, trackId: String): SubtitlesData? {
        if (!valid(trackId)) return null
        val f = addedFile(id, trackId)
        return try {
            if (!f.isFile) null else JSONObject(f.readText()).let { j ->
                val arr = j.getJSONArray("lines")
                SubtitlesData(TrackInfo.SOURCE_ADDED, j.getString("language"),
                    (0 until arr.length()).map { i -> arr.getJSONObject(i).let { l -> Line(l.getDouble("start"), l.getDouble("end"), l.getString("text")) } })
            }
        } catch (e: Exception) { null }
    }

    /** Removes an added track; false when there is none with that id. */
    fun removeTrack(id: String, trackId: String): Boolean {
        val meta = get(id) ?: return false
        if (meta.tracks.none { it.trackId == trackId && it.added }) return false
        addedFile(id, trackId).delete()
        save(meta.copy(tracks = meta.tracks.filter { it.trackId != trackId }))
        return true
    }

    fun videoFile(id: String): File? {
        val v = get(id)?.video ?: return null
        return File(dir(id), v.file).takeIf { it.isFile }
    }

    fun bytesOf(id: String): Long = (dir(id).listFiles() ?: emptyArray()).filter { it.isFile }.sumOf { it.length() }

    fun delete(id: String): Boolean {
        if (!valid(id) || !dir(id).isDirectory) return false
        return dir(id).deleteRecursively()
    }

    fun sync(id: String): List<Anchor> {
        val f = File(dir(id), "sync.json")
        return try { if (f.isFile) MediaJson.parseAnchors(JSONArray(f.readText())) ?: emptyList() else emptyList() } catch (e: Exception) { emptyList() }
    }

    /** Replaces the sync list; false when there is no such media. */
    fun putSync(id: String, anchors: List<Anchor>): Boolean {
        if (get(id) == null) return false
        File(dir(id), "sync.json").writeText(MediaJson.anchorsJson(SyncMath.normalize(anchors)).toString())
        return true
    }

    fun now() = clock()
}
