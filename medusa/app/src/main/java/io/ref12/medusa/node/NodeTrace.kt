package io.ref12.medusa.node

import org.json.JSONArray
import org.json.JSONObject

/** What a failed yt-dlp run leaves in the job record and the node log: the version, the format ids it chose, the client they came from and the last 20 lines, URL query strings removed. */
class FailureTrace(val ytdlp: String?, val formats: List<String>, val client: String?, val tail: List<String>) {
    fun putInto(o: JSONObject): JSONObject = o.put("ytdlp", ytdlp ?: JSONObject.NULL).put("formats", JSONArray(formats)).put("client", client ?: JSONObject.NULL).put("tail", JSONArray(tail))

    /** The same as readable lines, for the node log and the Test download result. */
    fun text(): String = "yt-dlp " + (ytdlp ?: "unknown") + "; formats " + (if (formats.isEmpty()) "-" else formats.joinToString(",")) + "; client " + (client ?: "-") +
        (if (tail.isEmpty()) "" else "\n" + tail.joinToString("\n"))

    companion object {
        const val TAIL_LINES = 20

        private val QUERY = Regex("(https?://[^\\s?\"'<>)]+)\\?[^\\s\"'<>)]*")
        private val FORMATS = Regex("Downloading (\\d+) format\\(s\\):\\s*(.+)")
        private val CLIENT = Regex("(?:player|client)[ =]+\\[?(?:youtube:)?([a-z_]+(?:_[a-z]+)*)\\]?|\\[youtube\\] [A-Za-z0-9_-]{11}: Downloading ([a-z_]+) player API JSON|using the ([a-z_]+) client", RegexOption.IGNORE_CASE)

        /** Removes every URL query string (signatures, tokens, expiry) and any SAS signature. */
        fun clean(line: String): String = Redact.sas(QUERY.replace(line) { it.groupValues[1] })

        /** The last [TAIL_LINES] non-blank lines of yt-dlp's output, cleaned. */
        fun tailOf(raw: String): List<String> = raw.lines().filter { it.isNotBlank() }.takeLast(TAIL_LINES).map { clean(it.trimEnd()) }

        /** The format ids of "Downloading 2 format(s): 399+251"; "a+b" is two formats. Empty when the line is not there. */
        fun formatsOf(raw: String): List<String> {
            val m = raw.lines().mapNotNull { FORMATS.find(it) }.lastOrNull() ?: return emptyList()
            return m.groupValues[2].trim().split('+', ',', ' ').filter { it.isNotBlank() }
        }

        /** The YouTube client the formats came from, when the output says ("player API JSON" lines name it), else null. */
        fun clientOf(raw: String): String? {
            for (line in raw.lines()) {
                val m = CLIENT.find(line) ?: continue
                val g = m.groups.drop(1).firstOrNull { it != null }?.value ?: continue
                if (g.lowercase() in setOf("webpage", "initial", "the", "api")) continue
                return g
            }
            return null
        }

        fun of(raw: String, ytdlp: String?) = FailureTrace(ytdlp, formatsOf(raw), clientOf(raw), tailOf(raw))
    }
}
