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
    /** Index node settings screen: configuration only. */
    fun buildSettings(a: MainActivity, card: LinearLayout) {
        val s = NodeSettings(a)
        // shown only when Android blocks this app's notifications; the button opens Medusa's notification settings
        val notifWarnView = LinearLayout(a).apply {
            orientation = LinearLayout.VERTICAL; tag = "node-notif-warning"
            addView(Ui.muted(a, "Notifications are off for Medusa, so the node cannot show its status. The node still runs."))
            addView(Ui.button(a, "Open notification settings") {
                a.startActivity(android.content.Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, a.packageName))
            })
            visibility = if (s.enabled && !StatusNotifier.notificationsAllowed(a)) View.VISIBLE else View.GONE
        }
        val notifWarn: View? = notifWarnView
        fun field(hint: String, value: String, tg: String, type: Int = InputType.TYPE_CLASS_TEXT) = EditText(a).apply {
            this.hint = hint; setText(value); tag = tg; contentDescription = hint; textSize = 14f; inputType = type; setSingleLine()
            setTextColor(Ui.color(a, R.color.on_surface)); setHintTextColor(Ui.color(a, R.color.muted))
        }
        card.addView(Ui.muted(a, "Medusa can download videos for the drama index in the background and upload them to the shared storage container. It only runs the download stage."))
        card.addView(CheckBox(a).apply {
            text = "Enable the index node"; tag = "node-enabled"; isChecked = s.enabled; setTextColor(Ui.color(a, R.color.on_surface))
            setOnCheckedChangeListener { _, on ->
                s.enabled = on; NodeWorker.schedule(a, s)
                // Android 13+: ask for the notification permission again when the node is turned on
                if (on && android.os.Build.VERSION.SDK_INT >= 33 && !StatusNotifier.notificationsAllowed(a)) a.requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 4217)
                notifWarn?.visibility = if (on && !StatusNotifier.notificationsAllowed(a)) View.VISIBLE else View.GONE
            }
        })
        card.addView(CheckBox(a).apply {
            text = "Show the status notification"; tag = "node-status-notification"; isChecked = s.statusNotification; setTextColor(Ui.color(a, R.color.on_surface))
            setOnCheckedChangeListener { _, on -> s.statusNotification = on; s.lastStatus = ""; NodeWorker.schedule(a, s) }
        })
        card.addView(notifWarnView)
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
        val testInfo = Ui.muted(a, "", 13f)
        fun refresh() {
            val u = s.vault.load()
            sasInfo.text = if (u == null) "No SAS saved" else "Saved: " + u.container + " - can " + SasUrl.describe(u.permissions) + (u.expiry?.let { " - expires $it" } ?: "")
            limitsInfo.text = if (s.lastLimits.isNotEmpty()) "Now: " + s.lastLimits else {
                val e = RunLimits.resolve(s.localLimits(), Control.RUN, s.nodeId)
                val left = RunLimits.remaining(e, s)
                "Now: " + (if (left != null) "budget left $left" else "no budget") + "; " + (e.schedule?.let { sc ->
                    sc.nextStart(System.currentTimeMillis(), java.time.ZoneId.systemDefault())?.let { "next window " + DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date(it)) } } ?: "any time")
            }
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
        // one scanner: a raw container SAS URL, a medusa://node-setup link or the https hand-off link, all through the same confirmation as the setup path
        card.addView(Ui.button(a, "Scan QR code") {
            a.startActivity(android.content.Intent(a, io.ref12.medusa.NodeSetupActivity::class.java).setAction(io.ref12.medusa.NodeSetupActivity.ACTION_SCAN))
        }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))
        card.addView(Ui.button(a, "Import settings file") {
            a.startActivity(android.content.Intent(a, io.ref12.medusa.NodeSetupActivity::class.java).setAction(io.ref12.medusa.NodeSetupActivity.ACTION_PICK))
        }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))
        card.addView(Ui.button(a, "Export settings") {
            // the same JSON the desktop node writes, without the storage key
            val send = android.content.Intent(android.content.Intent.ACTION_SEND).setType("text/plain")
                .putExtra(android.content.Intent.EXTRA_SUBJECT, "node.medusa-node.json").putExtra(android.content.Intent.EXTRA_TEXT, NodeSetup.export(s.current()))
            a.startActivity(android.content.Intent.createChooser(send, "Export node settings (no storage key)"))
        }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))
        card.addView(Ui.button(a, "Test connection") {
            val u = s.vault.load()
            if (u == null) { Toast.makeText(a, "Save a SAS first", Toast.LENGTH_SHORT).show(); return@button }
            testInfo.text = "Testing\u2026"
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
                a.runOnUiThread { refresh(); testInfo.text = msg }
            }.start()
        }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))
        card.addView(Ui.button(a, "Forget SAS", danger = true) { s.vault.clear(); NodeWorker.schedule(a, s); refresh() }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))
        card.addView(testInfo, Ui.lp(-1, -2, 0f, 0, 8, 0, 0, a))
        refresh()
    }

    /** Index node screen. Returns the function that refreshes its live parts (called by the page's tick). */
    fun buildMain(a: MainActivity, card: LinearLayout): () -> Unit {
        val s = NodeSettings(a)
        val line = Ui.text(a, "", 15f, bold = true).apply { tag = "node-status-line" }
        card.addView(line)
        val info = Ui.muted(a, "", 12f).apply { tag = "node-status-info" }
        card.addView(info, Ui.lp(-1, -2, 0f, 0, 4, 0, 0, a))
        card.addView(Ui.button(a, "Run now") {
            SafeClick.run("Run now", { Toast.makeText(a, it, Toast.LENGTH_LONG).show() }) {
                if (!s.enabled || !s.vault.has()) "Turn the node on and save a SAS in Index node settings first"
                else { NodeWorker.runNow(a, s); "Started one run (Wi-Fi, daily cap, budget and control still apply)" }
            }
        }, Ui.lp(-2, -2, 0f, 0, 6, 0, 0, a))
        val crashLine = Ui.muted(a, "", 12f).apply { tag = "node-last-crash" }
        val crashRow = LinearLayout(a).apply { orientation = LinearLayout.HORIZONTAL; tag = "node-crash-row" }
        crashRow.addView(Ui.button(a, "Share crash") {
            SafeClick.run("Share", { Toast.makeText(a, it, Toast.LENGTH_LONG).show() }) {
                val r = CrashLog.read(a.filesDir) ?: return@run "No crash recorded"
                a.startActivity(android.content.Intent.createChooser(android.content.Intent(android.content.Intent.ACTION_SEND).setType("text/plain")
                    .putExtra(android.content.Intent.EXTRA_SUBJECT, "Medusa crash").putExtra(android.content.Intent.EXTRA_TEXT, r.text()), "Share crash")); null
            }
        }, Ui.lp(-2, -2, 0f, 0, 4, 8, 0, a))
        crashRow.addView(Ui.button(a, "Clear crash", danger = true) { CrashLog.clear(a.filesDir); crashLine.visibility = android.view.View.GONE; crashRow.visibility = android.view.View.GONE }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))
        card.addView(crashLine, Ui.lp(-1, -2, 0f, 0, 6, 0, 0, a)); card.addView(crashRow)
        card.addView(Ui.button(a, "Index node settings") { a.go(io.ref12.medusa.Dest.INDEX_SETTINGS) }, Ui.lp(-2, -2, 0f, 0, 4, 0, 0, a))
        card.addView(Ui.header(a, "Last runs"), Ui.lp(-1, -2, 0f, 0, 12, 0, 0, a))
        val runs = Ui.muted(a, "", 13f).apply { tag = "node-recent" }
        card.addView(runs)
        sourcesSection(a, s, card)
        fun refresh() {
            val now = System.currentTimeMillis()
            line.text = StatusNotifier.statusText(a, s)
            val crash = CrashLog.read(a.filesDir)
            crashLine.text = crash?.line() ?: ""; crashLine.visibility = if (crash != null) android.view.View.VISIBLE else android.view.View.GONE; crashRow.visibility = crashLine.visibility
            info.text = "Node id: " + s.nodeId + "\nControl: " + (s.lastControl.ifEmpty { "not read yet" }) +
                (if (s.lastLimits.isNotEmpty()) "\nNow: " + s.lastLimits else "") +
                "\nDownloads today: " + s.doneToday(NodeRunner.day(now)) + " of " + s.dailyCap +
                (if (s.backoffUntil() > now) "\nBacking off until " + DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(s.backoffUntil())) else "")
            val recent = s.recent()
            runs.text = "Last run: " + (if (s.lastRunAt == 0L) "never" else DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date(s.lastRunAt)) + " - " + s.lastRun) +
                "\nRecent jobs:" + (if (recent.isEmpty()) " none" else recent.take(8).joinToString("") { "\n  " + it.jobId + " " + (it.title?.take(30) ?: "") + " - " + it.result + (it.message?.let { m -> " (" + m.take(220) + ")" } ?: "") })
        }
        refresh()
        return ::refresh
    }

    const val ADMIN_URL = "https://apps--hexad-project.ref12.dev/local-apps/drama-admin/"

    /** Add by URL (with a priority) and "Added from this phone": what this phone added, its state in the index, remove and change priority. No list of the whole index: that is the Drama admin web app. */
    private fun sourcesSection(a: MainActivity, s: NodeSettings, card: LinearLayout) {
        card.addView(Ui.header(a, "Add to the index"), Ui.lp(-1, -2, 0f, 0, 16, 0, 0, a))
        card.addView(Ui.muted(a, "Paste or share a YouTube video, playlist or channel URL. Whichever node runs the watch stage polls sources. Needs a SAS with r, c, w, l, f (and d to remove)."))
        fun edit(hint: String, tg: String, type: Int, value: String = "") = EditText(a).apply {
            this.hint = hint; setText(value); tag = tg; contentDescription = hint; textSize = 14f; inputType = type; setSingleLine()
            setTextColor(Ui.color(a, R.color.on_surface)); setHintTextColor(Ui.color(a, R.color.muted))
        }
        val url = edit("YouTube video, playlist or channel URL", "source-url", InputType.TYPE_CLASS_TEXT); card.addView(url)
        val follow = CheckBox(a).apply { text = "Follow channels of its videos"; tag = "source-follow-channels"; setTextColor(Ui.color(a, R.color.on_surface)) }
        card.addView(follow)
        card.addView(Ui.muted(a, "Priority, -1000 to 1000 (higher downloads first, default 0)"))
        val prio = edit("0", "source-priority", InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_SIGNED, "0"); card.addView(prio)
        card.addView(Ui.muted(a, "Check every (playlists and channels)"))
        var everyPick = 0
        card.addView(TrackUi.radios(a, WatchJobs.EVERY, WatchJobs.EVERY.map { "source-every:" + it }, 0) { everyPick = it })
        card.addView(Ui.muted(a, "Backfill: how many existing videos to download first (0: only new ones)"))
        val backfill = edit("0", "source-backfill", InputType.TYPE_CLASS_NUMBER, "0"); card.addView(backfill)
        val msg = Ui.muted(a, "", 13f)
        val list = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; tag = "added-list" }
        fun client() = s.vault.load()?.let { BlobClient(it) }
        fun reload() {
            list.removeAllViews()
            val items = s.added.all()
            if (items.isEmpty()) { list.addView(Ui.muted(a, "Nothing added from this phone yet")); return }
            val c = client()
            for (it in items) {
                val row = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL }
                val state = Ui.muted(a, if (c == null) "Save a SAS to see its state" else "checking\u2026", 12f)
                row.addView(Ui.text(a, it.name, 14f)); 
                row.addView(Ui.muted(a, it.kind + " \u00b7 " + Ui.date(it.addedAt) + " \u00b7 priority " + it.priority, 12f))
                row.addView(state)
                val pf = edit("priority", "added-prio:" + it.jobId, InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_SIGNED, it.priority.toString())
                val btns = LinearLayout(a).apply { orientation = LinearLayout.HORIZONTAL; gravity = android.view.Gravity.CENTER_VERTICAL }
                btns.addView(pf, Ui.lp(0, -2, 1f))
                btns.addView(Ui.button(a, "Set priority") { _ ->
                    val p = pf.text.toString().trim().toIntOrNull()?.takeIf { v -> v in Prio.MIN..Prio.MAX }
                    if (p == null) { msg.text = "Priority must be a whole number from -1000 to 1000"; return@button }
                    val cl = client() ?: run { msg.text = "Save a SAS first"; return@button }
                    msg.text = "Changing priority\u2026"
                    Thread {
                        val m = try {
                            val listed = if (it.kind == "video") null else try { a.app.sources.listIds(SourceRef(it.kind, it.jobId.substring(3))) } catch (e: Exception) { null }
                            val r = AddedState.changePriority(cl, it, p, listed)
                            s.added.setPriority(it.jobId, p)
                            "Priority " + p + ": " + r.videos + " video(s), " + r.changed + " pending job(s) re-tagged" + (if (r.skipped.isNotEmpty()) "; skipped " + r.skipped.entries.joinToString { e -> e.value.toString() + " " + e.key } else "")
                        } catch (e: Exception) { "Could not change priority: " + Redact.sas(e.message) }
                        a.runOnUiThread { msg.text = m; reload() }
                    }.start()
                })
                btns.addView(Ui.button(a, "Remove", danger = true) { _ ->
                    val cl = client()
                    Thread {
                        val m = try { if (cl != null) AddedState.removeFromIndex(cl, it) else "Entry removed" } catch (e: Exception) { "Removed from this list; the index said: " + Redact.sas(e.message) }
                        s.added.remove(it.jobId)
                        a.runOnUiThread { msg.text = m; reload() }
                    }.start()
                })
                row.addView(btns)
                list.addView(row, Ui.lp(-1, -2, 0f, 0, 8, 0, 0, a))
                if (c != null) Thread { val t = AddedState.describe(it, c); a.runOnUiThread { state.text = t } }.start()
            }
        }
        card.addView(Ui.button(a, "Add") {
            val c = client() ?: run { msg.text = "Save a SAS first"; return@button }
            val raw = url.text.toString().trim()
            if (raw.isEmpty()) { msg.text = "Paste a video, playlist or channel URL"; return@button }
            val p = prio.text.toString().trim().ifEmpty { "0" }.toIntOrNull()?.takeIf { v -> v in Prio.MIN..Prio.MAX }
            if (p == null) { msg.text = "Priority must be a whole number from -1000 to 1000"; return@button }
            val opts = FollowOptions(WatchJobs.EVERY[everyPick], backfill.text.toString().toIntOrNull()?.coerceAtLeast(0) ?: 0, follow.isChecked, priority = p)
            msg.text = "Adding\u2026"
            Thread {
                val m = IndexAdd.add(c, s.added, raw, opts, p, System.currentTimeMillis(), { a.app.sources.channelId(it) }, { a.app.sources.videoChannelId(it) })
                a.runOnUiThread { msg.text = m; if (!m.startsWith("Could not") && !m.startsWith("That is not")) url.setText(""); reload() }
            }.start()
        }, Ui.lp(-2, -2, 0f, 0, 6, 0, 0, a))
        card.addView(msg, Ui.lp(-1, -2, 0f, 0, 4, 0, 0, a))
        card.addView(Ui.header(a, "Added from this phone"), Ui.lp(-1, -2, 0f, 0, 12, 0, 0, a))
        card.addView(list, Ui.lp(-1, -2, 0f, 0, 6, 0, 0, a))
        card.addView(Ui.button(a, "Open Drama admin") {
            try { a.startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(ADMIN_URL))) }
            catch (e: Exception) { Toast.makeText(a, "No browser to open it", Toast.LENGTH_LONG).show() }
        }, Ui.lp(-2, -2, 0f, 0, 8, 0, 0, a))
        reload()
    }

    fun followMessage(r: FollowResult): String = when (r) {
        is FollowResult.Created -> "Following " + r.ref.jobId
        is FollowResult.Exists -> "Already followed: " + r.ref.jobId
        FollowResult.NotASource -> "That is not a YouTube playlist or channel URL"
        is FollowResult.Failed -> "Could not follow: " + r.message
    }
}
