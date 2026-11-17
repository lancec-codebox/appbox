package io.ref12.medusa.node

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class SubLangsTest {
    private fun info(subs: List<String>, autos: List<String>) =
        "{\"subtitles\":{" + subs.joinToString(",") { "\"$it\":[{}]" } + "},\"automatic_captions\":{" + autos.joinToString(",") { "\"$it\":[{}]" } + "}}"

    @Test fun uploadedEnAndZhAreExactAndOnlyTheOriginalAutoTrackIsAdded() {
        val p = SubLangs.plan(info(listOf("en", "en-US", "zh-Hans", "fr", "zh"), listOf("zh-orig", "zh-Hans", "en", "en-ar", "en-ja")))
        assertEquals(listOf("zh", "zh-Hans", "en", "en-US"), p.manual)
        assertEquals(listOf("zh-orig"), p.auto)
        assertEquals("zh,zh-Hans,en,en-US", p.manualArg)
    }

    @Test fun onlyAutoZhOrigWithAHundredTranslationsRequestsThatOneTrack() {
        val autos = (0 until 100).map { "tr$it" } + listOf("zh-orig", "zh-Hans", "en", "en-ar")
        val p = SubLangs.plan(info(emptyList(), autos))
        assertTrue(p.manual.isEmpty()); assertEquals(listOf("zh-orig"), p.auto)
    }

    @Test fun noSubtitlesOrNoInfoRequestsNothing() {
        for (s in listOf(info(emptyList(), emptyList()), null, "", "not json")) { val p = SubLangs.plan(s); assertTrue(p.manual.isEmpty() && p.auto.isEmpty()) }
        assertTrue(!SubLangs.plan(info(listOf("en"), listOf("en-orig"))).let { it.manualArg + it.autoArg }.contains("*"))
    }

    @Test fun aSubtitleErrorOfAnyStatusIsNeverABotBlockButARealOneStillIs() {
        for (code in listOf("403: Forbidden", "404: Not Found", "429: Too Many Requests")) {
            val line = "ERROR: Unable to download video subtitles for 'en-ar': HTTP Error $code"
            assertEquals(line, FailureKind.TRANSIENT, Failures.classify(line))
            assertEquals(line, Failures.errorLine("WARNING: x\n$line\n"))
        }
        assertEquals(FailureKind.BOT, Failures.classify("ERROR: [youtube] abc: Sign in to confirm you're not a bot"))
        assertEquals(FailureKind.BOT, Failures.classify("ERROR: unable to download webpage: HTTP Error 429: Too Many Requests"))
    }

    @Test fun aFailedPassIsRecordedNotThrown() {
        val calls = ArrayList<String>()
        val failed = SubLangs.fetch(SubPlan(listOf("zh", "en"), listOf("zh-orig"))) { auto, langs ->
            calls.add((if (auto) "auto:" else "manual:") + langs); if (!auto) throw RuntimeException("HTTP Error 429: Too Many Requests")
        }
        assertEquals(listOf("manual:zh,en", "auto:zh-orig"), calls)
        assertEquals(listOf("zh", "en"), failed)
        assertTrue(SubLangs.fetch(SubPlan.NONE) { _, _ -> throw AssertionError("no call expected") }.isEmpty())
    }
}
