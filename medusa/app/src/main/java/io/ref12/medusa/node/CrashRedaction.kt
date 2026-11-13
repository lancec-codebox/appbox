package io.ref12.medusa.node

/** Keeps a SAS signature out of crash reports: an uncaught exception whose message carries sig= is passed on with the message redacted. */
object CrashRedaction {
    fun sanitize(t: Throwable): Throwable {
        val msg = t.message
        if (msg != null && Redact.sas(msg) != msg) {
            val c = RuntimeException(t.javaClass.name + ": " + Redact.sas(msg), t.cause?.let { sanitize(it) })
            c.stackTrace = t.stackTrace
            return c
        }
        val cause = t.cause ?: return t
        val sc = sanitize(cause)
        if (sc === cause) return t
        return RuntimeException(t.javaClass.name + ": " + (t.message ?: ""), sc).also { it.stackTrace = t.stackTrace }
    }

    fun install() {
        val prev = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { th, e -> if (prev != null) prev.uncaughtException(th, sanitize(e)) }
    }
}
