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
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val url = SourceUrl.firstUrl(intent.getStringExtra(EXTRA_URL)) ?: run { finish(); return }
        val video = SourceUrl.videoId(url) != null && !SourceUrl.isSource(url)
        if (video) { askVideo(url); return }
        val isPlaylist = SourceUrl.parse(url)?.kind == "playlist" || url.contains("list=")
        val follow = CheckBox(this).apply { text = "Follow channels of its videos"; tag = "share-follow-channels"; visibility = if (isPlaylist) android.view.View.VISIBLE else android.view.View.GONE }
        val body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 16, 40, 0); addView(follow) }
        AlertDialog.Builder(this).setTitle("Follow in index?").setMessage(url).setView(body)
            .setPositiveButton("Follow in index") { _, _ -> go(url, follow.isChecked) }
            .setNegativeButton("Cancel") { _, _ -> finish() }.setOnCancelListener { finish() }.show()
    }

    /** A single video: "Add to index", optionally with its channel. */
    private fun askVideo(url: String) {
        val also = CheckBox(this).apply { text = "Also follow its channel"; tag = "share-also-channel" }
        val body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(40, 16, 40, 0); addView(also) }
        AlertDialog.Builder(this).setTitle("Add to index?").setMessage(url).setView(body)
            .setPositiveButton("Add to index") { _, _ -> addVideo(url, also.isChecked) }
            .setNegativeButton("Cancel") { _, _ -> finish() }.setOnCancelListener { finish() }.show()
    }

    private fun addVideo(url: String, alsoChannel: Boolean) {
        val sas = NodeSettings(this).vault.load()
        if (sas == null) { Toast.makeText(this, "Set up the Index node first (Settings, Index node)", Toast.LENGTH_LONG).show(); finish(); return }
        Thread {
            val (added, ch) = IndexVideo.share(BlobClient(sas), url, alsoChannel, System.currentTimeMillis()) { app.sources.videoChannelId(it) }
            val text = when (added) { AddResult.Added -> "Added"; AddResult.Already -> "Already indexed"; is AddResult.Failed -> "Could not add: " + added.message } +
                (ch?.let { "\n" + IndexNodeUi.followMessage(it) } ?: "")
            runOnUiThread { Toast.makeText(this, text, Toast.LENGTH_LONG).show(); finish() }
        }.start()
    }

    private fun go(url: String, followChannels: Boolean) {
        val sas = NodeSettings(this).vault.load()
        if (sas == null) { Toast.makeText(this, "Set up the Index node first (Settings, Index node)", Toast.LENGTH_LONG).show(); finish(); return }
        Thread {
            val res = SourceFollow.run(BlobClient(sas), url, FollowOptions(followChannels = followChannels), System.currentTimeMillis()) { app.sources.channelId(it) }
            runOnUiThread { Toast.makeText(this, IndexNodeUi.followMessage(res), Toast.LENGTH_LONG).show(); finish() }
        }.start()
    }

    companion object { const val EXTRA_URL = "url" }
}
