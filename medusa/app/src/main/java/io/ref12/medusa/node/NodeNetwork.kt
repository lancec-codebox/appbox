package io.ref12.medusa.node

import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.URL

/** The node's public network (registry field "network"): the public IPv4 and IPv6, where Cloudflare sees it, and how this phone is connected. */
class NetInfo(
    val ipv4: String?, val ipv6: String?, val ipv6Prefix: String?, val country: String?, val colo: String?,
    val transport: String?, val vpn: Boolean?, val checkedAt: String, val since: String,
) {
    fun samePair(o: NetInfo?) = o != null && ipv4 == o.ipv4 && ipv6 == o.ipv6

    fun toJson(): JSONObject = JSONObject().also { o ->
        ipv4?.let { o.put("ipv4", it) }; ipv6?.let { o.put("ipv6", it) }; ipv6Prefix?.let { o.put("ipv6Prefix", it) }
        country?.let { o.put("country", it) }; colo?.let { o.put("colo", it) }
        transport?.let { o.put("transport", it) }; vpn?.let { o.put("vpn", it) }
        o.put("checkedAt", checkedAt).put("since", since)
    }

    /** The network a download attempt started on: {ipv4, ipv6Prefix, vpn, transport}. */
    fun forJob(): JSONObject = JSONObject().also { o ->
        ipv4?.let { o.put("ipv4", it) }; ipv6Prefix?.let { o.put("ipv6Prefix", it) }; vpn?.let { o.put("vpn", it) }; transport?.let { o.put("transport", it) }
    }

    companion object {
        fun parse(text: String): NetInfo? = try { fromJson(JSONObject(text).optJSONObject("network")) } catch (e: Exception) { null }
        fun fromJson(o: JSONObject?): NetInfo? {
            if (o == null) return null
            fun s(k: String) = if (o.isNull(k)) null else o.optString(k).ifEmpty { null }
            return NetInfo(s("ipv4"), s("ipv6"), s("ipv6Prefix"), s("country"), s("colo"), s("transport"), if (o.has("vpn") && !o.isNull("vpn")) o.optBoolean("vpn") else null,
                s("checkedAt") ?: "", s("since") ?: "")
        }
    }
}

/** What the phone says about its active network (ConnectivityManager): transport "wifi" | "cellular" | "ethernet", and whether it is a VPN. */
class DeviceNet(val transport: String?, val vpn: Boolean?)

class TraceInfo(val ip: String?, val loc: String?, val colo: String?)

/** Looks up the public addresses with Cloudflare's trace endpoint, IPv4 and IPv6 separately; a family that does not answer within 5 s is null. */
object NetworkProbe {
    const val V4_URL = "https://1.1.1.1/cdn-cgi/trace"
    const val V6_URL = "https://[2606:4700:4700::1111]/cdn-cgi/trace"
    const val FALLBACK_URL = "https://www.cloudflare.com/cdn-cgi/trace"
    const val TIMEOUT_MS = 5000
    /** Re-check every 30 minutes, and before claiming a download when the last check is older than 10 minutes. */
    const val REFRESH_MS = 30 * 60_000L
    const val CLAIM_STALE_MS = 10 * 60_000L

    fun parseTrace(text: String?): TraceInfo {
        var ip: String? = null; var loc: String? = null; var colo: String? = null
        (text ?: "").lines().forEach { raw ->
            val line = raw.trim(); val eq = line.indexOf('=')
            if (eq > 0) { val v = line.substring(eq + 1).trim(); if (v.isNotEmpty()) when (line.substring(0, eq)) { "ip" -> ip = v; "loc" -> loc = v; "colo" -> colo = v } }
        }
        return TraceInfo(ip, loc, colo)
    }

    private val V4 = Regex("""^\d{1,3}(\.\d{1,3}){3}$""")
    private val V6 = Regex("""^[0-9a-fA-F:.]+$""")
    fun isV4(ip: String?) = ip != null && V4.matches(ip)
    fun isV6(ip: String?) = ip != null && ip.contains(':') && V6.matches(ip)

    /** "2001:db8:1:2:aaaa::1" -> "2001:db8:1:2::/64"; null when it is not an IPv6 address. */
    fun ipv6Prefix64(ip: String?): String? {
        if (!isV6(ip)) return null
        val b = try { InetAddress.getByName(ip).address } catch (e: Exception) { return null } // a literal: no DNS
        if (b.size != 16) return null
        val g = IntArray(8) { if (it < 4) ((b[2 * it].toInt() and 0xff) shl 8) or (b[2 * it + 1].toInt() and 0xff) else 0 }
        // canonical text: the first longest run of at least two zero groups becomes "::"
        var bestS = -1; var bestL = 0; var k = 0
        while (k < 8) { if (g[k] == 0) { var e = k; while (e < 8 && g[e] == 0) e++; if (e - k > bestL) { bestS = k; bestL = e - k }; k = e } else k++ }
        val hex = g.map { Integer.toHexString(it) }
        val text = if (bestL < 2) hex.joinToString(":") else hex.subList(0, bestS).joinToString(":") + "::" + hex.subList(bestS + bestL, 8).joinToString(":")
        return "$text/64"
    }

    /** The real fetch: a plain GET with 5 s timeouts; null when it fails. */
    fun httpFetch(url: String): String? = try {
        val c = URL(url).openConnection() as HttpURLConnection
        try { c.connectTimeout = TIMEOUT_MS; c.readTimeout = TIMEOUT_MS; if (c.responseCode in 200..299) c.inputStream.bufferedReader().readText() else null } finally { c.disconnect() }
    } catch (e: Exception) { null }

    /** One lookup. [fetch] returns the trace text or null (it may throw); the families are asked in parallel; the fallback endpoint fills a family that failed. */
    fun detect(fetch: (String) -> String?, now: String, device: DeviceNet = DeviceNet(null, null), previous: NetInfo? = null): NetInfo {
        fun one(url: String): TraceInfo? = try { parseTrace(fetch(url)).takeIf { it.ip != null } } catch (e: Exception) { null }
        var r4: TraceInfo? = null; var r6: TraceInfo? = null
        val t = Thread { r6 = one(V6_URL) }; t.start()
        r4 = one(V4_URL); t.join()
        if (!isV4(r4?.ip)) r4 = null
        if (!isV6(r6?.ip)) r6 = null
        if (r4 == null || r6 == null) {
            val f = one(FALLBACK_URL)
            if (f != null) { if (r4 == null && isV4(f.ip)) r4 = f else if (r6 == null && isV6(f.ip)) r6 = f }
        }
        val ip6 = r6?.ip
        val fresh = NetInfo(r4?.ip, ip6, ipv6Prefix64(ip6), r4?.loc ?: r6?.loc, r4?.colo ?: r6?.colo, device.transport, device.vpn, now, now)
        return if (previous != null && fresh.samePair(previous) && previous.since.isNotEmpty()) NetInfo(fresh.ipv4, fresh.ipv6, fresh.ipv6Prefix, fresh.country, fresh.colo, fresh.transport, fresh.vpn, now, previous.since) else fresh
    }

    /** "wifi", "cellular" or "ethernet" from the active network's transports (a VPN also reports the transport under it, when Android says so). */
    fun transportOf(wifi: Boolean, cellular: Boolean, ethernet: Boolean): String? = when { wifi -> "wifi"; cellular -> "cellular"; ethernet -> "ethernet"; else -> null }
}

/** Another node's registry record, as far as the cap needs it. */
class PeerNode(val nodeId: String, val state: String, val lastSeenMs: Long?, val network: NetInfo?, val runningDownloads: Int) {
    companion object {
        fun parse(text: String): PeerNode? = try {
            val o = JSONObject(text)
            val running = o.optJSONArray("running")
            var n = 0; if (running != null) for (i in 0 until running.length()) if (running.optJSONObject(i)?.optString("stage") == "download") n++
            PeerNode(o.optString("nodeId"), o.optString("state"), try { java.time.Instant.parse(o.optString("lastSeen")).toEpochMilli() } catch (e: Exception) { null }, NetInfo.fromJson(o.optJSONObject("network")), n)
        } catch (e: Exception) { null }
    }
}

/** The optional per-IP download cap (control.json downloads.maxPerIp); off unless set above 0. */
object NetCap {
    const val FRESH_MS = 3 * 60_000L

    /** Same IPv4, or the same IPv6 /64. */
    fun shares(a: NetInfo?, b: NetInfo?): Boolean {
        if (a == null || b == null) return false
        if (!a.ipv4.isNullOrEmpty() && a.ipv4 == b.ipv4) return true
        return !a.ipv6Prefix.isNullOrEmpty() && a.ipv6Prefix == b.ipv6Prefix
    }

    /** Downloads running on OTHER nodes that share this node's address: nodes seen under 3 minutes ago and not offline. */
    fun countOthers(self: NetInfo?, selfId: String, nodes: List<PeerNode>, nowMs: Long): Int =
        nodes.filter { it.nodeId != selfId && it.state != "offline" && shares(self, it.network) && it.lastSeenMs != null && nowMs - it.lastSeenMs < FRESH_MS }.sumOf { it.runningDownloads }

    /** No cap (null or 0): always. Else others plus our own running downloads must be under the cap. */
    fun mayClaim(maxPerIp: Int?, others: Int, own: Int) = maxPerIp == null || maxPerIp <= 0 || others + own < maxPerIp

    fun waitText(running: Int) = "Waiting: $running downloads already running from this IP"
}
