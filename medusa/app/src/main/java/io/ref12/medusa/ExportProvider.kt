package io.ref12.medusa

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File

/** Serves the files in cache/exports read-only, so the share sheet can hand them to other apps. */
class ExportProvider : ContentProvider() {
    private fun file(uri: Uri): File? {
        val dir = File(context!!.cacheDir, "exports").canonicalFile
        val f = File(dir, uri.lastPathSegment ?: return null).canonicalFile
        return f.takeIf { it.parentFile == dir && it.isFile }
    }

    override fun onCreate() = true
    override fun getType(uri: Uri): String? = file(uri)?.name?.let { n ->
        when { n.endsWith(".srt") -> "application/x-subrip"; n.endsWith(".vtt") -> "text/vtt"; else -> "application/json" }
    }
    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor =
        ParcelFileDescriptor.open(file(uri) ?: throw java.io.FileNotFoundException(uri.toString()), ParcelFileDescriptor.MODE_READ_ONLY)
    override fun query(uri: Uri, projection: Array<String>?, selection: String?, selectionArgs: Array<String>?, sortOrder: String?): Cursor? {
        val f = file(uri) ?: return null
        return MatrixCursor(arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)).apply { addRow(arrayOf(f.name, f.length())) }
    }
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<String>?) = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<String>?) = 0

    companion object {
        fun uriFor(ctx: android.content.Context, name: String): Uri =
            Uri.Builder().scheme("content").authority(ctx.packageName + ".files").appendPath(name).build()
    }
}
