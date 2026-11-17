package io.ref12.medusa

import android.app.Activity
import android.app.AlertDialog
import android.os.Bundle
import android.widget.CheckBox
import android.widget.LinearLayout
import android.widget.Toast
import io.ref12.medusa.node.AddResult
import io.ref12.medusa.node.BlobClient
import io.ref12.medusa.node.IndexVideo
import io.ref12.medusa.node.FollowOptions
import io.ref12.medusa.node.FollowResult
import io.ref12.medusa.node.IndexNodeUi
import io.ref12.medusa.node.NodeSettings
import io.ref12.medusa.node.SourceFollow
import io.ref12.medusa.node.SourceUrl

/** A shared playlist or channel URL: offers "Follow in index". Started by ImportActivity, which receives the share. */
class ShareUrlActivity : Activity() {
    private fun priorityField() = android.widget.EditText(this).apply {
        hint = "Priority -1000..1000 (default 0)"; tag = "share-priority"; setSingleLine()
        inputType = android.text.InputType.TYPE_CLASS_NUMBER or android.text.InputType.TYPE_NUMBER_FLAG_SIGNED
    }
    private fun prioOf(f: android.widget.EditText): Int? = f.text.toString().trim().ifEmpty { "0" }.toIntOrNull()?.takeIf { it in io.ref12.medusa.node.Prio.MIN..io.ref12.medusa.node.Prio.MAX }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val url = SourceUrl.firstUrl(intent.getStringExtra(EXTRA_URL)) ?: run { finish(); return }
        val video = SourceUrl.videoId(url) != null && !SourceUrl.isSource(url)
        if (video) { askVideo(url); return }
        val isPlaylist = SourceUrl.parse(url)?.kind == "playlist" || url.contains("list=")
        val follow = CheckBox(this).apply { text = "Follow channels of its videos"; tag = "share-follow-channels"; visibility = if (isPlaylist) android.view.View.VISIBLE else android.view.View.GONE }
        val pf = priorityField()
        val body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 16, 40, 0); addView(follow); addView(pf) }
        AlertDialog.Builder(this).setTitle("Follow in index?").setMessage(url).setView(body)
            .setPositiveButton("Follow in index") { _, _ -> val p = prioOf(pf); if (p == null) { Toast.makeText(this, "Priority must be a whole number from -1000 to 1000", Toast.LENGTH_LONG).show(); finish() } else go(url, follow.isChecked, p) }
            .setNegativeButton("Cancel") { _, _ -> finish() }.setOnCancelListener { finish() }.show()
    }

    /** A single video: "Add to index", optionally with its channel. */
    private fun askVideo(url: String) {
        val also = CheckBox(this).apply { text = "Also follow its channel"; tag = "share-also-channel" }
        val pf = priorityField()
        val body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 16, 40, 0); addView(also); addView(pf) }
        AlertDialog.Builder(this).setTitle("Add to index?").setMessage(url).setView(body)
            .setPositiveButton("Add to index") { _, _ -> val p = prioOf(pf); if (p == null) { Toast.makeText(this, "Priority must be a whole number from -1000 to 1000", Toast.LENGTH_LONG).show(); finish() } else addVideo(url, also.isChecked, p) }
            .setNegativeButton("Cancel") { _, _ -> finish() }.setOnCancelListener { finish() }.show()
    }

    private fun addVideo(url: String, alsoChannel: Boolean, priority: Int) {
        val settings = NodeSettings(this); val sas = settings.vault.load()
        if (sas == null) { Toast.makeText(this, "Set up the Index node first (Settings, Index node)", Toast.LENGTH_LONG).show(); finish(); return }
        Thread {
            val text = io.ref12.medusa.node.IndexAdd.add(BlobClient(sas), settings.added, url, FollowOptions(priority = priority), priority, System.currentTimeMillis(),
                { app.sources.channelId(it) }, { app.sources.videoChannelId(it) }, alsoChannel)
            runOnUiThread { Toast.makeText(this, text, Toast.LENGTH_LONG).show(); finish() }
        }.start()
    }

    private fun go(url: String, followChannels: Boolean, priority: Int) {
        val settings = NodeSettings(this); val sas = settings.vault.load()
        if (sas == null) { Toast.makeText(this, "Set up the Index node first (Settings, Index node)", Toast.LENGTH_LONG).show(); finish(); return }
        Thread {
            val text = io.ref12.medusa.node.IndexAdd.add(BlobClient(sas), settings.added, url, FollowOptions(followChannels = followChannels, priority = priority), priority, System.currentTimeMillis(),
                { app.sources.channelId(it) }, { app.sources.videoChannelId(it) })
            runOnUiThread { Toast.makeText(this, text, Toast.LENGTH_LONG).show(); finish() }
        }.start()
    }

    companion object { const val EXTRA_URL = "url" }
}
