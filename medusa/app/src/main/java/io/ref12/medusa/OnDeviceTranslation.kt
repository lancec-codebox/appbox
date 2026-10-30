package io.ref12.medusa

/** A translator that runs on this device. One instance is made per source language and reused for every call. */
interface TextTranslator : AutoCloseable {
    /** Translates [texts] in order, one result per input; blocks. Throws when the model is missing or the work fails. */
    fun translate(texts: List<String>): List<String>
}

/** The ML Kit side, behind an interface so the rest is unit-tested with a fake. */
interface TranslationEngine {
    fun modelReady(source: String, target: String): Boolean
    /** Downloads the language models (about 30 MB each, once); blocks until done or throws. */
    fun downloadModel(source: String, target: String, wifiOnly: Boolean)
    fun removeModel(language: String)
    fun translator(source: String, target: String): TextTranslator
    /** ML Kit language tag for a BCP-47 tag ("zh-Hans" -> "zh"), or null when ML Kit has no model for it. */
    fun supported(tag: String): String?
}

/** What the user has agreed to, and what we may therefore do without asking again. */
object TranslationConsent {
    enum class Step { NOT_NEEDED, ASK_DOWNLOAD, DOWNLOAD, OFF }

    /**
     * The next step for translating [source] into [target]: nothing when the setting is off, ask before the first
     * model download, download once consent is given, and just translate when the model is already on the device.
     */
    fun next(enabled: Boolean, consented: Boolean, modelReady: Boolean): Step = when {
        !enabled -> Step.OFF
        modelReady -> Step.NOT_NEEDED
        !consented -> Step.ASK_DOWNLOAD
        else -> Step.DOWNLOAD
    }
}

/** Translates titles of library items on this device, in the background, once the model is present. */
class TitleTranslator(private val media: MediaStore, private val engine: TranslationEngine) {
    /** Items that need a translation, newest first. */
    fun pending(deviceLanguage: String): List<MediaMeta> =
        media.list().filter { Titles.needsTranslation(it, deviceLanguage) && sourceOf(it) != null }

    /**
     * The original's language, guessed from its script: Han/kana/Hangul give zh/ja/ko, everything else is taken as English
     * (ML Kit has no detector here; a wrong guess just yields a poor or unchanged translation, which is then not stored).
     */
    fun sourceOf(m: MediaMeta): String? = LanguageTools.heuristic(m.title).substringBefore('-')

    /** Translates every pending item with one translator per source language; returns how many were stored. */
    fun run(deviceLanguage: String, shouldStop: () -> Boolean = { false }): Int {
        val target = engine.supported(deviceLanguage) ?: return 0
        var n = 0
        val bySource = pending(deviceLanguage).groupBy { engine.supported(sourceOf(it) ?: "") }
        for ((src, items) in bySource) {
            if (src == null || src == target || !engine.modelReady(src, target)) continue
            engine.translator(src, target).use { tr ->
                val out = tr.translate(items.map { it.title })
                for ((m, t) in items.zip(out)) {
                    if (shouldStop()) return n
                    media.setTranslatedTitle(m.mediaId, t.takeIf { it.isNotBlank() && it != m.title }, deviceLanguage); n++
                }
            }
        }
        return n
    }
}

/** Translate a subtitle track on this device: one translator, cues in batches, never a client per cue. */
object TrackTranslation {
    const val ORIGIN = "mlkit"
    const val BATCH = 40

    fun translate(lines: List<Line>, translator: TextTranslator, batch: Int = BATCH, progress: (Int, Int) -> Unit = { _, _ -> }): List<Line> {
        val out = ArrayList<Line>(lines.size)
        var i = 0
        while (i < lines.size) {
            val chunk = lines.subList(i, minOf(lines.size, i + batch))
            val tr = translator.translate(chunk.map { it.text })
            require(tr.size == chunk.size) { "translator returned " + tr.size + " results for " + chunk.size + " cues" }
            chunk.zip(tr).forEach { (l, t) -> out.add(Line(l.start, l.end, t)) }
            i += chunk.size
            progress(i, lines.size)
        }
        return out
    }

    fun label(targetTag: String) = LanguageTools.displayName(targetTag) + " (on-device)"

    fun toAdded(targetTag: String, lines: List<Line>) = AddedTrack(targetTag, label(targetTag), ORIGIN, lines)
}
