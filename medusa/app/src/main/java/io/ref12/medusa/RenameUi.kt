package io.ref12.medusa

import android.app.AlertDialog
import android.widget.EditText

/** Rename a library entry: a user title that wins everywhere; blank or "Reset to original" removes it. */
object RenameUi {
    fun show(a: MainActivity, m: MediaMeta, done: () -> Unit) {
        val input = EditText(a).apply {
            setText(m.userTitle ?: Titles.primary(m, a.app.settings.titleMode)); setSelectAllOnFocus(true); setSingleLine(); tag = "rename-input"
            contentDescription = "Title"
        }
        val b = AlertDialog.Builder(a).setTitle("Rename").setView(input)
            .setPositiveButton("Save") { _, _ -> a.app.media.rename(m.mediaId, input.text.toString()); done() }
            .setNegativeButton("Cancel", null)
        if (m.userTitle != null) b.setNeutralButton("Reset to original") { _, _ -> a.app.media.rename(m.mediaId, null); done() }
        b.show()
    }
}
