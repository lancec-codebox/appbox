package io.ref12.medusa

import android.content.Context

class Settings(ctx: Context) {
    private val prefs = ctx.getSharedPreferences("media_helper", Context.MODE_PRIVATE)

    var origins: List<String>
        get() = prefs.getString("origins", null)?.lines()?.map { it.trim() }?.filter { it.isNotEmpty() } ?: DEFAULT_ORIGINS
        set(v) { prefs.edit().putString("origins", v.joinToString("\n")).apply() }

    /** Default subtitle language: "" = the video's original language, else a language code like "en" or "zh-Hans". */
    var defaultLanguage: String
        get() = prefs.getString("defaultLanguage", "") ?: ""
        set(v) { prefs.edit().putString("defaultLanguage", v.trim()).apply() }

    /** The same as a languages list for a job (empty = original). */
    val defaultLanguages: List<String> get() = defaultLanguage.takeIf { it.isNotEmpty() }?.let { listOf(it) } ?: emptyList()

    /** How long the server stays up after the user leaves Medusa. */
    var keepAlive: KeepAliveMode
        get() = KeepAliveMode.fromMinutes(prefs.getInt("keepAliveMinutes", KeepAliveMode.DEFAULT.minutes))
        set(v) { prefs.edit().putInt("keepAliveMinutes", v.minutes).apply() }

    /** Whether titles show in the device's language (when the site has one) or only as the original. */
    var titleMode: TitleMode
        get() = TitleMode.values().firstOrNull { it.name == prefs.getString("titleMode", null) } ?: TitleMode.DEVICE
        set(v) { prefs.edit().putString("titleMode", v.name).apply() }

    /** The language to ask for titles in, or null when the mode is Original only. */
    fun titleLanguage(): String? = if (titleMode == TitleMode.ORIGINAL) null else {
        val l = java.util.Locale.getDefault(); Titles.deviceLanguage(l.language, l.country)
    }

    /** Which language the title of this item was last looked up in (so a backfill runs once per language). */
    fun titleChecked(mediaId: String): String? = prefs.getString("titlecheck.$mediaId", null)
    fun setTitleChecked(mediaId: String, lang: String) { prefs.edit().putString("titlecheck.$mediaId", lang).apply() }

    /** Subtitle text size in sp (the second line is drawn smaller). */
    var subtitleSp: Float
        get() = prefs.getFloat("subtitleSp", 18f)
        set(v) { prefs.edit().putFloat("subtitleSp", v).apply() }

    /** Remembered subtitle choice per media item. slot = "main" or "second"; "" = off; null = never chosen. */
    fun trackChoice(mediaId: String, slot: String): String? = prefs.getString("track.$mediaId.$slot", null)

    fun setTrackChoice(mediaId: String, slot: String, language: String?) {
        prefs.edit().putString("track.$mediaId.$slot", language ?: "").apply()
    }

    /** Automatic app update checks (when the app opens, at most once a day). */
    var autoUpdateCheck: Boolean
        get() = prefs.getBoolean("autoUpdateCheck", true)
        set(v) { prefs.edit().putBoolean("autoUpdateCheck", v).apply() }

    var lastAppUpdateCheck: Long
        get() = prefs.getLong("lastAppUpdateCheck", 0L)
        set(v) { prefs.edit().putLong("lastAppUpdateCheck", v).apply() }

    /** Last yt-dlp update check. */
    var lastUpdateCheck: Long
        get() = prefs.getLong("lastUpdateCheck", 0L)
        set(v) { prefs.edit().putLong("lastUpdateCheck", v).apply() }
}
