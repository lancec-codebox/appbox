package io.ref12.medusa

/** "Title language" setting: show titles in the device's language when the site offers one, or only the original. */
enum class TitleMode(val label: String) { DEVICE("Device language"), ORIGINAL("Original only") }

/** Which title to show first, which under it, and what search matches. Pure, so it is unit-tested. */
object Titles {
    private val PLACEHOLDER = Regex("(?i)[a-z0-9:_ -]{1,30} video #\\S+")

    /** The language yt-dlp's youtube:lang option takes for a device locale: "en", "fr", "zh-CN", "zh-TW", "zh-HK". */
    fun deviceLanguage(language: String, country: String = ""): String = when {
        language.isEmpty() -> "en"
        language == "zh" -> if (country == "TW" || country == "HK") "zh-$country" else "zh-CN"
        language == "iw" -> "he"
        language == "in" -> "id"
        else -> language
    }

    /** Only YouTube serves creator-provided translated titles by viewer language; other extractors keep what they give. */
    fun supportsLocalized(extractor: String) = extractor.lowercase().startsWith("youtube")

    /** The localized title worth storing: only when it is non-blank and differs from the original. */
    fun localizedIfDifferent(original: String, localized: String?): String? {
        val l = localized?.trim().orEmpty()
        // yt-dlp names a video it could not read "youtube video #<id>": that is not a title
        return if (l.isEmpty() || l == original.trim() || PLACEHOLDER.matches(l)) null else l
    }

    /** The title to show first. */
    fun primary(m: MediaMeta, mode: TitleMode): String =
        (if (mode == TitleMode.DEVICE) m.localizedTitle?.takeIf { it.isNotEmpty() } else null) ?: m.title.ifEmpty { m.url }

    /** The original title, shown smaller under a localized primary; null when the primary already is the original. */
    fun secondary(m: MediaMeta, mode: TitleMode): String? =
        if (mode == TitleMode.DEVICE && !m.localizedTitle.isNullOrEmpty() && m.title.isNotEmpty()) m.title else null

    /** Search matches the original and the localized title. */
    fun matches(m: MediaMeta, query: String): Boolean =
        query.isEmpty() || m.title.contains(query, true) || (m.localizedTitle?.contains(query, true) ?: false)
}
