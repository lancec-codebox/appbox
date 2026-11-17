package io.ref12.medusa.node

import io.ref12.medusa.MediaStore
import io.ref12.medusa.TrackExport
import java.io.File

/** Collects what the library already has of a stored video into the index layout: the video, the info JSON, description, thumbnail and the subtitle tracks, if present. */
object IndexStoreFiles {
    /** The 11-character YouTube id of [url], or null: only YouTube videos are stored. */
    fun youtubeId(url: String): String? = SourceUrl.videoId(url)

    /** The library's own files of the item, in the folder next to item.json (info.json, thumb.jpg and description.txt are kept there by the library download). */
    fun collect(media: MediaStore, mediaId: String, extraDir: File? = null): StoreFiles? {
        val m = media.get(mediaId) ?: return null
        val id = youtubeId(m.url) ?: return null
        val video = media.videoFile(mediaId) ?: return null
        val extras = ArrayList<Triple<String, File, String>>()
        val dir = video.parentFile!!
        // info.json and thumb.jpg are not kept by the library: the store worker fetches them in their own yt-dlp calls into [extraDir]
        val infoFile = listOfNotNull(extraDir?.let { File(it, "info.json") }, File(dir, "info.json")).firstOrNull { it.isFile }
        infoFile?.let { extras.add(Triple("info.json", it, "application/json")) }
        val info = infoFile?.let { try { NodeInfo.parse(it.readText()) } catch (e: Exception) { null } }
        val desc = info?.description?.takeIf { it.isNotBlank() }
        if (desc != null) { val f = File(dir, "description.txt").also { it.writeText(desc, Charsets.UTF_8) }; extras.add(Triple("description.txt", f, "text/plain; charset=utf-8")) }
        listOfNotNull(extraDir?.let { File(it, "thumb.jpg") }, File(dir, "thumb.jpg")).firstOrNull { it.isFile }?.let { extras.add(Triple("thumb.jpg", it, "image/jpeg")) }
        // every subtitle track the library already has, as WebVTT under subs/
        val subs = LinkedHashMap<String, File>()
        for (t in m.tracks.filter { !it.added }) {
            val s = media.loadTrack(mediaId, t.language) ?: continue
            val name = t.language + (if (s.source == "auto-captions") ".auto" else "")
            val f = File(dir, "store-sub-" + name.replace(Regex("[^A-Za-z0-9._-]"), "_") + ".vtt").also { it.writeText(TrackExport.vtt(s.lines), Charsets.UTF_8) }
            subs[name] = f
        }
        m.subtitles?.let { s -> val name = s.language + (if (s.source == "auto-captions") ".auto" else ""); if (name !in subs) {
            subs[name] = File(dir, "store-sub-" + name.replace(Regex("[^A-Za-z0-9._-]"), "_") + ".vtt").also { it.writeText(TrackExport.vtt(s.lines), Charsets.UTF_8) } } }
        val names = NodeInfo.order(subs.keys)
        for (n in names) extras.add(Triple("subs/$n.vtt", subs[n]!!, "text/vtt"))
        return StoreFiles(id, m.url, video, extras, names, m.height, m.durationSec?.toDouble(), info)
    }
}
