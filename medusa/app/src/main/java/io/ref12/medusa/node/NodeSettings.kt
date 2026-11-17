package io.ref12.medusa.node

import android.content.Context
import android.content.SharedPreferences
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyStore
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Keeps the SAS URL encrypted with an Android Keystore AES-GCM key (the key never leaves the Keystore). */
class SasVault(private val prefs: SharedPreferences) {
    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val g = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        g.init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        return g.generateKey()
    }

    fun save(sas: String) {
        val c = Cipher.getInstance("AES/GCM/NoPadding"); c.init(Cipher.ENCRYPT_MODE, key())
        val ct = c.doFinal(sas.toByteArray(Charsets.UTF_8))
        prefs.edit().putString("sas", Base64.encodeToString(c.iv, Base64.NO_WRAP) + ":" + Base64.encodeToString(ct, Base64.NO_WRAP)).apply()
    }

    fun load(): SasUrl? = try {
        val s = prefs.getString("sas", null) ?: return null
        val (iv, ct) = s.split(':').let { Base64.decode(it[0], Base64.NO_WRAP) to Base64.decode(it[1], Base64.NO_WRAP) }
        val c = Cipher.getInstance("AES/GCM/NoPadding"); c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
        SasUrl.parse(String(c.doFinal(ct), Charsets.UTF_8))
    } catch (e: Exception) { null }

    fun has() = prefs.contains("sas")
    fun clear() { prefs.edit().remove("sas").apply() }
    private companion object { const val ALIAS = "medusa_index_sas" }
}

enum class NodeConditions(val label: String, val wire: String) {
    CHARGING_WIFI("Wi-Fi (unmetered) and charging", "wifi+charging"),
    WIFI("Wi-Fi (unmetered), any time", "wifi"),
}

/** Settings and counters of the index node. The SAS goes only through [vault]. */
class NodeSettings(ctx: Context) : NodeState, SetupStore, DownloadPrefs {
    private val prefs = ctx.getSharedPreferences("index_node", Context.MODE_PRIVATE)
    val vault = SasVault(prefs)

    var enabled: Boolean get() = prefs.getBoolean("enabled", false); set(v) { prefs.edit().putBoolean("enabled", v).apply() }
    var name: String
        get() = prefs.getString("name", null) ?: Build.MODEL.lowercase().replace(Regex("[^a-z0-9]+"), "").ifEmpty { "medusa" }.take(20)
        set(v) { prefs.edit().putString("name", v.lowercase().replace(Regex("[^a-z0-9-]+"), "").take(20).ifEmpty { "medusa" }).apply() }
    var conditions: NodeConditions
        get() = NodeConditions.values().firstOrNull { it.name == prefs.getString("conditions", null) } ?: NodeConditions.CHARGING_WIFI
        set(v) { prefs.edit().putString("conditions", v.name).apply() }
    var dailyCap: Int get() = prefs.getInt("dailyCap", 20); set(v) { prefs.edit().putInt("dailyCap", v.coerceIn(1, 500)).apply() }

    /** Run limits: budget (-1 unset), schedule (cron) and window, as typed; a remote override in control.json wins. */
    var budget: Int? get() = prefs.getInt("budget", -1).takeIf { it >= 0 }; set(v) { prefs.edit().putInt("budget", v ?: -1).apply() }
    var schedule: String get() = prefs.getString("schedule", "") ?: ""; set(v) { prefs.edit().putString("schedule", v.trim()).apply() }
    var window: String get() = prefs.getString("window", "") ?: ""; set(v) { prefs.edit().putString("window", v.trim()).apply() }

    override fun localLimits() = LocalLimits(budget, schedule.ifEmpty { null }, window.ifEmpty { null })
    var lastLimits: String get() = prefs.getString("lastLimits", "") ?: ""; set(v) { prefs.edit().putString("lastLimits", v).apply() }
    override fun saveLimits(text: String) { lastLimits = text }
    override fun budgetKey() = prefs.getString("budgetKey", null)
    override fun budgetLeft() = prefs.getInt("budgetLeft", 0)
    override fun setBudget(key: String?, left: Int) { prefs.edit().putString("budgetKey", key).putInt("budgetLeft", left).apply() }

    /** The status notification: on by default while the node is enabled. */
    var statusNotification: Boolean get() = prefs.getBoolean("statusNotification", true); set(v) { prefs.edit().putBoolean("statusNotification", v).apply() }
    var lastStatus: String get() = prefs.getString("lastStatus", "") ?: ""; set(v) { prefs.edit().putString("lastStatus", v).apply() }
    /** What this phone added to the index (kept locally; see [AddedItems]). */
    val added = AddedItems(object : TextStore {
        override fun load() = prefs.getString("added", null)
        override fun save(text: String) { prefs.edit().putString("added", text).apply() }
    })

    var maxHeight: Int get() = prefs.getInt("maxHeight", 480); set(v) { prefs.edit().putInt("maxHeight", v.coerceIn(144, 4320)).apply() }

    /** "Also store my downloads in the index" (1.8.6): off by default. YouTube videos only. */
    var storeLibrary: Boolean get() = prefs.getBoolean("storeLibrary", false); set(v) { prefs.edit().putBoolean("storeLibrary", v).apply() }
    /** Store only (default) or Store and process, for the next library download. */
    var storeMode: StoreMode get() = StoreMode.parse(prefs.getString("storeMode", null)); set(v) { prefs.edit().putString("storeMode", v.wire).apply() }
    /** Upload over mobile data too (default off: Wi-Fi only). */
    var storeMobile: Boolean get() = prefs.getBoolean("storeMobile", false); set(v) { prefs.edit().putBoolean("storeMobile", v).apply() }
    /** Priority of the step jobs of "Store and process" (0 unless the owner sets one). */
    var storePriority: Int get() = prefs.getInt("storePriority", 0); set(v) { prefs.edit().putInt("storePriority", v.coerceIn(Prio.MIN, Prio.MAX)).apply() }
    val storeQueue = IndexStoreQueue(object : TextStore {
        override fun load() = prefs.getString("storeQueue", null)
        override fun save(text: String) { prefs.edit().putString("storeQueue", text).apply() }
    })

    /** "Try WebM first" (1.8.6): on by default, today's behaviour; off goes straight to the library's MP4 selector. */
    override var webmFirst: Boolean get() = prefs.getBoolean("webmFirst", true); set(v) { prefs.edit().putBoolean("webmFirst", v).apply() }
    /** The download profile the 403 diagnosis found (or the record that nothing passed); null: none yet. Reset profile clears it. */
    override var profile: DownloadProfile?
        get() = DownloadProfile.parse(prefs.getString("downloadProfile", null))
        set(v) { prefs.edit().apply { if (v == null) remove("downloadProfile") else putString("downloadProfile", v.toJson().toString()) }.apply() }
    override fun downloadProfile(): DownloadProfile? = profile?.takeIf { it.found || it.tries.isNotEmpty() }

    /** The node log: the newest lines, kept in the settings (about 60 KB), shared from Settings > Index node. Never holds a SAS or a URL query string. */
    override fun log(line: String) {
        val stamp = java.text.SimpleDateFormat("MM-dd HH:mm:ss", java.util.Locale.US).format(java.util.Date())
        val clean = FailureTrace.clean(Redact.sas(line))
        synchronized(LOG_LOCK) {
            var text = (prefs.getString("nodeLog", "") ?: "") + stamp + " " + clean + "\n"
            if (text.length > LOG_MAX) text = text.substring(text.length - LOG_MAX).substringAfter('\n')
            prefs.edit().putString("nodeLog", text).apply()
        }
    }
    fun logText(): String = prefs.getString("nodeLog", "") ?: ""
    fun clearLog() { prefs.edit().remove("nodeLog").apply() }

    override fun current() = SetupCurrent(name, conditions.wire, dailyCap, budget, schedule, window, maxHeight, vault.has())
    override fun putSas(sas: String) = vault.save(sas)
    override fun putName(v: String) { name = v }
    override fun putConditions(wire: String) { NodeConditions.values().firstOrNull { it.wire == wire }?.let { conditions = it } }
    override fun putDailyCap(v: Int) { dailyCap = v }
    override fun putBudget(v: Int) { budget = v }
    override fun putSchedule(v: String) { schedule = v }
    override fun putWindow(v: String) { window = v }
    override fun putMaxHeight(v: Int) { maxHeight = v }

    /** Fixed for the install: the name at first use plus 6 random hex characters. */
    val nodeId: String get() = prefs.getString("nodeId", null) ?: run {
        val b = ByteArray(3).also { SecureRandom().nextBytes(it) }
        (name + "-" + b.joinToString("") { "%02x".format(it) }).also { prefs.edit().putString("nodeId", it).apply() }
    }

    var lastRun: String get() = prefs.getString("lastRun", "") ?: ""; set(v) { prefs.edit().putString("lastRun", v).apply() }
    var lastRunAt: Long get() = prefs.getLong("lastRunAt", 0); set(v) { prefs.edit().putLong("lastRunAt", v).apply() }
    var lastRunVersion: String get() = prefs.getString("lastRunVersion", "") ?: ""; set(v) { prefs.edit().putString("lastRunVersion", v).apply() }
    /** How the last run ended (the runner's reason), so the headline can say "No downloads queued" between runs. */
    var lastEndReason: String get() = prefs.getString("lastEndReason", "") ?: ""; set(v) { prefs.edit().putString("lastEndReason", v).apply() }
    var lastControl: String get() = prefs.getString("lastControl", "") ?: ""; set(v) { prefs.edit().putString("lastControl", v).apply() }

    override fun saveControl(text: String) { lastControl = text }
    override fun doneToday(day: String) = if (prefs.getString("capDay", "") == day) prefs.getInt("capCount", 0) else 0
    override fun addDone(day: String) { prefs.edit().putString("capDay", day).putInt("capCount", doneToday(day) + 1).apply() }
    override fun backoffUntil() = prefs.getLong("backoffUntil", 0)
    override fun setBackoffUntil(t: Long) { prefs.edit().putLong("backoffUntil", t).apply() }

    override fun skipUntil(jobId: String): Long = try { JSONObject(prefs.getString("skip", "{}")).optLong(jobId, 0) } catch (e: Exception) { 0 }
    override fun skipJob(jobId: String, until: Long) {
        val o = try { JSONObject(prefs.getString("skip", "{}")) } catch (e: Exception) { JSONObject() }
        val now = System.currentTimeMillis()
        o.keys().asSequence().toList().forEach { if (o.optLong(it, 0) < now) o.remove(it) }
        o.put(jobId, until); prefs.edit().putString("skip", o.toString()).apply()
    }

    override fun record(entry: JobLogEntry) {
        val a = JSONArray(prefs.getString("recent", "[]"))
        val n = JSONArray().put(JSONObject().put("id", entry.jobId).put("title", entry.title ?: "").put("result", entry.result).put("at", entry.at).put("msg", Redact.sas(entry.message ?: "")).put("ver", entry.version ?: io.ref12.medusa.BuildConfig.VERSION_NAME))
        for (i in 0 until minOf(a.length(), 19)) n.put(a.get(i))
        prefs.edit().putString("recent", n.toString()).apply()
    }
    fun recent(): List<JobLogEntry> = JSONArray(prefs.getString("recent", "[]")).let { a ->
        (0 until a.length()).map { val o = a.getJSONObject(it); JobLogEntry(o.getString("id"), o.optString("title").ifEmpty { null }, o.getString("result"), o.getLong("at"), o.optString("msg").ifEmpty { null }, o.optString("ver").ifEmpty { null }) }
    }

    fun config(ctx: Context, version: String) = NodeConfig(nodeId, name, conditions.wire, version,
        "android-" + (Build.SUPPORTED_ABIS.firstOrNull() ?: "unknown"), Runtime.getRuntime().availableProcessors(), java.io.File(ctx.cacheDir, "index-node"))

    private companion object { val LOG_LOCK = Any(); const val LOG_MAX = 60_000 }
}
