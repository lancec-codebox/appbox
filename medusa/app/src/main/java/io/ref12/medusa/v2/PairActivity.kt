package io.ref12.medusa.v2

import android.app.Activity
import android.app.AlertDialog
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.os.Bundle
import io.ref12.medusa.app

/**
 * Asks the user "Allow <app> to use Medusa's tools?". Two ways in:
 * an app starts it for a result (action io.ref12.medusa.action.PAIR, extra appName; the token comes back as the result's "token" extra),
 * or Medusa's own notification opens it for a request made over HTTP (extra requestId; the app then polls for the token).
 */
class PairActivity : Activity() {
    private var requestId: String? = null
    private var viaIntent = false
    private var answered = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val store = app.v2.pairing
        val rid = intent.getStringExtra(EXTRA_REQUEST)
        val req = if (rid != null) store.get(rid) else {
            viaIntent = true
            val name = PairingStore.cleanName(intent.getStringExtra(EXTRA_APP_NAME))
            if (name == null) { setResult(RESULT_CANCELED); finish(); return }
            store.request(name, callingPackage)
        }
        if (req == null || req.state != PairingStore.State.PENDING) { setResult(RESULT_CANCELED); finish(); return }
        requestId = req.id
        (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).cancel(req.id.hashCode())
        val from = req.source?.let { "\n\n($it)" } ?: ""
        AlertDialog.Builder(this)
            .setTitle("Allow " + req.appName + " to use Medusa's tools?")
            .setMessage("It will be able to run yt-dlp, ffmpeg and Python through Medusa, and to read and write files in its own work folders. You can take this back in Medusa's Settings." + from)
            .setCancelable(false)
            .setPositiveButton("Allow") { _, _ -> answer(true) }
            .setNegativeButton("Don't allow") { _, _ -> answer(false) }
            .show()
    }

    private fun answer(allow: Boolean) {
        answered = true
        val id = requestId ?: return
        val store = app.v2.pairing
        if (allow) {
            val token = store.approve(id)
            if (viaIntent && token != null) { store.discard(id); setResult(RESULT_OK, Intent().putExtra(EXTRA_TOKEN, token)) }
            else setResult(if (token != null) RESULT_OK else RESULT_CANCELED)
        } else { store.deny(id); if (viaIntent) store.discard(id); setResult(RESULT_CANCELED) }
        finish()
    }

    override fun onDestroy() {
        // dismissed without an answer (e.g. the screen was closed): treat as not allowed
        if (!answered && requestId != null) app.v2.pairing.let { it.deny(requestId!!); if (viaIntent) it.discard(requestId!!) }
        super.onDestroy()
    }

    companion object {
        const val ACTION_PAIR = "io.ref12.medusa.action.PAIR"
        const val EXTRA_APP_NAME = "appName"
        const val EXTRA_TOKEN = "token"
        const val EXTRA_REQUEST = "requestId"
    }
}
