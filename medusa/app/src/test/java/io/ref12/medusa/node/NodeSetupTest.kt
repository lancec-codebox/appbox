package io.ref12.medusa.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.util.Base64

class NodeSetupTest {
    private val SAS = "https://acct.blob.core.windows.net/medusa-index?sv=2022-11-02&sp=racwdl&sig=SECRETSIGNATUREvalue123%2Bxyz"
    private fun link(json: String) = "medusa://node-setup?d=" + Base64.getUrlEncoder().withoutPadding().encodeToString(json.toByteArray())
    private val full = """{"v":1,"kind":"medusa-node-settings","sas":"$SAS","nodeName":"pixel","conditions":"wifi","dailyCap":50,"budget":10,"schedule":"0 1 * * *","window":"3h","maxHeight":720}"""

    class Store(var name: String = "old", var cond: String = "wifi+charging", var cap: Int = 20, var budget: Int? = 7, var sched: String = "", var win: String = "", var h: Int = 480, var sas: String? = null) : SetupStore {
        override fun current() = SetupCurrent(name, cond, cap, budget, sched, win, h, sas != null)
        override fun putSas(sas: String) { this.sas = sas }
        override fun putName(v: String) { name = v }
        override fun putConditions(wire: String) { cond = wire }
        override fun putDailyCap(v: Int) { cap = v }
        override fun putBudget(v: Int) { budget = v }
        override fun putSchedule(v: String) { sched = v }
        override fun putWindow(v: String) { win = v }
        override fun putMaxHeight(v: Int) { h = v }
    }

    @Test fun aLinkIsParsedAndApplied() {
        val s = Store(); NodeSetup.fromLink(link(full)).apply(s)
        assertEquals("pixel", s.name); assertEquals("wifi", s.cond); assertEquals(50, s.cap); assertEquals(10, s.budget)
        assertEquals("0 1 * * *", s.sched); assertEquals("3h", s.win); assertEquals(720, s.h); assertEquals(SAS, s.sas)
    }

    @Test fun aFileAndALinkGiveTheSameSetup() {
        val a = Store(); val b = Store(); NodeSetup.fromJson(full).apply(a); NodeSetup.fromAny(link(full)).apply(b)
        assertEquals(a.current().let { listOf(it.name, it.conditions, it.dailyCap, it.budget, it.schedule, it.window, it.maxHeight) },
            b.current().let { listOf(it.name, it.conditions, it.dailyCap, it.budget, it.schedule, it.window, it.maxHeight) })
        assertTrue(NodeSetup.looksLike(full)); assertFalse(NodeSetup.looksLike("1\n00:00:01,000 --> 00:00:02,000\nhi"))
    }

    @Test fun absentFieldsKeepTheirCurrentValues() {
        val s = Store(sas = "keep"); NodeSetup.fromJson("""{"v":1,"kind":"medusa-node-settings","dailyCap":30}""").apply(s)
        assertEquals(30, s.cap); assertEquals("old", s.name); assertEquals("wifi+charging", s.cond); assertEquals(7, s.budget); assertEquals("keep", s.sas); assertEquals(480, s.h)
        // padded base64 is fine too
        NodeSetup.fromLink("medusa://node-setup?d=" + Base64.getUrlEncoder().encodeToString("""{"v":1,"kind":"medusa-node-settings"}""".toByteArray()))
    }

    @Test fun theSummaryListsChangesAndNeverTheKey() {
        val cur = Store().current()
        val withKey = NodeSetup.fromJson(full).summary(cur).joinToString("\n")
        assertTrue(withKey.contains("Node name: old \u2192 pixel")); assertTrue(withKey.contains("Daily cap: 20 \u2192 50")); assertTrue(withKey.contains("Budget: 7 \u2192 10"))
        assertTrue(withKey.contains("Schedule: any time \u2192 0 1 * * *")); assertTrue(withKey.contains("Storage key: set"))
        for (bad in listOf("SECRETSIGNATURE", "sig=", "blob.core", "sv=2022")) assertFalse(bad, withKey.contains(bad))
        val noKey = NodeSetup.fromJson("""{"v":1,"kind":"medusa-node-settings","nodeName":"x"}""").summary(cur).joinToString("\n")
        assertTrue(noKey.contains("Storage key: unchanged")); assertFalse(noKey.contains("Daily cap"))
    }

    @Test fun aWrongKindOrVersionIsRefused() {
        for (j in listOf("""{"v":1,"kind":"other"}""", """{"v":1}""", """{"v":2,"kind":"medusa-node-settings"}""", """{"kind":"medusa-node-settings"}""", "not json", "[]"))
            try { NodeSetup.fromJson(j); fail(j) } catch (e: SetupRejected) { assertFalse(e.message!!.contains("SECRET")) }
        for (l in listOf("https://x/y", "medusa://node-setup", "medusa://node-setup?d=!!!", "medusa://open?d=abc"))
            try { NodeSetup.fromLink(l); fail(l) } catch (e: SetupRejected) { }
    }

    @Test fun badValuesAreRefusedWithoutEchoingTheKey() {
        for (j in listOf("""{"v":1,"kind":"medusa-node-settings","conditions":"always"}""", """{"v":1,"kind":"medusa-node-settings","dailyCap":0}""", """{"v":1,"kind":"medusa-node-settings","budget":-1}""",
            """{"v":1,"kind":"medusa-node-settings","schedule":"nope"}""", """{"v":1,"kind":"medusa-node-settings","window":"soon"}""", """{"v":1,"kind":"medusa-node-settings","dailyCap":"ten"}"""))
            try { NodeSetup.fromJson(j); fail(j) } catch (e: SetupRejected) { }
        try { NodeSetup.fromJson("""{"v":1,"kind":"medusa-node-settings","sas":"TOPSECRET-not-a-url"}"""); fail() } catch (e: SetupRejected) { assertFalse(e.message!!.contains("TOPSECRET")) }
    }

    @Test fun theExportHasNoKeyAndReadsBack() {
        val json = NodeSetup.export(Store(sas = SAS).current())
        assertFalse(json.contains("sas")); assertFalse(json.contains("SECRET"))
        assertEquals("medusa-node-settings", JSONObject(json).getString("kind"))
        val s = Store(name = "other", sas = "keep"); NodeSetup.fromJson(json).apply(s)
        assertEquals("old", s.name); assertEquals("keep", s.sas)
    }
}
