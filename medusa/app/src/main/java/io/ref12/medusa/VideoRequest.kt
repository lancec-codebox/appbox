package io.ref12.medusa

import com.yausername.youtubedl_android.YoutubeDLRequest

/** One yt-dlp option: a flag, or a flag with a value. */
class Opt(val key: String, val value: String? = null) {
    override fun toString() = if (value == null) key else "$key $value"
}

/**
 * The video request of Medusa's own download (YtDlpFetcher.fetchVideo), shared with the index node so that both ask YouTube for the video in exactly the same way.
 * Pure: no Android classes, so the unit tests can compare the two.
 */
object VideoRequest {
    /** The heights the library offers (Quality), ascending. */
    val HEIGHTS: List<Int> = Quality.entries.map { it.height }.sorted()

    /** MP4 (H.264 + AAC) so every Android player can play it; falls back to the best single MP4 file. */
    fun selector(height: Int) = "bv*[height<=$height][ext=mp4]+ba[ext=m4a]/b[height<=$height][ext=mp4]/b[height<=$height]"

    /** The next height the library offers above [height], or null when [height] is already the highest. */
    fun nextHeightAbove(height: Int): Int? = HEIGHTS.firstOrNull { it > height }

    /** The video options; [out] is the yt-dlp output template, [resume] the options that continue an unfinished file. */
    fun options(out: String, height: Int, resume: List<String> = emptyList()): List<Opt> {
        val l = ArrayList<Opt>()
        for (o in resume) l.add(Opt(o))
        l.add(Opt("--no-playlist")); l.add(Opt("--no-warnings"))
        l.add(Opt("-f", selector(height)))
        l.add(Opt("--merge-output-format", "mp4"))
        l.add(Opt("-o", out))
        return l
    }

    fun apply(req: YoutubeDLRequest, opts: List<Opt>): YoutubeDLRequest {
        for (o in opts) if (o.value == null) req.addOption(o.key) else req.addOption(o.key, o.value)
        return req
    }
}
