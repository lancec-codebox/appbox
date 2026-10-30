package io.ref12.medusa

import android.app.AlertDialog
import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Handler
import android.os.Looper

/** Consent before the one-time model download, then background title translation and the track action. */
object TranslationUi {
    val engine: TranslationEngine by lazy { MlKitTranslation() }
    private val main = Handler(Looper.getMainLooper())

    fun onWifi(ctx: Context): Boolean {
        val cm = ctx.getSystemService(ConnectivityManager::class.java)
        val c = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
        return c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || c.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
    }

    private fun device(a: MainActivity) = Titles.deviceLanguage(java.util.Locale.getDefault().language, java.util.Locale.getDefault().country)

    /** Asks (once) before downloading the model of the device language, then translates the library's titles in the background. */
    fun enableTitles(a: MainActivity, done: () -> Unit = {}) {
        val lang = engine.supported(device(a)) ?: run { a.snack("On-device translation does not support your language."); return }
        val step = TranslationConsent.next(true, a.app.settings.translateConsent, modelKnownReady(lang))
        when (step) {
            TranslationConsent.Step.ASK_DOWNLOAD -> AlertDialog.Builder(a).setTitle("Download translation model?")
                .setMessage("Translating titles on this device needs a language model of about 30 MB, downloaded once" + (if (onWifi(a)) "." else ". You are not on Wi-Fi: it will wait for Wi-Fi.") + " It then works offline.")
                .setPositiveButton("Download") { _, _ -> a.app.settings.translateConsent = true; a.app.settings.translateTitles = true; start(a, lang, done) }
                .setNegativeButton("Not now", null).show()
            TranslationConsent.Step.DOWNLOAD, TranslationConsent.Step.NOT_NEEDED -> { a.app.settings.translateTitles = true; start(a, lang, done) }
            TranslationConsent.Step.OFF -> {}
        }
    }

    private fun modelKnownReady(lang: String): Boolean = try { engine.modelReady("en", lang) } catch (e: Exception) { false }

    /** Switching off removes the downloaded models. */
    fun disableTitles(a: MainActivity) {
        a.app.settings.translateTitles = false
        Thread { try { (engine as? MlKitTranslation)?.downloaded()?.forEach { if (it != "en") engine.removeModel(it) } } catch (e: Exception) {} }.start()
    }

    private fun start(a: MainActivity, lang: String, done: () -> Unit) {
        Thread {
            try {
                for (s in listOf("en", "zh", "ja", "ko").filter { engine.supported(it) != null && it != lang }) {
                    if (!engine.modelReady(s, lang)) engine.downloadModel(s, lang, wifiOnly = !onWifi(a) && true)
                }
                TitleTranslator(a.app.media, engine).run(device(a)) { !a.app.settings.translateTitles }
            } catch (e: Exception) { main.post { a.snack("Title translation stopped: " + (e.message ?: "error")) } }
            main.post { done() }
        }.start()
    }

    /** Translate one subtitle track into the device language (English by default) as an "English (on-device)" track. */
    fun translateTrack(a: MainActivity, m: MediaMeta, t: TrackInfo, done: () -> Unit) {
        val src = engine.supported(t.language) ?: run { a.snack("This language is not supported on-device."); return }
        val target = "en"
        if (src == target) { a.snack("This track is already English."); return }
        val step = TranslationConsent.next(true, a.app.settings.translateConsent, try { engine.modelReady(src, target) } catch (e: Exception) { false })
        fun go() = Thread {
            try {
                if (!engine.modelReady(src, target)) engine.downloadModel(src, target, wifiOnly = false)
                val data = a.app.jobs.subtitlesFor(m.mediaId, t.language, t.trackId) ?: throw FetchException("No lines", Codes.NO_CAPTIONS)
                val lines = engine.translator(src, target).use { TrackTranslation.translate(data.lines, it) { d, n -> main.post { a.snack("Translating $d / $n") } } }
                a.app.media.addTrack(m.mediaId, TrackTranslation.toAdded(target, lines))
                main.post { a.snack("Added English (on-device)"); done() }
            } catch (e: Exception) { main.post { a.snack("Translation failed: " + (e.message ?: "error")) } }
        }.start()
        if (step == TranslationConsent.Step.ASK_DOWNLOAD) AlertDialog.Builder(a).setTitle("Download translation model?")
            .setMessage("About 30 MB, once. Translation then works offline.").setPositiveButton("Download") { _, _ -> a.app.settings.translateConsent = true; go() }
            .setNegativeButton("Cancel", null).show()
        else go()
    }
}
