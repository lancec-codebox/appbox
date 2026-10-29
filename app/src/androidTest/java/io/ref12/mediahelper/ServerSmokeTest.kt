package io.ref12.mediahelper

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
    private val api = object : HelperApi {
        override fun appVersion() = "test"
        override fun ytDlpVersion() = "none"
        override fun submit(id: String?, url: String, languages: List<String>?) = id ?: "job-1"
        override fun jobInfo(id: String) = JobInfo(id, JobState.FETCHING, "Listing captions")
        override fun resultJson(id: String): String? = null
        override fun touch() {}
    }

    @Before fun start() {
        server = HelperServer(Router(api) { DEFAULT_ORIGINS }, 0)
        server.start(5000, false)
    }

    @After fun stop() = server.stop()

    private fun open(path: String, method: String, origin: String): HttpURLConnection =
        (URL("http://127.0.0.1:" + server.listeningPort + path).openConnection() as HttpURLConnection).apply {
            requestMethod = method
            setRequestProperty("Origin", origin)
        }

    @Test fun status_over_http_with_cors() {
        val c = open("/v1/status", "GET", "https://ref12.github.io")
        assertEquals(200, c.responseCode)
        assertEquals("https://ref12.github.io", c.getHeaderField("Access-Control-Allow-Origin"))
        assertEquals("media-helper", JSONObject(c.inputStream.bufferedReader().readText()).getString("app"))
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
