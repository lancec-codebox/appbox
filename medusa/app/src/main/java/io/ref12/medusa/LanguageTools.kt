package io.ref12.medusa

import android.content.Context
import android.os.Build
import java.util.Locale

/** Language ordering and detection for subtitle pickers and imports (v1.5.4). */
object LanguageTools {
    fun displayName(tag: String): String =
        Locale.forLanguageTag(tag).getDisplayName(Locale.ENGLISH).takeIf { it.isNotBlank() && !it.equals(tag, true) } ?: tag

    private fun base(tag: String) = tag.substringBefore('-').lowercase()

    /** Original language first, then English, then the rest alphabetically by display name (stable). */
    fun <T> order(items: List<T>, original: String?, lang: (T) -> String, name: (T) -> String = { displayName(lang(it)) }): List<T> {
        fun rank(t: T): Int {
            val l = lang(t)
            return when {
                original != null && (l.equals(original, true) || base(l) == base(original)) -> 0
                base(l) == "en" -> 1
                else -> 2
            }
        }
        return items.sortedWith(compareBy<T>({ rank(it) }).thenBy(String.CASE_INSENSITIVE_ORDER) { name(it) })
    }

    /** Languages offered in the import picker; "Other..." is appended by the UI. */
    val COMMON = listOf("en", "es", "fr", "de", "it", "pt", "ru", "ja", "ko", "zh-Hans", "zh-Hant", "ar", "hi", "tr", "nl", "pl", "sv", "id", "vi", "th")

    private val SEP = "[-_.\\s]"
    private val TAG = Regex("(?:^|[\\[(._\\s-])([A-Za-z]{2,3}(?:[-_](?:Hans|Hant|[A-Za-z]{2}|[0-9]{3}))?)(?:[\\])._\\s-]|$)")

    /** "movie-en.srt", "movie [en].srt", "movie.en.srt", "movie_zh-Hans.srt" -> tag; null when none looks like a language. */
    fun fromFileName(name: String): String? {
        val stem = name.substringBeforeLast('.', name)
        val cands = TAG.findAll(stem + ".").map { it.groupValues[1].replace('_', '-') }.toList()
        for (c in cands.asReversed()) {
            val b = c.substringBefore('-').lowercase()
            if (isKnown(b)) return normalize(c)
        }
        return null
    }

    private val KNOWN: Set<String> = Locale.getISOLanguages().toSet()
    private fun isKnown(b: String) = b in KNOWN || b in setOf("fil", "yue", "haw")
    private fun normalize(c: String): String {
        val p = c.split('-')
        return p[0].lowercase() + p.drop(1).joinToString("") { "-" + (if (it.length == 4) it.lowercase().replaceFirstChar { x -> x.uppercase() } else if (it.length == 2) it.uppercase() else it) }
    }

    /** Heuristic by script: Han -> zh (Hans/Hant by distinguishing characters), kana -> ja, Hangul -> ko, else en. */
    fun heuristic(text: String): String {
        var han = 0; var kana = 0; var hangul = 0; var simp = 0; var trad = 0
        for (ch in text) {
            when (Character.UnicodeScript.of(ch.code)) {
                Character.UnicodeScript.HAN -> { han++; if (ch in SIMPLIFIED) simp++; if (ch in TRADITIONAL) trad++ }
                Character.UnicodeScript.HIRAGANA, Character.UnicodeScript.KATAKANA -> kana++
                Character.UnicodeScript.HANGUL -> hangul++
                else -> {}
            }
        }
        return when {
            kana > 0 && kana * 10 >= han -> "ja"
            hangul > 0 && hangul >= han -> "ko"
            han > 0 -> if (trad > simp) "zh-Hant" else "zh-Hans"
            else -> "en"
        }
    }

    private const val SIMPLIFIED = "们这个说对时会为过还见头现两开关门问间车长东书买来从国应实总点"
    private const val TRADITIONAL = "們這個說對時會為過還見頭現兩開關門問間車長東書買來從國應實總點"

    /** Best guess: file name tag, then the file's own language, then the text. */
    fun detect(ctx: Context?, fileName: String, fileLanguage: String?, lines: List<String>): String {
        fromFileName(fileName)?.let { return it }
        fileLanguage?.takeIf { it.isNotBlank() }?.let { return it }
        val sample = lines.filter { it.isNotBlank() }.take(40).joinToString("\n")
        val h = heuristic(sample)
        if (h != "en") return h
        if (ctx != null && Build.VERSION.SDK_INT >= 29 && sample.isNotEmpty()) try {
            val tcm = ctx.getSystemService(android.view.textclassifier.TextClassificationManager::class.java)
            val r = tcm.textClassifier.detectLanguage(android.view.textclassifier.TextLanguage.Request.Builder(sample).build())
            if (r.localeHypothesisCount > 0) {
                val loc = r.getLocale(0)
                if (r.getConfidenceScore(loc) >= 0.6f) return loc.toLanguageTag()
            }
        } catch (e: Exception) { /* fall back */ }
        return h
    }
}
