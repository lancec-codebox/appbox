package io.ref12.medusa

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.InputStream
import java.security.MessageDigest

/** Plain x.y.z version (no pre-release part: those are filtered out before). */
data class Semver(val major: Int, val minor: Int, val patch: Int) : Comparable<Semver> {
    override fun compareTo(other: Semver) = compareValuesBy(this, other, { it.major }, { it.minor }, { it.patch })
    override fun toString() = "$major.$minor.$patch"

    companion object {
        private val RE = Regex("""^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$""")

        /** "1.3.0", "1.3.0-debug" -> 1.3.0; anything else -> null. */
        fun parse(s: String?): Semver? = s?.trim()?.let { RE.matchEntire(it) }?.destructured
            ?.let { (a, b, c) -> runCatching { Semver(a.toInt(), b.toInt(), c.toInt()) }.getOrNull() }
    }
}

/** A newer release offered to the user. */
data class UpdateOffer(
    val version: Semver,
    val tag: String,
    val notes: String,
    val apkName: String,
    val apkUrl: String,
    val apkBytes: Long,
    /** Lower-case hex SHA-256 from the API's digest field, when the API gave one. */
    val digest: String?,
    /** The medusa-<version>.apk.sha256 asset, used when there is no digest. */
    val checksumUrl: String?,
)

object UpdateLogic {
    const val REPO = "lancec-codebox/appbox"
    const val RELEASES_URL = "https://api.github.com/repos/$REPO/releases?per_page=30"
    const val CHECK_INTERVAL_MS = 24L * 3600 * 1000
    private val TAG = Regex("""^medusa-v(\d+\.\d+\.\d+)$""")

    /**
     * The newest eligible release that is newer than [installed], or null. Eligible: not a draft, not a
     * pre-release, tag `medusa-v<x.y.z>`, with the asset `medusa-<x.y.z>.apk` (a release without it is skipped,
     * so an older complete release can still be offered only if it is newer than what is installed).
     */
    fun pickUpdate(releasesJson: String, installed: Semver): UpdateOffer? {
        val arr = try { JSONArray(releasesJson) } catch (e: Exception) { return null }
        var best: UpdateOffer? = null
        for (i in 0 until arr.length()) {
            val r = arr.optJSONObject(i) ?: continue
            if (r.optBoolean("draft", false) || r.optBoolean("prerelease", false)) continue
            val tag = r.optString("tag_name", "")
            val v = TAG.matchEntire(tag)?.groupValues?.get(1)?.let { Semver.parse(it) } ?: continue
            if (v <= installed) continue
            if (best != null && v <= best.version) continue
            val assets = r.optJSONArray("assets") ?: continue
            val apkName = "medusa-$v.apk"
            var apk: JSONObject? = null
            var sum: JSONObject? = null
            for (k in 0 until assets.length()) {
                val a = assets.optJSONObject(k) ?: continue
                when (a.optString("name")) { apkName -> apk = a; "$apkName.sha256" -> sum = a }
            }
            val a = apk ?: continue
            val url = a.optString("browser_download_url", "").takeIf { it.startsWith("https://") } ?: continue
            best = UpdateOffer(v, tag, r.optString("body", "").trim(), apkName, url, a.optLong("size", -1),
                parseDigest(a.optString("digest", "")), sum?.optString("browser_download_url")?.takeIf { it.startsWith("https://") })
        }
        return best
    }

    /** "sha256:<64 hex>" -> the hex, else null. */
    fun parseDigest(d: String?): String? {
        val m = Regex("""^sha256:([0-9a-fA-F]{64})$""").matchEntire(d?.trim() ?: "") ?: return null
        return m.groupValues[1].lowercase()
    }

    /** The hash in a `sha256sum` style file ("<hex>  name"), or null. */
    fun parseChecksumFile(text: String): String? =
        Regex("""^([0-9a-fA-F]{64})(\s|$)""").find(text.trim())?.groupValues?.get(1)?.lowercase()

    fun sha256(file: File): String = file.inputStream().use { sha256(it) }

    fun sha256(ins: InputStream): String {
        val md = MessageDigest.getInstance("SHA-256")
        val buf = ByteArray(64 * 1024)
        while (true) { val n = ins.read(buf); if (n < 0) break; md.update(buf, 0, n) }
        return md.digest().joinToString("") { "%02x".format(it) }
    }

    fun verify(file: File, expectedHex: String?): Boolean =
        expectedHex != null && sha256(file).equals(expectedHex.trim(), ignoreCase = true)

    /** Automatic checks: on, and none in the last 24 h. A manual check always runs. */
    fun shouldAutoCheck(enabled: Boolean, lastCheckMs: Long, nowMs: Long): Boolean =
        enabled && (lastCheckMs <= 0 || nowMs - lastCheckMs >= CHECK_INTERVAL_MS || nowMs < lastCheckMs)
}

/** What a PackageInstaller session callback means. Status numbers are PackageInstaller.STATUS_*. */
data class InstallOutcome(val kind: Kind, val message: String) {
    enum class Kind { PENDING_USER_ACTION, SUCCESS, FAILED, ABORTED }

    companion object {
        fun fromStatus(status: Int, systemMessage: String?): InstallOutcome {
            val extra = systemMessage?.takeIf { it.isNotBlank() }?.let { " ($it)" } ?: ""
            return when (status) {
                -1 -> InstallOutcome(Kind.PENDING_USER_ACTION, "Android needs your OK to install the update")
                0 -> InstallOutcome(Kind.SUCCESS, "Medusa was updated")
                3 -> InstallOutcome(Kind.ABORTED, "The update was cancelled")
                2 -> InstallOutcome(Kind.FAILED, "Android blocked the install (a policy or another app)$extra")
                4 -> InstallOutcome(Kind.FAILED, "Android rejected the update file as invalid$extra")
                5 -> InstallOutcome(Kind.FAILED, "The update conflicts with the installed app, probably a different signing key$extra")
                6 -> InstallOutcome(Kind.FAILED, "Not enough storage to install the update$extra")
                7 -> InstallOutcome(Kind.FAILED, "The update is not compatible with this phone$extra")
                else -> InstallOutcome(Kind.FAILED, "The install failed$extra")
            }
        }
    }
}

/** Starts a PackageInstaller session for a verified APK; results come back through [UpdateManager.onInstallStatus]. */
interface ApkInstaller {
    fun canInstall(): Boolean
    /** Throws java.io.IOException when the session cannot be created or written. */
    fun install(apk: File)
}

/** The update flow's state, as the UI shows it. */
sealed class UpdateState {
    object Idle : UpdateState()
    object Checking : UpdateState()
    data class UpToDate(val version: String) : UpdateState()
    data class Available(val offer: UpdateOffer) : UpdateState()
    data class Downloading(val offer: UpdateOffer, val fraction: Double?) : UpdateState()
    data class NeedsPermission(val offer: UpdateOffer) : UpdateState()
    data class Installing(val offer: UpdateOffer, val message: String) : UpdateState()
    data class Done(val message: String) : UpdateState()
    data class Failed(val message: String, val offer: UpdateOffer? = null) : UpdateState()
}
