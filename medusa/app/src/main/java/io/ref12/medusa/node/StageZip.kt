package io.ref12.medusa.node

import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.security.MessageDigest
import java.util.zip.CRC32
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/**
 * IndexNode 0.10.0's download.zip, written the way its download stage writes it: a standard zip, members stored (no compression) with real CRC-32, sorted by name,
 * the members' names being the paths under the video folder (info.json, description.txt, thumb.jpg, subs/<lang>.vtt). downloaded.json is not a member: its facts are
 * the zip's idx_summary. Blob metadata: idx_v, idx_sha256, idx_inputs, idx_node, idx_at, idx_summary (ASCII-escaped compact JSON, all under 8 KB).
 */
object StageZip {
    const val FORMAT_VERSION = 1
    const val MAX_METADATA = 8000
    const val NAME = "download.zip"

    fun build(members: List<Pair<String, File>>, out: File): File {
        ZipOutputStream(FileOutputStream(out)).use { z ->
            z.setLevel(0)
            for ((name, f) in members.sortedBy { it.first }) {
                val bytes = f.readBytes(); val crc = CRC32().also { it.update(bytes) }
                val e = ZipEntry(name); e.method = ZipEntry.STORED; e.size = bytes.size.toLong(); e.compressedSize = bytes.size.toLong(); e.crc = crc.value
                e.time = 0
                z.putNextEntry(e); z.write(bytes); z.closeEntry()
            }
        }
        return out
    }

    fun sha256(f: File): String { val md = MessageDigest.getInstance("SHA-256"); f.inputStream().use { i -> val b = ByteArray(1 shl 16); while (true) { val n = i.read(b); if (n < 0) break; md.update(b, 0, n) } }; return md.digest().joinToString("") { "%02x".format(it) } }

    /** The download stage has no input zips: its fingerprint is the hash of the empty list, as IndexNode's StageLayout.Inputs computes it. */
    val NO_INPUTS: String = MessageDigest.getInstance("SHA-256").digest(ByteArray(0)).joinToString("") { "%02x".format(it) }

    fun ascii(s: String): String { val b = StringBuilder(); for (c in s) if (c.code < 128) b.append(c) else b.append("\\u").append("%04x".format(c.code)); return b.toString() }

    fun metadata(zip: File, nodeId: String, at: String, summary: JSONObject): Map<String, String> {
        val m = linkedMapOf("idx_v" to FORMAT_VERSION.toString(), "idx_sha256" to sha256(zip), "idx_inputs" to NO_INPUTS,
            "idx_node" to nodeId.filter { it.code in 32..126 }, "idx_at" to at, "idx_summary" to ascii(summary.toString()))
        if (m.entries.sumOf { it.key.length + it.value.length } > MAX_METADATA) m["idx_summary"] = "{}"
        return m
    }

    /** Builds the zip in [dir] and uploads it whole with its metadata. [ifAbsent]: create-only (false when it exists). */
    fun upload(api: BlobApi, base: String, extras: List<Triple<String, File, String>>, summary: JSONObject, nodeId: String, at: String, dir: File,
               ifAbsent: Boolean, cancelled: () -> Boolean = { false }): Boolean {
        dir.mkdirs()
        val zip = File(dir, "download-" + System.nanoTime() + ".zip")
        try {
            build(extras.filter { it.first != "downloaded.json" }.map { it.first to it.second }, zip)
            return api.putZip(base + NAME, zip, metadata(zip, nodeId, at, summary), emptyMap(), ifAbsent, cancelled)
        } finally { zip.delete() }
    }
}
