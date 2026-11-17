package io.ref12.medusa.node

import java.io.File
import java.io.PrintWriter
import java.io.StringWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/** The last uncaught exception, kept in a file so the Index node screen can show and share it (no adb needed). SAS signatures are redacted. */
object CrashLog {
    const val FILE = "last-crash.txt"

    class Record(val time: String, val summary: String, val version: String, val stack: String) {
        /** "Last crash: <time> <type: message>" */
        fun line() = "Last crash: $time $summary"
        fun text() = "Medusa $version\n$time\n$summary\n\n$stack"
    }

    fun write(dir: File, t: Throwable, version: String, nowMs: Long = System.currentTimeMillis()) {
        try {
            val s = CrashRedaction.sanitize(t)
            val sw = StringWriter(); s.printStackTrace(PrintWriter(sw))
            val time = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }.format(Date(nowMs))
            val summary = Redact.sas(t.javaClass.name + (t.message?.let { ": $it" } ?: "")).replace('\n', ' ').take(500)
            val body = "$time\n$summary\n$version\n" + Redact.sas(sw.toString()).take(20_000)
            dir.mkdirs(); File(dir, FILE).writeText(body)
        } catch (e: Throwable) { /* the crash itself matters more than its record */ }
    }

    fun read(dir: File): Record? = try {
        val f = File(dir, FILE)
        if (!f.isFile) null else f.readText().split("\n", limit = 4).let { p -> if (p.size < 4) null else Record(p[0], p[1], p[2], p[3]) }
    } catch (e: Exception) { null }

    fun clear(dir: File) { File(dir, FILE).delete() }
}

/** What a button does when its action throws: nothing crashes, the reason is shown. */
object SafeClick {
    fun run(what: String, show: (String) -> Unit, action: () -> String?) {
        try { action()?.let(show) } catch (e: Throwable) { show(what + " failed: " + Redact.sas(e.javaClass.simpleName + (e.message?.let { ": $it" } ?: ""))) }
    }
}
