package io.ref12.medusa.node

import org.json.JSONObject
import java.util.Base64

/** Where a setup is applied; NodeSettings is the real one. */
interface SetupStore {
    fun current(): SetupCurrent
    fun putSas(sas: String)
    fun putName(v: String)
    fun putConditions(wire: String)
    fun putDailyCap(v: Int)
    fun putBudget(v: Int)
    fun putSchedule(v: String)
    fun putWindow(v: String)
    fun putMaxHeight(v: Int)
}

class SetupCurrent(val name: String, val conditions: String, val dailyCap: Int, val budget: Int?, val schedule: String, val window: String, val maxHeight: Int, val hasSas: Boolean)

class SetupRejected(message: String) : Exception(message)

/** Node settings from the desktop node: a medusa://node-setup?d=<base64url json> link, a QR code of it, or a .medusa-node.json file. Any field may be absent (it then keeps its current value). */
class NodeSetup private constructor(
    val sas: String?, val nodeName: String?, val conditions: String?, val dailyCap: Int?, val budget: Int?, val schedule: String?, val window: String?, val maxHeight: Int?,
) {
    /** What will change, one line per field, for the confirmation. The storage key is only ever "set" or "unchanged". */
    fun summary(cur: SetupCurrent): List<String> {
        val l = ArrayList<String>()
        fun line(label: String, now: String, next: String?) { if (next != null) l.add(label + ": " + (if (now == next) "$next (same)" else "$now \u2192 $next")) }
        line("Node name", cur.name, nodeName)
        line("Conditions", cur.conditions, conditions)
        line("Daily cap", cur.dailyCap.toString(), dailyCap?.toString())
        line("Budget", cur.budget?.toString() ?: "no limit", budget?.toString())
        line("Schedule", cur.schedule.ifEmpty { "any time" }, schedule?.ifEmpty { "any time" })
        line("Window", cur.window.ifEmpty { "1h" }, window?.ifEmpty { "1h" })
        line("Max height", cur.maxHeight.toString(), maxHeight?.toString())
        l.add("Storage key: " + if (sas != null) "set" else "unchanged")
        if (l.size == 1 && sas == null) l.add(0, "No settings change")
        return l
    }

    /** Applies the present fields; absent ones keep their values. */
    fun apply(s: SetupStore) {
        sas?.let { s.putSas(it) }; nodeName?.let { s.putName(it) }; conditions?.let { s.putConditions(it) }; dailyCap?.let { s.putDailyCap(it) }
        budget?.let { s.putBudget(it) }; schedule?.let { s.putSchedule(it) }; window?.let { s.putWindow(it) }; maxHeight?.let { s.putMaxHeight(it) }
    }

    companion object {
        const val KIND = "medusa-node-settings"
        const val SCHEME_PREFIX = "medusa://node-setup"
        const val MAX_BYTES = 16 * 1024

        /** True when [text] looks like a settings file (cheap check, to route a shared .json here). */
        fun looksLike(text: String): Boolean = text.length <= MAX_BYTES && text.contains("\"" + KIND + "\"")

        fun isLink(s: String?): Boolean = s != null && s.trim().startsWith(SCHEME_PREFIX, ignoreCase = true)

        /** A medusa://node-setup?d=... link. */
        fun fromLink(link: String): NodeSetup {
            val t = link.trim()
            if (!isLink(t)) throw SetupRejected("This is not a Medusa node setup link")
            val q = t.substringAfter('?', "").substringBefore('#')
            val d = q.split('&').firstOrNull { it.startsWith("d=") }?.substring(2)?.takeIf { it.isNotEmpty() } ?: throw SetupRejected("The setup link has no data")
            val bytes = try { Base64.getUrlDecoder().decode(d.replace("%3D", "=").replace('+', '-').replace('/', '_')) } catch (e: IllegalArgumentException) { throw SetupRejected("The setup link is damaged") }
            return fromJson(String(bytes, Charsets.UTF_8))
        }

        /** The link, or the JSON itself (what a file or a pasted text holds). */
        fun fromAny(text: String): NodeSetup = if (isLink(text)) fromLink(text) else fromJson(text)

        fun fromJson(text: String): NodeSetup {
            if (text.length > MAX_BYTES) throw SetupRejected("This file is too large to be node settings")
            val o = try { JSONObject(text.trim().removePrefix("\uFEFF")) } catch (e: Exception) { throw SetupRejected("This is not a Medusa node settings file") }
            if (o.optString("kind") != KIND) throw SetupRejected("This is not a Medusa node settings file")
            if (o.optInt("v", -1) != 1) throw SetupRejected("These settings are version " + o.opt("v") + "; this Medusa reads version 1. Update Medusa or the desktop node")
            fun str(k: String): String? = if (o.has(k) && !o.isNull(k)) o.get(k).let { if (it is String) it else throw SetupRejected("$k must be text") } else null
            fun int(k: String, lo: Int, hi: Int): Int? = if (o.has(k) && !o.isNull(k)) {
                val n = o.get(k); val v = (n as? Number)?.toInt()?.takeIf { (n as Number).toDouble() == it.toDouble() } ?: throw SetupRejected("$k must be a number")
                if (v < lo || v > hi) throw SetupRejected("$k must be between $lo and $hi") else v
            } else null
            val sas = str("sas")?.trim()?.takeIf { it.isNotEmpty() }
            if (sas != null && SasUrl.parse(sas) == null) throw SetupRejected("The storage key in these settings is not a container SAS URL")
            val cond = str("conditions")?.trim()?.also { c -> if (NodeConditions.values().none { it.wire == c }) throw SetupRejected("conditions must be wifi+charging or wifi") }
            val name = str("nodeName")?.trim()?.takeIf { it.isNotEmpty() }
            val sched = str("schedule")?.trim()?.also { if (it.isNotEmpty() && Cron.parse(it) == null) throw SetupRejected("schedule is not a 5-field cron expression") }
            val win = str("window")?.trim()?.also { if (it.isNotEmpty() && Durations.parse(it) == null) throw SetupRejected("window must look like 90m, 3h or 1d") }
            return NodeSetup(sas, name, cond, int("dailyCap", 1, 500), int("budget", 0, 1_000_000), sched, win, int("maxHeight", 144, 4320))
        }

        /** The same settings without the storage key, for backup (a .medusa-node.json). */
        fun export(c: SetupCurrent): String = JSONObject().put("v", 1).put("kind", KIND).put("nodeName", c.name).put("conditions", c.conditions)
            .put("dailyCap", c.dailyCap).also { o -> c.budget?.let { o.put("budget", it) } }
            .put("schedule", c.schedule).put("window", c.window).put("maxHeight", c.maxHeight).toString(2)
    }
}
