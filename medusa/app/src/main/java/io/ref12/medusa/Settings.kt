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

    var lastUpdateCheck: Long
        get() = prefs.getLong("lastUpdateCheck", 0L)
        set(v) { prefs.edit().putLong("lastUpdateCheck", v).apply() }
}
