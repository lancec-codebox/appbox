package io.ref12.medusa

import android.content.Intent
import android.net.Uri
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/** About & updates: versions, server status, app and yt-dlp updates, licences and the repo link. */
class AboutPage(a: MainActivity) : Page(a) {
    override val title = "About & updates"
    private lateinit var banner: LinearLayout
    private lateinit var status: TextView
    private lateinit var ytdlp: TextView
    private lateinit var ytBtn: android.widget.Button
    private var wentToPermission = false

    override fun build(): View {
        val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(Ui.dp(a, 12), Ui.dp(a, 8), Ui.dp(a, 12), Ui.dp(a, 24)) }
        banner = UpdateUi.box(a)
        col.addView(banner, Ui.lp(-1, -2, 0f, 0, 6, 0, 6, a))
        col.addView(Ui.header(a, "Version").apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 10), 0, Ui.dp(a, 6)) })
        val v = Ui.card(a)
        v.addView(Ui.text(a, "Medusa " + BuildConfig.VERSION_NAME, 16f, bold = true).apply { tag = "app-version" })
        ytdlp = Ui.muted(a, "yt-dlp: ...", 14f).apply { tag = "ytdlp-version" }
        v.addView(ytdlp)
        status = Ui.muted(a, "", 13f).apply { setPadding(0, Ui.dp(a, 6), 0, 0) }
        v.addView(status)
        col.addView(v)
        col.addView(Ui.header(a, "Updates").apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 18), 0, Ui.dp(a, 6)) })
        val u = Ui.card(a)
        u.addView(LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL
            addView(Ui.button(a, "Check for Medusa updates") { a.app.updates.check(manual = true) }, Ui.lp(-2, -2, 0f, 0, 0, 8, 0, a))
            ytBtn = Ui.button(a, "Update yt-dlp") { updateYt() }
            addView(ytBtn)
        })
        col.addView(u)
        col.addView(Ui.header(a, "About").apply { setPadding(Ui.dp(a, 4), Ui.dp(a, 18), 0, Ui.dp(a, 6)) })
        val l = Ui.card(a)
        l.addView(Ui.muted(a, "Medusa captures transcripts and videos on this phone and serves them to web apps through a local API on 127.0.0.1.", 13f))
        l.addView(Ui.text(a, "Source: github.com/" + UpdateLogic.REPO, 14f, R.color.accent).apply {
            tag = "repo-link"; setPadding(0, Ui.dp(a, 8), 0, Ui.dp(a, 4))
            setOnClickListener { try { a.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://github.com/" + UpdateLogic.REPO))) } catch (e: Exception) {} }
        })
        l.addView(Ui.text(a, "Licences", 13f, bold = true).apply { setPadding(0, Ui.dp(a, 8), 0, Ui.dp(a, 2)) })
        l.addView(Ui.muted(a, "yt-dlp: Unlicense. youtubedl-android: GPL-3.0. FFmpeg: LGPL/GPL. NanoHTTPD: BSD-3-Clause. AndroidX Media3 (ExoPlayer): Apache-2.0.", 12f))
        col.addView(l)
        refresh()
        Thread {
            val ver = a.app.fetcher.version()
            a.runOnUiThread { ytdlp.text = "yt-dlp: $ver" }
        }.start()
        return ScrollView(a).apply { addView(col) }
    }

    override fun resume() { if (wentToPermission) { wentToPermission = false; a.app.updates.resumeAfterPermission() } }
    override fun tick() { if (::banner.isInitialized) refresh() }

    private fun refresh() {
        UpdateUi.render(a, banner) { wentToPermission = true }
        status.text = when {
            HelperService.running -> "Server running on 127.0.0.1:$SERVER_PORT"
            HelperService.lastError != null -> HelperService.lastError
            else -> "Server stopped (starts when a web app or a capture asks for a transcript)"
        }
    }

    private fun updateYt() {
        ytBtn.isEnabled = false; ytBtn.text = "Updating..."
        Thread {
            val msg = try { a.app.fetcher.update() } catch (e: Exception) { "Update failed: " + e.message }
            a.runOnUiThread { ytBtn.isEnabled = true; ytBtn.text = "Update yt-dlp"; ytdlp.text = msg }
        }.start()
    }
}
