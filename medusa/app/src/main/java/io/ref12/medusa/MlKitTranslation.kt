package io.ref12.medusa

import com.google.android.gms.tasks.Tasks
import com.google.mlkit.common.model.DownloadConditions
import com.google.mlkit.common.model.RemoteModelManager
import com.google.mlkit.nl.translate.TranslateLanguage
import com.google.mlkit.nl.translate.TranslateRemoteModel
import com.google.mlkit.nl.translate.Translation
import com.google.mlkit.nl.translate.Translator
import com.google.mlkit.nl.translate.TranslatorOptions
import java.util.concurrent.TimeUnit

/** ML Kit Translation: offline, models of about 30 MB downloaded once. Calls block, so run them off the main thread. */
class MlKitTranslation : TranslationEngine {
    private val manager get() = RemoteModelManager.getInstance()

    override fun supported(tag: String): String? = TranslateLanguage.fromLanguageTag(tag.substringBefore('-').lowercase())

    private fun model(lang: String) = TranslateRemoteModel.Builder(lang).build()

    override fun modelReady(source: String, target: String): Boolean =
        listOf(source, target).all { Tasks.await(manager.isModelDownloaded(model(it)), 30, TimeUnit.SECONDS) }

    override fun downloadModel(source: String, target: String, wifiOnly: Boolean) {
        val cond = DownloadConditions.Builder().also { if (wifiOnly) it.requireWifi() }.build()
        for (l in listOf(source, target)) Tasks.await(manager.download(model(l), cond), 20, TimeUnit.MINUTES)
    }

    override fun removeModel(language: String) { Tasks.await(manager.deleteDownloadedModel(model(language)), 60, TimeUnit.SECONDS) }

    fun downloaded(): List<String> = Tasks.await(manager.getDownloadedModels(TranslateRemoteModel::class.java), 30, TimeUnit.SECONDS).map { it.language }

    override fun translator(source: String, target: String): TextTranslator {
        val t: Translator = Translation.getClient(TranslatorOptions.Builder().setSourceLanguage(source).setTargetLanguage(target).build())
        return object : TextTranslator {
            override fun translate(texts: List<String>): List<String> {
                // the API is one string per call; the single client is reused, and a cue is translated as one unit
                return texts.map { Tasks.await(t.translate(it), 60, TimeUnit.SECONDS) }
            }
            override fun close() = t.close()
        }
    }
}
