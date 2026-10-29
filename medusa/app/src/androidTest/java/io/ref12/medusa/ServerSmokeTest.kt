package io.ref12.medusa

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.net.HttpURLConnection
import java.net.URL

/** Starts the real embedded server on a free loopback port and talks HTTP to it. */
@RunWith(AndroidJUnit4::class)
class ServerSmokeTest {
    private lateinit var server: HelperServer
    private val videoBytes = ByteArray(5000) { (it % 251).toByte() }
    private lateinit var videoFile: java.io.File
    private val api = object : HelperApi {
        override fun appVersion() = "test"
        override fun ytDlpVersion() = "none"
        override fun submit(req: JobSubmit) = req.id ?: "job-1"
        override fun jobInfo(id: String) = JobInfo(id, JobState.DOWNLOADING, 0.5, null, "Downloading")
        override fun resultJson(id: String): String? = null
        override fun mediaListJson() = "[]"
        override fun mediaJson(mediaId: String): String? = null
        override fun deleteMedia(mediaId: String) = false
        override fun videoFile(mediaId: String) = if (mediaId == "local") videoFile to "video/mp4" else null
        override fun sync(mediaId: String): List<Anchor>? = null
        override fun putSync(mediaId: String, anchors: List<Anchor>) = false
        override fun subtitles(mediaId: String, language: String, trackId: String?): SubtitlesData? = null
        override fun addTrack(mediaId: String, track: AddedTrack): String? = null
        override fun removeTrack(mediaId: String, trackId: String) = false
        override fun setSubtitles(mediaId: String, language: String): String? = null
        override fun touch() {}
    }

    @Before fun start() {
        videoFile = java.io.File.createTempFile("local", ".mp4").also { it.writeBytes(videoBytes) }
        server = HelperServer(Router(api) { DEFAULT_ORIGINS }, 0)
        server.start(5000, false)
    }

    @After fun stop() { server.stop(); videoFile.delete() }

    @Test fun local_mp4_is_served_with_range_support() {
        val full = open("/v1/media/local/video", "GET", "https://ref12.github.io")
        assertEquals(200, full.responseCode)
        assertEquals("bytes", full.getHeaderField("Accept-Ranges"))
        assertEquals(5000, full.inputStream.readBytes().size)
        val part = open("/v1/media/local/video", "GET", "https://ref12.github.io")
        part.setRequestProperty("Range", "bytes=1000-1999")
        assertEquals(206, part.responseCode)
        assertEquals("bytes 1000-1999/5000", part.getHeaderField("Content-Range"))
        val got = part.inputStream.readBytes()
        assertEquals(1000, got.size)
        assertEquals(videoBytes.copyOfRange(1000, 2000).toList(), got.toList())
        val bad = open("/v1/media/local/video", "GET", "https://ref12.github.io")
        bad.setRequestProperty("Range", "bytes=9000-")
        assertEquals(416, bad.responseCode)
    }

    private fun open(path: String, method: String, origin: String): HttpURLConnection =
        (URL("http://127.0.0.1:" + server.listeningPort + path).openConnection() as HttpURLConnection).apply {
            requestMethod = method
            setRequestProperty("Origin", origin)
        }

    @Test fun status_over_http_with_cors() {
        val c = open("/v1/status", "GET", "https://ref12.github.io")
        assertEquals(200, c.responseCode)
        assertEquals("https://ref12.github.io", c.getHeaderField("Access-Control-Allow-Origin"))
        assertEquals("medusa", JSONObject(c.inputStream.bufferedReader().readText()).getString("app"))
    }

    @Test fun preflight_and_job_creation() {
        val p = open("/v1/jobs", "OPTIONS", "http://localhost:5173")
        p.setRequestProperty("Access-Control-Request-Method", "POST")
        assertEquals(204, p.responseCode)
        assertEquals("true", p.getHeaderField("Access-Control-Allow-Private-Network"))

        val c = open("/v1/jobs", "POST", "http://localhost:5173")
        c.doOutput = true
        c.setRequestProperty("Content-Type", "application/json")
        c.outputStream.use { it.write("""{"id":"abc","url":"https://youtu.be/x"}""".toByteArray()) }
        assertEquals(200, c.responseCode)
        assertEquals("abc", JSONObject(c.inputStream.bufferedReader().readText()).getString("id"))
    }

    @Test fun foreign_origin_is_refused() {
        assertEquals(403, open("/v1/status", "GET", "https://evil.example").responseCode)
    }
}
