package io.ref12.medusa

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DeepLinkTest {
    @Test fun parses_all_fields_and_percent_decoding() {
        val r = DeepLink.parse("medusa://open?job=abc-123&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DX%26t%3D5%2B1&return=https%3A%2F%2Fref12.github.io%2Fapp%2F%23%2Fmedia")!!
        assertEquals("abc-123", r.id)
        assertEquals("https://www.youtube.com/watch?v=X&t=5+1", r.url)
        assertEquals("https://ref12.github.io/app/#/media", r.returnUrl)
    }

    @Test fun return_is_optional_and_bad_job_ids_are_replaced() {
        val r = DeepLink.parse("medusa://open?job=../../etc&url=https%3A%2F%2Fa.b%2Fc", { "gen-1" })!!
        assertEquals("gen-1", r.id)
        assertNull(r.returnUrl)
    }

    @Test fun rejects_missing_or_non_http_url_and_other_links() {
        assertNull(DeepLink.parse("medusa://open?job=a"))
        assertNull(DeepLink.parse("medusa://open?url=file%3A%2F%2F%2Fsdcard"))
        assertNull(DeepLink.parse("https://example.com/?url=https%3A%2F%2Fa.b"))
        assertNull(DeepLink.parse(null))
    }

    @Test fun non_http_return_is_dropped() {
        assertNull(DeepLink.parse("medusa://open?url=https%3A%2F%2Fa.b&return=javascript%3Aalert(1)")!!.returnUrl)
    }

    @Test fun return_url_goes_to_query_without_hash() {
        assertEquals("https://x.y/media?job=j1&state=done", DeepLink.buildReturnUrl("https://x.y/media", "j1", "done"))
        assertEquals("https://x.y/media?a=1&job=j1&state=failed", DeepLink.buildReturnUrl("https://x.y/media?a=1", "j1", "failed"))
    }

    @Test fun return_url_goes_to_hash_query_with_hash() {
        assertEquals("https://x.y/app/#/media?job=j1&state=done", DeepLink.buildReturnUrl("https://x.y/app/#/media", "j1", "done"))
        assertEquals("https://x.y/#/m?k=v&job=j1&state=done", DeepLink.buildReturnUrl("https://x.y/#/m?k=v", "j1", "done"))
    }
}
