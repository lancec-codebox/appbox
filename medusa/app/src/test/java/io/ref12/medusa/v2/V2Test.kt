package io.ref12.medusa.v2

import io.ref12.medusa.HttpReply
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** A tool that does what the test script says. */
class FakeRunner : ToolRunner {
    @Volatile var script: (String, List<String>, File?, RunControl) -> Int = { _, _, _, _ -> 0 }
    val started = CountDownLatch(1)
    override fun run(runId: String, tool: String, args: List<String>, workDir: File?, ctl: RunControl): Int {
        started.countDown()
        return script(tool, args, workDir, ctl)
    }
}

class FakeHost : V2Host {
    val prompts = ArrayList<PairingStore.Request>()
    override fun tools() = listOf(ToolInfo("yt-dlp", true, "2025.01.01"), ToolInfo("ffmpeg", true, "ffmpeg 6.0"), ToolInfo("ffprobe", false, null), ToolInfo("python", true, "Python 3.11"))
    override fun device() = JSONObject().put("network", "wifi").put("vpn", false).put("metered", false).put("charging", true).put("batteryPercent", 80).put("freeBytes", 1234)
    override fun promptPairing(req: PairingStore.Request) { prompts.add(req) }
}

class V2Test {
    private lateinit var dir: File
    private lateinit var host: FakeHost
    private lateinit var kv: MemoryKeyValue
    private lateinit var pairing: PairingStore
    private lateinit var runner: FakeRunner
    private lateinit var runs: RunManager
    private lateinit var ws: WorkspaceStore
    private lateinit var router: V2Router
    private var now = 1_000_000L
    private lateinit var token: String

    @Before fun setUp() {
        dir = kotlin.io.path.createTempDirectory("v2test").toFile()
        host = FakeHost(); kv = MemoryKeyValue(); pairing = PairingStore(kv, { now })
        runner = FakeRunner()
        ws = WorkspaceStore(File(dir, "ws"), { now })
        runs = RunManager(runner, io.ref12.medusa.WorkGate { io.ref12.medusa.GateLimits(1, 1) }, maxQueued = 2, workDir = { ws.dir(it) }, secondMs = 20)
        router = V2Router(host, pairing, runs, ws, V2Limits(1, 2, maxUploadBytes = 1000), "1.10.0")
        token = pair()
    }

    @After fun tearDown() { dir.deleteRecursively() }

    private fun call(method: String, path: String, body: String? = null, tok: String? = token, headers: Map<String, String> = emptyMap(),
                     stream: ByteArray? = null): HttpReply {
        val h = HashMap(headers)
        if (tok != null) h["x-medusa-token"] = tok
        return router.handle(V2Request(method, path.substringBefore('?'), path.substringAfter('?', "").ifEmpty { null }, h, body,
            stream?.let { ByteArrayInputStream(it) }, stream?.size?.toLong() ?: -1))
    }
    private fun j(r: HttpReply) = JSONObject(r.body)

    private fun pair(): String {
        val r = call("POST", "/v2/pair", """{"appName":"Test app"}""", tok = null)
        assertEquals(200, r.status)
        val id = j(r).getString("requestId")
        val t = pairing.approve(id)!!
        return t
    }

    private fun newWorkspace() = j(call("POST", "/v2/workspaces")).getString("workspace")

    private fun waitDone(id: String): JSONObject {
        val run = runs.get(id)!!
        assertTrue("run did not finish", run.await(5000) { run.state.finished })
        return j(call("GET", "/v2/runs/$id"))
    }

    // ---- routing

    @Test fun routing() {
        assertEquals(404, call("GET", "/v2/nope").status)
        assertEquals(404, call("GET", "/v1/status").status)
        assertEquals(404, call("GET", "/v2").status)
        assertEquals(405, call("DELETE", "/v2/capabilities").status)
        assertEquals(405, call("GET", "/v2/runs").status)
        assertEquals(405, call("GET", "/v2/pair").status)
        assertEquals(405, call("POST", "/v2/device").status)
        assertEquals(404, call("GET", "/v2/runs/nothing").status)
        assertEquals(404, call("POST", "/v2/runs/nothing/cancel").status)
        assertEquals(404, call("GET", "/v2/workspaces/0123456789abcdef/files").status)
        assertEquals(200, call("GET", "/v2/capabilities/").status)
    }

    @Test fun capabilitiesAndDevice() {
        val c = j(call("GET", "/v2/capabilities"))
        assertEquals(2, c.getInt("api")); assertEquals("1.10.0", c.getString("medusa"))
        val tools = c.getJSONArray("tools")
        assertEquals("yt-dlp", tools.getJSONObject(0).getString("name"))
        assertEquals("2025.01.01", tools.getJSONObject(0).getString("version"))
        assertFalse(tools.getJSONObject(2).getBoolean("available"))
        assertEquals(1, c.getJSONObject("limits").getInt("maxConcurrentHeavyRuns"))
        assertEquals(80, j(call("GET", "/v2/device")).getInt("batteryPercent"))
    }

    @Test fun browsersAreRefused() {
        assertEquals(403, call("GET", "/v2/capabilities", headers = mapOf("origin" to "https://evil.example")).status)
    }

    // ---- auth

    @Test fun authRequiredAndRevoked() {
        for (p in listOf("/v2/capabilities", "/v2/device", "/v2/workspaces/0123456789abcdef/files")) {
            assertEquals(p, 401, call("GET", p, tok = null).status)
            assertEquals(p, 401, call("GET", p, tok = "mt_wrong").status)
        }
        assertEquals(401, call("POST", "/v2/runs", "{}", tok = null).status)
        assertEquals(401, call("POST", "/v2/workspaces", tok = null).status)
        assertEquals(200, call("GET", "/v2/capabilities").status)
        val app = pairing.list().single()
        assertTrue(pairing.revoke(app.id))
        assertEquals(401, call("GET", "/v2/capabilities").status)
        assertTrue(j(call("GET", "/v2/capabilities", tok = token)).optString("error").isEmpty().not())
    }

    @Test fun tokensAreStoredHashedAndSurviveRestart() {
        val stored = kv.get("apps")!!
        assertFalse(stored.contains(token))
        val again = PairingStore(kv, { now })
        assertEquals("Test app", again.authenticate(token)?.appName)
        assertNull(again.authenticate("mt_other"))
    }

    // ---- pairing

    @Test fun pairingFlow() {
        val r = call("POST", "/v2/pair", """{"appName":"  My   IndexNode "}""", tok = null)
        val id = j(r).getString("requestId")
        assertEquals("pending", j(r).getString("state"))
        assertEquals("My IndexNode", host.prompts.last().appName)
        assertEquals("pending", j(call("GET", "/v2/pair/$id", tok = null)).getString("state"))
        val t = pairing.approve(id)!!
        val polled = j(call("GET", "/v2/pair/$id", tok = null))
        assertEquals("approved", polled.getString("state"))
        assertEquals(t, polled.getString("token"))
        // the token is handed out once
        assertEquals(404, call("GET", "/v2/pair/$id", tok = null).status)
        assertEquals(200, call("GET", "/v2/capabilities", tok = t).status)
        assertTrue(t.startsWith("mt_") && t.length > 30)
    }

    @Test fun pairingDeniedExpiredAndLimited() {
        val id = j(call("POST", "/v2/pair", """{"appName":"A"}""", tok = null)).getString("requestId")
        pairing.deny(id)
        assertEquals("denied", j(call("GET", "/v2/pair/$id", tok = null)).getString("state"))
        assertNull(pairing.approve(id))
        val id2 = j(call("POST", "/v2/pair", """{"appName":"B"}""", tok = null)).getString("requestId")
        now += PairingStore.REQUEST_TTL_MS + 1
        assertEquals(404, call("GET", "/v2/pair/$id2", tok = null).status)
        assertNull(pairing.approve(id2))
        repeat(PairingStore.MAX_PENDING) { assertEquals(200, call("POST", "/v2/pair", """{"appName":"x$it"}""", tok = null).status) }
        assertEquals(429, call("POST", "/v2/pair", """{"appName":"one more"}""", tok = null).status)
        assertEquals(400, call("POST", "/v2/pair", """{"appName":""}""", tok = null).status)
        assertEquals(400, call("POST", "/v2/pair", "nonsense", tok = null).status)
    }

    @Test fun nameCleaning() {
        assertEquals("a b", PairingStore.cleanName("a\u0000\n b"))
        assertNull(PairingStore.cleanName("   "))
        assertNull(PairingStore.cleanName("x".repeat(61)))
    }

    // ---- workspaces and files

    @Test fun workspacePathEscapeRefused() {
        val id = newWorkspace()
        val outside = File(dir, "secret.txt").apply { writeText("secret") }
        File(dir, "ws").resolve(id).resolve("ok.txt").writeText("fine")
        assertEquals(200, call("GET", "/v2/workspaces/$id/files/ok.txt").status)
        for (bad in listOf("../secret.txt", "a/../../secret.txt", "%2e%2e/secret.txt", "sub/..", ".medusa-ws")) {
            // the server hands the router the decoded path, so test both literal forms
            assertEquals(bad, 400, call("GET", "/v2/workspaces/$id/files/" + bad.replace("%2e", ".")).status)
        }
        assertNull(ws.resolve(id, "/etc/passwd"))
        assertNull(ws.resolve(id, "..\\secret.txt"))
        assertNull(ws.resolve(id, "a/./b"))
        assertNull(WorkspaceStore.checkPath("a//b"))
        assertNull(ws.resolve("../" + dir.name, "x"))
        assertEquals(400, call("PUT", "/v2/workspaces/$id/files/../evil.txt", stream = "x".toByteArray()).status)
        assertFalse(File(dir, "ws/evil.txt").exists())
        assertEquals("secret", outside.readText())
    }

    @Test fun symlinkOutIsRefused() {
        val id = newWorkspace()
        val d = ws.dir(id)!!
        val secret = File(dir, "outside.txt").apply { writeText("secret") }
        try { java.nio.file.Files.createSymbolicLink(File(d, "link.txt").toPath(), secret.toPath()) } catch (e: Exception) { return }
        assertEquals(400, call("GET", "/v2/workspaces/$id/files/link.txt").status)
    }

    @Test fun uploadListDownloadDelete() {
        val id = newWorkspace()
        val up = call("PUT", "/v2/workspaces/$id/files/in/video.mp4", stream = "hello world".toByteArray())
        assertEquals(200, up.status); assertEquals(11, j(up).getInt("size"))
        val files = j(call("GET", "/v2/workspaces/$id/files")).getJSONArray("files")
        assertEquals(1, files.length())
        assertEquals("in/video.mp4", files.getJSONObject(0).getString("path"))
        val d = call("GET", "/v2/workspaces/$id/files/in/video.mp4")
        assertEquals(200, d.status); assertEquals("video/mp4", d.headers["Content-Type"]); assertEquals(11L, d.fileLength)
        assertEquals(413, call("PUT", "/v2/workspaces/$id/files/big.bin", stream = ByteArray(1001)).status)
        assertEquals(404, call("GET", "/v2/workspaces/$id/files/missing.txt").status)
        assertEquals(411, call("PUT", "/v2/workspaces/$id/files/x.txt").status)
        assertEquals(200, call("DELETE", "/v2/workspaces/$id").status)
        assertEquals(404, call("GET", "/v2/workspaces/$id/files").status)
        assertEquals(404, call("DELETE", "/v2/workspaces/$id").status)
    }

    @Test fun truncatedUploadLeavesNothing() {
        val id = newWorkspace()
        val r = router.handle(V2Request("PUT", "/v2/workspaces/$id/files/a.bin", null, mapOf("x-medusa-token" to token), null, ByteArrayInputStream(ByteArray(5)), 10))
        assertEquals(400, r.status)
        assertTrue(ws.list(id)!!.isEmpty())
        assertEquals(0, ws.dir(id)!!.listFiles()!!.count { it.name.startsWith(".medusa-upload") })
    }

    @Test fun rangeOnFiles() {
        val id = newWorkspace()
        File(ws.dir(id)!!, "data.bin").writeBytes(ByteArray(100) { it.toByte() })
        val p = call("GET", "/v2/workspaces/$id/files/data.bin", headers = mapOf("range" to "bytes=10-19"))
        assertEquals(206, p.status); assertEquals("bytes 10-19/100", p.headers["Content-Range"]); assertEquals(10L, p.fileLength); assertEquals(10L, p.fileOffset)
        assertEquals("bytes", p.headers["Accept-Ranges"])
        val tail = call("GET", "/v2/workspaces/$id/files/data.bin", headers = mapOf("range" to "bytes=-5"))
        assertEquals("bytes 95-99/100", tail.headers["Content-Range"])
        val open = call("GET", "/v2/workspaces/$id/files/data.bin", headers = mapOf("range" to "bytes=90-"))
        assertEquals(10L, open.fileLength)
        val bad = call("GET", "/v2/workspaces/$id/files/data.bin", headers = mapOf("range" to "bytes=500-"))
        assertEquals(416, bad.status); assertEquals("bytes */100", bad.headers["Content-Range"])
        assertEquals(200, call("GET", "/v2/workspaces/$id/files/data.bin").status)
    }

    @Test fun oldWorkspacesAreCleanedUp() {
        val old = newWorkspace(); now += 23 * 3600_000L
        val fresh = newWorkspace(); now += 2 * 3600_000L
        // the marker's modification time comes from the fake clock
        assertEquals(1, ws.cleanup())
        assertNull(ws.dir(old)); assertNotNull(ws.dir(fresh))
        now += 30 * 3600_000L
        assertEquals(0, ws.cleanup { it == fresh }); assertNotNull(ws.dir(fresh))
        assertEquals(1, ws.cleanup())
    }

    // ---- runs

    @Test fun runLifecycleWithProgress() {
        val id = newWorkspace()
        runner.script = { tool, args, wd, ctl ->
            File(wd, "out.txt").writeText(tool + " " + args.joinToString(" "))
            ctl.emit("[download]  10.5% of 3MiB"); ctl.emit("hello"); ctl.emit("[download]  50.0% of 3MiB")
            0
        }
        val r = call("POST", "/v2/runs", """{"tool":"yt-dlp","args":["-J","--flat-playlist","u"],"workspace":"$id","timeoutSec":30}""")
        assertEquals(200, r.status)
        val st = waitDone(j(r).getString("runId"))
        assertEquals("succeeded", st.getString("state")); assertEquals(0, st.getInt("exitCode"))
        assertEquals(100.0, st.getDouble("progress"), 0.0)
        assertEquals("[download]  50.0% of 3MiB", st.getJSONArray("tail").getString(2))
        assertEquals("yt-dlp -J --flat-playlist u", File(ws.dir(id)!!, "out.txt").readText())
        val log = call("GET", "/v2/runs/${j(r).getString("runId")}/log?from=1")
        assertEquals("hello\n[download]  50.0% of 3MiB\n", log.body)
        assertEquals("1", log.headers["X-Log-From"]); assertEquals("3", log.headers["X-Log-Next"]); assertEquals("succeeded", log.headers["X-Run-State"])
        assertEquals("", call("GET", "/v2/runs/${j(r).getString("runId")}/log?from=3").body)
    }

    @Test fun progressOnlyParsedForYtDlp() {
        val id = newWorkspace()
        runner.script = { _, _, _, ctl -> ctl.emit("[download]  10.5% of 3MiB"); 0 }
        val r = call("POST", "/v2/runs", """{"tool":"ffmpeg","args":[],"workspace":"$id"}""")
        assertTrue(waitDone(j(r).getString("runId")).isNull("progress"))
    }

    @Test fun failedRunKeepsExitCode() {
        val id = newWorkspace()
        runner.script = { _, _, _, ctl -> ctl.emit("boom"); 3 }
        val st = waitDone(j(call("POST", "/v2/runs", """{"tool":"ffmpeg","args":["-x"],"workspace":"$id"}""")).getString("runId"))
        assertEquals("failed", st.getString("state")); assertEquals(3, st.getInt("exitCode"))
    }

    @Test fun cancelRunningRun() {
        val id = newWorkspace()
        val stopped = CountDownLatch(1)
        runner.script = { _, _, _, ctl -> ctl.setKiller { stopped.countDown() }; stopped.await(5, TimeUnit.SECONDS); -9 }
        val rid = j(call("POST", "/v2/runs", """{"tool":"ffmpeg","args":[],"workspace":"$id"}""")).getString("runId")
        assertTrue(runner.started.await(5, TimeUnit.SECONDS))
        assertEquals(200, call("POST", "/v2/runs/$rid/cancel").status)
        assertEquals("cancelled", waitDone(rid).getString("state"))
        assertEquals(200, call("DELETE", "/v2/workspaces/$id").status)
    }

    @Test fun queuedRunCancelsAtOnceAndQueueIsLimited() {
        val id = newWorkspace()
        val release = CountDownLatch(1)
        runner.script = { _, _, _, _ -> release.await(5, TimeUnit.SECONDS); 0 }
        val body = """{"tool":"ffmpeg","args":[],"workspace":"$id"}"""
        val first = j(call("POST", "/v2/runs", body)).getString("runId")
        assertTrue(runner.started.await(5, TimeUnit.SECONDS))
        val second = j(call("POST", "/v2/runs", body))
        assertEquals("queued", second.getString("state"))
        call("POST", "/v2/runs", body)
        assertEquals(429, call("POST", "/v2/runs", body).status) // two queued behind one running
        assertEquals(409, call("DELETE", "/v2/workspaces/$id").status) // busy
        val c = j(call("POST", "/v2/runs/${second.getString("runId")}/cancel"))
        assertEquals("cancelled", c.getString("state"))
        release.countDown()
        assertEquals("succeeded", waitDone(first).getString("state"))
    }

    @Test fun timeoutStopsTheRun() {
        val id = newWorkspace()
        val stopped = CountDownLatch(1)
        runner.script = { _, _, _, ctl -> ctl.setKiller { stopped.countDown() }; stopped.await(5, TimeUnit.SECONDS); -9 }
        val rid = j(call("POST", "/v2/runs", """{"tool":"python","args":["-c","while True: pass"],"workspace":"$id","timeoutSec":1}""")).getString("runId")
        assertEquals("timed_out", waitDone(rid).getString("state"))
    }

    @Test fun throwingToolFailsTheRun() {
        val id = newWorkspace()
        runner.script = { _, _, _, _ -> throw IllegalStateException("no binary") }
        val st = waitDone(j(call("POST", "/v2/runs", """{"tool":"ffmpeg","args":[],"workspace":"$id"}""")).getString("runId"))
        assertEquals("failed", st.getString("state"))
        assertTrue(st.getJSONArray("tail").getString(0).contains("no binary"))
    }

    @Test fun runRequestValidation() {
        val id = newWorkspace()
        fun post(b: String) = call("POST", "/v2/runs", b).status
        assertEquals(400, post("nonsense"))
        assertEquals(400, post("""{"tool":"rm","args":[],"workspace":"$id"}"""))
        assertEquals(409, post("""{"tool":"ffprobe","args":[],"workspace":"$id"}""")) // unavailable
        assertEquals(400, post("""{"tool":"ffmpeg","workspace":"$id"}"""))
        assertEquals(400, post("""{"tool":"ffmpeg","args":[1],"workspace":"$id"}"""))
        assertEquals(404, post("""{"tool":"ffmpeg","args":[],"workspace":"0123456789abcdef"}"""))
        assertEquals(404, post("""{"tool":"ffmpeg","args":[]}"""))
        assertEquals(400, post("""{"tool":"ffmpeg","args":[],"workspace":"$id","timeoutSec":0}"""))
        assertEquals(400, post("""{"tool":"ffmpeg","args":[],"workspace":"$id","timeoutSec":999999}"""))
        assertEquals(200, post("""{"tool":"ffmpeg","args":[],"workspace":"$id"}"""))
    }

    @Test fun ytDlpUpdateRunsOnce() {
        val release = CountDownLatch(1)
        runner.script = { tool, _, _, ctl -> assertEquals(V2Router.UPDATE_TOOL, tool); release.await(5, TimeUnit.SECONDS); ctl.emit("Updated"); 0 }
        val a = j(call("POST", "/v2/tools/yt-dlp/update")).getString("runId")
        assertTrue(runner.started.await(5, TimeUnit.SECONDS))
        assertEquals(a, j(call("POST", "/v2/tools/yt-dlp/update")).getString("runId"))
        release.countDown()
        assertEquals("succeeded", waitDone(a).getString("state"))
    }

    @Test fun logCanWaitForNewLines() {
        val id = newWorkspace()
        val go = CountDownLatch(1)
        runner.script = { _, _, _, ctl -> go.await(5, TimeUnit.SECONDS); ctl.emit("late"); 0 }
        val rid = j(call("POST", "/v2/runs", """{"tool":"ffmpeg","args":[],"workspace":"$id"}""")).getString("runId")
        Thread { Thread.sleep(100); go.countDown() }.start()
        val log = call("GET", "/v2/runs/$rid/log?from=0&wait=5")
        assertEquals("late\n", log.body)
    }

    @Test fun queuedRunReportsPositionAndWhatItWaitsFor() {
        val id = newWorkspace()
        val release = CountDownLatch(1)
        runner.script = { _, _, _, _ -> release.await(5, TimeUnit.SECONDS); 0 }
        val first = j(call("POST", "/v2/runs", """{"tool":"ffmpeg","args":[],"workspace":"$id"}""")).getString("runId")
        assertTrue(runner.started.await(5, TimeUnit.SECONDS))
        val second = j(call("POST", "/v2/runs", """{"tool":"ffmpeg","args":[],"workspace":"$id","priority":"background"}""")).getString("runId")
        var q: JSONObject? = null
        for (n in 0 until 200) { q = j(call("GET", "/v2/runs/$second")).optJSONObject("queue")?.takeIf { it.getJSONArray("waitingFor").length() > 0 }; if (q != null) break; Thread.sleep(10) }
        val info = j(call("GET", "/v2/runs/$second"))
        assertEquals("queued", info.getString("state"))
        assertEquals("background", info.getString("priority"))
        assertEquals(1, q!!.getInt("position"))
        assertTrue(q.getJSONArray("waitingFor").getString(0).contains(first))
        assertTrue(q.getString("message").startsWith("Waiting for v2 run $first"))
        release.countDown(); waitDone(first); waitDone(second)
    }

    @Test fun lightRunDoesNotQueueBehindAHeavyOne() {
        val id = newWorkspace()
        val release = CountDownLatch(1)
        runner.script = { tool, _, _, _ -> if (tool == "ffmpeg") release.await(5, TimeUnit.SECONDS); 0 }
        val heavy = j(call("POST", "/v2/runs", """{"tool":"ffmpeg","args":[],"workspace":"$id"}""")).getString("runId")
        assertTrue(runner.started.await(5, TimeUnit.SECONDS))
        val light = j(call("POST", "/v2/runs", """{"tool":"yt-dlp","args":["--flat-playlist","u"],"workspace":"$id"}""")).getString("runId")
        assertEquals("succeeded", waitDone(light).getString("state"))
        assertEquals("light", j(call("GET", "/v2/runs/$light")).getString("weight"))
        release.countDown(); waitDone(heavy)
    }

    @Test fun badPriorityIsRefused() {
        val id = newWorkspace()
        assertEquals(400, call("POST", "/v2/runs", """{"tool":"ffmpeg","args":[],"workspace":"$id","priority":"urgent"}""").status)
    }
}
