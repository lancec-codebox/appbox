package io.ref12.mediahelper

import android.content.ContentValues
import android.content.Context
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import java.io.File

/** Saves a result JSON to Downloads as <title>.media.json (the protocol's fallback). */
object TranscriptSaver {
    fun fileName(title: String?): String {
        val clean = (title ?: "").replace(Regex("[\\\\/:*?\"<>|\\u0000-\\u001f]"), "_").trim().take(100).ifEmpty { "transcript" }
        return "$clean.media.json"
    }

    /** Returns a description of where it went. */
    fun save(ctx: Context, title: String?, json: String): String {
        val name = fileName(title)
        if (Build.VERSION.SDK_INT >= 29) {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, name)
                put(MediaStore.Downloads.MIME_TYPE, "application/json")
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS)
            }
            val uri = ctx.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                ?: throw java.io.IOException("Could not create the file in Downloads")
            ctx.contentResolver.openOutputStream(uri)!!.use { it.write(json.toByteArray()) }
        } else {
            val dir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
            dir.mkdirs()
            File(dir, name).writeText(json)
        }
        return "Downloads/$name"
    }
}
