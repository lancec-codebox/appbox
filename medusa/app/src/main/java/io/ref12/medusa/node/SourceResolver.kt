package io.ref12.medusa.node

import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLRequest
import io.ref12.medusa.YtDlpFetcher

/** Resolves a channel handle, /c/ or /user/ URL to its channel id with yt-dlp, and follows sources from the phone. */
class SourceResolver(private val yt: YtDlpFetcher) {
    /** yt-dlp --print channel_id on a single video URL. */
    fun videoChannelId(url: String): String? {
        try { yt.ensureInit() } catch (e: Exception) { return null }
        return try {
            YoutubeDL.getInstance().execute(YoutubeDLRequest(url).apply {
                addOption("--no-playlist"); addOption("--skip-download"); addOption("--print", "channel_id"); addOption("--no-warnings")
            }, "medusa-index-resolve") { _, _, _ -> }.out.lineSequence().map { it.trim() }.firstOrNull { it.startsWith("UC") }
        } catch (e: Throwable) { null }
    }

    /** yt-dlp --flat-playlist --playlist-end 1 --print channel_id on the channel URL (then on its /videos tab); null when it fails. */
    fun channelId(url: String): String? {
        try { yt.ensureInit() } catch (e: Exception) { return null }
        for (u in listOf(url, url.trimEnd('/') + "/videos")) {
            try {
                val r = YoutubeDL.getInstance().execute(YoutubeDLRequest(u).apply {
                    addOption("--flat-playlist"); addOption("--playlist-end", "1"); addOption("--print", "channel_id"); addOption("--no-warnings")
                }, "medusa-index-resolve") { _, _, _ -> }
                val id = r.out.lineSequence().map { it.trim() }.firstOrNull { it.startsWith("UC") }
                if (id != null) return id
            } catch (e: Throwable) { /* try the next form */ }
        }
        return null
    }
}
