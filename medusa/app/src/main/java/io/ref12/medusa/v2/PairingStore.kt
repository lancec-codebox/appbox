package io.ref12.medusa.v2

import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64

/** Where the paired apps are kept (SharedPreferences on the device, a map in tests). */
interface KeyValue {
    fun get(key: String): String?
    fun put(key: String, value: String)
}

class MemoryKeyValue : KeyValue {
    private val m = HashMap<String, String>()
    @Synchronized override fun get(key: String) = m[key]
    @Synchronized override fun put(key: String, value: String) { m[key] = value }
}

class PairedApp(val id: String, val appName: String, val pairedAt: Long, internal val tokenHash: String) {
    @Volatile var lastUsed: Long = 0
}

/**
 * Pairing: an app asks, the user allows on the phone, the app gets a random token once.
 * Only the SHA-256 of a token is stored, so a token cannot be read back from the phone.
 */
class PairingStore(
    private val kv: KeyValue,
    private val clock: () -> Long = System::currentTimeMillis,
    private val random: SecureRandom = SecureRandom(),
) {
    enum class State(val wire: String) { PENDING("pending"), APPROVED("approved"), DENIED("denied") }

    class Request(val id: String, val appName: String, val source: String?, val createdAt: Long) {
        @Volatile var state = State.PENDING
        @Volatile internal var token: String? = null
        @Volatile internal var decidedAt = 0L
    }

    private val pending = LinkedHashMap<String, Request>()
    private val apps = ArrayList<PairedApp>()

    init {
        try {
            val a = JSONArray(kv.get("apps") ?: "[]")
            for (i in 0 until a.length()) {
                val o = a.getJSONObject(i)
                apps.add(PairedApp(o.getString("id"), o.getString("appName"), o.optLong("pairedAt"), o.getString("hash")))
            }
        } catch (e: Exception) { apps.clear() }
    }

    private fun save() {
        val a = JSONArray()
        for (p in apps) a.put(JSONObject().put("id", p.id).put("appName", p.appName).put("pairedAt", p.pairedAt).put("hash", p.tokenHash))
        kv.put("apps", a.toString())
    }

    private fun expire() {
        val now = clock()
        pending.values.removeAll { now - (if (it.decidedAt > 0) it.decidedAt else it.createdAt) > REQUEST_TTL_MS }
    }

    /** A new request, or null when too many are waiting for the user. */
    @Synchronized fun request(appName: String, source: String?): Request? {
        expire()
        if (pending.size >= MAX_PENDING) return null
        val r = Request(randomId(), appName, source, clock())
        pending[r.id] = r
        return r
    }

    @Synchronized fun get(id: String): Request? { expire(); return pending[id] }

    @Synchronized fun pendingRequests(): List<Request> { expire(); return pending.values.filter { it.state == State.PENDING } }

    /** The user said Allow: a new token (also kept for [poll] until fetched), or null when the request is gone. */
    @Synchronized fun approve(id: String): String? {
        expire()
        val r = pending[id] ?: return null
        if (r.state != State.PENDING) return null
        val token = "mt_" + Base64.getUrlEncoder().withoutPadding().encodeToString(ByteArray(32).also { random.nextBytes(it) })
        r.state = State.APPROVED
        r.token = token
        r.decidedAt = clock()
        apps.add(PairedApp(randomId(), r.appName, clock(), hash(token)))
        save()
        return token
    }

    @Synchronized fun deny(id: String) {
        val r = pending[id] ?: return
        if (r.state == State.PENDING) { r.state = State.DENIED; r.decidedAt = clock() }
    }

    /** Forgets a request (after its token went back as an intent result). */
    @Synchronized fun discard(id: String) { pending.remove(id) }

    /** What a polling caller sees; an approved token is handed out once and then forgotten. Null = unknown or expired. */
    @Synchronized fun poll(id: String): Pair<State, String?>? {
        expire()
        val r = pending[id] ?: return null
        return when (r.state) {
            State.PENDING -> State.PENDING to null
            State.DENIED -> { pending.remove(id); State.DENIED to null }
            State.APPROVED -> { pending.remove(id); State.APPROVED to r.token }
        }
    }

    @Synchronized fun authenticate(token: String): PairedApp? {
        val h = hash(token)
        val hb = h.toByteArray()
        for (p in apps) if (MessageDigest.isEqual(hb, p.tokenHash.toByteArray())) { p.lastUsed = clock(); return p }
        return null
    }

    @Synchronized fun list(): List<PairedApp> = apps.toList()

    @Synchronized fun revoke(id: String): Boolean {
        val removed = apps.removeAll { it.id == id }
        if (removed) save()
        return removed
    }

    private fun randomId() = ByteArray(12).also { random.nextBytes(it) }.joinToString("") { "%02x".format(it) }

    companion object {
        const val REQUEST_TTL_MS = 120_000L
        const val MAX_PENDING = 5

        fun hash(token: String) = MessageDigest.getInstance("SHA-256").digest(token.toByteArray()).joinToString("") { "%02x".format(it) }

        /** A name safe to show in the prompt: no control characters, 1 to 60 characters. */
        fun cleanName(raw: String?): String? {
            val s = (raw ?: return null).filter { it.code >= 0x20 && it.code != 0x7f }.trim().replace(Regex("\\s+"), " ")
            return s.takeIf { it.isNotEmpty() && it.length <= 60 }
        }
    }
}
