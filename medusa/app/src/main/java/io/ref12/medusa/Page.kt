package io.ref12.medusa

import android.view.View

/** One screen of the shell. [build] makes its view; [tick] runs about every 1.5 s while it is shown. */
abstract class Page(val a: MainActivity) {
    abstract val title: String
    abstract fun build(): View
    open fun tick() {}
    open fun resume() {}
}
