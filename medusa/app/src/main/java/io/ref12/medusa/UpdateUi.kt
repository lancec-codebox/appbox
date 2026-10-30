package io.ref12.medusa

import android.app.Activity
import android.content.Intent
import android.view.View
import android.widget.LinearLayout

/** The app-update banner (shared by Library and About): shows the [UpdateManager] state and its actions. */
object UpdateUi {
    /** Fills [box] for the current state; hidden when idle. [onPermission] is called when the user opens the install settings. */
    fun render(a: Activity, box: LinearLayout, quietWhenCurrent: Boolean = false, onPermission: () -> Unit) {
        val u = a.app.updates
        box.removeAllViews()
        fun text(t: String, bold: Boolean = false) = box.addView(Ui.text(a, t, 14f, bold = bold))
        fun buttons(vararg b: Pair<String, () -> Unit>) = box.addView(LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL
            for ((i, p) in b.withIndex()) addView(Ui.button(a, p.first, primary = i == 0, onClick = { p.second() }), Ui.lp(-2, -2, 0f, if (i == 0) 0 else 8, 8, 0, 0, a))
        })
        when (val s = u.state) {
            is UpdateState.Available -> {
                text("Medusa " + s.offer.version + " is available", true)
                if (s.offer.notes.isNotEmpty()) text(s.offer.notes.take(1200))
                buttons("Update" to { u.update() }, "Later" to { u.later() })
            }
            is UpdateState.Downloading -> text("Downloading Medusa " + s.offer.version + (s.fraction?.let { " " + (it * 100).toInt() + "%" } ?: "..."), true)
            is UpdateState.NeedsPermission -> {
                text("Allow Medusa to install apps", true)
                text("Android needs your OK once. Turn on Allow from this source, then come back: the update continues.")
                buttons("Open settings" to { onPermission(); a.startActivity(PackageApkInstaller.permissionIntent(a)) })
            }
            is UpdateState.Installing -> text(s.message + " Medusa " + s.offer.version + "...", true)
            is UpdateState.Done -> text(s.message, true)
            is UpdateState.Failed -> {
                text(s.message, true)
                buttons(*(listOfNotNull(if (s.offer != null) "Try again" to { u.update() } else null, "Dismiss" to { u.later() }).toTypedArray()))
            }
            is UpdateState.UpToDate -> if (!quietWhenCurrent) text("Medusa " + s.version + " is up to date")
            is UpdateState.Checking -> if (!quietWhenCurrent) text("Checking for updates...")
            UpdateState.Idle -> {}
        }
        box.visibility = if (box.childCount == 0) View.GONE else View.VISIBLE
    }

    fun box(a: Activity) = LinearLayout(a).apply {
        orientation = LinearLayout.VERTICAL; visibility = View.GONE; tag = "update-banner"
        background = Ui.rounded(a, Ui.color(a, R.color.selected), 8, Ui.color(a, R.color.accent))
        setPadding(Ui.dp(a, 12), Ui.dp(a, 10), Ui.dp(a, 12), Ui.dp(a, 10))
    }
}
