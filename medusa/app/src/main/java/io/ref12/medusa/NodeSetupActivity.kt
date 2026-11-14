package io.ref12.medusa

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.widget.Toast
import io.ref12.medusa.node.NodeSetup
import io.ref12.medusa.node.NodeSettings
import io.ref12.medusa.node.NodeWorker
import io.ref12.medusa.node.SetupRejected

/**
 * Node setup from the desktop node: a medusa://node-setup link (the phone camera or a tap), a .medusa-node.json file (opened or shared),
 * or, from the Index node screen, the QR scanner / the file picker. It always asks before it changes anything.
 */
class NodeSetupActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        when (intent.action) {
            ACTION_PICK -> startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*"), PICK)
            ACTION_SCAN -> scan()
            Intent.ACTION_SEND -> intent.getStringExtra(Intent.EXTRA_TEXT)?.let { handleText(it) }
                ?: (intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))?.let { readFile(it) } ?: finish()
            else -> intent.data?.let { if (it.scheme == "medusa") handleText(it.toString()) else readFile(it) } ?: finish()
        }
    }

    private fun scan() {
        try {
            com.google.mlkit.vision.codescanner.GmsBarcodeScanning.getClient(this).startScan()
                .addOnSuccessListener { b -> handleText(b.rawValue ?: "") }
                .addOnCanceledListener { finish() }
                .addOnFailureListener { e -> fail("Scan failed: " + io.ref12.medusa.node.Redact.sas(e.message)) }
        } catch (e: Exception) { fail("No QR scanner on this device") }
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        val u = data?.data
        if (requestCode == PICK && resultCode == RESULT_OK && u != null) readFile(u) else finish()
    }

    private fun readFile(u: Uri) {
        Thread {
            val r = try { Result.success(String(contentResolver.openInputStream(u)!!.use { it.readNBytes(NodeSetup.MAX_BYTES + 1) }, Charsets.UTF_8)) } catch (e: Exception) { Result.failure(e) }
            runOnUiThread { r.fold({ handleText(it) }, { fail("Could not read this file") }) }
        }.start()
    }

    private fun handleText(text: String) {
        val setup = try { NodeSetup.fromAny(text) } catch (e: SetupRejected) { fail(e.message ?: "Not a setup"); return }
        val s = NodeSettings(this)
        val lines = setup.summary(s.current())
        AlertDialog.Builder(this).setTitle("Set up the index node?").setMessage(lines.joinToString("\n"))
            .setPositiveButton("Apply") { _, _ ->
                try {
                    setup.apply(s); NodeWorker.schedule(this, s)
                    Toast.makeText(this, "Index node settings applied", Toast.LENGTH_LONG).show()
                } catch (e: Exception) { Toast.makeText(this, "Could not apply: " + io.ref12.medusa.node.Redact.sas(e.message), Toast.LENGTH_LONG).show() }
                finish()
            }
            .setNegativeButton("Cancel") { _, _ -> finish() }.setOnCancelListener { finish() }.show()
    }

    private fun fail(msg: String) {
        AlertDialog.Builder(this).setTitle("Index node setup").setMessage(msg).setPositiveButton("OK") { _, _ -> finish() }.setOnCancelListener { finish() }.show()
    }

    companion object {
        const val ACTION_PICK = "io.ref12.medusa.NODE_SETUP_PICK"
        const val ACTION_SCAN = "io.ref12.medusa.NODE_SETUP_SCAN"
        private const val PICK = 41
    }
}
