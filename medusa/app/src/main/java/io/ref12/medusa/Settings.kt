package io.ref12.medusa

import android.content.Context

class Settings(ctx: Context) {
    private val prefs = ctx.getSharedPreferences("media_helper", Context.MODE_PRIVATE)

    var origins: List<String>
        get() = prefs.getString("origins", null)?.lines()?.map { it.trim() }?.filter { it.isNotEmpty() } ?: DEFAULT_ORIGINS
        set(v) { prefs.edit().putString("origins", v.joinToString("\n")).apply() }

    var lastUpdateCheck: Long
        get() = prefs.getLong("lastUpdateCheck", 0L)
        set(v) { prefs.edit().putLong("lastUpdateCheck", v).apply() }
}
