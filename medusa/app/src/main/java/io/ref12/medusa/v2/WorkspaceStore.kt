package io.ref12.medusa.v2

import java.io.File
import java.security.SecureRandom

/** Working folders for tool runs: one folder per workspace, paths can never leave it, unused ones are removed after a day. */
class WorkspaceStore(
    private val root: File,
    private val clock: () -> Long = System::currentTimeMillis,
    private val ttlMs: Long = DAY_MS,
    private val random: SecureRandom = SecureRandom(),
) {
    data class Entry(val path: String, val size: Long, val modified: Long)

    fun create(): String {
        val id = ByteArray(8).also { random.nextBytes(it) }.joinToString("") { "%02x".format(it) }
        val d = File(root, id)
        d.mkdirs()
        touch(id)
        return id
    }

    fun count(): Int = (root.listFiles() ?: emptyArray()).count { it.isDirectory }

    fun dir(id: String): File? = if (ID.matches(id)) File(root, id).takeIf { it.isDirectory } else null

    fun touch(id: String) {
        val d = dir(id) ?: return
        val m = File(d, MARKER)
        try { if (!m.exists()) m.writeText("") ; m.setLastModified(clock()) } catch (e: Exception) { /* best effort */ }
    }

    fun delete(id: String): Boolean = dir(id)?.deleteRecursively() ?: false

    /** The file for [path] inside the workspace, or null when the path is not allowed (absolute, "..", backslash, hidden Medusa files, a link leading out). */
    fun resolve(id: String, path: String): File? {
        val d = dir(id) ?: return null
        val rel = checkPath(path) ?: return null
        val base = d.canonicalFile
        val c = File(base, rel).canonicalFile
        return if (c.path.startsWith(base.path + File.separator)) c else null
    }

    fun list(id: String): List<Entry>? {
        val d = dir(id) ?: return null
        val base = d.canonicalFile
        return base.walkTopDown().filter { it.isFile && !it.name.startsWith(HIDDEN) }.take(MAX_LIST)
            .map { Entry(it.relativeTo(base).path.replace(File.separatorChar, '/'), it.length(), it.lastModified()) }
            .sortedBy { it.path }.toList()
    }

    /** Removes workspaces nobody touched for the time to live, except those [inUse]; returns how many. */
    fun cleanup(inUse: (String) -> Boolean = { false }): Int {
        var n = 0
        for (d in root.listFiles() ?: emptyArray()) {
            if (!d.isDirectory || inUse(d.name)) continue
            val m = File(d, MARKER)
            val last = if (m.exists()) m.lastModified() else d.lastModified()
            if (clock() - last > ttlMs) { d.deleteRecursively(); n++ }
        }
        return n
    }

    companion object {
        const val DAY_MS = 24L * 3600 * 1000
        const val MARKER = ".medusa-ws"
        const val HIDDEN = ".medusa-"
        const val MAX_LIST = 5000
        private val ID = Regex("[0-9a-f]{16}")

        /** The relative path when it is safe, else null. */
        fun checkPath(p: String): String? {
            if (p.isEmpty() || p.length > 512 || p.startsWith("/") || p.contains('\\') || p.any { it.code < 0x20 || it.code == 0x7f }) return null
            val segs = p.split('/')
            if (segs.any { it.isEmpty() || it == "." || it == ".." || it.startsWith(HIDDEN) }) return null
            return segs.joinToString("/")
        }
    }
}
