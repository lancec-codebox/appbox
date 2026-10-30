package io.ref12.medusa

import android.app.Activity
import android.content.res.Configuration
import android.graphics.Color
import android.os.Build
import android.view.View
import androidx.core.graphics.Insets
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat

/**
 * Edge-to-edge on every Android version (Android 15 enforces it at targetSdk 35+): the window draws under the system
 * bars and each screen pads itself by the bar and cutout insets, never by fixed heights.
 */
object Edge {
    /** The insets a screen must respect: system bars plus display cutout. */
    fun of(w: WindowInsetsCompat): Insets = w.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())

    /** True when the app is in the dark theme. */
    fun isNight(a: Activity) = (a.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES

    /**
     * Makes [a] edge-to-edge. [lightIcons] = the system bar icons are dark (for a light bar); pass false over dark bars.
     * [onInsets] is called with the current insets now and whenever they change (rotation, bars hidden or shown).
     */
    fun setup(a: Activity, root: View, lightBar: Boolean = !isNight(a), onInsets: (Insets) -> Unit) {
        WindowCompat.setDecorFitsSystemWindows(a.window, false)
        a.window.statusBarColor = Color.TRANSPARENT
        a.window.navigationBarColor = Color.TRANSPARENT
        if (Build.VERSION.SDK_INT >= 29) { a.window.isNavigationBarContrastEnforced = false; a.window.isStatusBarContrastEnforced = false }
        WindowCompat.getInsetsController(a.window, a.window.decorView).apply {
            isAppearanceLightStatusBars = lightBar
            isAppearanceLightNavigationBars = lightBar
        }
        ViewCompat.setOnApplyWindowInsetsListener(root) { _, w ->
            onInsets(of(w))
            WindowInsetsCompat.CONSUMED
        }
        ViewCompat.requestApplyInsets(root)
    }
}
