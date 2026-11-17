package io.ref12.medusa.node

import org.json.JSONObject

/** The exact subtitle tracks to request for one video. Never a regex such as "en.*": it matches every auto-translated track (en-ar, en-ja, ...) and the burst of timedtext requests gets the IP rate-limited. */
class SubPlan(val manual: List<String>, val auto: List<String>) {
    val manualArg get() = manual.joinToString(",")
    val autoArg get() = auto.joinToString(",")
    companion object { val NONE = SubPlan(emptyList(), emptyList()) }
}

object SubLangs {
    val CHINESE = listOf("zh", "zh-Hans", "zh-Hant", "zh-CN", "zh-TW", "zh-HK")
    private val ORIG = Regex("^(en|zh)(-[A-Za-z]+)*-orig$")

    /** Uploaded: the Chinese set and every en / en-* key of "subtitles" (an upload because it is there). Automatic: only the original-language *-orig tracks of "automatic_captions", never translations. No or broken info: nothing. */
    fun plan(infoJson: String?): SubPlan {
        if (infoJson.isNullOrBlank()) return SubPlan.NONE
        val o = try { JSONObject(infoJson) } catch (e: Exception) { return SubPlan.NONE }
        fun keys(k: String): List<String> = o.optJSONObject(k)?.keys()?.asSequence()?.toList() ?: emptyList()
        val subs = keys("subtitles"); val autos = keys("automatic_captions")
        val manual = (CHINESE.filter { it in subs } + subs.filter { it == "en" || it.startsWith("en-") }.sorted()).distinct()
        val auto = autos.filter { ORIG.matches(it) }.sortedWith(compareBy({ !it.startsWith("zh") }, { it })).distinct()
        return SubPlan(manual, auto)
    }

    /** What the subtitle passes left: failed track names ("lang" or "lang.auto"). A failure is recorded, never thrown: the video is already downloaded. */
    fun fetch(plan: SubPlan, run: (auto: Boolean, langs: String) -> Unit): List<String> {
        val failed = ArrayList<String>()
        for (auto in listOf(false, true)) {
            val langs = if (auto) plan.auto else plan.manual
            if (langs.isEmpty()) continue
            try { run(auto, langs.joinToString(",")) } catch (e: Exception) { failed.addAll(langs.map { if (auto) "$it.auto" else it }) }
        }
        return failed
    }
}
