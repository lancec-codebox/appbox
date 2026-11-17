package io.ref12.medusa.node

/** The "Test download" button: one URL through the node's own attempt order, in the foreground, writing nothing to the store. A try passes once 5 MB of video data arrived (then it is cancelled), like a diagnosis try. */
object TestDownload {
    /** The three switches; WebM first starts from the node setting, the other two put the options back on the video request just for the test. */
    class Switches(val webmFirst: Boolean, val infoWithVideo: Boolean, val thumbWithVideo: Boolean)

    class Result(val passed: Boolean, val variant: String, val attempt: String, val error: String?, val trace: FailureTrace?, val formats: List<String>, val client: String?, val ytdlp: String) {
        /** The recorded fields and pass or fail, as the screen and the shared log show them. */
        fun text(): String = (if (passed) "PASS" else "FAIL") + " - " + variant + " (" + attempt + ")" + (error?.let { "\nerror: $it" } ?: "") +
            "\nytdlp: $ytdlp\nformats: " + (if (formats.isEmpty()) "-" else formats.joinToString(",")) + "\nclient: " + (client ?: "-") +
            (trace?.tail?.takeIf { it.isNotEmpty() }?.let { "\ntail:\n" + it.joinToString("\n") } ?: "")
    }

    fun run(io: VideoIo, job: DownloadJob, dir: java.io.File, sw: Switches, progress: (String) -> Unit = {}): Result {
        dir.deleteRecursively(); dir.mkdirs()
        val v = Variant(sw.webmFirst, sw.infoWithVideo, sw.thumbWithVideo, job.maxHeight)
        val base = Variant(true, false, false, job.maxHeight)
        var last: TryResult? = null; var used = ""
        try {
            val (a, _) = NodeAttempts.run(v.webmFirst, v.height, progress) { att ->
                used = (if (att.webm) "WebM/Opus first" else "MP4 selector") + " at " + att.height + "p"
                val r = io.probe(job, dir, v, att); last = r
                when (r.outcome) {
                    TryOutcome.PASS -> {}
                    TryOutcome.FORBIDDEN -> throw NodeFetchException(FailureKind.FORBIDDEN, r.error ?: "HTTP Error 403: Forbidden")
                    TryOutcome.BOT -> throw NodeFetchException(FailureKind.BOT, r.error ?: "bot block")
                    TryOutcome.ERROR -> throw NodeFetchException(FailureKind.TRANSIENT, r.error ?: "error")
                }
            }
            return Result(true, v.describe(base), (if (a.webm) "WebM/Opus first" else "MP4 selector") + " at " + a.height + "p", null, null, last?.formats ?: emptyList(), last?.client, io.ytdlpVersion())
        } catch (e: NodeFetchException) {
            return Result(false, v.describe(base), used, e.message, e.trace, last?.formats ?: emptyList(), last?.client, io.ytdlpVersion())
        } finally { dir.deleteRecursively() }
    }
}
