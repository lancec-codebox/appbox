package io.ref12.medusa.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.File
import java.nio.file.Files

class NodeNetworkTest {
    private val T4 = "fl=1\nh=1.1.1.1\nip=203.0.113.7\nloc=US\ncolo=SEA\n"
    private val T6 = "fl=1\nip=2001:db8:1:2:aaaa:bbbb:cccc:dddd\nloc=US\ncolo=SEA\n"
    private val NOW = "2026-11-17T12:00:00Z"

    private fun fetch(v4: String? = T4, v6: String? = T6, fallback: String? = null): (String) -> String? = { u ->
        when (u) { NetworkProbe.V4_URL -> v4; NetworkProbe.V6_URL -> v6; NetworkProbe.FALLBACK_URL -> fallback; else -> null }
    }

    @Test fun traceParsingReadsIpLocAndColo() {
        val t = NetworkProbe.parseTrace(T4)
        assertEquals("203.0.113.7", t.ip); assertEquals("US", t.loc); assertEquals("SEA", t.colo)
        assertNull(NetworkProbe.parseTrace(null).ip); assertNull(NetworkProbe.parseTrace("junk\nip=\n").ip)
    }

    @Test fun theIpv6PrefixIsTheFirst64BitsInCanonicalForm() {
        assertEquals("2001:db8:1:2::/64", NetworkProbe.ipv6Prefix64("2001:db8:1:2:aaaa:bbbb:cccc:dddd"))
        assertEquals("2001:db8::/64", NetworkProbe.ipv6Prefix64("2001:db8::1"))
        assertEquals("2001:db8:0:1::/64", NetworkProbe.ipv6Prefix64("2001:db8:0:1:2:3:4:5"))
        assertNull(NetworkProbe.ipv6Prefix64("203.0.113.7")); assertNull(NetworkProbe.ipv6Prefix64(null)); assertNull(NetworkProbe.ipv6Prefix64("nope"))
    }

    @Test fun bothFamiliesAreLookedUpSeparately() {
        val n = NetworkProbe.detect(fetch(), NOW, DeviceNet("wifi", true))
        assertEquals("203.0.113.7", n.ipv4); assertEquals("2001:db8:1:2:aaaa:bbbb:cccc:dddd", n.ipv6); assertEquals("2001:db8:1:2::/64", n.ipv6Prefix)
        assertEquals("US", n.country); assertEquals("SEA", n.colo); assertEquals("wifi", n.transport); assertEquals(true, n.vpn)
    }

    @Test fun aMissingFamilyIsNullAndTheFallbackFillsAFamily() {
        val noV6 = NetworkProbe.detect(fetch(v6 = null), NOW)
        assertEquals("203.0.113.7", noV6.ipv4); assertNull(noV6.ipv6); assertNull(noV6.ipv6Prefix)
        val blocked = NetworkProbe.detect(fetch(v4 = null, v6 = null, fallback = T4), NOW)
        assertEquals("203.0.113.7", blocked.ipv4); assertNull(blocked.ipv6)
        val none = NetworkProbe.detect({ throw java.io.IOException("timeout") }, NOW)
        assertNull(none.ipv4); assertNull(none.ipv6); assertEquals(NOW, none.checkedAt)
    }

    @Test fun sinceStaysWhileTheAddressPairStays() {
        val first = NetworkProbe.detect(fetch(), "2026-11-01T00:00:00Z")
        assertEquals("2026-11-01T00:00:00Z", NetworkProbe.detect(fetch(), NOW, DeviceNet(null, null), first).since)
        assertEquals(NOW, NetworkProbe.detect(fetch(v4 = T4.replace("203.0.113.7", "198.51.100.9")), NOW, DeviceNet(null, null), first).since)
    }

    @Test fun transportNames() {
        assertEquals("wifi", NetworkProbe.transportOf(true, false, false)); assertEquals("cellular", NetworkProbe.transportOf(false, true, false))
        assertEquals("ethernet", NetworkProbe.transportOf(false, false, true)); assertNull(NetworkProbe.transportOf(false, false, false))
    }

    // ---------------------------------------------------------------- the runner

    lateinit var api: FakeBlobs; lateinit var state: MemState; lateinit var work: File
    var now = 1_800_000_000_000L

    @Before fun up() { api = FakeBlobs(); state = MemState(); work = Files.createTempDirectory("node").toFile() }

    private fun runner(f: NodeFetcher, net: (String) -> String? = fetch(), device: DeviceNet = DeviceNet("wifi", true)) = NodeRunner(api, f,
        NodeConfig("phone-a1b2c3", "phone", "wifi+charging", "1.8.5", "android-arm64-v8a", 8, work), state,
        NodeEnv(clock = { now }, sleep = {}, gapMs = { 0 }, renewMs = 20, netFetch = net, deviceNet = { device }))

    @Test fun theNodeRecordCarriesTheNetworkAndKeepsItsOtherFields() {
        runner(FakeFetcher()).run()
        val o = JSONObject(api.blobText("index/nodes/phone-a1b2c3.json"))
        val n = o.getJSONObject("network")
        assertEquals("203.0.113.7", n.getString("ipv4")); assertEquals("2001:db8:1:2::/64", n.getString("ipv6Prefix")); assertEquals("US", n.getString("country")); assertEquals("SEA", n.getString("colo"))
        assertEquals("wifi", n.getString("transport")); assertTrue(n.getBoolean("vpn")); assertTrue(n.has("checkedAt")); assertTrue(n.has("since"))
        for (k in listOf("nodeId", "name", "kind", "version", "platform", "stages", "jobs", "threads", "conditions", "state", "control", "running", "lastSeen", "startedAt")) assertTrue(k, o.has(k))
        assertEquals("medusa", o.getString("kind"))
    }

    @Test fun sinceCarriesOverARunWithTheSameAddress() {
        runner(FakeFetcher()).run()
        val first = JSONObject(api.blobText("index/nodes/phone-a1b2c3.json")).getJSONObject("network").getString("since")
        now += 3_600_000
        runner(FakeFetcher()).run()
        assertEquals(first, JSONObject(api.blobText("index/nodes/phone-a1b2c3.json")).getJSONObject("network").getString("since"))
    }

    @Test fun aSuccessfulDownloadRecordsTheNetworkInTheJobAndDownloadedJson() {
        api.addJob("aaaaaaaaaaa", 1)
        runner(FakeFetcher()).run()
        val m = JSONObject(api.blobText("index/videos/aaaaaaaaaaa/downloaded.json")).getJSONObject("network")
        assertEquals("203.0.113.7", m.getString("ipv4")); assertEquals("2001:db8:1:2::/64", m.getString("ipv6Prefix")); assertTrue(m.getBoolean("vpn")); assertEquals("wifi", m.getString("transport"))
        val j = api.jobJson("aaaaaaaaaaa").getJSONObject("network")
        assertEquals("203.0.113.7", j.getString("ipv4")); assertEquals("wifi", j.getString("transport"))
    }

    @Test fun aFailedAttemptAndABotBlockRecordTheNetworkNextToLastError() {
        api.addJob("bbbbbbbbbbb", 1)
        runner(FakeFetcher { _, _, _ -> throw NodeFetchException(FailureKind.TRANSIENT, "disk full") }).run()
        val j = api.jobJson("bbbbbbbbbbb")
        // a transient failure goes back to pending, so the same run retries it until it has failed 3 times
        assertEquals("disk full", j.getString("lastError")); assertEquals(3, j.getInt("attempts")); assertEquals("failed", api.job("bbbbbbbbbbb").tags["idx_state"]); assertEquals("203.0.113.7", j.getJSONObject("network").getString("ipv4"))

        val api2 = FakeBlobs(); api2.addJob("ccccccccccc", 1); api = api2
        runner(FakeFetcher { _, _, _ -> throw NodeFetchException(FailureKind.BOT, "HTTP Error 429: Too Many Requests") }).run()
        val b = api.jobJson("ccccccccccc")
        assertTrue(b.getString("lastError").contains("429")); assertEquals(0, b.optInt("attempts", 0)); assertEquals("pending", api.job("ccccccccccc").tags["idx_state"])
        assertEquals("203.0.113.7", b.getJSONObject("network").getString("ipv4"))
        assertTrue(state.backoffUntil() > now) // the bot back-off stays
    }

    // ---------------------------------------------------------------- the per-IP cap

    private fun peer(id: String, ip4: String?, prefix: String?, downloads: Int, seenMs: Long, st: String = "working", stage: String = "download") =
        PeerNode(id, st, seenMs, NetInfo(ip4, null, prefix, null, null, null, null, NOW, NOW), downloads.let { if (stage == "download") it else 0 })

    @Test fun capCountsTheSameV4TheSameV64AndNothingElse() {
        val self = NetInfo("203.0.113.7", null, "2001:db8:1:2::/64", null, null, null, null, NOW, NOW)
        val t = 1_800_000_000_000L
        val peers = listOf(
            peer("same-v4", "203.0.113.7", null, 1, t), peer("same-v6", null, "2001:db8:1:2::/64", 2, t - 30_000),
            peer("other-64", "198.51.100.9", "2001:db8:1:3::/64", 3, t), peer("stale", "203.0.113.7", null, 4, t - 4 * 60_000),
            peer("offline", "203.0.113.7", null, 5, t, st = "offline"), peer("process-only", "203.0.113.7", null, 6, t, stage = "process"), peer("me", "203.0.113.7", null, 7, t))
        assertEquals(3, NetCap.countOthers(self, "me", peers, t))
        assertEquals(0, NetCap.countOthers(null, "me", peers, t))
        assertEquals(0, NetCap.countOthers(NetInfo(null, null, null, null, null, null, null, NOW, NOW), "me", peers, t))
    }

    @Test fun anUnsetCapMeansUnlimited() {
        assertTrue(NetCap.mayClaim(null, 99, 99)); assertTrue(NetCap.mayClaim(0, 99, 99))
        assertTrue(NetCap.mayClaim(2, 1, 0)); assertFalse(NetCap.mayClaim(2, 1, 1)); assertFalse(NetCap.mayClaim(2, 2, 0))
        assertNull(Control.parse("{\"mode\":\"run\"}").maxPerIp); assertNull(Control.parse("{\"downloads\":{\"maxPerIp\":null}}").maxPerIp); assertNull(Control.parse("{\"downloads\":{\"maxPerIp\":0}}").maxPerIp)
        assertEquals(2, Control.parse("{\"downloads\":{\"maxPerIp\":2}}").maxPerIp)
    }

    private fun addPeer() {
        val busy = JSONObject().put("nodeId", "laptop").put("state", "working").put("lastSeen", NodeRunner.iso(now))
            .put("running", org.json.JSONArray().put(JSONObject().put("jobId", "x").put("stage", "download")).put(JSONObject().put("jobId", "y").put("stage", "download")))
            .put("network", JSONObject().put("ipv4", "203.0.113.7"))
        api.blobs["index/nodes/laptop.json"] = FakeBlobs.B(busy.toString().toByteArray(), mapOf("idx_kind" to "node"))
    }

    @Test fun aCappedNodeWaitsWithoutAnAttemptOrABackoffAndAnUncappedOneClaims() {
        api.addJob("ddddddddddd", 1); addPeer(); api.setControl("{\"v\":1,\"mode\":\"run\",\"downloads\":{\"maxPerIp\":2}}")
        val f = FakeFetcher(); val s = runner(f).run()
        assertEquals(0, f.jobs.size); assertEquals(NodeRunner.IP_CAP_REASON + "2", s.endReason)
        assertEquals("pending", api.job("ddddddddddd").tags["idx_state"]); assertEquals(0, api.jobJson("ddddddddddd").optInt("attempts", 0)); assertEquals(0L, state.backoff)
        assertEquals(0, api.count("acquire:")) // nothing claimed
        // room for one more, or no cap at all: claims as today
        for (ctl in listOf("{\"v\":1,\"downloads\":{\"maxPerIp\":3}}", "{\"v\":1,\"downloads\":{\"maxPerIp\":null}}", "{\"v\":1}")) {
            api = FakeBlobs(); api.addJob("ddddddddddd", 1); addPeer(); api.setControl(ctl)
            val g = FakeFetcher(); runner(g).run(); assertEquals(ctl, 1, g.jobs.size)
        }
    }

    @Test fun theCappedStateReadsWaitingNDownloadsAlreadyRunningFromThisIp() {
        val after = NodeStatus.afterRun(NodeRunner.IP_CAP_REASON + "2", 0, 20)
        assertEquals("Waiting: 2 downloads already running from this IP", NodeStatus.text(after))
        assertEquals("Waiting: 3 downloads already running from this IP", NodeStatus.text(StatusInput(ipCapRunning = 3)))
        assertEquals("downloaded 0, failed 0, 2 downloads already running from this IP", NodeStatus.runSummary(0, 0, 0, NodeRunner.IP_CAP_REASON + "2"))
        assertEquals("Downloading: x", NodeStatus.text(StatusInput(downloading = true, title = "x", ipCapRunning = 2))) // a running download wins
    }
}
