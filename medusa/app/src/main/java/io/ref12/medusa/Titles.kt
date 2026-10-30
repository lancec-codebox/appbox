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

    /** The title to show first: the user's own, else the site's localized one, else the original. */
    fun primary(m: MediaMeta, mode: TitleMode): String =
        m.userTitle?.takeIf { it.isNotBlank() }
            ?: (if (mode == TitleMode.DEVICE) m.localizedTitle?.takeIf { it.isNotEmpty() } else null)
            ?: m.title.ifEmpty { m.url }

    /** The original title, shown smaller under the primary; null when the primary already is the original. */
    fun secondary(m: MediaMeta, mode: TitleMode): String? {
        val p = primary(m, mode)
        return m.title.takeIf { it.isNotEmpty() && it != p }
    }

    /**
     * A machine translation to show under the primary, marked as such. Only when nothing better exists: no user title,
     * no localized title from the site (the localized one always wins), and the translation differs from the original.
     */
    fun machineTranslated(m: MediaMeta, mode: TitleMode): String? {
        if (mode != TitleMode.DEVICE || !m.userTitle.isNullOrBlank() || !m.localizedTitle.isNullOrEmpty()) return null
        return m.translatedTitle?.takeIf { it.isNotBlank() && it != m.title }
    }

    /** The translation is wanted for an item when it has no localized title in the device language and no translation yet. */
    fun needsTranslation(m: MediaMeta, deviceLanguage: String): Boolean =
        m.localizedTitle.isNullOrEmpty() && m.title.isNotBlank() && !(m.translatedTitle != null && m.translatedLanguage == deviceLanguage)

    /** Search matches the original, the localized, the translated and the user's own title. */
    fun matches(m: MediaMeta, query: String): Boolean = query.isEmpty() ||
        listOf(m.title, m.localizedTitle, m.translatedTitle, m.userTitle).any { it?.contains(query, true) == true }

    /** A rename the store accepts: trimmed, blank means "reset to original" (null). */
    fun cleanUserTitle(input: String?): String? = input?.trim()?.takeIf { it.isNotEmpty() }
}
