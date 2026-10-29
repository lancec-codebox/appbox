package io.ref12.medusa

import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executor

/** Talks to GitHub; separate so the flow can be tested with a fake. */
interface UpdateSource {
    fun releasesJson(): String
    fun text(url: String): String
    /** Downloads to [target]; calls [progress] with the fraction when the size is known. */
    fun download(url: String, target: File, progress: (Double?) -> Unit)
}

class HttpUpdateSource : UpdateSource {
    private fun open(url: String): HttpURLConnection {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 15000; c.readTimeout = 30000; c.instanceFollowRedirects = true
        c.setRequestProperty("User-Agent", "Medusa-updater")
        c.setRequestProperty("Accept", "application/vnd.github+json")
        if (c.responseCode !in 200..299) throw IOException("HTTP " + c.responseCode)
        return c
    }

    override fun releasesJson() = open(UpdateLogic.RELEASES_URL).inputStream.use { it.readBytes().toString(Charsets.UTF_8) }
    override fun text(url: String) = open(url).inputStream.use { it.readBytes().take(4096).toByteArray().toString(Charsets.UTF_8) }

    override fun download(url: String, target: File, progress: (Double?) -> Unit) {
        val c = open(url)
        val total = c.contentLengthLong
        target.parentFile?.mkdirs()
        c.inputStream.use { ins ->
            target.outputStream().use { out ->
                val buf = ByteArray(64 * 1024)
                var done = 0L
                while (true) {
                    val n = ins.read(buf)
                    if (n < 0) break
                    out.write(buf, 0, n)
                    done += n
                    progress(if (total > 0) done.toDouble() / total else null)
                }
            }
        }
    }
}

/**
 * Check, offer, download, verify, install. All the decisions live here; Android parts come in through
 * [installer], [source] and the settings lambdas, so it runs in unit tests.
 */
class UpdateManager(
    private val installedVersion: String,
    private val cacheDir: File,
    private val source: UpdateSource,
    private val installer: ApkInstaller,
    private val executor: Executor,
    private val clock: () -> Long,
    private val autoEnabled: () -> Boolean,
    private val lastCheck: () -> Long,
    private val setLastCheck: (Long) -> Unit,
    private val busy: () -> Boolean, // a capture job is running
) {
    @Volatile var state: UpdateState = UpdateState.Idle
        private set
    @Volatile private var dismissed: String? = null
    @Volatile private var verifiedApk: File? = null

    private fun dir() = File(cacheDir, "updates")

    /** On app open: only when automatic checks are on and none ran in the last 24 h. */
    fun checkIfDue() {
        if (!UpdateLogic.shouldAutoCheck(autoEnabled(), lastCheck(), clock())) return
        check(manual = false)
    }

    fun check(manual: Boolean = true) {
        val s = state
        if (s is UpdateState.Checking || s is UpdateState.Downloading || s is UpdateState.Installing) return
        state = UpdateState.Checking
        executor.execute {
            try {
                val v = Semver.parse(installedVersion)
                    ?: run { state = UpdateState.Failed("Unknown installed version: $installedVersion"); return@execute }
                val offer = UpdateLogic.pickUpdate(source.releasesJson(), v)
                setLastCheck(clock())
                state = when {
                    offer == null -> UpdateState.UpToDate(installedVersion)
                    !manual && dismissed == offer.version.toString() -> UpdateState.Idle
                    else -> UpdateState.Available(offer)
                }
            } catch (e: Exception) {
                // an automatic check that fails (offline) stays quiet
                state = if (manual) UpdateState.Failed("Could not check for updates: " + (e.message ?: e.javaClass.simpleName)) else UpdateState.Idle
            }
        }
    }

    /** "Later": hides the offer until the next version (or a manual check). */
    fun later() {
        (state as? UpdateState.Available)?.let { dismissed = it.offer.version.toString() }
        state = UpdateState.Idle
    }

    /** Update: never while a capture job runs (installing restarts the app). */
    fun update() {
        val offer = when (val s = state) {
            is UpdateState.Available -> s.offer
            is UpdateState.Failed -> s.offer ?: return
            is UpdateState.NeedsPermission -> s.offer
            else -> return
        }
        if (busy()) { state = UpdateState.Failed("A capture is running. Update when it has finished.", offer); return }
        state = UpdateState.Downloading(offer, null)
        executor.execute { downloadAndInstall(offer) }
    }

    /** Called when the user comes back from the "install unknown apps" screen. */
    fun resumeAfterPermission() {
        val s = state as? UpdateState.NeedsPermission ?: return
        if (!installer.canInstall()) return
        val apk = verifiedApk
        if (apk != null && apk.isFile) { startInstall(s.offer, apk) } else update()
    }

    private fun downloadAndInstall(offer: UpdateOffer) {
        val apk = File(dir(), offer.apkName)
        try {
            dir().deleteRecursively()
            source.download(offer.apkUrl, apk) { f -> state = UpdateState.Downloading(offer, f) }
            val expected = offer.digest ?: offer.checksumUrl?.let { UpdateLogic.parseChecksumFile(source.text(it)) }
            if (expected == null) return fail("This release has no checksum, so the update was not installed", offer, apk)
            if (!UpdateLogic.verify(apk, expected)) return fail("The downloaded file does not match its checksum, so it was not installed", offer, apk)
            verifiedApk = apk
            if (!installer.canInstall()) { state = UpdateState.NeedsPermission(offer); return }
            startInstall(offer, apk)
        } catch (e: Exception) {
            fail("Download failed: " + (e.message ?: e.javaClass.simpleName), offer, apk)
        }
    }

    private fun startInstall(offer: UpdateOffer, apk: File) {
        state = UpdateState.Installing(offer, "Installing")
        try { installer.install(apk) } catch (e: Exception) { fail("Could not start the install: " + (e.message ?: ""), offer, apk) }
    }

    /** Result of the PackageInstaller session. [confirm] shows Android's own prompt when it is needed. */
    fun onInstallStatus(status: Int, systemMessage: String?, confirm: () -> Unit) {
        val o = InstallOutcome.fromStatus(status, systemMessage)
        val offer = (state as? UpdateState.Installing)?.offer
        when (o.kind) {
            InstallOutcome.Kind.PENDING_USER_ACTION -> { confirm(); state = UpdateState.Installing(offer ?: return, o.message) }
            InstallOutcome.Kind.SUCCESS -> { cleanup(); state = UpdateState.Done(o.message) }
            InstallOutcome.Kind.ABORTED -> { cleanup(); state = UpdateState.Failed(o.message, offer) }
            InstallOutcome.Kind.FAILED -> { cleanup(); state = UpdateState.Failed(o.message, offer) }
        }
    }

    private fun fail(msg: String, offer: UpdateOffer, apk: File) {
        cleanup()
        state = UpdateState.Failed(msg, offer)
    }

    fun cleanup() { verifiedApk = null; dir().deleteRecursively() }
}
