package io.ref12.medusa.node

import org.w3c.dom.Element
import java.io.File
import java.io.FileInputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.Base64
import javax.xml.parsers.DocumentBuilderFactory

/** A failed storage call. The message never contains a signature. */
class BlobException(val status: Int, val errorCode: String?, message: String) : IOException(Redact.sas(message))

/** The upload was stopped by the caller (control stop, lost lease, system stop). */
class BlobCancelled : IOException("cancelled")

class JobRef(val name: String, val tags: Map<String, String>) {
    val id: String get() = name.substringAfterLast('/').removeSuffix(".json")
    val added: String get() = tags["idx_added"] ?: ""
}

class GetResult(val status: Int, val body: ByteArray, val etag: String?) { val text get() = String(body, Charsets.UTF_8) }

/** What the node needs from storage; the fake in the tests implements it too. */
interface BlobApi {
    /** Pending jobs of [stage] (Find Blobs by Tags, else List Blobs with tags). */
    fun findJobs(stage: String): List<JobRef>
    fun get(path: String, ifNoneMatch: String? = null): GetResult
    fun getTags(path: String): Map<String, String>
    /** Put Blob; [ifAbsent] sends If-None-Match: * and returns false when the blob exists. */
    fun put(path: String, body: ByteArray, contentType: String, tags: Map<String, String> = emptyMap(), leaseId: String? = null, ifAbsent: Boolean = false): Boolean
    fun putBlocks(path: String, file: File, contentType: String, tags: Map<String, String> = emptyMap(), cancelled: () -> Boolean = { false }, onProgress: (Long, Long) -> Unit = { _, _ -> })
    /** Acquire / renew / release. Acquire returns the lease id, or null when another node holds the blob (409). Renew throws BlobException when the lease is lost. */
    fun acquireLease(path: String, seconds: Int = 60): String?
    fun renewLease(path: String, leaseId: String)
    fun releaseLease(path: String, leaseId: String)
    fun setMetadata(path: String, leaseId: String?, meta: Map<String, String>)
    /** Lists blob names under [prefix] (for Test connection). */
    fun listNames(prefix: String, max: Int = 5000): List<String>
}

/** Azure Blob REST over a container SAS. HttpURLConnection only: nothing is added to the app for it. */
class BlobClient(val sas: SasUrl, private val retryDelayMs: Long = 1000, private val blockSize: Int = BLOCK, private val root: String = "index") : BlobApi {
    class Resp(val code: Int, val headers: Map<String, String>, val body: ByteArray) {
        val text get() = String(body, Charsets.UTF_8)
        fun header(n: String) = headers[n.lowercase()]
    }

    private fun send(method: String, url: String, headers: Map<String, String> = emptyMap(), body: ByteArray? = null, len: Int = body?.size ?: 0): Resp {
        val c = URL(url).openConnection() as HttpURLConnection
        try {
            c.requestMethod = method; c.connectTimeout = 20_000; c.readTimeout = 90_000
            c.setRequestProperty("x-ms-version", VERSION)
            headers.forEach { (k, v) -> c.setRequestProperty(k, v) }
            if (body != null) { c.doOutput = true; c.setFixedLengthStreamingMode(len); c.outputStream.use { it.write(body, 0, len) } }
            val code = c.responseCode
            val bytes = (if (code >= 400) c.errorStream else c.inputStream)?.use { it.readBytes() } ?: ByteArray(0)
            val h = HashMap<String, String>()
            for ((k, v) in c.headerFields) if (k != null && v.isNotEmpty()) h[k.lowercase()] = v[0]
            return Resp(code, h, bytes)
        } catch (e: BlobException) { throw e
        } catch (e: IOException) { throw BlobException(0, null, "network error: " + (e.message ?: e.javaClass.simpleName))
        } finally { c.disconnect() }
    }

    private fun fail(r: Resp, what: String): Nothing {
        val code = r.header("x-ms-error-code") ?: Regex("<Code>([^<]+)</Code>").find(r.text)?.groupValues?.get(1)
        throw BlobException(r.code, code, "$what failed: HTTP ${r.code}" + (if (code != null) " $code" else ""))
    }

    private fun tagHeader(tags: Map<String, String>) =
        if (tags.isEmpty()) emptyMap() else mapOf("x-ms-tags" to tags.entries.joinToString("&") { urlEnc(it.key) + "=" + urlEnc(it.value) })

    override fun findJobs(stage: String): List<JobRef> {
        val prefix = "$root/jobs/$stage/"
        val where = "\"idx_stage\"='$stage' AND \"idx_state\"='pending'"
        var found: List<JobRef>? = null
        if (sas.can('f')) {
            try { found = paged("restype=container&comp=blobs&where=" + urlEnc(where)) }
            catch (e: BlobException) { if (e.status != 400 && e.status != 403) throw e }
        }
        if (found == null) found = paged("restype=container&comp=list&include=tags&prefix=" + urlEnc(prefix))
        return found.filter { it.name.startsWith(prefix) && it.name.endsWith(".json") && it.tags["idx_stage"] == stage && it.tags["idx_state"] == "pending" }
            .sortedBy { it.added }
    }

    private fun paged(query: String): List<JobRef> {
        val out = ArrayList<JobRef>(); var marker = ""
        do {
            val r = send("GET", sas.containerUrl(query + (if (marker.isEmpty()) "" else "&marker=" + urlEnc(marker))))
            if (r.code != 200) fail(r, "Listing jobs")
            val doc = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(r.body.inputStream())
            val blobs = doc.getElementsByTagName("Blob")
            for (i in 0 until blobs.length) {
                val b = blobs.item(i) as Element
                val name = b.getElementsByTagName("Name").item(0)?.textContent ?: continue
                val tags = HashMap<String, String>(); val tg = b.getElementsByTagName("Tag")
                for (j in 0 until tg.length) { val t = tg.item(j) as Element; tags[t.getElementsByTagName("Key").item(0).textContent] = t.getElementsByTagName("Value").item(0)?.textContent ?: "" }
                out.add(JobRef(name, tags))
            }
            marker = doc.getElementsByTagName("NextMarker").item(0)?.textContent ?: ""
        } while (marker.isNotEmpty())
        return out
    }

    override fun listNames(prefix: String, max: Int): List<String> {
        val out = ArrayList<String>(); var marker = ""
        do {
            val r = send("GET", sas.containerUrl("restype=container&comp=list&prefix=" + urlEnc(prefix) + (if (marker.isEmpty()) "" else "&marker=" + urlEnc(marker))))
            if (r.code != 200) fail(r, "Listing")
            val doc = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(r.body.inputStream())
            val n = doc.getElementsByTagName("Name")
            for (i in 0 until n.length) out.add(n.item(i).textContent)
            marker = doc.getElementsByTagName("NextMarker").item(0)?.textContent ?: ""
        } while (marker.isNotEmpty() && out.size < max)
        return out
    }

    override fun get(path: String, ifNoneMatch: String?): GetResult {
        val r = send("GET", sas.blobUrl(path), if (ifNoneMatch != null) mapOf("If-None-Match" to ifNoneMatch) else emptyMap())
        return when (r.code) { 200, 304, 404 -> GetResult(r.code, r.body, r.header("etag")); else -> fail(r, "Get $path") }
    }

    override fun getTags(path: String): Map<String, String> {
        val r = send("GET", sas.blobUrl(path, "comp=tags"))
        if (r.code != 200) fail(r, "Get tags of $path")
        val doc = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(r.body.inputStream())
        val out = HashMap<String, String>(); val tg = doc.getElementsByTagName("Tag")
        for (j in 0 until tg.length) { val t = tg.item(j) as Element; out[t.getElementsByTagName("Key").item(0).textContent] = t.getElementsByTagName("Value").item(0)?.textContent ?: "" }
        return out
    }

    override fun put(path: String, body: ByteArray, contentType: String, tags: Map<String, String>, leaseId: String?, ifAbsent: Boolean): Boolean {
        val h = LinkedHashMap<String, String>()
        h["x-ms-blob-type"] = "BlockBlob"; h["Content-Type"] = contentType
        h.putAll(tagHeader(tags))
        if (leaseId != null) h["x-ms-lease-id"] = leaseId
        if (ifAbsent) h["If-None-Match"] = "*"
        val r = send("PUT", sas.blobUrl(path), h, body)
        if (r.code == 201) return true
        if (ifAbsent && (r.code == 409 || r.code == 412)) return false
        fail(r, "Put $path")
    }

    override fun putBlocks(path: String, file: File, contentType: String, tags: Map<String, String>, cancelled: () -> Boolean, onProgress: (Long, Long) -> Unit) {
        val total = file.length()
        if (total == 0L) { put(path, ByteArray(0), contentType, tags); return }
        val ids = ArrayList<String>(); val buf = ByteArray(minOf(blockSize.toLong(), total).toInt()); var sent = 0L
        FileInputStream(file).use { ins ->
            while (true) {
                if (cancelled()) throw BlobCancelled()
                var n = 0
                while (n < buf.size) { val k = ins.read(buf, n, buf.size - n); if (k < 0) break; n += k }
                if (n <= 0) break
                val id = blockId(ids.size)
                putBlock(path, id, buf, n, cancelled)
                ids.add(id); sent += n; onProgress(sent, total)
            }
        }
        val xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>" + ids.joinToString("") { "<Latest>$it</Latest>" } + "</BlockList>"
        val h = LinkedHashMap<String, String>()
        h["x-ms-blob-content-type"] = contentType; h["Content-Type"] = "application/xml"; h.putAll(tagHeader(tags))
        val r = send("PUT", sas.blobUrl(path, "comp=blocklist"), h, xml.toByteArray(Charsets.UTF_8))
        if (r.code != 201) fail(r, "Put Block List $path")
    }

    private fun putBlock(path: String, id: String, buf: ByteArray, n: Int, cancelled: () -> Boolean) {
        var last: BlobException? = null
        for (attempt in 1..3) {
            if (cancelled()) throw BlobCancelled()
            try {
                val r = send("PUT", sas.blobUrl(path, "comp=block&blockid=" + urlEnc(id)), mapOf("Content-Type" to "application/octet-stream"), buf, n)
                if (r.code == 201) return
                val e = try { fail(r, "Put Block $path") } catch (e: BlobException) { e }
                if (r.code in 400..499 && r.code != 408 && r.code != 429) throw e
                last = e
            } catch (e: BlobException) {
                if (e.status in 400..499 && e.status != 408 && e.status != 429) throw e
                last = e
            }
            if (attempt < 3 && retryDelayMs > 0) Thread.sleep(retryDelayMs * attempt)
        }
        throw last!!
    }

    override fun acquireLease(path: String, seconds: Int): String? {
        val r = send("PUT", sas.blobUrl(path, "comp=lease"), mapOf("x-ms-lease-action" to "acquire", "x-ms-lease-duration" to seconds.toString()), ByteArray(0))
        if (r.code == 201) return r.header("x-ms-lease-id")
        if (r.code == 409) return null
        fail(r, "Lease acquire $path")
    }

    override fun renewLease(path: String, leaseId: String) {
        val r = send("PUT", sas.blobUrl(path, "comp=lease"), mapOf("x-ms-lease-action" to "renew", "x-ms-lease-id" to leaseId), ByteArray(0))
        if (r.code != 200) fail(r, "Lease renew $path")
    }

    override fun releaseLease(path: String, leaseId: String) {
        val r = send("PUT", sas.blobUrl(path, "comp=lease"), mapOf("x-ms-lease-action" to "release", "x-ms-lease-id" to leaseId), ByteArray(0))
        if (r.code != 200) fail(r, "Lease release $path")
    }

    /** Delete Blob (the node itself never deletes; the live test cleans up with it). */
    fun delete(path: String) {
        val r = send("DELETE", sas.blobUrl(path))
        if (r.code != 202 && r.code != 404) fail(r, "Delete $path")
    }

    override fun setMetadata(path: String, leaseId: String?, meta: Map<String, String>) {
        val h = LinkedHashMap<String, String>()
        meta.forEach { (k, v) -> h["x-ms-meta-$k"] = v.filter { it.code in 32..126 } }
        if (leaseId != null) h["x-ms-lease-id"] = leaseId
        val r = send("PUT", sas.blobUrl(path, "comp=metadata"), h, ByteArray(0))
        if (r.code != 200) fail(r, "Set metadata of $path")
    }

    companion object {
        const val VERSION = "2021-12-02"
        const val BLOCK = 8 * 1024 * 1024
        fun blockId(i: Int): String = Base64.getEncoder().encodeToString("%08d".format(i).toByteArray())
    }
}
