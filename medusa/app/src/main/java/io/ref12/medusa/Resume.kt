package io.ref12.medusa

import java.io.File

/** What a download that stopped half way leaves behind, and how the next attempt continues from it. */
object Resume {
    /** Bytes of unfinished (.part) files under [dir]: what yt-dlp can continue from. */
    fun partialBytes(dir: File): Long = dir.walkTopDown().filter { it.isFile && it.name.endsWith(".part") }.sumOf { it.length() }

    fun hasPartial(dir: File): Boolean = partialBytes(dir) > 0

    /** yt-dlp options for the video folder: continue the unfinished file when there is one. */
    fun videoOptions(videoDir: File): List<String> = if (hasPartial(videoDir)) listOf("--continue") else emptyList()

    fun message(videoDir: File, quality: String): String {
        val b = partialBytes(videoDir)
        return if (b > 0) "Resuming video (" + (b / 1_000_000) + " MB already downloaded)" else "Downloading video ($quality)"
    }
}
