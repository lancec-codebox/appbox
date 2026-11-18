package io.ref12.medusa

import fi.iki.elonen.NanoHTTPD
import java.io.FileInputStream

/** The embedded HTTP server on 127.0.0.1; all behaviour lives in [Router]. */
class HelperServer(private val router: Router, port: Int = SERVER_PORT, private val v2: io.ref12.medusa.v2.V2Router? = null) : NanoHTTPD("127.0.0.1", port) {

    override fun serve(session: IHTTPSession): Response {
        val headers = session.headers // keys are lower case
        if (v2 != null && (session.uri == "/v2" || session.uri.startsWith("/v2/"))) return respond(serveV2(session, v2))
        var body: String? = null
        if (session.method == Method.POST || session.method == Method.PUT) {
            val files = HashMap<String, String>()
            try { session.parseBody(files) } catch (e: Exception) { /* leave the body empty: the router answers 400 */ }
            body = files["postData"]
        }
        return respond(router.handle(session.method.name, session.uri, headers["origin"], headers, body))
    }

    /** /v2 reads its body straight from the socket, so a large upload never sits in memory. */
    private fun serveV2(session: IHTTPSession, v2: io.ref12.medusa.v2.V2Router): HttpReply {
        val headers = session.headers
        val len = headers["content-length"]?.toLongOrNull() ?: -1
        val hasBody = session.method == Method.POST || session.method == Method.PUT
        var body: String? = null
        var stream: java.io.InputStream? = null
        if (hasBody && session.uri.contains("/files/")) stream = session.inputStream
        else if (hasBody && len in 0..MAX_JSON_BODY) {
            val buf = ByteArray(len.toInt()); var off = 0
            try { while (off < buf.size) { val n = session.inputStream.read(buf, off, buf.size - off); if (n < 0) break; off += n } } catch (e: java.io.IOException) { }
            body = String(buf, 0, off, Charsets.UTF_8)
        } else if (hasBody && len > MAX_JSON_BODY) return HttpReply(413, mapOf("Content-Type" to "application/json"), "{\"error\":\"body too large\"}")
        return v2.handle(io.ref12.medusa.v2.V2Request(session.method.name, session.uri, session.queryParameterString, headers, body, stream, len))
    }

    private fun respond(reply: HttpReply): Response {
        val status = object : Response.IStatus {
            override fun getRequestStatus() = reply.status
            override fun getDescription() = "${reply.status} " + when (reply.status) {
                200 -> "OK"; 204 -> "No Content"; 206 -> "Partial Content"; 400 -> "Bad Request"; 403 -> "Forbidden"
                401 -> "Unauthorized"; 404 -> "Not Found"; 405 -> "Method Not Allowed"; 409 -> "Conflict"; 411 -> "Length Required"
                413 -> "Payload Too Large"; 416 -> "Range Not Satisfiable"; 429 -> "Too Many Requests"; else -> "Error"
            }
        }
        val type = reply.headers["Content-Type"] ?: "text/plain"
        val r = if (reply.file != null) {
            val ins = FileInputStream(reply.file)
            ins.skip(reply.fileOffset)
            newFixedLengthResponse(status, type, ins, reply.fileLength)
        } else newFixedLengthResponse(status, type, reply.body)
        for ((k, v) in reply.headers) if (k != "Content-Type") r.addHeader(k, v)
        return r
    }

    private companion object { const val MAX_JSON_BODY = 1_000_000L }
}
