package io.ref12.medusa

import java.io.ByteArrayOutputStream
import java.util.UUID

data class JobRequest(val id: String, val url: String, val returnUrl: String?, val languages: List<String>? = null)

object DeepLink {
    private val ID_OK = Regex("[A-Za-z0-9_-]{1,64}")

    /** Parses medusa://open?job=..&url=..&return=..; null when it is not a usable link. */
    fun parse(link: String?, newId: () -> String = { UUID.randomUUID().toString() }): JobRequest? {
        if (link == null) return null
        val s = link.trim()
        val q = s.indexOf('?')
        if (!s.startsWith("medusa://open", ignoreCase = true) || q < 0) return null
        val query = s.substring(q + 1).substringBefore('#')
        val params = HashMap<String, String>()
        for (pair in query.split('&')) {
            if (pair.isEmpty()) continue
            val eq = pair.indexOf('=')
            val k = decode(if (eq < 0) pair else pair.substring(0, eq))
            val v = if (eq < 0) "" else decode(pair.substring(eq + 1))
            if (k !in params) params[k] = v
        }
        val url = params["url"]?.trim().orEmpty()
        if (!isHttp(url)) return null
        val job = params["job"]?.takeIf { ID_OK.matches(it) } ?: newId()
        val ret = params["return"]?.trim()?.takeIf { isHttp(it) }
        val langs = params["languages"]?.split(',')?.map { it.trim() }?.filter { it.isNotEmpty() }?.takeIf { it.isNotEmpty() }
        return JobRequest(job, url, ret, langs)
    }

    /** The return URL with job and state appended: to the query, or after a "?" inside the hash. */
    fun buildReturnUrl(returnUrl: String, jobId: String, state: String): String {
        val extra = "job=" + encode(jobId) + "&state=" + state
        val h = returnUrl.indexOf('#')
        if (h < 0) return returnUrl + (if (returnUrl.contains('?')) "&" else "?") + extra
        val base = returnUrl.substring(0, h)
        val hash = returnUrl.substring(h)
        return base + hash + (if (hash.contains('?')) "&" else "?") + extra
    }

    fun isHttp(u: String) = u.startsWith("http://", true) || u.startsWith("https://", true)

    private fun encode(s: String) = java.net.URLEncoder.encode(s, "UTF-8")

    /** Percent-decoding; unlike form decoding a plus stays a plus. */
    fun decode(s: String): String {
        val out = ByteArrayOutputStream()
        var i = 0
        val b = s.toByteArray(Charsets.UTF_8)
        while (i < b.size) {
            val c = b[i].toInt()
            if (c == '%'.code && i + 2 < b.size + 0 && hex(b[i + 1]) >= 0 && hex(b[i + 2]) >= 0) {
                out.write(hex(b[i + 1]) * 16 + hex(b[i + 2])); i += 3
            } else { out.write(c); i++ }
        }
        return out.toString("UTF-8")
    }

    private fun hex(x: Byte): Int = Character.digit(x.toInt().toChar(), 16)
}
