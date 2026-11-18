package io.ref12.medusa.v2

import io.ref12.medusa.WorkGate

/** Decides whether a run is light (a listing or a probe: it should not wait behind a big download) or heavy. */
object RunClass {
    private val YT_LIGHT = setOf("--flat-playlist", "--skip-download", "-J", "--dump-json", "--dump-single-json", "-j", "--simulate", "-s",
        "-F", "--list-formats", "--list-subs", "--list-thumbnails", "--version", "--get-id", "--get-title", "--get-url", "--get-duration")

    fun weightOf(tool: String, args: List<String>): WorkGate.Weight = when (tool) {
        "ffprobe", V2Router.UPDATE_TOOL -> WorkGate.Weight.LIGHT
        "yt-dlp" -> if (args.any { it in YT_LIGHT }) WorkGate.Weight.LIGHT else WorkGate.Weight.HEAVY
        else -> WorkGate.Weight.HEAVY
    }
}
