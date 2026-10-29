package io.ref12.medusa

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class RouterTest {
    @get:Rule val tmp = TemporaryFolder()

    private class FakeApi(val video: File?) : HelperApi {
        var touched = 0
        var submitted: JobSubmit? = null
        var info: JobInfo? = JobInfo("j1", JobState.DONE, 1.0, null, null)
        var result: String? = """{"url":"u","lines":[]}"""
        val deleted = ArrayList<String>()
        var sync = listOf<Anchor>()
        override fun appVersion() = "1.1.0"
        override fun ytDlpVersion() = "2026.10.01"
        override fun submit(req: JobSubmit): String { submitted = req; return req.id ?: "new" }
        override fun jobInfo(id: String) = info?.takeIf { it.id == id }
        override fun resultJson(id: String) = result
        override fun mediaListJson() = """[{"mediaId":"m1"}]"""
        override fun mediaJson(mediaId: String) = if (mediaId == "m1") """{"mediaId":"m1"}""" else null
        override fun deleteMedia(mediaId: String) = mediaId == "m1" && deleted.add(mediaId)
        override fun videoFile(mediaId: String) = if (mediaId == "m1" && video != null) video to "video/mp4" else null
        override fun sync(mediaId: String) = if (mediaId == "m1") sync else null
        override fun putSync(mediaId: String, anchors: List<Anchor>): Boolean { if (mediaId != "m1") return false; sync = anchors; return true }
        var subsResult: SubtitlesData? = SubtitlesData("captions", "en", listOf(Line(1.0, 2.0, "hi")))
        var subsError: FetchException? = null
        var asked: String? = null
        var mainSet: String? = null
        override fun subtitles(mediaId: String, language: String): SubtitlesData? {
            asked = language
            subsError?.let { throw it }
            return if (mediaId == "m1") subsResult else null
        }
        override fun setSubtitles(mediaId: String, language: String): String? {
            subsError?.let { throw it }
            if (mediaId != "m1") return null
            mainSet = language
            return """{"mediaId":"m1","subtitles":{"language":"$language"}}"""
        }
        override fun touch() { touched++ }

    }

    private val video by lazy { tmp.newFile("v.mp4").also { it.writeBytes(ByteArray(1000) { b -> b.toByte() }) } }
    private val api by lazy { FakeApi(video) }
    private val router by lazy { Router(api) { DEFAULT_ORIGINS } }
    private val origin = "https://ref12.github.io"

    private fun get(path: String, o: String? = origin, h: Map<String, String> = emptyMap()) = router.handle("GET", path, o, h, null)
    private fun post(body: String?) = router.handle("POST", "/v1/jobs", origin, emptyMap(), body)

    @Test fun status_with_cors_header() {
        val r = get("/v1/status")
        assertEquals(200, r.status)
        val j = JSONObject(r.body)
        assertEquals("medusa", j.getString("app"))
        assertEquals("1.1.0", j.getString("version"))
        assertEquals(origin, r.headers["Access-Control-Allow-Origin"])
        assertEquals(1, api.touched)
    }

    @Test fun preflight_has_private_network_header_and_all_methods() {
        val r = router.handle("OPTIONS", "/v1/media/m1/sync", "http://localhost:5173",
            mapOf("access-control-request-headers" to "content-type", "access-control-request-private-network" to "true"), null)
        assertEquals(204, r.status)
        assertEquals("true", r.headers["Access-Control-Allow-Private-Network"])
        assertEquals("http://localhost:5173", r.headers["Access-Control-Allow-Origin"])
        assertTrue(r.headers["Access-Control-Allow-Methods"]!!.let { it.contains("PUT") && it.contains("DELETE") })
    }

    @Test fun unlisted_origin_is_refused_and_no_origin_is_fine() {
        val r = get("/v1/status", "https://evil.example")
        assertEquals(403, r.status)
        assertNull(r.headers["Access-Control-Allow-Origin"])
        assertEquals(403, router.handle("OPTIONS", "/v1/jobs", "https://evil.example", emptyMap(), null).status)
        assertNull(get("/v1/status", null).headers["Access-Control-Allow-Origin"])
    }

    @Test fun create_job_defaults_and_options() {
        val r = post("""{"id":"abc","url":"https://youtu.be/x","languages":["zh","en"],"want":["subtitles"],"quality":"480p"}""")
        assertEquals(200, r.status)
        assertEquals("abc", JSONObject(r.body).getString("id"))
        val s = api.submitted!!
        assertEquals(listOf("zh", "en"), s.languages)
        assertEquals(Want(true, false), s.want)
        assertEquals(Quality.P480, s.quality)
        post("""{"url":"https://youtu.be/x"}""")
        assertEquals(Want(true, true), api.submitted!!.want) // default: both
        assertEquals(Quality.P720, api.submitted!!.quality)
        assertNull(api.submitted!!.languages)
    }

    @Test fun create_job_validation() {
        assertEquals(400, post("nope").status)
        assertEquals(400, post(null).status)
        assertEquals(400, post("""{"url":"file:///x"}""").status)
        assertEquals(400, post("""{"url":"https://a.b","id":"../x"}""").status)
        assertEquals(400, post("""{"url":"https://a.b","languages":"zh"}""").status)
        assertEquals(400, post("""{"url":"https://a.b","want":["audio"]}""").status)
        assertEquals(400, post("""{"url":"https://a.b","want":[]}""").status)
        assertEquals(400, post("""{"url":"https://a.b","quality":"4k"}""").status)
    }

    @Test fun job_status_has_progress_code_and_message() {
        api.info = JobInfo("j1", JobState.FAILED, null, "no_captions", "No Chinese captions")
        val j = JSONObject(get("/v1/jobs/j1").body)
        assertEquals("failed", j.getString("state"))
        assertEquals("no_captions", j.getString("code"))
        assertEquals("No Chinese captions", j.getString("message"))
        api.info = JobInfo("j1", JobState.DOWNLOADING, 0.4567, null, null)
        val p = JSONObject(get("/v1/jobs/j1").body)
        assertEquals("downloading", p.getString("state"))
        assertEquals(0.457, p.getDouble("progress"), 1e-9)
        assertEquals(404, get("/v1/jobs/other").status)
    }

    @Test fun result_only_when_done() {
        assertEquals(200, get("/v1/jobs/j1/result").status)
        api.info = JobInfo("j1", JobState.FETCHING, 0.1, null, "x")
        assertEquals(409, get("/v1/jobs/j1/result").status)
        api.info = JobInfo("j1", JobState.DONE, 1.0, null, null); api.result = null
        assertEquals(404, get("/v1/jobs/j1/result").status)
    }

    @Test fun media_list_detail_and_delete() {
        assertEquals("""[{"mediaId":"m1"}]""", get("/v1/media").body)
        assertEquals(200, get("/v1/media/m1").status)
        assertEquals(404, get("/v1/media/zz").status)
        assertEquals(200, router.handle("DELETE", "/v1/media/m1", origin, emptyMap(), null).status)
        assertEquals(listOf("m1"), api.deleted)
        assertEquals(404, router.handle("DELETE", "/v1/media/zz", origin, emptyMap(), null).status)
        assertEquals(405, router.handle("POST", "/v1/media/m1", origin, emptyMap(), null).status)
    }

    @Test fun video_full_range_and_unsatisfiable() {
        val f = get("/v1/media/m1/video")
        assertEquals(200, f.status)
        assertEquals("bytes", f.headers["Accept-Ranges"])
        assertEquals("video/mp4", f.headers["Content-Type"])
        assertEquals(1000L, f.fileLength)
        val p = get("/v1/media/m1/video", h = mapOf("range" to "bytes=100-199"))
        assertEquals(206, p.status)
        assertEquals("bytes 100-199/1000", p.headers["Content-Range"])
        assertEquals(100L, p.fileLength)
        assertEquals(100L, p.fileOffset)
        val s = get("/v1/media/m1/video", h = mapOf("range" to "bytes=-10"))
        assertEquals("bytes 990-999/1000", s.headers["Content-Range"])
        val u = get("/v1/media/m1/video", h = mapOf("range" to "bytes=5000-"))
        assertEquals(416, u.status)
        assertEquals("bytes */1000", u.headers["Content-Range"])
        assertEquals(404, get("/v1/media/zz/video").status)
    }

    @Test fun sync_get_and_put() {
        assertEquals(0, JSONObject(get("/v1/media/m1/sync").body).getJSONArray("sync").length())
        val r = router.handle("PUT", "/v1/media/m1/sync", origin, emptyMap(), """{"sync":[{"at":30,"offset":0.5},{"at":10,"offset":-0.25}]}""")
        assertEquals(200, r.status)
        assertEquals(listOf(Anchor(10.0, -0.25), Anchor(30.0, 0.5)), api.sync) // sorted
        assertEquals(400, router.handle("PUT", "/v1/media/m1/sync", origin, emptyMap(), """{"sync":[{"at":"x"}]}""").status)
        assertEquals(400, router.handle("PUT", "/v1/media/m1/sync", origin, emptyMap(), "{}").status)
        assertEquals(404, router.handle("PUT", "/v1/media/zz/sync", origin, emptyMap(), """{"sync":[]}""").status)
        assertEquals(404, get("/v1/media/zz/sync").status)
    }

    @Test fun unknown_routes_and_wrong_methods() {
        assertEquals(404, get("/").status)
        assertEquals(404, get("/v2/status").status)
        assertEquals(405, router.handle("POST", "/v1/status", origin, emptyMap(), "{}").status)
        assertEquals(405, router.handle("GET", "/v1/jobs", origin, emptyMap(), null).status)
        assertEquals(200, get("/v1/status/").status)
    }

    @Test fun subtitles_get_returns_another_tracks_lines() {
        val r = get("/v1/media/m1/subtitles?language=en")
        assertEquals(200, r.status)
        val j = JSONObject(r.body)
        assertEquals("en", j.getString("language"))
        assertEquals("hi", j.getJSONArray("lines").getJSONObject(0).getString("text"))
        get("/v1/media/m1/subtitles?language=zh-Hans")
        assertEquals("zh-Hans", api.asked)
    }

    @Test fun subtitles_get_errors() {
        assertEquals(400, get("/v1/media/m1/subtitles").status)
        assertEquals(404, get("/v1/media/zz/subtitles?language=en").status)
        api.subsError = FetchException("No captions in ko (available: en)", Codes.NO_CAPTIONS)
        val r = get("/v1/media/m1/subtitles?language=ko")
        assertEquals(404, r.status)
        assertEquals("no_captions", JSONObject(r.body).getString("code"))
        api.subsError = FetchException("Network problem", Codes.NETWORK)
        assertEquals(502, get("/v1/media/m1/subtitles?language=en").status)
    }

    @Test fun subtitles_post_changes_the_main_track() {
        val r = router.handle("POST", "/v1/media/m1/subtitles", origin, emptyMap(), """{"language":"fr"}""")
        assertEquals(200, r.status)
        assertEquals("fr", JSONObject(r.body).getJSONObject("subtitles").getString("language"))
        assertEquals("fr", api.mainSet)
        assertEquals(400, router.handle("POST", "/v1/media/m1/subtitles", origin, emptyMap(), "{}").status)
        assertEquals(400, router.handle("POST", "/v1/media/m1/subtitles", origin, emptyMap(), "nope").status)
        assertEquals(404, router.handle("POST", "/v1/media/zz/subtitles", origin, emptyMap(), """{"language":"fr"}""").status)
        assertEquals(405, router.handle("DELETE", "/v1/media/m1/subtitles", origin, emptyMap(), null).status)
    }

    @Test fun v11_client_sending_zh_languages_is_accepted_unchanged() {
        val r = post("""{"id":"c1","url":"https://youtu.be/x","languages":["zh-Hans","zh-CN","zh","zh-Hant","zh-TW"]}""")
        assertEquals(200, r.status)
        assertEquals(listOf("zh-Hans", "zh-CN", "zh", "zh-Hant", "zh-TW"), api.submitted!!.languages)
        post("""{"url":"https://youtu.be/x"}""")
        assertNull(api.submitted!!.languages)
    }
}

