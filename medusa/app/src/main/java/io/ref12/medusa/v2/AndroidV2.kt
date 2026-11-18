package io.ref12.medusa.v2

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.BatteryManager
import android.os.StatFs
import io.ref12.medusa.BuildConfig
import io.ref12.medusa.GateLimits
import io.ref12.medusa.MedusaApp
import io.ref12.medusa.NotificationIcons
import io.ref12.medusa.app
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLRequest
import org.json.JSONObject
import java.io.File

/** Pairing records in the app's preferences. */
class PrefsKeyValue(ctx: Context) : KeyValue {
    private val prefs = ctx.getSharedPreferences("medusa_v2", Context.MODE_PRIVATE)
    override fun get(key: String) = prefs.getString(key, null)
    override fun put(key: String, value: String) { prefs.edit().putString(key, value).apply() }
}

/**
 * Starts the tools. yt-dlp goes through youtubedl-android; ffmpeg, ffprobe and Python are the binaries that library's packages
 * ship (libffmpeg.so, libffprobe.so, libpython.so in the app's native library folder), started with the same environment.
 */
class AndroidToolRunner(private val ctx: Context) : ToolRunner {
    private val native = File(ctx.applicationInfo.nativeLibraryDir)
    private val packages = File(File(ctx.noBackupFilesDir, "youtubedl-android"), "packages")

    private fun binary(tool: String): File? {
        val name = when (tool) { "ffmpeg" -> "libffmpeg.so"; "ffprobe" -> "libffprobe.so"; "python" -> "libpython.so"; else -> return null }
        return File(native, name).takeIf { it.exists() }
    }

    fun environment(): Map<String, String> {
        val ff = File(packages, "ffmpeg"); val py = File(packages, "python")
        return mapOf(
            "LD_LIBRARY_PATH" to listOf(ff.path + "/usr/lib", py.path + "/usr/lib", native.path).joinToString(":"),
            "PYTHONHOME" to py.path + "/usr",
            "SSL_CERT_FILE" to py.path + "/usr/etc/tls/cert.pem",
            "HOME" to ctx.cacheDir.path,
            "TMPDIR" to ctx.cacheDir.path,
        )
    }

    fun available(tool: String) = when (tool) {
        "yt-dlp" -> true
        "python" -> binary(tool) != null && File(packages, "python/usr").isDirectory
        else -> binary(tool) != null
    }

    override fun run(runId: String, tool: String, args: List<String>, workDir: File?, ctl: RunControl): Int {
        val app = ctx.app
        if (tool == V2Router.UPDATE_TOOL) {
            ctl.emit(app.fetcher.update())
            return 0
        }
        app.fetcher.ensureInit() // also unpacks ffmpeg and Python
        if (tool == "yt-dlp") return ytDlp(runId, args, workDir, ctl)
        val bin = binary(tool) ?: throw IllegalStateException("$tool is not available")
        val pb = ProcessBuilder(listOf(bin.path) + args).redirectErrorStream(true)
        pb.environment().putAll(environment())
        workDir?.let { pb.directory(it) }
        val p = pb.start()
        ctl.setKiller { p.destroy(); Thread { try { if (!p.waitFor(3, java.util.concurrent.TimeUnit.SECONDS)) p.destroyForcibly() } catch (e: Exception) { } }.start() }
        p.inputStream.bufferedReader().useLines { for (l in it) ctl.emit(l) }
        return p.waitFor()
    }

    private fun ytDlp(runId: String, args: List<String>, workDir: File?, ctl: RunControl): Int {
        val req = YoutubeDLRequest(emptyList<String>())
        // yt-dlp has no working-folder setting here, so the workspace is its home folder for outputs
        if (workDir != null) { req.addOption("--paths", "home:" + workDir.path); req.addOption("--paths", "temp:" + workDir.path) }
        req.addCommands(args)
        ctl.setKiller { try { YoutubeDL.getInstance().destroyProcessById(runId) } catch (e: Throwable) { } }
        return try {
            YoutubeDL.getInstance().execute(req, runId) { _, _, line -> if (line.isNotEmpty()) for (l in line.lines()) ctl.emit(l) }
            0
        } catch (e: YoutubeDL.CanceledException) { -1
        } catch (e: com.yausername.youtubedl_android.YoutubeDLException) { ctl.emit("yt-dlp failed: " + (e.message ?: "").lines().lastOrNull { it.isNotBlank() }); 1 }
    }

    /** First output line of "tool version flag", null when it cannot run. */
    fun versionOf(tool: String, flag: String): String? = try {
        val bin = binary(tool) ?: return null
        val pb = ProcessBuilder(bin.path, flag).redirectErrorStream(true)
        pb.environment().putAll(environment())
        val p = pb.start()
        val out = p.inputStream.bufferedReader().readLine()
        p.destroy()
        out?.trim()
    } catch (e: Exception) { null }
}

/** Everything /v2 needs on the device, built once per app. */
class V2Services(private val ctx: Context) {
    val pairing = PairingStore(PrefsKeyValue(ctx))
    private val runner = AndroidToolRunner(ctx)
    val workspaces = WorkspaceStore(File(ctx.filesDir, "v2/workspaces"))
    val limits = V2Limits({ GateLimits(ctx.app.settings.heavyRuns, ctx.app.settings.lightRuns) }, maxQueued = 20)
    val runs = RunManager(runner, ctx.app.gate, limits.maxQueued, workDir = { workspaces.dir(it) }, onFinished = { it.workspace?.let(workspaces::touch) })
    @Volatile private var versionCache: Map<String, String?>? = null
    @Volatile private var lastCleanup = 0L

    private val host = object : V2Host {
        override fun tools(): List<ToolInfo> {
            val v = versionCache ?: synchronized(this@V2Services) {
                versionCache ?: mapOf(
                    "yt-dlp" to ctx.app.fetcher.version().takeIf { it != "unknown" },
                    "ffmpeg" to runner.versionOf("ffmpeg", "-version"),
                    "ffprobe" to runner.versionOf("ffprobe", "-version"),
                    "python" to runner.versionOf("python", "--version"),
                ).also { versionCache = it }
            }
            return listOf("yt-dlp", "ffmpeg", "ffprobe", "python").map {
                ToolInfo(it, runner.available(it) && (it == "yt-dlp" || v[it] != null), v[it])
            }
        }
        override fun device() = DeviceInfo.read(ctx)
        override fun promptPairing(req: PairingStore.Request) = notifyPairing(req)
    }

    val router = V2Router(host, pairing, runs, workspaces, limits, BuildConfig.VERSION_NAME)

    /** Removes old workspaces; cheap enough to call often. */
    fun housekeeping(now: Long = System.currentTimeMillis()) {
        if (now - lastCleanup < 10 * 60_000L) return
        lastCleanup = now
        try { workspaces.cleanup { runs.isBusy(it) } } catch (e: Exception) { /* next time */ }
    }

    /** A heads-up notification that opens the Allow/Deny screen (an app in the background may not open a screen itself). */
    private fun notifyPairing(req: PairingStore.Request) {
        val nm = ctx.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Tool access requests", NotificationManager.IMPORTANCE_HIGH))
        val open = Intent(ctx, PairActivity::class.java).putExtra(PairActivity.EXTRA_REQUEST, req.id).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        val pi = PendingIntent.getActivity(ctx, req.id.hashCode(), open, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        nm.notify(req.id.hashCode(), Notification.Builder(ctx, CHANNEL)
            .setSmallIcon(NotificationIcons.res(NotificationIcons.forServer(0, false)))
            .setContentTitle("Allow " + req.appName + " to use Medusa's tools?")
            .setContentText("Tap to answer")
            .setContentIntent(pi).setAutoCancel(true).setTimeoutAfter(PairingStore.REQUEST_TTL_MS).build())
        try { ctx.startActivity(open) } catch (e: Exception) { /* the notification carries it */ }
    }

    companion object { const val CHANNEL = "medusa_v2_pairing" }
}

object DeviceInfo {
    fun read(ctx: Context): JSONObject {
        val o = JSONObject()
        val cm = ctx.getSystemService(ConnectivityManager::class.java)
        val caps = try { cm.getNetworkCapabilities(cm.activeNetwork) } catch (e: Exception) { null }
        val type = when {
            caps == null -> "none"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) && !caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) && !caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "vpn"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cellular"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ethernet"
            else -> "other"
        }
        o.put("network", type)
        o.put("vpn", caps?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true)
        o.put("metered", try { cm.isActiveNetworkMetered } catch (e: Exception) { JSONObject.NULL })
        val b = ctx.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        if (b != null) {
            val level = b.getIntExtra(BatteryManager.EXTRA_LEVEL, -1); val scale = b.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
            o.put("batteryPercent", if (level >= 0 && scale > 0) level * 100 / scale else JSONObject.NULL)
            o.put("charging", b.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0)
        } else { o.put("batteryPercent", JSONObject.NULL); o.put("charging", JSONObject.NULL) }
        o.put("freeBytes", try { StatFs(ctx.filesDir.path).availableBytes } catch (e: Exception) { JSONObject.NULL })
        return o
    }
}
