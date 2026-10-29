package io.ref12.medusa

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class UpdateLogicTest {
    @get:Rule val tmp = TemporaryFolder()
    private val v130 = Semver(1, 3, 0)
    private val HEX = "a".repeat(64)

    private fun release(tag: String, draft: Boolean = false, pre: Boolean = false, body: String = "notes",
                        assets: List<JSONObject>? = null): JSONObject {
        val v = tag.removePrefix("medusa-v")
        return JSONObject().put("tag_name", tag).put("draft", draft).put("prerelease", pre).put("body", body)
            .put("assets", JSONArray(assets ?: listOf(apk(v))))
    }
    private fun apk(v: String, digest: String? = null) = JSONObject().put("name", "medusa-$v.apk").put("size", 1234)
        .put("browser_download_url", "https://github.com/x/medusa-$v.apk").also { if (digest != null) it.put("digest", digest) }
    private fun sum(v: String) = JSONObject().put("name", "medusa-$v.apk.sha256").put("browser_download_url", "https://github.com/x/medusa-$v.apk.sha256")
    private fun pick(vararg r: JSONObject, installed: Semver = v130) = UpdateLogic.pickUpdate(JSONArray(r.toList()).toString(), installed)

    // ---- versions ----
    @Test fun semver_parse_and_compare() {
        assertEquals(Semver(1, 10, 2), Semver.parse("1.10.2"))
        assertEquals(Semver(1, 3, 0), Semver.parse("1.3.0-debug"))
        assertNull(Semver.parse("1.3"))
        assertNull(Semver.parse("v1.3.0"))
        assertNull(Semver.parse(null))
        assertTrue(Semver(1, 10, 0) > Semver(1, 9, 9)) // numeric, not text
        assertTrue(Semver(2, 0, 0) > Semver(1, 99, 99))
        assertEquals("1.3.1", Semver(1, 3, 1).toString())
    }

    // ---- release filtering ----
    @Test fun picks_the_newest_newer_release() {
        val o = pick(release("medusa-v1.3.1"), release("medusa-v1.4.0", body = "new stuff"), release("medusa-v1.2.0"))!!
        assertEquals(Semver(1, 4, 0), o.version)
        assertEquals("new stuff", o.notes)
        assertEquals("medusa-1.4.0.apk", o.apkName)
        assertEquals("https://github.com/x/medusa-1.4.0.apk", o.apkUrl)
    }

    @Test fun nothing_when_installed_is_current_or_newer() {
        assertNull(pick(release("medusa-v1.3.0")))
        assertNull(pick(release("medusa-v1.2.9")))
        assertNull(pick(release("medusa-v1.3.0"), installed = Semver(1, 3, 1)))
        assertNull(UpdateLogic.pickUpdate("[]", v130))
        assertNull(UpdateLogic.pickUpdate("not json", v130))
    }

    @Test fun drafts_prereleases_and_foreign_tags_are_ignored() {
        assertNull(pick(release("medusa-v1.4.0", draft = true)))
        assertNull(pick(release("medusa-v1.4.0", pre = true)))
        assertNull(pick(release("v1.4.0")))
        assertNull(pick(release("other-v1.4.0")))
        assertNull(pick(release("medusa-v1.4")))
        assertNull(pick(release("medusa-v1.4.0-rc1")))
        assertEquals(Semver(1, 3, 5), pick(release("medusa-v1.9.0", pre = true), release("medusa-v1.3.5"))!!.version)
    }

    @Test fun release_without_the_apk_asset_is_skipped() {
        assertNull(pick(release("medusa-v1.4.0", assets = emptyList())))
        assertNull(pick(release("medusa-v1.4.0", assets = listOf(apk("9.9.9")))))
        assertNull(pick(release("medusa-v1.4.0", assets = listOf(JSONObject().put("name", "medusa-1.4.0.apk").put("browser_download_url", "http://insecure/x.apk")))))
        // an incomplete newest release falls back to the next newer complete one
        assertEquals(Semver(1, 3, 2), pick(release("medusa-v1.4.0", assets = emptyList()), release("medusa-v1.3.2"))!!.version)
    }

    @Test fun digest_and_checksum_asset_are_picked_up() {
        val withDigest = pick(release("medusa-v1.4.0", assets = listOf(apk("1.4.0", "sha256:$HEX".uppercase().replace("SHA256", "sha256")))))!!
        assertEquals(HEX, withDigest.digest)
        val withSum = pick(release("medusa-v1.4.0", assets = listOf(apk("1.4.0"), sum("1.4.0"))))!!
        assertNull(withSum.digest)
        assertEquals("https://github.com/x/medusa-1.4.0.apk.sha256", withSum.checksumUrl)
        assertNull(pick(release("medusa-v1.4.0"))!!.checksumUrl)
    }

    // ---- checksums ----
    @Test fun digest_and_checksum_file_parsing() {
        assertEquals(HEX, UpdateLogic.parseDigest("sha256:$HEX"))
        assertNull(UpdateLogic.parseDigest("sha1:$HEX"))
        assertNull(UpdateLogic.parseDigest("sha256:abc"))
        assertNull(UpdateLogic.parseDigest(""))
        assertEquals(HEX, UpdateLogic.parseChecksumFile("$HEX  medusa-1.4.0.apk\n"))
        assertEquals(HEX, UpdateLogic.parseChecksumFile(HEX.uppercase()))
        assertNull(UpdateLogic.parseChecksumFile("<html>not found</html>"))
    }

    @Test fun sha256_and_verify() {
        val f = tmp.newFile("a.apk"); f.writeText("hello")
        val known = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"
        assertEquals(known, UpdateLogic.sha256(f))
        assertTrue(UpdateLogic.verify(f, known))
        assertTrue(UpdateLogic.verify(f, known.uppercase()))
        assertFalse(UpdateLogic.verify(f, HEX))
        assertFalse(UpdateLogic.verify(f, null))
    }

    // ---- throttle ----
    @Test fun automatic_check_throttle() {
        val day = UpdateLogic.CHECK_INTERVAL_MS
        assertTrue(UpdateLogic.shouldAutoCheck(true, 0, 1000))
        assertFalse(UpdateLogic.shouldAutoCheck(true, 1000, 1000 + day - 1))
        assertTrue(UpdateLogic.shouldAutoCheck(true, 1000, 1000 + day))
        assertFalse(UpdateLogic.shouldAutoCheck(false, 0, 10 * day)) // switched off
        assertTrue(UpdateLogic.shouldAutoCheck(true, 5000, 1000)) // clock went back
    }

    // ---- installer status mapping ----
    @Test fun install_status_mapping() {
        assertEquals(InstallOutcome.Kind.PENDING_USER_ACTION, InstallOutcome.fromStatus(-1, null).kind)
        assertEquals(InstallOutcome.Kind.SUCCESS, InstallOutcome.fromStatus(0, null).kind)
        assertEquals(InstallOutcome.Kind.ABORTED, InstallOutcome.fromStatus(3, null).kind)
        for (s in listOf(1, 2, 4, 5, 6, 7, 99)) assertEquals(InstallOutcome.Kind.FAILED, InstallOutcome.fromStatus(s, null).kind)
        assertTrue(InstallOutcome.fromStatus(5, null).message.contains("signing key"))
        assertTrue(InstallOutcome.fromStatus(6, "INSTALL_FAILED_INSUFFICIENT_STORAGE").message.contains("INSTALL_FAILED_INSUFFICIENT_STORAGE"))
    }

    @Test fun script_writes_a_sha256sum_file() {
        val script = listOf("scripts/make-release-assets.sh", "../scripts/make-release-assets.sh").map { File(it) }.firstOrNull { it.isFile }
        assertNotNull("script not found from " + File(".").absolutePath, script)
        val apk = tmp.newFile("medusa-9.9.9.apk"); apk.writeText("apk bytes")
        val p = ProcessBuilder("bash", script!!.absolutePath, apk.absolutePath).redirectErrorStream(true).start()
        p.inputStream.readBytes(); assertEquals(0, p.waitFor())
        val line = File(apk.path + ".sha256").readText()
        assertEquals(UpdateLogic.sha256(apk), UpdateLogic.parseChecksumFile(line))
        assertTrue(line.trim().endsWith("  medusa-9.9.9.apk")) // plain name: `sha256sum -c` works next to the apk
    }
}
