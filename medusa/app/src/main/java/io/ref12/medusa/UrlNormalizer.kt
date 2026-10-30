package io.ref12.medusa

/** Maps the many spellings of one video URL to one canonical string: the media cache key. */
object UrlNormalizer {
    private val PARTS = Regex("""^(https?)://([^/?#]*)([^?#]*)(\?[^#]*)?(#.*)?$""", RegexOption.IGNORE_CASE)
    private val YT_ID = Regex("[A-Za-z0-9_-]{11}")
    private val YT_HOSTS = setOf("youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtube-nocookie.com", "www.youtube-nocookie.com")

    private val TRACKING = Regex("(?i)^(utm_[a-z0-9_]*|fbclid|gclid|dclid|msclkid|mc_cid|mc_eid|igshid|si|pp|feature|ref_src|ref_url|spm|_hsenc|_hsmi)$")

    /** The link as the user means it: no fragment (#bottom-sheet) and no tracking parameters (utm_*, fbclid, si, ...). */
    fun clean(url: String): String {
        val u = url.trim().substringBefore('#')
        val q = u.indexOf('?')
        if (q < 0) return u
        val kept = u.substring(q + 1).split('&').filter { p -> p.isNotEmpty() && !TRACKING.matches(p.substringBefore('=')) }
        return u.substring(0, q) + (if (kept.isEmpty()) "" else "?" + kept.joinToString("&"))
    }

    fun canonical(url: String): String {
        val u = clean(url)
        val m = PARTS.matchEntire(u) ?: return u
        val scheme = m.groupValues[1].lowercase()
        val authority = m.groupValues[2]
        val host = authority.substringAfterLast('@').substringBefore(':').lowercase()
        val path = m.groupValues[3]
        val query = m.groupValues[4].removePrefix("?")
        youtubeId(host, path, query)?.let { return "https://www.youtube.com/watch?v=$it" }
        return scheme + "://" + authority.substringBeforeLast('@', "").let { if (it.isEmpty()) "" else "$it@" } +
            host + authority.substringAfterLast('@').let { a -> if (a.contains(':')) ":" + a.substringAfter(':') else "" } + path +
            (if (query.isEmpty()) "" else "?$query")
    }

    private fun youtubeId(host: String, path: String, query: String): String? {
        val segs = path.split('/').filter { it.isNotEmpty() }
        val id: String? = when {
            host == "youtu.be" -> segs.firstOrNull()
            host in YT_HOSTS -> when (segs.firstOrNull()) {
                "watch" -> query.split('&').firstOrNull { it.startsWith("v=") }?.substring(2)?.let { DeepLink.decode(it) }
                "shorts", "embed", "live", "v" -> segs.getOrNull(1)
                else -> null
            }
            else -> null
        }
        return id?.takeIf { YT_ID.matches(it) }
    }
}
