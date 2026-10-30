package io.ref12.medusa

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.io.IOException
import java.util.concurrent.Executor

class UpdateManagerTest {
    @get:Rule val tmp = TemporaryFolder()

    private val apkBytes = "new apk".toByteArray()
    private val goodHash get() = UpdateLogic.sha256(apkBytes.inputStream())
    private var now = 10_000_000_000L
    private var auto = true
    private var last = 0L
    private var busy = false
    private var releases = ""
    private var checksumText = ""
    private var sourceFails = false
    private var downloadBytes = apkBytes
    private var canInstall = true
    private var installFails = false
    private val installed = ArrayList<File>()
    private var installedExisted = false
    private val queue = ArrayList<Runnable>()
    private val exec = Executor { queue.add(it) }
    private fun run() { while (queue.isNotEmpty()) queue.removeAt(0).run() }
    private var downloads = 0

    private val source = object : UpdateSource {
        override fun releasesJson(): String { if (sourceFails) throw IOException("offline"); return releases }
        override fun text(url: String) = checksumText
        override fun download(url: String, target: File, progress: (Double?) -> Unit) {
            downloads++
            target.parentFile!!.mkdirs(); target.writeBytes(downloadBytes); progress(0.5); progress(1.0)
        }
    }
    private val installer = object : ApkInstaller {
        override fun canInstall() = canInstall
        override fun install(apk: File) { if (installFails) throw IOException("no session"); installedExisted = apk.isFile; installed.add(apk) }
    }

    private fun mgr(version: String = "1.3.0") = UpdateManager(version, tmp.root, source, installer, exec, { now },
        { auto }, { last }, { last = it }, { busy })

    private fun setReleases(digest: String? = goodHash, withSumAsset: Boolean = false, v: String = "1.3.1", body: String = "Fixes") {
        val assets = JSONArray().put(JSONObject().put("name", "medusa-$v.apk").put("browser_download_url", "https://g/medusa-$v.apk")
            .also { if (digest != null) it.put("digest", "sha256:$digest") })
        if (withSumAsset) assets.put(JSONObject().put("name", "medusa-$v.apk.sha256").put("browser_download_url", "https://g/medusa-$v.apk.sha256"))
        releases = JSONArray().put(JSONObject().put("tag_name", "medusa-v$v").put("draft", false).put("prerelease", false).put("body", body).put("assets", assets)).toString()
    }

    private fun updateToInstalling(m: UpdateManager) { m.check(); run(); m.update(); run() }

    @Test fun check_offers_a_newer_release_and_remembers_the_time() {
        setReleases(); val m = mgr(); m.check(); run()
        val s = m.state as UpdateState.Available
        assertEquals("1.3.1", s.offer.version.toString()); assertEquals("Fixes", s.offer.notes)
        assertEquals(now, last)
    }

    @Test fun up_to_date_and_manual_check_failure_message() {
        setReleases(v = "1.3.0"); val m = mgr(); m.check(); run()
        assertEquals(UpdateState.UpToDate("1.3.0"), m.state)
        sourceFails = true; m.check(); run()
        assertTrue((m.state as UpdateState.Failed).message.contains("offline"))
    }

    @Test fun automatic_check_is_throttled_to_once_a_day_and_can_be_switched_off() {
        setReleases(); val m = mgr()
        m.checkIfDue(); run(); assertTrue(m.state is UpdateState.Available)
        val m2 = mgr(); m2.checkIfDue(); run(); assertEquals(UpdateState.Idle, m2.state) // checked just now
        now += UpdateLogic.CHECK_INTERVAL_MS
        auto = false; val m3 = mgr(); m3.checkIfDue(); run(); assertEquals(UpdateState.Idle, m3.state) // off
        auto = true; val m4 = mgr(); m4.checkIfDue(); run(); assertTrue(m4.state is UpdateState.Available)
    }

    @Test fun an_automatic_check_that_fails_stays_quiet() {
        sourceFails = true; val m = mgr(); m.checkIfDue(); run()
        assertEquals(UpdateState.Idle, m.state)
    }

    @Test fun later_hides_the_offer_for_automatic_checks_but_not_manual_ones() {
        setReleases(); val m = mgr(); m.check(); run(); m.later()
        assertEquals(UpdateState.Idle, m.state)
        now += UpdateLogic.CHECK_INTERVAL_MS; m.checkIfDue(); run(); assertEquals(UpdateState.Idle, m.state)
        m.check(); run(); assertTrue(m.state is UpdateState.Available)
    }

    @Test fun update_downloads_verifies_with_the_digest_and_installs() {
        setReleases(); val m = mgr(); updateToInstalling(m)
        assertTrue(m.state is UpdateState.Installing)
        assertEquals(1, installed.size); assertTrue(installedExisted)
        assertEquals("medusa-1.3.1.apk", installed[0].name)
    }

    @Test fun checksum_asset_is_used_when_there_is_no_digest() {
        setReleases(digest = null, withSumAsset = true); checksumText = "$goodHash  medusa-1.3.1.apk\n"
        val m = mgr(); updateToInstalling(m)
        assertEquals(1, installed.size)
    }

    @Test fun mismatch_refuses_to_install_and_cleans_up() {
        setReleases(); downloadBytes = "tampered".toByteArray()
        val m = mgr(); updateToInstalling(m)
        assertTrue((m.state as UpdateState.Failed).message.contains("checksum"))
        assertTrue(installed.isEmpty())
        assertFalse(File(tmp.root, "updates/medusa-1.3.1.apk").exists())
    }

    @Test fun no_checksum_at_all_refuses() {
        setReleases(digest = null); val m = mgr(); updateToInstalling(m)
        assertTrue(m.state is UpdateState.Failed); assertTrue(installed.isEmpty())
        setReleases(digest = null, withSumAsset = true); checksumText = "<html>404</html>"
        val m2 = mgr(); updateToInstalling(m2)
        assertTrue(m2.state is UpdateState.Failed); assertTrue(installed.isEmpty())
    }

    @Test fun never_updates_while_a_download_runs() {
        setReleases(); busy = true; val m = mgr(); updateToInstalling(m)
        assertTrue((m.state as UpdateState.Failed).message.contains("download"))
        assertEquals(0, downloads)
        busy = false; m.update(); run(); assertEquals(1, installed.size) // "Try again" works
    }

    @Test fun missing_install_permission_waits_then_resumes_without_downloading_again() {
        setReleases(); canInstall = false; val m = mgr(); updateToInstalling(m)
        assertTrue(m.state is UpdateState.NeedsPermission); assertTrue(installed.isEmpty())
        m.resumeAfterPermission(); assertTrue(m.state is UpdateState.NeedsPermission) // still not allowed
        canInstall = true; m.resumeAfterPermission(); run()
        assertEquals(1, installed.size); assertEquals(1, downloads)
    }

    @Test fun session_could_not_start_is_a_failure_with_cleanup() {
        setReleases(); installFails = true; val m = mgr(); updateToInstalling(m)
        assertTrue((m.state as UpdateState.Failed).message.contains("Could not start"))
        assertFalse(File(tmp.root, "updates").exists())
    }

    @Test fun installer_callbacks_drive_the_state() {
        setReleases(); val m = mgr(); updateToInstalling(m)
        var confirmed = 0
        m.onInstallStatus(-1, null) { confirmed++ }
        assertEquals(1, confirmed); assertTrue(m.state is UpdateState.Installing)
        m.onInstallStatus(0, null) { confirmed++ }
        assertTrue(m.state is UpdateState.Done); assertFalse(File(tmp.root, "updates").exists())

        val m2 = mgr(); updateToInstalling(m2)
        m2.onInstallStatus(3, null) {}
        assertEquals("The update was cancelled", (m2.state as UpdateState.Failed).message)
        val m3 = mgr(); updateToInstalling(m3)
        m3.onInstallStatus(5, null) {}
        assertTrue((m3.state as UpdateState.Failed).message.contains("signing key"))
        assertEquals(1, confirmed)
    }

    @Test fun repeated_checks_do_not_run_in_parallel() {
        setReleases(); val m = mgr(); m.check(); m.check(); assertEquals(1, queue.size); run()
    }
}
