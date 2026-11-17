package io.ref12.medusa

import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import io.ref12.medusa.node.IndexNodeUi

private fun scroller(a: MainActivity, fill: (LinearLayout) -> Unit): View {
    val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(Ui.dp(a, 12), Ui.dp(a, 8), Ui.dp(a, 12), Ui.dp(a, 24)) }
    val card = Ui.card(a).also { col.addView(it) }
    fill(card)
    return ScrollView(a).apply { addView(col) }
}

/** The node's day-to-day screen: status, Run now, last runs and recent jobs, what this phone added, Drama admin. */
class IndexNodePage(a: MainActivity) : Page(a) {
    override val title = "Index node"
    private var refresh: (() -> Unit)? = null
    override fun build(): View = scroller(a) { refresh = IndexNodeUi.buildMain(a, it) }
    override fun tick() { refresh?.invoke() }
    override fun resume() { refresh?.invoke() }
}

/** The node's configuration only: enable, QR scanner, SAS, name, conditions, limits, status notification. */
class IndexNodeSettingsPage(a: MainActivity) : Page(a) {
    override val title = "Index node settings"
    override fun build(): View = scroller(a) { IndexNodeUi.buildSettings(a, it) }
}
