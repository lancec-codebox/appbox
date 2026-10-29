package io.ref12.medusa

import fi.iki.elonen.NanoHTTPD
import java.io.FileInputStream

/** The embedded HTTP server on 127.0.0.1; all behaviour lives in [Router]. */
class HelperServer(private val router: Router, port: Int = SERVER_PORT) : NanoHTTPD("127.0.0.1", port) {

    override fun serve(session: IHTTPSession): Response {
        val headers = session.headers // keys are lower case
        var body: String? = null
        if (session.method == Method.POST || session.method == Method.PUT) {
            val files = HashMap<String, String>()
            try { session.parseBody(files) } catch (e: Exception) { /* leave the body empty: the router answers 400 */ }
            body = files["postData"]
        }
        val reply = router.handle(session.method.name, session.uri, headers["origin"], headers, body)
        val status = object : Response.IStatus {
            override fun getRequestStatus() = reply.status
            override fun getDescription() = "${reply.status} " + when (reply.status) {
                200 -> "OK"; 204 -> "No Content"; 206 -> "Partial Content"; 400 -> "Bad Request"; 403 -> "Forbidden"
                404 -> "Not Found"; 405 -> "Method Not Allowed"; 409 -> "Conflict"; 416 -> "Range Not Satisfiable"; else -> "Error"
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
}
