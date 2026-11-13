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
class NodeSettings(ctx: Context) : NodeState {
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

    /** Fixed for the install: the name at first use plus 6 random hex characters. */
    val nodeId: String get() = prefs.getString("nodeId", null) ?: run {
        val b = ByteArray(3).also { SecureRandom().nextBytes(it) }
        (name + "-" + b.joinToString("") { "%02x".format(it) }).also { prefs.edit().putString("nodeId", it).apply() }
    }

    var lastRun: String get() = prefs.getString("lastRun", "") ?: ""; set(v) { prefs.edit().putString("lastRun", v).apply() }
    var lastRunAt: Long get() = prefs.getLong("lastRunAt", 0); set(v) { prefs.edit().putLong("lastRunAt", v).apply() }
    var lastControl: String get() = prefs.getString("lastControl", "") ?: ""; set(v) { prefs.edit().putString("lastControl", v).apply() }

    override fun saveControl(text: String) { lastControl = text }
    override fun doneToday(day: String) = if (prefs.getString("capDay", "") == day) prefs.getInt("capCount", 0) else 0
    override fun addDone(day: String) { prefs.edit().putString("capDay", day).putInt("capCount", doneToday(day) + 1).apply() }
    override fun backoffUntil() = prefs.getLong("backoffUntil", 0)
    override fun setBackoffUntil(t: Long) { prefs.edit().putLong("backoffUntil", t).apply() }

    override fun record(entry: JobLogEntry) {
        val a = JSONArray(prefs.getString("recent", "[]"))
        val n = JSONArray().put(JSONObject().put("id", entry.jobId).put("title", entry.title ?: "").put("result", entry.result).put("at", entry.at).put("msg", Redact.sas(entry.message ?: "")))
        for (i in 0 until minOf(a.length(), 19)) n.put(a.get(i))
        prefs.edit().putString("recent", n.toString()).apply()
    }
    fun recent(): List<JobLogEntry> = JSONArray(prefs.getString("recent", "[]")).let { a ->
        (0 until a.length()).map { val o = a.getJSONObject(it); JobLogEntry(o.getString("id"), o.optString("title").ifEmpty { null }, o.getString("result"), o.getLong("at"), o.optString("msg").ifEmpty { null }) }
    }

    fun config(ctx: Context, version: String) = NodeConfig(nodeId, name, conditions.wire, version,
        "android-" + (Build.SUPPORTED_ABIS.firstOrNull() ?: "unknown"), Runtime.getRuntime().availableProcessors(), java.io.File(ctx.cacheDir, "index-node"))
}
