package io.ref12.medusa.node

import org.junit.Assert.*
import org.junit.Test

class RedactTest {
    @Test fun sigIsRemovedFromUrlsAndMessages() {
        val u = "https://acct.blob.core.windows.net/media?sv=2022&sp=rl&sig=abc%2Bdef%3D&se=2027"
        val r = Redact.sas(u)
        assertFalse(r.contains("abc")); assertTrue(r.contains("sig=REDACTED&se=2027"))
        assertFalse(Redact.sas("GET " + u + " failed").contains("def"))
        assertFalse(Redact.sas("url?a=1&sig=ZZZ").contains("ZZZ"))
        assertFalse(Redact.sas("encoded sig%3DQQQ").contains("QQQ"))
        assertEquals("", Redact.sas(null))
    }
    @Test fun aSasUrlPrintsRedacted() {
        val s = SasUrl.parse("https://acct.blob.core.windows.net/media?sv=2022&sp=racwdlf&sig=TOPSECRET&se=2030-01-01T00:00:00Z")!!
        assertFalse(s.toString().contains("TOPSECRET")); assertEquals("racwdlf", s.permissions); assertTrue(s.can('f'))
        assertEquals("https://acct.blob.core.windows.net/media", s.container)
        assertTrue(s.blobUrl("index/jobs/a b.json").contains("index/jobs/a%20b.json?sv=2022"))
    }
    @Test fun badUrlsAreRejected() {
        assertNull(SasUrl.parse("https://acct.blob.core.windows.net/media")); assertNull(SasUrl.parse("not a url?sig=1"))
        assertNull(SasUrl.parse("https://acct.blob.core.windows.net/?sig=1")); assertNull(SasUrl.parse("https://a.b/c/d?sig=1"))
    }
    @Test fun permissionsAreDescribed() { assertEquals("read, list, find by tags", SasUrl.describe("rlf")); assertEquals("none", SasUrl.describe("")) }
}
