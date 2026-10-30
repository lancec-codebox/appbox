package io.ref12.medusa

import android.text.InputType
import android.view.View
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Toast
import java.io.File

/** Settings in groups: Subtitles, Background, Web apps, Updates, Storage. */
class SettingsPage(a: MainActivity) : Page(a) {
    override val title = "Settings"
    private lateinit var storageText: android.widget.TextView

    override fun build(): View {
        val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(Ui.dp(a, 12), 0, Ui.dp(a, 12), Ui.dp(a, 24)) }
        fun section(t: String): LinearLayout {
            col.addView(Ui.header(a, t).apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 18), 0, Ui.dp(a, 6)) })
            return Ui.card(a).also { col.addView(it) }
        }
        fun field(hint: String, value: String, tg: String, multi: Boolean = false) = EditText(a).apply {
            this.hint = hint; setText(value); tag = tg; contentDescription = hint; textSize = 14f
            setTextColor(Ui.color(a, R.color.on_surface)); setHintTextColor(Ui.color(a, R.color.muted))
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI or (if (multi) InputType.TYPE_TEXT_FLAG_MULTI_LINE else 0)
            if (!multi) setSingleLine()
        }

        val sub = section("Subtitles")
        sub.addView(Ui.muted(a, "Default language when you download (blank = the video's original language)"))
        val lang = field("Original, or a code like en, zh-Hans", a.app.settings.defaultLanguage, "default-language")
        sub.addView(lang)
        sub.addView(Ui.muted(a, "Title language (YouTube serves translated titles; the original is shown under it)"), Ui.lp(-1, -2, 0f, 0, 10, 0, 0, a))
        sub.addView(TrackUi.radios(a, TitleMode.values().map { it.label }, TitleMode.values().map { "titlemode:" + it.name }, a.app.settings.titleMode.ordinal) { a.app.settings.titleMode = TitleMode.values()[it] })
        sub.addView(android.widget.CheckBox(a).apply {
            text = "Translate titles on this device"; tag = "translate-titles"; isChecked = a.app.settings.translateTitles
            setOnCheckedChangeListener { _, on -> if (on) { if (a.app.settings.translateTitles.not()) { isChecked = false; TranslationUi.enableTitles(a) { isChecked = a.app.settings.translateTitles } } } else TranslationUi.disableTitles(a) }
        }, Ui.lp(-1, -2, 0f, 0, 8, 0, 0, a))
        sub.addView(Ui.muted(a, "Titles YouTube has no translation for are translated offline and marked as machine-translated. Turning this off deletes the language models.", 12f))
        sub.addView(Ui.muted(a, "Text size in the player"), Ui.lp(-1, -2, 0f, 0, 10, 0, 0, a))
        val sizes = listOf("Small" to 14f, "Medium" to 18f, "Large" to 24f, "Extra large" to 30f)
        val cur = sizes.indexOfFirst { it.second >= a.app.settings.subtitleSp }.let { if (it < 0) sizes.size - 1 else it }
        sub.addView(TrackUi.radios(a, sizes.map { it.first + " (" + it.second.toInt() + " sp)" }, sizes.map { "size:" + it.first }, cur) { a.app.settings.subtitleSp = sizes[it].second })
        sub.addView(Ui.button(a, "Save default language") { a.app.settings.defaultLanguage = lang.text.toString(); Toast.makeText(a, "Saved", Toast.LENGTH_SHORT).show() }, Ui.lp(-2, -2, 0f, 0, 6, 0, 0, a))

        val bg = section("Background")
        bg.addView(Ui.muted(a, "Keep Medusa ready after you leave it (so web apps can reach its local server)"))
        bg.addView(TrackUi.radios(a, KeepAliveMode.values().map { it.label }, KeepAliveMode.values().map { "keepalive:" + it.minutes }, a.app.settings.keepAlive.ordinal) {
            a.app.settings.keepAlive = KeepAliveMode.values()[it]
        })

        val web = section("Web apps")
        web.addView(Ui.muted(a, "Allowed origins, one per line"))
        val origins = field("https://example.app", a.app.settings.origins.joinToString("\n"), "origins", multi = true)
        web.addView(origins)
        web.addView(Ui.button(a, "Save origins") {
            a.app.settings.origins = origins.text.toString().lines().map { it.trim() }.filter { it.isNotEmpty() }
            Toast.makeText(a, "Saved", Toast.LENGTH_SHORT).show()
        }, Ui.lp(-2, -2, 0f, 0, 6, 0, 0, a))

        val up = section("Updates")
        up.addView(CheckBox(a).apply {
            text = "Check for Medusa updates automatically"; isChecked = a.app.settings.autoUpdateCheck; setTextColor(Ui.color(a, R.color.on_surface))
            setOnCheckedChangeListener { _, on -> a.app.settings.autoUpdateCheck = on }
        })
        up.addView(Ui.button(a, "Check for updates now") { a.app.updates.check(manual = true); a.go(Dest.ABOUT) }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))

        val st = section("Storage")
        storageText = Ui.muted(a, "", 14f)
        st.addView(storageText)
        st.addView(Ui.button(a, "Clear cache") {
            val n = clearCache()
            Toast.makeText(a, "Cleared " + Ui.size(n), Toast.LENGTH_SHORT).show(); updateStorage()
        }, Ui.lp(-2, -2, 0f, 0, 6, 0, 0, a))
        updateStorage()
        return ScrollView(a).apply { addView(col) }
    }

    private fun mediaBytes() = a.app.media.list().sumOf { a.app.media.bytesOf(it.mediaId) }

    private fun updateStorage() {
        val cache = a.cacheDir.walkTopDown().filter { it.isFile }.sumOf { it.length() }
        storageText.text = "Downloaded media: " + Ui.size(mediaBytes()) + " in " + a.app.media.list().size + " items\nCache (temporary files, exports): " + Ui.size(cache)
    }

    /** Deletes temporary files only (the cache folder, and half-finished downloads); captured items are kept. */
    private fun clearCache(): Long {
        var n = 0L
        for (f in (a.cacheDir.listFiles() ?: emptyArray())) { n += f.walkTopDown().filter { it.isFile }.sumOf { it.length() }; f.deleteRecursively() }
        for (m in a.app.media.list()) for (d in (File(a.filesDir, "data/media/" + m.mediaId).listFiles() ?: emptyArray()).filter { it.isDirectory && it.name.startsWith("tmp-") }) {
            n += d.walkTopDown().filter { it.isFile }.sumOf { it.length() }; d.deleteRecursively()
        }
        return n
    }
}
