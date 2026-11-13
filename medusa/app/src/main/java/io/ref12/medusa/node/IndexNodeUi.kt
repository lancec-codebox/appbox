package io.ref12.medusa.node

import android.text.InputType
import android.view.View
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import io.ref12.medusa.MainActivity
import io.ref12.medusa.R
import io.ref12.medusa.TrackUi
import io.ref12.medusa.Ui
import io.ref12.medusa.app
import java.text.DateFormat
import java.util.Date

/** The "Index node" group of the settings screen. */
object IndexNodeUi {
    fun build(a: MainActivity, card: LinearLayout) {
        val s = NodeSettings(a)
        fun field(hint: String, value: String, tg: String, type: Int = InputType.TYPE_CLASS_TEXT) = EditText(a).apply {
            this.hint = hint; setText(value); tag = tg; contentDescription = hint; textSize = 14f; inputType = type; setSingleLine()
            setTextColor(Ui.color(a, R.color.on_surface)); setHintTextColor(Ui.color(a, R.color.muted))
        }
        card.addView(Ui.muted(a, "Medusa can download videos for the drama index in the background and upload them to the shared storage container. It only runs the download stage."))
        card.addView(CheckBox(a).apply {
            text = "Enable the index node"; tag = "node-enabled"; isChecked = s.enabled; setTextColor(Ui.color(a, R.color.on_surface))
            setOnCheckedChangeListener { _, on -> s.enabled = on; NodeWorker.schedule(a, s) }
        })
        card.addView(Ui.muted(a, "Node name"), Ui.lp(-1, -2, 0f, 0, 8, 0, 0, a))
        val name = field("Node name", s.name, "node-name"); card.addView(name)
        card.addView(Ui.muted(a, "Run when"), Ui.lp(-1, -2, 0f, 0, 10, 0, 0, a))
        card.addView(TrackUi.radios(a, NodeConditions.values().map { it.label }, NodeConditions.values().map { "node-cond:" + it.name }, s.conditions.ordinal) {
            s.conditions = NodeConditions.values()[it]; NodeWorker.schedule(a, s)
        })
        card.addView(Ui.muted(a, "Daily cap on downloads"), Ui.lp(-1, -2, 0f, 0, 8, 0, 0, a))
        val cap = field("20", s.dailyCap.toString(), "node-cap", InputType.TYPE_CLASS_NUMBER); card.addView(cap)
        card.addView(Ui.muted(a, "Container SAS URL (paste it, or scan its QR code). It is kept encrypted with an Android Keystore key and never shown or logged."), Ui.lp(-1, -2, 0f, 0, 10, 0, 0, a))
        val sasInfo = Ui.muted(a, "", 12f)
        val sasField = field("https://<account>.blob.core.windows.net/<container>?...", "", "node-sas", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        card.addView(sasField)
        card.addView(sasInfo)
        val status = Ui.muted(a, "", 13f)
        fun refresh() {
            val u = s.vault.load()
            sasInfo.text = if (u == null) "No SAS saved" else "Saved: " + u.container + " - can " + SasUrl.describe(u.permissions) + (u.expiry?.let { " - expires $it" } ?: "")
            val recent = s.recent()
            status.text = "Node id: " + s.nodeId + "\nControl: " + (s.lastControl.ifEmpty { "not read yet" }) + "\nLast run: " +
                (if (s.lastRunAt == 0L) "never" else DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date(s.lastRunAt)) + " - " + s.lastRun) +
                "\nDownloads today: " + s.doneToday(NodeRunner.day(System.currentTimeMillis())) + " of " + s.dailyCap +
                (if (s.backoffUntil() > System.currentTimeMillis()) "\nBacking off until " + DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(s.backoffUntil())) else "") +
                "\nRecent jobs:" + (if (recent.isEmpty()) " none" else recent.take(8).joinToString("") { "\n  " + it.jobId + " " + (it.title?.take(30) ?: "") + " - " + it.result + (it.message?.let { m -> " (" + m.take(60) + ")" } ?: "") })
        }
        fun save() {
            s.name = name.text.toString()
            s.dailyCap = cap.text.toString().toIntOrNull() ?: 20
            val t = sasField.text.toString().trim()
            if (t.isNotEmpty()) {
                if (SasUrl.parse(t) == null) { Toast.makeText(a, "That is not a container SAS URL", Toast.LENGTH_LONG).show(); return }
                s.vault.save(t); sasField.setText("")
            }
            NodeWorker.schedule(a, s); refresh(); Toast.makeText(a, "Saved", Toast.LENGTH_SHORT).show()
        }
        card.addView(Ui.button(a, "Save") { save() }, Ui.lp(-2, -2, 0f, 0, 6, 0, 0, a))
        card.addView(Ui.button(a, "Scan QR code") {
            try {
                com.google.mlkit.vision.codescanner.GmsBarcodeScanning.getClient(a).startScan()
                    .addOnSuccessListener { b -> val v = b.rawValue ?: ""; if (SasUrl.parse(v) != null) { sasField.setText(v); save() } else Toast.makeText(a, "That QR code is not a container SAS URL", Toast.LENGTH_LONG).show() }
                    .addOnFailureListener { e -> Toast.makeText(a, "Scan failed: " + Redact.sas(e.message), Toast.LENGTH_LONG).show() }
            } catch (e: Exception) { Toast.makeText(a, "No QR scanner on this device", Toast.LENGTH_LONG).show() }
        }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))
        card.addView(Ui.button(a, "Test connection") {
            val u = s.vault.load()
            if (u == null) { Toast.makeText(a, "Save a SAS first", Toast.LENGTH_SHORT).show(); return@button }
            status.text = "Testing\u2026"
            Thread {
                val msg = try {
                    val c = BlobClient(u)
                    val n = c.listNames("index/jobs/download/").size
                    val ctl = ControlWatcher(c).refresh()
                    s.lastControl = NodeRunner.describe(ctl)
                    "Connected. index/jobs/download/ has $n blobs.\nPermissions (sp=" + u.permissions + "): " + SasUrl.describe(u.permissions) +
                        (if (!u.can('f')) "\nNo 'f': jobs are found by listing with tags instead." else "") +
                        (if (listOf('r', 'c', 'w', 'l').any { !u.can(it) }) "\nThe node needs r, c, w and l at least." else "") + "\nControl: " + s.lastControl
                } catch (e: Exception) { "Failed: " + Redact.sas(e.message) }
                a.runOnUiThread { refresh(); status.text = msg + "\n\n" + status.text }
            }.start()
        }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))
        card.addView(Ui.button(a, "Forget SAS", danger = true) { s.vault.clear(); NodeWorker.schedule(a, s); refresh() }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))
        card.addView(status, Ui.lp(-1, -2, 0f, 0, 8, 0, 0, a))
        refresh()
    }
}
