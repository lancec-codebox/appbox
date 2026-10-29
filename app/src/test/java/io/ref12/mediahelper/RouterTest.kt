package io.ref12.mediahelper

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class RouterTest {
    private class FakeApi : HelperApi {
        var touched = 0
        var submitted: Triple<String?, String, List<String>?>? = null
        var info: JobInfo? = JobInfo("j1", JobState.DONE, null)
        var result: String? = """{"url":"u","lines":[]}"""
        override fun appVersion() = "1.0.0"
        override fun ytDlpVersion() = "2026.10.01"
        override fun submit(id: String?, url: String, languages: List<String>?): String { submitted = Triple(id, url, languages); return id ?: "new" }
        override fun jobInfo(id: String) = info?.takeIf { it.id == id }
        override fun resultJson(id: String) = result
        override fun touch() { touched++ }
    }

    private val api = FakeApi()
    private val router = Router(api) { DEFAULT_ORIGINS }
    private val origin = "https://ref12.github.io"

    private fun get(path: String, o: String? = origin) = router.handle("GET", path, o, emptyMap(), null)

    @Test fun status_with_cors_header() {
        val r = get("/v1/status")
        assertEquals(200, r.status)
        val j = JSONObject(r.body)
        assertEquals("media-helper", j.getString("app"))
        assertEquals("1.0.0", j.getString("version"))
        assertEquals("2026.10.01", j.getString("ytDlp"))
        assertEquals(origin, r.headers["Access-Control-Allow-Origin"])
        assertEquals("Origin", r.headers["Vary"])
        assertEquals(1, api.touched)
    }

    @Test fun preflight_has_private_network_header() {
        val r = router.handle("OPTIONS", "/v1/jobs", "http://localhost:5173",
            mapOf("access-control-request-headers" to "content-type", "access-control-request-private-network" to "true"), null)
        assertEquals(204, r.status)
        assertEquals("true", r.headers["Access-Control-Allow-Private-Network"])
        assertEquals("http://localhost:5173", r.headers["Access-Control-Allow-Origin"])
        assertEquals("content-type", r.headers["Access-Control-Allow-Headers"])
        assertTrue(r.headers["Access-Control-Allow-Methods"]!!.contains("POST"))
    }

    @Test fun unlisted_origin_is_refused_without_cors_headers() {
        val r = get("/v1/status", "https://evil.example")
        assertEquals(403, r.status)
        assertNull(r.headers["Access-Control-Allow-Origin"])
        val p = router.handle("OPTIONS", "/v1/jobs", "https://evil.example", emptyMap(), null)
        assertEquals(403, p.status)
        assertNull(p.headers["Access-Control-Allow-Private-Network"])
    }

    @Test fun no_origin_header_is_fine_and_gets_no_cors_headers() {
        val r = get("/v1/status", null)
        assertEquals(200, r.status)
        assertNull(r.headers["Access-Control-Allow-Origin"])
    }

    @Test fun origin_match_ignores_trailing_slash_and_follows_settings() {
        val r = Router(api) { listOf("https://my.app/") }
        assertEquals(200, r.handle("GET", "/v1/status", "https://my.app", emptyMap(), null).status)
        assertEquals(403, r.handle("GET", "/v1/status", origin, emptyMap(), null).status)
    }

    @Test fun create_job() {
        val r = router.handle("POST", "/v1/jobs", origin, emptyMap(), """{"id":"abc","url":"https://youtu.be/x","languages":["zh","en"]}""")
        assertEquals(200, r.status)
        assertEquals("abc", JSONObject(r.body).getString("id"))
        assertEquals(Triple<String?, String, List<String>?>("abc", "https://youtu.be/x", listOf("zh", "en")), api.submitted)
        val d = router.handle("POST", "/v1/jobs", origin, emptyMap(), """{"url":"https://youtu.be/x"}""")
        assertEquals("new", JSONObject(d.body).getString("id"))
        assertNull(api.submitted!!.third)
    }

    @Test fun create_job_validation() {
        assertEquals(400, router.handle("POST", "/v1/jobs", origin, emptyMap(), "nope").status)
        assertEquals(400, router.handle("POST", "/v1/jobs", origin, emptyMap(), null).status)
        assertEquals(400, router.handle("POST", "/v1/jobs", origin, emptyMap(), """{"url":"file:///x"}""").status)
        assertEquals(400, router.handle("POST", "/v1/jobs", origin, emptyMap(), """{"url":"https://a.b","id":"../x"}""").status)
        assertEquals(400, router.handle("POST", "/v1/jobs", origin, emptyMap(), """{"url":"https://a.b","languages":"zh"}""").status)
    }

    @Test fun job_status_and_failure_message() {
        api.info = JobInfo("j1", JobState.FAILED, "No Chinese captions")
        val j = JSONObject(get("/v1/jobs/j1").body)
        assertEquals("failed", j.getString("state"))
        assertEquals("No Chinese captions", j.getString("message"))
        assertEquals(404, get("/v1/jobs/other").status)
    }

    @Test fun result_only_when_done() {
        assertEquals(200, get("/v1/jobs/j1/result").status)
        api.info = JobInfo("j1", JobState.FETCHING, "Listing captions")
        val r = get("/v1/jobs/j1/result")
        assertEquals(409, r.status)
        assertEquals("fetching", JSONObject(r.body).getString("state"))
        api.info = JobInfo("j1", JobState.DONE, null); api.result = null
        assertEquals(404, get("/v1/jobs/j1/result").status)
        assertEquals(404, get("/v1/jobs/zzz/result").status)
    }

    @Test fun unknown_routes_and_wrong_methods() {
        assertEquals(404, get("/").status)
        assertEquals(404, get("/v2/status").status)
        assertEquals(404, get("/v1/nothing").status)
        assertEquals(405, router.handle("POST", "/v1/status", origin, emptyMap(), "{}").status)
        assertEquals(405, router.handle("GET", "/v1/jobs", origin, emptyMap(), null).status)
        assertEquals(200, get("/v1/status/").status)
        assertEquals(200, get("/v1/jobs/j1?x=1").status)
    }
}
