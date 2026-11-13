package io.ref12.medusa.node

import org.junit.Assert.*
import org.junit.Test

class CrashRedactionTest {
    @Test fun aSignatureInACauseIsRedacted() {
        val e = RuntimeException("outer", java.io.IOException("GET https://a.blob.core.windows.net/c?sp=r&sig=SECRET failed"))
        val s = CrashRedaction.sanitize(e)
        assertFalse(generateSequence(s) { it.cause }.any { it.message!!.contains("SECRET") })
        assertTrue(s.cause!!.message!!.contains("sig=REDACTED"))
        val plain = IllegalStateException("fine"); assertSame(plain, CrashRedaction.sanitize(plain))
    }
}
