package io.ref12.medusa

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore as AndroidMedia
import java.io.File

/** A file saved to the public Downloads folder: where it is and the name it finally got. */
class SavedFile(val uri: Uri, val name: String, val mime: String)

/** Saves text files into the public Downloads folder so they show in Files and Recent, like any download. */
object DownloadsSaver {
    /** "name.srt", then "name (2).srt", "name (3).srt"... : the first not taken. [ext] is without the dot and may contain dots. */
    fun uniqueName(base: String, ext: String, exists: (String) -> Boolean): String {
        var n = 1
        while (true) {
            val name = if (n == 1) "$base.$ext" else "$base ($n).$ext"
            if (!exists(name)) return name
            n++
        }
    }

    /** Splits a file name made by TrackExport.fileName into base and extension using the format's extension. */
    fun split(fileName: String, f: ExportFormat): Pair<String, String> =
        if (fileName.endsWith("." + f.ext)) fileName.removeSuffix("." + f.ext) to f.ext else fileName.substringBeforeLast('.') to fileName.substringAfterLast('.', "")

    fun save(ctx: Context, fileName: String, f: ExportFormat, content: String): SavedFile {
        val (base, ext) = split(fileName, f)
        val bytes = content.toByteArray(Charsets.UTF_8)
        return if (Build.VERSION.SDK_INT >= 29) saveScoped(ctx, base, ext, f, bytes) else saveLegacy(ctx, base, ext, f, bytes)
    }

    private fun saveScoped(ctx: Context, base: String, ext: String, f: ExportFormat, bytes: ByteArray): SavedFile {
        val r = ctx.contentResolver
        val coll = AndroidMedia.Downloads.EXTERNAL_CONTENT_URI
        fun taken(name: String): Boolean = r.query(coll, arrayOf(AndroidMedia.MediaColumns._ID),
            "${AndroidMedia.MediaColumns.DISPLAY_NAME} = ? AND ${AndroidMedia.MediaColumns.RELATIVE_PATH} LIKE ?",
            arrayOf(name, Environment.DIRECTORY_DOWNLOADS + "%"), null)?.use { it.count > 0 } ?: false
        val name = uniqueName(base, ext, ::taken)
        val v = ContentValues().apply {
            put(AndroidMedia.MediaColumns.DISPLAY_NAME, name)
            put(AndroidMedia.MediaColumns.MIME_TYPE, f.mime)
            put(AndroidMedia.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS)
            put(AndroidMedia.MediaColumns.IS_PENDING, 1)
        }
        val uri = r.insert(coll, v) ?: throw java.io.IOException("Downloads is not available")
        try {
            (r.openOutputStream(uri) ?: throw java.io.IOException("Cannot write to Downloads")).use { it.write(bytes) }
            r.update(uri, ContentValues().apply { put(AndroidMedia.MediaColumns.IS_PENDING, 0) }, null, null)
        } catch (e: Exception) { r.delete(uri, null, null); throw e }
        return SavedFile(uri, name, f.mime)
    }

    @Suppress("DEPRECATION")
    private fun saveLegacy(ctx: Context, base: String, ext: String, f: ExportFormat, bytes: ByteArray): SavedFile {
        val dir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS).also { it.mkdirs() }
        val name = uniqueName(base, ext) { File(dir, it).exists() }
        val file = File(dir, name).also { it.writeBytes(bytes) }
        android.media.MediaScannerConnection.scanFile(ctx, arrayOf(file.path), arrayOf(f.mime), null)
        return SavedFile(Uri.fromFile(file), name, f.mime)
    }
}
