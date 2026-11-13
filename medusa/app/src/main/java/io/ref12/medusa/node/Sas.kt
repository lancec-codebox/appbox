package io.ref12.medusa.node

import java.net.URLEncoder

/** Removes the signature from anything that may carry a SAS URL. Use it on every log line, error message and crash report. */
object Redact {
    private val SIG = Regex("(?i)(sig(?:=|%3D))[^&\\s\"'<>)]+")
    fun sas(s: String?): String = if (s == null) "" else SIG.replace(s) { it.groupValues[1] + "REDACTED" }
}

internal fun urlEnc(s: String): String = URLEncoder.encode(s, "UTF-8").replace("+", "%20")

/** A container SAS URL: https://<account>.blob.core.windows.net/<container>?sv=...&sig=... (its toString is redacted). */
class SasUrl private constructor(private val raw: String, val container: String, val query: String) {
    val params: Map<String, String> = query.split('&').filter { it.contains('=') }.associate {
        val i = it.indexOf('='); java.net.URLDecoder.decode(it.substring(0, i), "UTF-8") to java.net.URLDecoder.decode(it.substring(i + 1), "UTF-8")
    }
    /** The sp= field, e.g. "racwdlf". */
    val permissions: String get() = params["sp"] ?: ""
    val expiry: String? get() = params["se"]
    fun can(p: Char) = permissions.contains(p)

    fun blobUrl(path: String, extra: String = ""): String =
        container + "/" + path.split('/').joinToString("/") { urlEnc(it) } + "?" + (if (extra.isEmpty()) "" else "$extra&") + query
    fun containerUrl(extra: String): String = "$container?$extra&$query"

    /** The SAS as typed in, for storing encrypted. Never log it. */
    fun secret(): String = raw
    override fun toString() = Redact.sas(raw)

    companion object {
        private val SHAPE = Regex("^https?://[^/?#]+/[^/?#]+$")
        /** Null when [text] is not a container SAS URL. */
        fun parse(text: String): SasUrl? {
            val t = text.trim()
            val q = t.indexOf('?')
            if (q < 0) return null
            val base = t.substring(0, q).trimEnd('/')
            val query = t.substring(q + 1)
            if (!SHAPE.matches(base) || !query.contains("sig=")) return null
            return SasUrl(t, base, query)
        }

        /** What each sp= letter allows, for the settings screen. */
        fun describe(sp: String): String {
            val names = mapOf('r' to "read", 'a' to "add", 'c' to "create", 'w' to "write", 'd' to "delete", 'l' to "list", 'f' to "find by tags", 't' to "tags", 'x' to "delete versions")
            return sp.map { names[it] ?: it.toString() }.joinToString(", ").ifEmpty { "none" }
        }
    }
}
