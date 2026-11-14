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
        card.addView(Ui.muted(a, "Stop after N more downloads (budget). It counts down with each finished download; at 0 the node stops claiming. Empty: no limit. The daily cap above stays a separate limit."), Ui.lp(-1, -2, 0f, 0, 10, 0, 0, a))
        val budget = field("no limit", s.budget?.toString() ?: "", "node-budget", InputType.TYPE_CLASS_NUMBER); card.addView(budget)
        card.addView(Ui.muted(a, "Schedule (cron, 5 fields, phone time) and window"), Ui.lp(-1, -2, 0f, 0, 10, 0, 0, a))
        val schedule = field("0 1 * * *", s.schedule, "node-schedule"); card.addView(schedule)
        val window = field("3h", s.window, "node-window"); card.addView(window)
        card.addView(Ui.muted(a, "Example: \"0 1 * * *\" with \"3h\" claims new jobs only between 01:00 and 04:00; a download in progress finishes. Window is like 90m, 3h, 1d (1h when empty). Empty schedule: any time. Wi-Fi and charging still apply. A budget, schedule or window set for this node in control.json overrides these.", 12f))
        val limitsInfo = Ui.muted(a, "", 12f); card.addView(limitsInfo)
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
            limitsInfo.text = if (s.lastLimits.isNotEmpty()) "Now: " + s.lastLimits else {
                val e = RunLimits.resolve(s.localLimits(), Control.RUN, s.nodeId)
                val left = RunLimits.remaining(e, s)
                "Now: " + (if (left != null) "budget left $left" else "no budget") + "; " + (e.schedule?.let { sc ->
                    sc.nextStart(System.currentTimeMillis(), java.time.ZoneId.systemDefault())?.let { "next window " + DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date(it)) } } ?: "any time")
            }
            status.text = "Node id: " + s.nodeId + "\nControl: " + (s.lastControl.ifEmpty { "not read yet" }) + "\nLast run: " +
                (if (s.lastRunAt == 0L) "never" else DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date(s.lastRunAt)) + " - " + s.lastRun) +
                "\nDownloads today: " + s.doneToday(NodeRunner.day(System.currentTimeMillis())) + " of " + s.dailyCap +
                (if (s.backoffUntil() > System.currentTimeMillis()) "\nBacking off until " + DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(s.backoffUntil())) else "") +
                "\nRecent jobs:" + (if (recent.isEmpty()) " none" else recent.take(8).joinToString("") { "\n  " + it.jobId + " " + (it.title?.take(30) ?: "") + " - " + it.result + (it.message?.let { m -> " (" + m.take(60) + ")" } ?: "") })
        }
        fun save() {
            s.name = name.text.toString()
            s.dailyCap = cap.text.toString().toIntOrNull() ?: 20
            val bt = budget.text.toString().trim()
            if (bt.isNotEmpty() && RunLimits.parseBudget(bt) == null) { Toast.makeText(a, "Budget must be a number, or empty", Toast.LENGTH_LONG).show(); return }
            val sc = schedule.text.toString().trim(); val wd = window.text.toString().trim()
            if (sc.isNotEmpty() && Cron.parse(sc) == null) { Toast.makeText(a, "Schedule must be 5 cron fields, like 0 1 * * *", Toast.LENGTH_LONG).show(); return }
            if (wd.isNotEmpty() && Durations.parse(wd) == null) { Toast.makeText(a, "Window must look like 90m, 3h or 1d", Toast.LENGTH_LONG).show(); return }
            s.budget = RunLimits.parseBudget(bt); s.schedule = sc; s.window = wd; s.lastLimits = ""
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
        sourcesSection(a, s, card)
        refresh()
    }

    /** Sources: follow a playlist or channel (a watch job any node can poll), and the list of followed sources with a remove button each. */
    private fun sourcesSection(a: MainActivity, s: NodeSettings, card: LinearLayout) {
        card.addView(Ui.header(a, "Sources"), Ui.lp(-1, -2, 0f, 0, 16, 0, 0, a))
        card.addView(Ui.muted(a, "Followed playlists and channels. Medusa only adds them; whichever node runs the watch stage polls them. Needs a SAS with r, c, w, l, f (and d to remove)."))
        val url = EditText(a).apply {
            hint = "YouTube playlist or channel URL"; tag = "source-url"; contentDescription = hint; textSize = 14f; setSingleLine()
            setTextColor(Ui.color(a, R.color.on_surface)); setHintTextColor(Ui.color(a, R.color.muted))
        }
        card.addView(url)
        val follow = CheckBox(a).apply { text = "Follow channels of its videos"; tag = "source-follow-channels"; setTextColor(Ui.color(a, R.color.on_surface)) }
        card.addView(follow)
        card.addView(Ui.muted(a, "Check every"))
        var everyPick = 0
        card.addView(TrackUi.radios(a, WatchJobs.EVERY, WatchJobs.EVERY.map { "source-every:" + it }, 0) { everyPick = it })
        card.addView(Ui.muted(a, "Backfill: how many existing videos to download first (0: only new ones)"))
        val backfill = EditText(a).apply {
            hint = "0"; setText("0"); tag = "source-backfill"; contentDescription = "Backfill"; textSize = 14f; inputType = InputType.TYPE_CLASS_NUMBER; setSingleLine()
            setTextColor(Ui.color(a, R.color.on_surface)); setHintTextColor(Ui.color(a, R.color.muted))
        }
        card.addView(backfill)
        val msg = Ui.muted(a, "", 13f)
        val list = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; tag = "source-list" }
        fun client() = s.vault.load()?.let { BlobClient(it) }
        fun reload() {
            val c = client() ?: run { list.removeAllViews(); list.addView(Ui.muted(a, "Save a SAS first")); return }
            Thread {
                val r = try { Result.success(WatchJobs.list(c)) } catch (e: Exception) { Result.failure(e) }
                a.runOnUiThread {
                    list.removeAllViews()
                    r.fold({ all ->
                        if (all.isEmpty()) list.addView(Ui.muted(a, "No sources followed yet"))
                        for (src in all) {
                            val row = LinearLayout(a).apply { orientation = LinearLayout.HORIZONTAL; gravity = android.view.Gravity.CENTER_VERTICAL }
                            row.addView(Ui.muted(a, src.name + "\n" + src.kind + " \u00b7 every " + src.every + " \u00b7 polled " + (src.lastPolled ?: "never"), 13f), Ui.lp(0, -2, 1f))
                            row.addView(Ui.button(a, "Remove", danger = true) {
                                Thread {
                                    val m = try { WatchJobs.remove(c, src.jobId); "Removed " + src.name } catch (e: Exception) { "Remove failed: " + Redact.sas(e.message) }
                                    a.runOnUiThread { msg.text = m; reload() }
                                }.start()
                            })
                            list.addView(row, Ui.lp(-1, -2, 0f, 0, 4, 0, 0, a))
                        }
                    }, { e -> list.addView(Ui.muted(a, "Could not list sources: " + Redact.sas(e.message))) })
                }
            }.start()
        }
        card.addView(Ui.button(a, "Follow") {
            val c = client() ?: run { msg.text = "Save a SAS first"; return@button }
            val raw = url.text.toString().trim()
            if (raw.isEmpty()) { msg.text = "Paste a playlist or channel URL"; return@button }
            val opts = FollowOptions(WatchJobs.EVERY[everyPick], backfill.text.toString().toIntOrNull()?.coerceAtLeast(0) ?: 0, follow.isChecked)
            msg.text = "Following\u2026"
            Thread {
                val res = SourceFollow.run(c, raw, opts, System.currentTimeMillis()) { a.app.sources.channelId(it) }
                a.runOnUiThread { msg.text = followMessage(res); if (res is FollowResult.Created) url.setText(""); reload() }
            }.start()
        }, Ui.lp(-2, -2, 0f, 0, 6, 0, 0, a))
        card.addView(msg, Ui.lp(-1, -2, 0f, 0, 4, 0, 0, a))
        card.addView(list, Ui.lp(-1, -2, 0f, 0, 6, 0, 0, a))
        reload()
    }

    fun followMessage(r: FollowResult): String = when (r) {
        is FollowResult.Created -> "Following " + r.ref.jobId
        is FollowResult.Exists -> "Already followed: " + r.ref.jobId
        FollowResult.NotASource -> "That is not a YouTube playlist or channel URL"
        is FollowResult.Failed -> "Could not follow: " + r.message
    }
}
